using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.GraphicsInterface;
using Gengyi.Core;
using Geo=Gengyi.Core.Geometry;
using Layout=Gengyi.Core.Layout;

namespace Gengyi.Cad.AutoCAD2024
{
    internal sealed class TrackJig : DrawJig,IDisposable
    {
        private readonly Session session;
        private readonly List<V2> vertices;
        private readonly List<V2> keepLow=new List<V2>();
        private readonly List<Entity> graphics=new List<Entity>();
        private readonly Stopwatch refresh=Stopwatch.StartNew();
        private readonly int remaining;
        private bool flip;
        private bool stack;
        private V2? cursor,lastDrawn;
        private Candidate draft=new Candidate();
        internal bool Finished { get; private set; }

        internal TrackJig(Session session,V2 start)
        {
            this.session=session;vertices=new List<V2>{start};
            remaining=session.Room.Statistics.ConditionalDeficit;
            stack=session.Room.Options.AllowStacking;
            Update(false);
        }

        internal bool AcceptPoint()
        {
            if(cursor==null || cursor.Distance(vertices[vertices.Count-1])<=.001)return false;
            var candidate=Build(true,out _);
            if(candidate.Items.Count<=Build(false,out _).Items.Count)return false;
            vertices.Add(cursor);
            cursor=null;lastDrawn=null;
            Update(false);
            return true;
        }

        internal void ApplyKeyword(string keyword)
        {
            if(keyword=="Undo" && vertices.Count>1)vertices.RemoveAt(vertices.Count-1);
            if(keyword=="Flip")flip=!flip;
            if(keyword=="Stack")stack=!stack;
            if(keyword=="Low")
            {
                var p=CadGeometry.Local(session.Room.Boundary!.Frame,
                    CadGeometry.Point(session.Document.Editor,"选择已画好的鞋柜，切换保留单层："));
                var candidate=Build(false,out _);
                var point=Geo.Factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(p.X,p.Y));
                var item=candidate.Items.FirstOrDefault(x=>x.Equipment.IsShoe && Geo.Footprint(x).Covers(point));
                if(item!=null)
                {
                    var shape=Geo.Footprint(item);
                    if(keepLow.RemoveAll(low=>shape.Covers(Geo.Factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(low.X,low.Y))))==0)
                        keepLow.Add(p);
                }
                else session.Document.Editor.WriteMessage("\n请选择已落点轨迹中的鞋柜。");
            }
            cursor=null;lastDrawn=null;
            Update(false);
        }

        internal Candidate Complete()
        {
            Finished=false;
            var candidate=Build(false,out string note);
            if(candidate.Items.Count==0 || session.Room.Mode==RoomMode.Shoe && !Layout.IsShoeLayout(candidate.Items))
                throw new ArgumentException(note.Length>0?note:"轨迹无法布置设备，请重新指定。");
            // A build which stopped at an invalid corner must not commit only its prefix.
            if(note.Length>0)throw new ArgumentException(note);
            Check(candidate);
            var stats=candidate.Statistics;
            if(stats.Checks["geometry"]==CheckState.Fail || stats.Checks["boundary"]==CheckState.Fail ||
                stats.Checks[LockerAisles.CheckKey]==CheckState.Fail || stats.Checks["connections"]==CheckState.Fail)
                throw new ArgumentException(stats.Issues.FirstOrDefault()?.Message??"轨迹存在碰撞或净距不足，请调整后重试。");
            RoomAccess.RequireValid(stats);
            if(session.Room.Mode==RoomMode.FirstChange && !LockerAisles.PreservesCapacity(session.Room.Statistics,stats))
                throw new ArgumentException("轨迹使已有衣柜的可用人位减少，请调整柜位。");
            return candidate;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options=new JigPromptPointOptions(session.Room.Mode==RoomMode.Shoe
                ?$"\n指定鞋柜中心线下一点 [切换叠放(Stack)/锁单层(Low)/撤回点(Undo)]，回车完成，Esc取消（{(stack?"局部叠放":"单层")}）： "
                :"\n指定衣柜柜背线下一点 [翻侧(Flip)/撤回点(Undo)]，回车完成，Esc取消：");
            options.Keywords.Add("Undo");
            if(session.Room.Mode==RoomMode.Shoe){options.Keywords.Add("Low");options.Keywords.Add("Stack");}
            else options.Keywords.Add("Flip");
            options.UserInputControls=UserInputControls.Accept3dCoordinates|UserInputControls.NullResponseAccepted;
            options.BasePoint=CadGeometry.World(session.Room.Boundary!.Frame,vertices[vertices.Count-1]);
            options.UseBasePoint=true;
            var p=prompts.AcquirePoint(options);
            if(p.Status==PromptStatus.None){Finished=true;return SamplerStatus.OK;}
            if(p.Status==PromptStatus.Keyword)return SamplerStatus.OK;
            if(p.Status!=PromptStatus.OK)return SamplerStatus.Cancel;
            var position=Layout.TrackPoint(session.Room,vertices,CadGeometry.Local(session.Room.Boundary.Frame,p.Value));
            cursor=position;
            if(refresh.ElapsedMilliseconds<100)return SamplerStatus.NoChange;
            if(lastDrawn!=null && lastDrawn.Distance(position)<.01)return SamplerStatus.NoChange;
            lastDrawn=position;
            Update(true);
            return SamplerStatus.OK;
        }

        private Candidate Build(bool withCursor,out string note)
        {
            var route=withCursor && cursor!=null && cursor.Distance(vertices[vertices.Count-1])>.001?
                vertices.Concat(new[]{cursor}).ToList():vertices;
            return Layout.Track(session.Room,route,remaining,flip,keepLow,out note,stack);
        }
        private void Check(Candidate candidate)
        {
            foreach(var item in candidate.Items)Layout.ResolveMapping(session.Project,item);
            candidate.Statistics=Validation.Evaluate(session.Project,session.Room,candidate.Scene(session.Room));
        }
        private void Update(bool withCursor)
        {
            refresh.Restart();
            draft=Build(withCursor,out string note);Check(draft);
            foreach(var entity in graphics)entity.Dispose();
            graphics.Clear();
            var frame=session.Room.Boundary!.Frame;
            var route=withCursor && cursor!=null?vertices.Concat(new[]{cursor}):vertices;
            if(route.Count()>1)
            {
                var guide=CadGeometry.PreviewPolyline(session.Document.Database,route,frame,8);
                guide.Closed=false;graphics.Add(guide);
            }
            var blocked=new HashSet<string>(draft.Statistics.Issues.Where(i=>i.Code!="CAPACITY_BLOCKED" &&
                i.InstanceId.Length>0).Select(i=>i.InstanceId));
            foreach(var item in draft.Items)
            {
                short color=blocked.Contains(item.Id)?(short)1:item.Locked?(short)2:item.EquipmentId=="S48"?(short)6:(short)3;
                graphics.Add(CadGeometry.PreviewPolyline(session.Document.Database,Geo.Outline(item),frame,color));
                foreach(var text in Blocks.Labels(session.Document.Database,session.Room,item))graphics.Add(text);
            }
            Host.Panel?.SetAppendStatistics(session.Room,draft,note);
        }
        protected override bool WorldDraw(WorldDraw draw)
        {
            foreach(var entity in graphics)draw.Geometry.Draw(entity);
            return true;
        }
        public void Dispose(){foreach(var entity in graphics)entity.Dispose();graphics.Clear();}
    }
}
