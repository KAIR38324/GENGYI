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
        internal Session(Document document)
        {
            Document=document;
            document.Database.ObjectModified+=Changed;document.Database.ObjectAppended+=Changed;
            document.Database.ObjectErased+=Erased;
        }
        private void Changed(object sender,ObjectEventArgs args){if(!Writing){Dirty=true;WitnessPath=null;}}
        private void Erased(object sender,ObjectErasedEventArgs args){if(!Writing){Dirty=true;WitnessPath=null;}}
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
                        if(!(transaction.GetObject(id,OpenMode.ForRead) is Polyline pl) || !pl.Closed || pl.NumberOfVertices!=4 || Enumerable.Range(0,4).Any(i=>Math.Abs(pl.GetBulgeAt(i))>1e-9))
                            throw new ArgumentException("原生编辑后的边界不再是闭合直线矩形。");
                        var a=pl.GetPoint3dAt(0);var x=pl.GetPoint3dAt(1)-a;var y=pl.GetPoint3dAt(3)-a;
                        if(x.Length<1e-8 || y.Length<1e-8 || Math.Abs(x.GetNormal().DotProduct(y.GetNormal()))>1e-8 || (pl.GetPoint3dAt(2)-(a+x+y)).Length>1e-6)
                            throw new ArgumentException("原生编辑后的房间边界不是有效矩形。");
                        var frame=new Frame{Origin=CadGeometry.A(a),XAxis=CadGeometry.A(x.GetNormal()),YAxis=CadGeometry.A(y.GetNormal()),MillimetersPerDrawingUnit=boundary.Frame.MillimetersPerDrawingUnit};
                        if(Serialization.Write(frame)!=Serialization.Write(boundary.Frame))
                        {
                            if(room.DirtySeed!=null)room.DirtySeed=CadGeometry.Local(frame,CadGeometry.World(boundary.Frame,room.DirtySeed));
                            if(room.CleanSeed!=null)room.CleanSeed=CadGeometry.Local(frame,CadGeometry.World(boundary.Frame,room.CleanSeed));
                            room.Stale=true;
                        }
                        boundary.Frame=frame;boundary.Width=x.Length*frame.MillimetersPerDrawingUnit;boundary.Height=y.Length*frame.MillimetersPerDrawingUnit;
                    }
                    catch(System.Exception ex) when(ex is ArgumentException || ex is Autodesk.AutoCAD.Runtime.Exception)
                    {room.ConstraintIssue=ex.Message;}
                }
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
                        var definition=(BlockTableRecord)transaction.GetObject(reference.IsDynamicBlock?reference.DynamicBlockTableRecord:reference.BlockTableRecord,OpenMode.ForRead);
                        if(definition.Name!=mapping.BlockName){item.NativeGeometryUnverified=true;item.NativeIssue="原生块定义已变，需重新绑定设备。";}
                        if(reference.IsDynamicBlock)
                            foreach(DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
                                if(!property.ReadOnly && (!mapping.DynamicValues.TryGetValue(property.PropertyName,out var expected) ||
                                    Convert.ToString(property.Value,System.Globalization.CultureInfo.InvariantCulture)!=Convert.ToString(expected,System.Globalization.CultureInfo.InvariantCulture)))
                                {item.NativeGeometryUnverified=true;item.NativeIssue="原生动态属性已变化，需核对并重新绑定，不能继续用旧容量。";}
                    }
                    else{item.NativeGeometryUnverified=true;item.NativeIssue="缺少原块坐标标定，当前几何和容量未核。";}
                    room.Placed.Add(item);
                }
                foreach(var room in Project.Rooms)
                {
                    foreach(var region in room.Obstacles.Concat(room.Closures))
                    {
                        if(region.NativeHandle.Length==0 || room.Boundary==null)continue;
                        try
                        {
                            var id=Document.Database.GetObjectId(false,new Handle(Convert.ToInt64(region.NativeHandle,16)),0);
                            if(id.IsErased){region.Points.Clear();continue;}
                            if(transaction.GetObject(id,OpenMode.ForRead) is Polyline pl)
                                region.Points=CadGeometry.PolylinePoints(pl).Select(p=>CadGeometry.Local(room.Boundary.Frame,p)).ToList();
                        }
                        catch(Autodesk.AutoCAD.Runtime.Exception){region.Points.Clear();}
                        catch(ArgumentException ex){room.ConstraintIssue="现场轮廓失效："+ex.Message;}
                    }
                    room.Obstacles.RemoveAll(r=>r.Points.Count<3);room.Closures.RemoveAll(r=>r.Points.Count<3);
                    room.Statistics=Validation.Evaluate(Project,room,room.Placed);
                    if(room.PreviewVisible && room.Candidates.Count>0)
                    {
                        room.SelectedCandidate=Math.Max(0,Math.Min(room.SelectedCandidate,room.Candidates.Count-1));
                        var candidate=room.Candidates[room.SelectedCandidate];
                        candidate.Statistics=Validation.Evaluate(Project,room,candidate.Items);
                    }
                }
                transaction.Commit();
            }
            Dirty=false;
        }
        internal void Edit(Action<Transaction> action)
        {
            WitnessPath=null;
            string snapshot=Serialization.Write(Project);
            Writing=true;
            try
            {
                using(var transaction=Document.Database.TransactionManager.StartTransaction())
                {
                    action(transaction);Store.Project(Document.Database,Project,transaction);transaction.Commit();
                }
                saved=Store.Pack(Project);
            }
            catch{Project=Serialization.Read<Project>(snapshot);throw;}
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
                if(s.Room.PreviewVisible && s.Room.Candidates.Count>0)s.Preview.Show(s,s.Room.Candidates[Math.Min(s.Room.SelectedCandidate,s.Room.Candidates.Count-1)].Items);
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
