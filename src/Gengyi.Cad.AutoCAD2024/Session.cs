using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Gengyi.Core;
using CadApplication=Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Gengyi.Cad.AutoCAD2024
{
    internal sealed class Session : IDisposable
    {
        internal Document Document {get;}
        internal Project Project {get;set;}=Project.Create();
        internal Room Room=>Project.Current;
        internal bool Dirty=true;
        internal bool Writing;
        internal List<V2>? WitnessPath;
        internal string WitnessRoom="";
        internal readonly Preview Preview=new Preview();
        private string? saved;
        private readonly HashSet<string> roomsWithGraphics=new HashSet<string>();
        internal Session(Document document)
        {
            Document=document;
            document.Database.ObjectModified+=Changed;document.Database.ObjectAppended+=Changed;
            document.Database.ObjectErased+=Erased;
        }
        private void Changed(object sender,ObjectEventArgs args){if(!Writing){Dirty=true;WitnessPath=null;}}
        private void Erased(object sender,ObjectErasedEventArgs args){if(!Writing){Dirty=true;WitnessPath=null;}}
        private static void Unverified(Placement item,string issue)
        {
            item.NativeGeometryUnverified=true;
            if(item.NativeIssue.Length>0 && !item.NativeIssue.Contains(issue))item.NativeIssue+=" "+issue;
            else if(item.NativeIssue.Length==0)item.NativeIssue=issue;
        }
        internal void Refresh()
        {
            using(var transaction=Document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var nod=(DBDictionary)transaction.GetObject(Document.Database.NamedObjectsDictionaryId,OpenMode.ForRead);
                string? text=Store.Read(nod,Store.ProjectKey,transaction);
                if(text!=saved && text!=null){Project=Store.Unpack(text);saved=text;}
                else if(text==null && saved!=null){Project=Project.Create();saved=null;}
                foreach(var room in Project.Rooms)
                {
                    room.ConstraintIssue="";
                    if(room.Boundary==null)continue;
                    var boundary=room.Boundary;
                    if(Document.Database.Insunits!=UnitsValue.Undefined && Math.Abs(UnitsConverter.GetConversionFactor(Document.Database.Insunits,UnitsValue.Millimeters)-boundary.Frame.MillimetersPerDrawingUnit)>1e-8)
                        room.ConstraintIssue="图纸单位已与房间标定不一致，需要在实际 CAD 中核对单位；不能静默缩放设备。";
                    if(boundary.NativeHandle.Length==0)continue;
                    try
                    {
                        var id=Document.Database.GetObjectId(false,new Handle(Convert.ToInt64(boundary.NativeHandle,16)),0);
                        if(id.IsErased)throw new ArgumentException("所选房间边界已删除。");
                        if(!(transaction.GetObject(id,OpenMode.ForRead) is Polyline pl))
                            throw new ArgumentException("原生编辑后的房间边界不再是有效闭合多段线。");
                        var next=CadGeometry.ReadBoundary(pl,boundary.Frame.MillimetersPerDrawingUnit,boundary.Frame);
                        if(!CadGeometry.SameBoundary(next,boundary))
                        {
                            string issue=room.ConstraintIssue;
                            BoundaryEditing.Replace(room,next,false);
                            room.ConstraintIssue=issue;
                        }
                    }
                    catch(System.Exception ex) when(ex is ArgumentException || ex is Autodesk.AutoCAD.Runtime.Exception)
                    {room.ConstraintIssue=ex.Message;}
                }
                if(Preview.PlanningVisible && (Preview.PlanningRoomId!=Room.Id || Preview.PlanningSignature!=AreaPlanning.Signature(Project.Population,Room)))Preview.Clear();
                foreach(var room in Project.Rooms)room.Placed.Clear();
                var table=(BlockTable)transaction.GetObject(Document.Database.BlockTableId,OpenMode.ForRead);
                var model=(BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForRead);
                foreach(ObjectId id in model)
                {
                    if(!(transaction.GetObject(id,OpenMode.ForRead) is BlockReference reference))continue;
                    var json=Store.Instance(reference,transaction);if(json==null)continue;
                    Placement item;
                    try{item=Serialization.Read<Placement>(json);}
                    catch(Newtonsoft.Json.JsonException)
                    {foreach(var r in Project.Rooms)r.ConstraintIssue="块 "+reference.Handle+" 的设备元数据损坏，需核对/修复，不能忽略后认定人数满足。";continue;}
                    var room=Project.Rooms.FirstOrDefault(r=>r.Id==item.RoomId);
                    if(room?.Boundary==null)continue;
                    if(item.NativeHandle!=reference.Handle.ToString())
                    {
                        item.Id=item.Id.Split(':')[0]+":"+reference.Handle;
                        if(item.GroupId.Length>0){item.GroupId="";item.NativeCopyDetached=true;}
                    }
                    item.NativeHandle=reference.Handle.ToString();
                    item.NativeGeometryUnverified=false;item.NativeIssue="";
                    if(Project.Mappings.TryGetValue(item.MappingKey,out var mapping))
                    {
                        CadGeometry.ReadTransform(room,item,reference,mapping);
                        if(!mapping.Accepts(item))Unverified(item,Blocks.MappingMismatch(item));
                        var definition=(BlockTableRecord)transaction.GetObject(reference.IsDynamicBlock?reference.DynamicBlockTableRecord:reference.BlockTableRecord,OpenMode.ForRead);
                        if(definition.Name!=mapping.BlockName)Unverified(item,"原生块定义已变，需重新绑定设备。");
                        if(reference.IsDynamicBlock)
                            foreach(DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
                                if(!property.ReadOnly && (!mapping.DynamicValues.TryGetValue(property.PropertyName,out var expected) ||
                                    Convert.ToString(property.Value,System.Globalization.CultureInfo.InvariantCulture)!=Convert.ToString(expected,System.Globalization.CultureInfo.InvariantCulture)))
                                Unverified(item,"原生动态属性已变化，需核对并重新绑定，不能继续用旧容量。");
                    }
                    else Unverified(item,"缺少原块坐标标定，当前几何和容量未核。");
                    room.Placed.Add(item);
                }
                foreach(var room in Project.Rooms)
                {
                    foreach(var region in room.Obstacles.Concat(room.Closures).Concat(room.Passages))
                    {
                        if(region.NativeHandle.Length==0 || room.Boundary==null)continue;
                        bool passage=room.Passages.Contains(region);
                        try
                        {
                            var id=Document.Database.GetObjectId(false,new Handle(Convert.ToInt64(region.NativeHandle,16)),0);
                            if(id.IsErased)
                            {
                                if(passage)throw new ArgumentException("所选轮廓已删除。");
                                region.Points.Clear();continue;
                            }
                            if(transaction.GetObject(id,OpenMode.ForRead) is Polyline pl)
                            {
                                var points=CadGeometry.PolylinePoints(pl,.05/room.Boundary.Frame.MillimetersPerDrawingUnit).Select(p=>CadGeometry.Local(room.Boundary.Frame,p)).ToList();
                                Geometry.Polygon(points);
                                if(passage && (points.Count!=region.Points.Count || points.Where((p,i)=>p.Distance(region.Points[i])>.00001).Any()))
                                    room.InvalidateCandidates();
                                region.Points=points;
                                region.CurveToleranceMm=Enumerable.Range(0,pl.NumberOfVertices).Any(i=>Math.Abs(pl.GetBulgeAt(i))>1e-10)?.05:0;
                            }
                            else throw new ArgumentException("所选轮廓已不再是闭合多段线。");
                        }
                        catch(System.Exception ex) when(ex is ArgumentException || ex is Autodesk.AutoCAD.Runtime.Exception)
                        {
                            if(passage){room.ConstraintIssue="通行保留区失效，请替换或清空："+ex.Message;room.InvalidateCandidates();}
                            else if(ex is Autodesk.AutoCAD.Runtime.Exception)region.Points.Clear();
                            else room.ConstraintIssue="现场轮廓失效："+ex.Message;
                        }
                    }
                    room.Obstacles.RemoveAll(r=>r.Points.Count<3);room.Closures.RemoveAll(r=>r.Points.Count<3);
                    room.Statistics=Validation.Evaluate(Project,room,room.Placed);
                    if(room.PreviewVisible && room.Candidates.Count>0)
                    {
                        room.SelectedCandidate=Math.Max(0,Math.Min(room.SelectedCandidate,room.Candidates.Count-1));
                        var candidate=room.Candidates[room.SelectedCandidate];
                        candidate.Statistics=Validation.Evaluate(Project,room,candidate.Scene(room));
                    }
                }
                transaction.Commit();
            }
            Dirty=false;
        }
        internal void EnableRoomGraphics()
        {
            Preview.Clear();
            roomsWithGraphics.Add(Room.Id);
        }
        internal void HideRoomGraphics()
        {
            roomsWithGraphics.Remove(Project.CurrentRoomId);
            var room=Project.Rooms.FirstOrDefault(r=>r.Id==Project.CurrentRoomId);
            if(room!=null)room.PreviewVisible=false;
            WitnessPath=null;
            Preview.Clear();
            Document.Editor.Regen();
        }
        internal void ShowRoomPreview()
        {
            if(Preview.PlanningVisible)return;
            if(!roomsWithGraphics.Contains(Room.Id))
            {
                Preview.Clear();
                return;
            }
            var items=Room.PreviewVisible && Room.Candidates.Count>0?Room.Candidates[Room.SelectedCandidate].Items:Enumerable.Empty<Placement>();
            Preview.Show(this,items);
            if(Room.Boundary!=null && WitnessRoom==Room.Id && WitnessPath!=null && WitnessPath.Count>1)
            {
                var line=CadGeometry.Polyline(WitnessPath,Room.Boundary.Frame,1);
                line.Closed=false;Preview.Overlay(line);
            }
        }
        internal void Edit(Action<Transaction> action)
        {
            WitnessPath=null;
            string snapshot=Serialization.Write(Project);
            var previews=new HashSet<string>(Project.Rooms.Where(room=>room.PreviewVisible).Select(room=>room.Id));
            Writing=true;
            try
            {
                using(var transaction=Document.Database.TransactionManager.StartTransaction())
                {
                    action(transaction);
                    string projectData=Store.SaveProject(Document.Database,Project,transaction);
                    transaction.Commit();
                    saved=projectData;
                }
            }
            catch
            {
                Project=Serialization.Read<Project>(snapshot);
                foreach(var room in Project.Rooms)room.PreviewVisible=previews.Contains(room.Id);
                throw;
            }
            finally{Writing=false;Dirty=true;}
        }
        internal void Save()=>Edit(_=>{});
        internal void Report(bool detailed=false,Statistics? selected=null)
        {
            var stats=selected??Room.Statistics;
            Document.Editor.WriteMessage("\n"+Room.Name+"："+stats.Summary);
            Document.Editor.WriteMessage($"\n柜组 {stats.Groups} / 实体箱体 {stats.Boxes} / 转角 {stats.Corners} / 单层鞋柜 {stats.SingleShoe} / 叠柜 {stats.StackedShoe} / 投影 {stats.ProjectionM2:0.###} ㎡ / 选区 {stats.BoundaryM2:0.###} ㎡");
            Document.Editor.WriteMessage($"\n一更柜 {stats.LockerCabinets} / 挂架段 {stats.HangerSegments} / 挂架总长 {stats.HangerLengthMm:0.###} mm / 辅助设备 {stats.AuxiliaryCount}");
            if(Room.Mode==RoomMode.Shoe)Document.Editor.WriteMessage($"\n名义储格 {stats.NominalShoeCells}，每人脏净配对两格，不能将储格数当人数。");
            foreach(var check in stats.Checks)Document.Editor.WriteMessage("\n"+check.Key+"："+check.Value);
            if(detailed)foreach(var issue in stats.Issues.Take(100))Document.Editor.WriteMessage("\n"+issue.Code+" ["+issue.InstanceId+"] "+issue.Message);
        }
        public void Dispose()
        {
            Preview.Dispose();Document.Database.ObjectModified-=Changed;Document.Database.ObjectAppended-=Changed;Document.Database.ObjectErased-=Erased;
        }
    }

    internal static class Host
    {
        private static readonly Dictionary<Document,Session> Sessions=new Dictionary<Document,Session>();
        internal static Palette? Panel;
        internal static Session Active
        {
            get
            {
                var document=CadApplication.DocumentManager.MdiActiveDocument??throw new ArgumentException("请先打开图纸。");
                if(!Sessions.TryGetValue(document,out var session))Sessions[document]=session=new Session(document);
                return session;
            }
        }
        internal static void Start()
        {
            CadApplication.Idle+=Idle;
            CadApplication.DocumentManager.DocumentToBeDestroyed+=Closing;
            CadApplication.DocumentManager.DocumentActivated+=Activated;
        }
        private static void Closing(object sender,DocumentCollectionEventArgs args)
        {if(Sessions.TryGetValue(args.Document,out var s)){s.Dispose();Sessions.Remove(args.Document);}}
        private static void Activated(object sender,DocumentCollectionEventArgs args)
        {foreach(var s in Sessions.Values)s.Preview.Clear();if(!Sessions.TryGetValue(args.Document,out var active))Sessions[args.Document]=active=new Session(args.Document);active.Dirty=true;}
        private static void Idle(object? sender,EventArgs args)
        {
            var doc=CadApplication.DocumentManager.MdiActiveDocument;
            if(doc==null || !Sessions.TryGetValue(doc,out var s) || !s.Dirty || s.Writing || doc.CommandInProgress.Length>0)return;
            try
            {
                s.Refresh();Panel?.Update(s);
                s.ShowRoomPreview();
            }
            catch(Exception ex){s.Dirty=false;doc.Editor.WriteMessage("\nGENGYI 同步失败："+ex.Message);}
        }
        internal static void Stop()
        {
            CadApplication.Idle-=Idle;CadApplication.DocumentManager.DocumentToBeDestroyed-=Closing;CadApplication.DocumentManager.DocumentActivated-=Activated;
            foreach(var s in Sessions.Values)s.Dispose();Sessions.Clear();Panel?.Dispose();Panel=null;
        }
    }
}
