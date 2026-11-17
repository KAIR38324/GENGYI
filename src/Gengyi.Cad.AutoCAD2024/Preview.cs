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
        internal void Overlay(Entity entity)
        {
            entities.Add(entity);
            TransientManager.CurrentTransientManager.AddTransient(entity,TransientDrawingMode.DirectShortTerm,128,new IntegerCollection());
        }
        internal void Show(Session session,IEnumerable<Placement> items)
        {
            Clear();if(session.Room.Boundary==null)return;
            try
            {
                using(var transaction=session.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach(var item in items)
                    {
                        Entity entity;
                        try{entity=Blocks.Create(session.Document.Database,transaction,session.Room,item,Blocks.Mapping(session,item),false);}
                        catch(ArgumentException){entity=CadGeometry.Polyline(Gengyi.Core.Geometry.Outline(item),session.Room.Boundary.Frame,1);}
                        entities.Add(entity);
                        TransientManager.CurrentTransientManager.AddTransient(entity,TransientDrawingMode.DirectShortTerm,128,new IntegerCollection());
                        foreach(var text in Blocks.Labels(session.Document.Database,session.Room,item))Overlay(text);
                    }
                    transaction.Commit();
                }
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
