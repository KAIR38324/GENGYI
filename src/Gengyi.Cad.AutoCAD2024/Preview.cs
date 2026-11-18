using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Gengyi.Core;
using System.Linq;
using Polyline=Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace Gengyi.Cad.AutoCAD2024
{
    internal sealed class Preview : IDisposable
    {
        private readonly List<Entity> entities=new List<Entity>();
        internal bool PlanningVisible{get;private set;}
        // A completed display is an event even when the same saved option is shown again.
        // Clear deliberately preserves this counter so idle refreshes cannot replay the event.
        internal long PlanningRevision{get;private set;}
        internal string PlanningSignature{get;private set;}="";
        internal string PlanningRoomId{get;private set;}="";
        internal void Overlay(Entity entity)
        {
            entities.Add(entity);
            TransientManager.CurrentTransientManager.AddTransient(entity,TransientDrawingMode.DirectShortTerm,128,new IntegerCollection());
        }
        internal void Show(Session session,IEnumerable<Placement> items)
        {
            Clear();if(session.Room.Boundary==null)return;
            ShowItems(session,session.Room,items);
            var room=session.Room;
            foreach(var door in room.Doors)
                try
                {
                    var zone=RoomAccess.Landing(room.Boundary,door,room.Options.WalkWidthMm);
                    short color=door.Side==DoorSide.Clean?(short)3:(short)4;
                    Overlay(CadGeometry.Polyline(zone,room.Boundary.Frame,color));
                    var opening=CadGeometry.Polyline(new[]{door.Start,door.End},room.Boundary.Frame,color);
                    opening.Closed=false;Overlay(opening);
                }
                catch(ArgumentException){ /* Report invalid doors in the room statistics. */ }
            foreach(var region in room.Passages.Where(r=>r.Points.Count>=3))
                Overlay(CadGeometry.Polyline(region.Points,room.Boundary.Frame,6));
        }
        private void ShowItems(Session session,Room room,IEnumerable<Placement> items)
        {
            try
            {
                using(var transaction=session.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach(var item in items)
                    {
                        Entity entity;
                        try{entity=Blocks.Create(session.Document.Database,transaction,room,item,Blocks.Mapping(session,item),false);}
                        catch(ArgumentException){entity=CadGeometry.Polyline(Gengyi.Core.Geometry.Outline(item),room.Boundary!.Frame,1);}
                        entities.Add(entity);
                        TransientManager.CurrentTransientManager.AddTransient(entity,TransientDrawingMode.DirectShortTerm,128,new IntegerCollection());
                        foreach(var text in Blocks.Labels(session.Document.Database,room,item))Overlay(text);
                    }
                    transaction.Commit();
                }
            }
            catch{Clear();throw;}
        }
        internal void ShowPlanning(Session session,RectangleEstimate estimate,Boundary boundary,bool rotated,string signature)
        {
            Clear();
            try
            {
                var room=new Room{Id=session.Room.Id,Mode=session.Room.Mode,Boundary=Serialization.Copy(boundary),Options=Serialization.Copy(session.Room.Options)};
                var items=estimate.Items.Select(original=>
                {
                    var item=Serialization.Copy(original);
                    if(rotated){item.Position=new V2(estimate.HeightMm-original.Position.Y,original.Position.X);item.Rotation=Gengyi.Core.Geometry.Normalize(original.Rotation+90);}
                    Gengyi.Core.Layout.ResolveMapping(session.Project,item);return item;
                }).ToList();
                ShowItems(session,room,items);
                Overlay(CadGeometry.Polyline(Gengyi.Core.Geometry.RoomOutline(boundary),boundary.Frame,3));
                double height=Math.Max(120,Math.Min(boundary.Width,boundary.Height)/45);
                var text=new DBText();text.SetDatabaseDefaults(session.Document.Database);text.ColorIndex=3;
                text.TextString=$"人数预排参考 · {boundary.Width/1000:0.##}×{boundary.Height/1000:0.##}m · {boundary.AreaM2:0.##}㎡ · 非实际房间边界";
                text.Position=new Point3d(0,boundary.Height+height,0);text.Height=height;
                text.TransformBy(CadGeometry.ToWorld(boundary.Frame));Overlay(text);
                PlanningVisible=true;PlanningSignature=signature;PlanningRoomId=session.Room.Id;PlanningRevision++;
            }
            catch{Clear();throw;}
        }
        internal void Clear()
        {
            foreach(var entity in entities)
            {
                try{TransientManager.CurrentTransientManager.EraseTransient(entity,new IntegerCollection());}
                catch(Autodesk.AutoCAD.Runtime.Exception){ }
                finally{entity.Dispose();}
            }
            entities.Clear();
            PlanningVisible=false;PlanningSignature="";PlanningRoomId="";
        }
        public void Dispose()=>Clear();
    }

    internal sealed class MoveJig : Autodesk.AutoCAD.EditorInput.DrawJig, IDisposable
    {
        private readonly Session session;
        private readonly List<Placement> items;
        private readonly IReadOnlyList<Placement> scene;
        private readonly V2 anchor;
        private V2 delta=new V2();
        private readonly List<Entity> lines=new List<Entity>();
        private readonly System.Diagnostics.Stopwatch feedback=System.Diagnostics.Stopwatch.StartNew();
        internal V2 Delta=>delta;
        internal MoveJig(Session session,List<Placement> items,V2 anchor,IReadOnlyList<Placement>? scene=null)
        {this.session=session;this.items=items;this.anchor=anchor;this.scene=scene??session.Room.Placed;}
        protected override Autodesk.AutoCAD.EditorInput.SamplerStatus Sampler(Autodesk.AutoCAD.EditorInput.JigPrompts prompts)
        {
            var options=new Autodesk.AutoCAD.EditorInput.JigPromptPointOptions("\n拖动落位（沿房间局部轴正交；Esc 取消）：");
            options.UserInputControls=Autodesk.AutoCAD.EditorInput.UserInputControls.Accept3dCoordinates;
            var value=prompts.AcquirePoint(options);
            if(value.Status!=Autodesk.AutoCAD.EditorInput.PromptStatus.OK)return Autodesk.AutoCAD.EditorInput.SamplerStatus.Cancel;
            var p=CadGeometry.Local(session.Room.Boundary!.Frame,value.Value);var next=p-anchor;
            if(session.Room.Options.Orthogonal)next=Math.Abs(next.X)>=Math.Abs(next.Y)?new V2(next.X,0):new V2(0,next.Y);
            if(next.Distance(delta)<.01)return Autodesk.AutoCAD.EditorInput.SamplerStatus.NoChange;
            delta=next;
            if(feedback.ElapsedMilliseconds>150)
            {
                var proposed=scene.Select(p=>
                {
                    if(!items.Any(i=>i.Id==p.Id))return p;
                    var copy=Serialization.Copy(p);copy.Position+=delta;return copy;
                }).ToList();
                Host.Panel?.SetStatistics(Validation.Evaluate(session.Project,session.Room,proposed));feedback.Restart();
            }
            return Autodesk.AutoCAD.EditorInput.SamplerStatus.OK;
        }
        protected override bool WorldDraw(WorldDraw draw)
        {
            foreach(var line in lines)line.Dispose();lines.Clear();
            foreach(var original in items)
            {
                var item=Serialization.Copy(original);item.Position+=delta;
                var line=CadGeometry.Polyline(Gengyi.Core.Geometry.Outline(item),session.Room.Boundary!.Frame,3);
                lines.Add(line);draw.Geometry.Draw(line);
                foreach(var text in Blocks.Labels(session.Document.Database,session.Room,item))
                {lines.Add(text);draw.Geometry.Draw(text);}
            }
            return true;
        }
        public void Dispose(){foreach(var line in lines)line.Dispose();lines.Clear();}
    }
}
