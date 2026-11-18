using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Gengyi.Core;
using CadApplication=Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Geometry=Gengyi.Core.Geometry;
using Layout=Gengyi.Core.Layout;
using Region=Gengyi.Core.Region;

namespace Gengyi.Cad.AutoCAD2024
{
    public sealed class Commands
    {
        internal static void Run(Action<Session> action,bool hideGraphicsAfter=false)
        {
            Session? session=null;
            bool completed=false;
            try
            {
                session=Host.Active;
                session.Refresh();
                action(session);
                completed=true;
                session.Refresh();
            }
            catch(Cancelled){session?.Document.Editor.WriteMessage("\n已取消，未提交当前操作。");}
            catch(System.Exception ex)
            {
                var prefix=completed?"操作已完成，刷新图纸状态失败：":"GENGYI：";
                session?.Document.Editor.WriteMessage("\n"+prefix+ex.Message);
            }
            finally
            {
                if(session!=null)
                {
                    try
                    {
                        if(hideGraphicsAfter)session.HideRoomGraphics();
                        else session.ShowRoomPreview();
                    }
                    catch(System.Exception ex){session.Document.Editor.WriteMessage("\n临时图形刷新失败："+ex.Message);}
                    Host.Panel?.Update(session);
                }
            }
        }
        [CommandMethod("GENGYI","GYSETUP",CommandFlags.Modal)]
        public void Setup()=>Run(s=>{Blocks.EnsureDescriptions(s);if(Host.Panel==null)Host.Panel=new Palette();Host.Panel.Show(s);});
        [CommandMethod("GENGYI","GYROOM",CommandFlags.Modal)]
        public void Switch()=>Run(s=>
        {
            var value=s.Document.Editor.GetString("\n房间 ID（shoe_shared/male_first/female_first/male_second/female_second）：");
            if(value.Status!=PromptStatus.OK)throw new Cancelled();
            if(!s.Project.Rooms.Any(r=>r.Id==value.StringResult))throw new ArgumentException("未知房间实例。");
            s.Preview.Clear();s.Edit(_=>s.Project.CurrentRoomId=value.StringResult);
        });
        [CommandMethod("GENGYI","GYNEWROOM",CommandFlags.Modal)]
        public void NewRoom()=>Run(s=>
        {
            string mode=CadGeometry.Keyword(s.Document.Editor,"房间模式","Shoe First Second","Shoe");
            var name=s.Document.Editor.GetString(new PromptStringOptions("\n房间名称："){AllowSpaces=true});
            if(name.Status!=PromptStatus.OK || string.IsNullOrWhiteSpace(name.StringResult))throw new Cancelled();
            var count=s.Document.Editor.GetInteger(new PromptIntegerOptions("\n本房间每班人数："){AllowNegative=false,AllowZero=true,LowerLimit=0,UpperLimit=10000});
            if(count.Status!=PromptStatus.OK)throw new Cancelled();int people=count.Value;
            s.Edit(_=>{var r=new Room{Id="custom_"+Guid.NewGuid().ToString("N"),Name=name.StringResult,Gender="custom",CustomPeople=people,
                Mode=mode=="Shoe"?RoomMode.Shoe:mode=="First"?RoomMode.FirstChange:RoomMode.SecondChange};s.Project.Rooms.Add(r);s.Project.CurrentRoomId=r.Id;});
        });
        private static RectangleEstimate PlanningChoice(Session s,out int selectedIndex)
        {
            var estimate=AreaPlanning.Estimate(s.Project.Population,s.Room);
            if(estimate.Options.Count==0)throw new ArgumentException(estimate.Message);
            var editor=s.Document.Editor;
            editor.WriteMessage("\n"+estimate.Basis+"\n"+estimate.Message);
            for(int i=0;i<estimate.Options.Count;i++)editor.WriteMessage("\n"+(i+1)+". "+estimate.Options[i]);
            var pick=editor.GetInteger(new PromptIntegerOptions("\n选择预排尺寸编号：")
                {DefaultValue=1,UseDefaultValue=true,AllowNegative=false,AllowZero=false,LowerLimit=1,UpperLimit=estimate.Options.Count});
            if(pick.Status!=PromptStatus.OK)throw new Cancelled();
            selectedIndex=pick.Value-1;return estimate.Options[selectedIndex];
        }
        [CommandMethod("GENGYI","GYPLAN",CommandFlags.Modal)]
        public void Plan()=>Run(s=>
        {
            NeedModelSpace(s);var estimate=PlanningChoice(s,out int selectedIndex);var editor=s.Document.Editor;
            double units=CadGeometry.Units(s.Document.Database,editor);
            Boundary reference;bool rotated;
            // Keep the previous preview intact until the user accepts this one.
            using(var jig=new SuggestedRectangleJig(estimate,editor.CurrentUserCoordinateSystem,units,s.Document.Database,s.Room.Options))
            {
                PromptResult result;
                do{result=editor.Drag(jig);}while(result.Status==PromptStatus.Keyword);
                if(result.Status!=PromptStatus.OK)throw new Cancelled();
                reference=jig.Boundary();rotated=jig.Rotated;
            }
            string signature=AreaPlanning.Signature(s.Project.Population,s.Room);
            s.Preview.ShowPlanning(s,estimate,reference,rotated,signature);
            s.Edit(_=>{s.Room.PlanningPreviewSignature=signature;s.Room.PlanningPreviewOption=selectedIndex;s.Room.PreviewVisible=false;});
            editor.WriteMessage("\n已显示人数与叠放策略的临时预排。参考框不是实际房间边界；接下来选择你自己的闭合房间边界，再生成候选。没有设备或参考框写入图纸。");
        });
        [CommandMethod("GENGYI","GYRECT",CommandFlags.Modal)]
        public void Rectangle()=>Run(s=>
        {
            NeedModelSpace(s);
            var editor=s.Document.Editor;
            string method=CadGeometry.Keyword(editor,"选择自己的房间边界 / 手动矩形两角 / 建议尺寸","Select Corners Suggested","Select");
            RectangleEstimate? suggestion=null;
            if(method=="Suggested")
            {
                suggestion=PlanningChoice(s,out _);
            }
            double units=CadGeometry.Units(s.Document.Database,editor);
            Boundary boundary;
            if(suggestion!=null)
            {
                s.Preview.Clear();
                using(var jig=new SuggestedRectangleJig(suggestion,editor.CurrentUserCoordinateSystem,units,s.Document.Database,s.Room.Options))
                {
                    PromptResult result;
                    do{result=editor.Drag(jig);}while(result.Status==PromptStatus.Keyword);
                    if(result.Status!=PromptStatus.OK)throw new Cancelled();
                    boundary=jig.Boundary();
                }
                editor.WriteMessage("\n已采用预估尺寸；这一步只确定范围，接下来生成并校核实际候选。");
            }
            else if(method=="Select")
            {
                var opt=new PromptEntityOptions("\n选择你自己的闭合房间多段线（支持凹形、斜边和弧边）：");opt.SetRejectMessage("\n需要闭合房间多段线。");opt.AddAllowedClass(typeof(Polyline),true);
                var pick=editor.GetEntity(opt);if(pick.Status!=PromptStatus.OK)throw new Cancelled();
                using(var tx=s.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    var pl=(Polyline)tx.GetObject(pick.ObjectId,OpenMode.ForRead);
                    boundary=CadGeometry.ReadBoundary(pl,units,s.Room.Boundary?.Frame??CadGeometry.UcsFrame(editor.CurrentUserCoordinateSystem,units));
                }
            }
            else
            {
                var a=CadGeometry.Point(editor,"矩形第一角点：");var b=CadGeometry.Point(editor,"矩形另一角点（按当前 UCS）：",a);
                var ucs=editor.CurrentUserCoordinateSystem;var ua=a.TransformBy(ucs.Inverse());var ub=b.TransformBy(ucs.Inverse());
                if(Math.Abs(ua.Z-ub.Z)*units>.1)throw new ArgumentException("矩形两个角点不在同一 UCS 平面。");
                var origin=new Point3d(Math.Min(ua.X,ub.X),Math.Min(ua.Y,ub.Y),ua.Z).TransformBy(ucs);
                boundary=new Boundary{Width=Math.Abs(ua.X-ub.X)*units,Height=Math.Abs(ua.Y-ub.Y)*units,
                    Frame=new Frame{Origin=CadGeometry.A(origin),XAxis=CadGeometry.A(ucs.CoordinateSystem3d.Xaxis.GetNormal()),YAxis=CadGeometry.A(ucs.CoordinateSystem3d.Yaxis.GetNormal()),MillimetersPerDrawingUnit=units}};
            }
            if(boundary.Width<1 || boundary.Height<1)throw new ArgumentException("房间边界的两个方向跨度必须至少 1 mm。");
            s.Edit(_=>BoundaryEditing.Replace(s.Room,boundary));s.EnableRoomGraphics();
            if(s.Room.Placed.Count>0)editor.WriteMessage("\n边界已更换，已有设备的位置保持不变；原预览已清除，请重新生成或检查方案。");
            editor.WriteMessage($"\n房间实际面积 {boundary.AreaM2:0.###} ㎡；外接范围 {boundary.Width:0.###} × {boundary.Height:0.###} mm。布置只使用完整房间轮廓，凹口和斜边之外均不可占用。");
            if(boundary.CurveToleranceMm>0)editor.WriteMessage("\n弧边按≤0.05mm弦误差离散，并内缩≥0.05mm作保守布置校核；显示面积来自原多段线。");
        });
        [CommandMethod("GENGYI","GYDOORS",CommandFlags.Modal)]
        public void Doors()=>Run(s=>
        {
            NeedBoundary(s);
            var room=s.Room;
            var editor=s.Document.Editor;
            var boundary=room.Boundary!;
            string action=CadGeometry.Keyword(editor,"本房间出入口：追加 / 替换 / 清空","Append Replace Clear",
                room.Doors.Count>0?"Append":"Replace");
            var doors=action=="Append"?room.Doors.ToList():new List<DoorOpening>();
            if(action!="Clear")
            {
                string next;
                do
                {
                    var a=CadGeometry.Point(editor,"捕捉直墙上的第一个门边点：");
                    var b=CadGeometry.Point(editor,"捕捉同一段墙上的第二个门边点（两点确定实际门宽）：",a);
                    var door=new DoorOpening{Start=CadGeometry.Local(boundary.Frame,a),End=CadGeometry.Local(boundary.Frame,b)};
                    var landing=RoomAccess.Landing(boundary,door,room.Options.WalkWidthMm);
                    if(room.Mode==RoomMode.Shoe)
                        door.Side=CadGeometry.Keyword(editor,"这个出入口位于哪一侧","Dirty Clean","Dirty")=="Dirty"?DoorSide.Dirty:DoorSide.Clean;
                    doors.Add(door);
                    editor.WriteMessage($"\n出入口 {doors.Count}：净宽 {door.Start.Distance(door.End):0.##} mm，室内预留 {landing[1].Distance(landing[2]):0.##} mm。");
                    next=CadGeometry.Keyword(editor,"继续选门 / 完成","More Done","Done");
                } while(next=="More");
            }
            s.Edit(_=>{room.Doors=doors;room.InvalidateCandidates();});
            s.EnableRoomGraphics();
        });
        [CommandMethod("GENGYI","GYPASSAGES",CommandFlags.Modal)]
        public void Passages()=>Run(s=>
        {
            NeedBoundary(s);
            var editor=s.Document.Editor;
            string action=CadGeometry.Keyword(editor,"本房间通行保留区：追加 / 替换 / 清空","Append Replace Clear",
                s.Room.Passages.Count>0?"Append":"Replace");
            var regions=action=="Clear"?new List<Region>():SelectRegions(s,"请选择通行保留区的闭合多段线：");
            if(regions.Any(region=>region.NativeHandle==s.Room.Boundary!.NativeHandle))
                throw new ArgumentException("请选择房间内部的通行轮廓。");
            var boundary=s.Room.Boundary!;
            var actual=Geometry.RoomPolygon(boundary).Buffer(.001+2*boundary.CurveToleranceMm);
            if(regions.Any(region=>!actual.Covers(Geometry.Polygon(region.Points))))
                throw new ArgumentException("通行保留区须位于房间内。");
            if(action=="Append")regions=s.Room.Passages.Concat(regions).GroupBy(r=>r.NativeHandle).Select(g=>g.Last()).ToList();
            s.Edit(_=>{s.Room.Passages=regions;s.Room.InvalidateCandidates();});
            s.EnableRoomGraphics();
        });
        [CommandMethod("GENGYI","GYOBSTACLES",CommandFlags.Modal)]
        public void Obstacles()=>Regions(false);
        [CommandMethod("GENGYI","GYCLOSURES",CommandFlags.Modal)]
        public void Closures()=>Regions(true);
        private static void Regions(bool closure)=>Run(s=>
        {
            NeedBoundary(s);
            var regions=SelectRegions(s,closure?"选择固定封闭物的闭合轮廓（替换本房间封闭物清单）：":"选择柱或其他固定障碍的闭合轮廓（替换本房间障碍清单）：");
            s.Edit(_=>{if(closure)s.Room.Closures=regions;else s.Room.Obstacles=regions;s.Room.InvalidateCandidates();});
            s.Preview.Clear();
        });
        private static List<Region> SelectRegions(Session s,string prompt)
        {
            var options=new PromptSelectionOptions{MessageForAdding="\n"+prompt};
            var selection=s.Document.Editor.GetSelection(options,new SelectionFilter(new[]{new TypedValue((int)DxfCode.Start,"LWPOLYLINE")}));
            if(selection.Status!=PromptStatus.OK)throw new Cancelled();
            var regions=new List<Region>();
            using(var tx=s.Document.Database.TransactionManager.StartOpenCloseTransaction())
                foreach(ObjectId id in selection.Value.GetObjectIds())
                {
                    var pl=(Polyline)tx.GetObject(id,OpenMode.ForRead);
                    var frame=s.Room.Boundary!.Frame;
                    var points=CadGeometry.PolylinePoints(pl,.05/frame.MillimetersPerDrawingUnit).Select(p=>CadGeometry.Local(frame,p)).ToList();
                    Geometry.Polygon(points);regions.Add(new Region{NativeHandle=pl.Handle.ToString(),Points=points,
                        CurveToleranceMm=Enumerable.Range(0,pl.NumberOfVertices).Any(i=>Math.Abs(pl.GetBulgeAt(i))>1e-10)?.05:0});
                }
            return regions;
        }
        [CommandMethod("GENGYI","GYSEEDS",CommandFlags.Modal)]
        public void Seeds()=>Run(s=>
        {
            NeedBoundary(s);var editor=s.Document.Editor;
            var dirty=CadGeometry.Local(s.Room.Boundary!.Frame,CadGeometry.Point(editor,s.Room.Mode==RoomMode.Shoe?"脏侧入口种子（可行走区域内）：":"房间入口种子（可行走区域内）："));
            var clean=s.Room.Mode==RoomMode.Shoe?CadGeometry.Local(s.Room.Boundary.Frame,CadGeometry.Point(editor,"净侧出口种子（可行走区域内）：")):null;
            s.Edit(_=>{s.Room.DirtySeed=dirty;s.Room.CleanSeed=clean;s.Room.InvalidateCandidates();});
        });
        [CommandMethod("GENGYI","GYGENERATE",CommandFlags.Modal)]
        public void Generate()=>GenerateCandidates(false);
        [CommandMethod("GENGYI","GYFILL",CommandFlags.Modal)]
        public void Fill()=>GenerateCandidates(true);
        private static void GenerateCandidates(bool supplement)=>Run(s=>
        {
            NeedBoundary(s);
            if(supplement && s.Room.Mode!=RoomMode.FirstChange)throw new ArgumentException("自动补排仅支持一更房间。");
            var project=Serialization.Copy(s.Project);var room=project.Current;
            SearchResult result;
            using(var dialog=new SearchDialog(project,room,supplement))
            {
                Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(dialog);
                if(dialog.Cancelled)throw new Cancelled();
                result=dialog.Result();
            }
            if(CadApplication.DocumentManager.MdiActiveDocument!=s.Document)throw new Cancelled();
            s.Document.Editor.WriteMessage("\n"+result.Message);
            if(supplement && result.Candidates.Count==0)return;
            s.Edit(_=>{s.Room.Candidates=result.Candidates;s.Room.SelectedCandidate=0;s.Room.PreviewVisible=result.Candidates.Count>0;s.Room.Stale=false;});
            s.EnableRoomGraphics();
        });
        [CommandMethod("GENGYI","GYTRACK",CommandFlags.Modal)]
        public void Track()=>Run(s=>
        {
            NeedBoundary(s);
            if(s.Room.Mode==RoomMode.SecondChange)throw new ArgumentException("轨迹布置支持换鞋间和一更。");
            if(s.Room.ConstraintIssue.Length>0)throw new ArgumentException(s.Room.ConstraintIssue);
            if(Host.Panel==null){Host.Panel=new Palette();Host.Panel.Show(s);}
            var editor=s.Document.Editor;
            var frame=s.Room.Boundary!.Frame;
            var start=CadGeometry.Local(frame,CadGeometry.Point(editor,
                s.Room.Mode==RoomMode.Shoe?"指定鞋柜中心线起点：":"指定衣柜柜背线起点，默认布在行进方向左侧："));
            s.Preview.Show(s,Array.Empty<Placement>());
            using(var jig=new TrackJig(s,start))
            {
                Candidate candidate;
                while(true)
                {
                    var p=editor.Drag(jig);
                    if(p.Status==PromptStatus.Keyword){jig.ApplyKeyword(p.StringResult);continue;}
                    if(jig.Finished || p.Status==PromptStatus.None)
                    {
                        try{candidate=jig.Complete();break;}
                        catch(ArgumentException ex){editor.WriteMessage("\n"+ex.Message+" 可继续画或用 Undo 撤回上一点。");continue;}
                    }
                    if(p.Status!=PromptStatus.OK)throw new Cancelled();
                    if(!jig.AcceptPoint())editor.WriteMessage("\n请选择能放下完整柜体的下一点，或回车结束。");
                }
                s.Edit(_=>
                {
                    s.Room.Candidates.Add(candidate);s.Room.SelectedCandidate=s.Room.Candidates.Count-1;
                    s.Room.PreviewVisible=true;s.Room.Stale=false;
                });
                s.EnableRoomGraphics();
                editor.WriteMessage("\n轨迹已成为可预览候选。检查后用确认入图追加到当前房间。");
            }
        });
        [CommandMethod("GENGYI","GYPREVIEW",CommandFlags.Modal)]
        public void ShowPreview()=>Run(s=>
        {
            if(s.Room.Candidates.Count==0)throw new ArgumentException("请先生成当前房间的候选。");
            s.Room.SelectedCandidate=Math.Max(0,Math.Min(s.Room.Candidates.Count-1,s.Room.SelectedCandidate));
            var candidate=s.Room.Candidates[s.Room.SelectedCandidate];
            foreach(var p in candidate.Items)Layout.ResolveMapping(s.Project,p);
            candidate.Statistics=Validation.Evaluate(s.Project,s.Room,candidate.Scene(s.Room));s.Room.PreviewVisible=true;
            s.EnableRoomGraphics();
        });
        [CommandMethod("GENGYI","GYCANCEL",CommandFlags.Modal)]
        public void CancelPreview()
        {
            var session=Host.Active;
            try
            {
                session.HideRoomGraphics();
                session.Document.Editor.WriteMessage("\n已关闭柜体预览、出入口、通行区和检查标记。用 GYPREVIEW 可重新显示候选。");
                Host.Panel?.Update(session);
            }
            catch(System.Exception ex){session.Document.Editor.WriteMessage("\n临时图形清理失败："+ex.Message);}
        }
        [CommandMethod("GENGYI","GYCOMMIT",CommandFlags.Modal)]
        public void Commit()=>Run(s=>
        {
            NeedBoundary(s);
            if(s.Room.Candidates.Count==0)throw new ArgumentException("当前房间没有候选。");
            var chosen=Serialization.Copy(s.Room.Candidates[s.Room.SelectedCandidate]);
            if(chosen.Items.Count==0 && s.Project.Population.For(s.Room)>0)
                throw new ArgumentException("当前方案没有可放设备，请调整房间范围或通行区后重新生成。");
            ShoeStacking.RequireValid(chosen.Items);
            foreach(var p in chosen.Items){Layout.ResolveMapping(s.Project,p);Blocks.Mapping(s,p);}
            LockerAisles.RequireValid(s.Room,chosen.Items);
            string action=chosen.AppendToPlaced?"Append":s.Room.Placed.Count==0?"Replace":
                CadGeometry.Keyword(s.Document.Editor,"当前房间已有设备","Replace Append Cancel","Replace");
            if(action=="Replace" && s.Room.Placed.Any(p=>p.Locked))throw new ArgumentException("当前房间含锁定设备，请先解锁后替换方案。");
            if(action=="Cancel")throw new Cancelled();
            var finalItems=chosen.Items;
            if(action=="Append")
            {
                finalItems=s.Room.Placed.Concat(chosen.Items).ToList();
                ShoeStacking.RequireValid(finalItems);LockerAisles.RequireValid(s.Room,finalItems);
            }
            chosen.Statistics=Validation.Evaluate(s.Project,s.Room,finalItems);
            if(s.Room.Mode==RoomMode.Shoe && finalItems.Count>0 &&
                (!Layout.IsShoeLayout(finalItems) || chosen.Statistics.Checks["connections"]==CheckState.Fail))
                throw new ArgumentException("换鞋间入图须使用完整相接的 S 形鞋柜及交替的 R650 转角。请重新生成或恢复柜链。");
            RoomAccess.RequireValid(chosen.Statistics);
            if(chosen.Statistics.Checks["geometry"]==CheckState.Fail || chosen.Statistics.Checks["boundary"]==CheckState.Fail)
                throw new ArgumentException("存在实体碰撞、越界或边界净距问题，请调整后再入图。");
            if(action=="Append" && s.Room.Mode==RoomMode.FirstChange && !LockerAisles.PreservesCapacity(s.Room.Statistics,chosen.Statistics))
                throw new ArgumentException("追加后已有衣柜的可用人位减少，请重新补排或调整柜位。");
            int deficit=chosen.Statistics.Effective.HasValue?Math.Max(0,chosen.Statistics.Target-chosen.Statistics.Effective.Value):chosen.Statistics.ConditionalDeficit;
            if((chosen.Statistics.Effective==null || deficit>0) && CadGeometry.Keyword(s.Document.Editor,
                deficit>0?$"当前方案仍缺 {deficit} 人位，是否按容量不足的待核草案入图":"当前方案尚有待核项，是否作为待核草案入图","Yes No","No")!="Yes")throw new Cancelled();
            s.Edit(tx=>
            {
                if(action=="Replace")
                {
                    foreach(var old in s.Room.Placed)Erase(s,old.NativeHandle,tx);
                    foreach(string handle in s.Room.AnnotationHandles)Erase(s,handle,tx);
                    s.Room.Placed.Clear();s.Room.AnnotationHandles.Clear();
                }
                var groups=new Dictionary<string,string>();
                foreach(var p in chosen.Items)
                {
                    p.Id=Guid.NewGuid().ToString("N");p.RoomId=s.Room.Id;
                    if(p.GroupId.Length>0){if(!groups.ContainsKey(p.GroupId))groups[p.GroupId]=Guid.NewGuid().ToString("N");p.GroupId=groups[p.GroupId];}
                    Blocks.Create(s.Document.Database,tx,s.Room,p,Blocks.Mapping(s,p),true);
                    s.Room.Placed.Add(p);
                }
                if(s.Room.AnnotationHandles.Count==0)
                {
                    var table=(BlockTable)tx.GetObject(s.Document.Database.BlockTableId,OpenMode.ForRead);
                    var model=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                    var title=new DBText{TextString=s.Room.Name,Height=200,Position=new Point3d(0,-350,0),LayerId=Blocks.Layer(s.Document.Database,tx,"GY_TEXT",7)};
                    title.TransformBy(CadGeometry.ToWorld(s.Room.Boundary!.Frame));model.AppendEntity(title);tx.AddNewlyCreatedDBObject(title,true);
                    s.Room.AnnotationHandles.Add(title.Handle.ToString());
                }
                if(chosen.AppendToPlaced)
                {
                    if(s.Room.Mode==RoomMode.FirstChange)s.Room.Candidates.RemoveAll(candidate=>candidate.AppendToPlaced);
                    else s.Room.Candidates.RemoveAt(s.Room.SelectedCandidate);
                    s.Room.SelectedCandidate=Math.Max(0,Math.Min(s.Room.SelectedCandidate-1,s.Room.Candidates.Count-1));
                }
                s.Room.PreviewVisible=false;
            });
            s.Document.Editor.WriteMessage("\n当前房间已用原生块参照入图。一次 UNDO 可撤销本次命令；校核状态仍以实际检查为准。");
        },hideGraphicsAfter:true);
        [CommandMethod("GENGYI","GYREBUILD",CommandFlags.Modal)]
        public void Reconstruct()=>Run(s=>
        {
            if(s.Room.Candidates.Count==0)throw new ArgumentException("请先生成候选，再对其中的设备明确启用图片重建模式。");
            if(CadGeometry.Keyword(s.Document.Editor,"重建块会标为 image_reconstructed，需按原图比对，不是用户原 CAD 块。继续","Yes No","No")!="Yes")throw new Cancelled();
            s.Edit(tx=>
            {
                Blocks.Reconstruct(s,tx,s.Room.Candidates[s.Room.SelectedCandidate].Items);
                foreach(var c in s.Room.Candidates)foreach(var p in c.Items)Layout.ResolveMapping(s.Project,p);
            });
            s.Room.PreviewVisible=true;
            s.EnableRoomGraphics();
        });
        [CommandMethod("GENGYI","GYDRAG",CommandFlags.Modal)]
        public void Drag()=>Run(s=>
        {
            NeedBoundary(s);bool preview=HasPreview(s);var scene=preview?s.Room.Candidates[s.Room.SelectedCandidate].Items:s.Room.Placed;
            var picked=preview?PickPreview(s):Pick(s);bool group=CadGeometry.Keyword(s.Document.Editor,"移动范围","Single Group","Single")=="Group";
            var items=group && picked.GroupId.Length>0?scene.Where(p=>p.GroupId==picked.GroupId).ToList():new List<Placement>{picked};
            if(items.Any(p=>p.Locked))throw new ArgumentException("设备/柜组已锁定。");
            var anchor=CadGeometry.Local(s.Room.Boundary!.Frame,CadGeometry.Point(s.Document.Editor,"拖动基点："));
            s.Preview.Clear();
            using(var jig=new MoveJig(s,items,anchor,preview?s.Room.Candidates[s.Room.SelectedCandidate].Scene(s.Room):scene))
            {
                var status=s.Document.Editor.Drag(jig);if(status.Status!=PromptStatus.OK)throw new Cancelled();
                var copies=items.Select(Serialization.Copy).ToList();foreach(var p in copies)p.Position+=jig.Delta;
                if(preview)SavePreviewEdits(s,copies);else SaveEdits(s,copies);
            }
        });
        [CommandMethod("GENGYI","GYROTATE",CommandFlags.Modal)]
        public void Rotate()=>Run(s=>
        {
            bool preview=HasPreview(s);var picked=preview?PickPreview(s):Pick(s);if(picked.Locked)throw new ArgumentException("设备已锁定。");
            var copy=Serialization.Copy(picked);copy.Rotation=Geometry.Normalize(copy.Rotation+90);
            if(preview)SavePreviewEdits(s,new List<Placement>{copy});else SaveEdits(s,new List<Placement>{copy});
        });
        [CommandMethod("GENGYI","GYTYPE",CommandFlags.Modal)]
        public void Type()=>Run(s=>
        {
            bool preview=HasPreview(s);var original=preview?PickPreview(s):Pick(s);if(original.Locked)throw new ArgumentException("设备已锁定。");
            if(!original.Equipment.IsShoe)throw new ArgumentException("换型号/叠放命令用于鞋柜；挂架按实际挂点与独立长度块编辑。");
            string id=CadGeometry.Keyword(s.Document.Editor,"鞋柜型号","S12 S18 S24 S48",original.EquipmentId);
            var next=Serialization.Copy(original);next.EquipmentId=id;next.CrossingEnabled=id!="S48";next.PhysicalOutline=null;next.ScaleX=1;next.ScaleY=1;next.NativeGeometryUnverified=false;next.NativeIssue="";
            Layout.ResolveMapping(s.Project,next);
            if(preview){SavePreviewEdits(s,new List<Placement>{next});return;}
            var proposed=s.Room.Placed.Select(p=>p.Id==next.Id?next:p).ToList();
            ShoeStacking.RejectNewViolations(s.Room.Placed,proposed);
            CheckAccessEdit(s,s.Room.Placed,proposed);
            s.Edit(tx=>
            {
                if(next.MappingKey.Length==0 && original.Provenance=="image_reconstructed"){Blocks.Reconstruct(s,tx,new[]{next});Layout.ResolveMapping(s.Project,next);}
                var mapping=Blocks.Mapping(s,next);Erase(s,original.NativeHandle,tx);Blocks.Create(s.Document.Database,tx,s.Room,next,mapping,true);
            });
        });
        [CommandMethod("GENGYI","GYLOCK",CommandFlags.Modal)]
        public void Lock()=>Run(s=>
        {
            bool preview=HasPreview(s);var item=preview?PickPreview(s):Pick(s);var scene=preview?s.Room.Candidates[s.Room.SelectedCandidate].Items:s.Room.Placed;
            bool group=CadGeometry.Keyword(s.Document.Editor,"锁定范围（插件编辑与替换）","Single Group","Single")=="Group";
            var items=group && item.GroupId.Length>0?scene.Where(p=>p.GroupId==item.GroupId).ToList():new List<Placement>{item};
            bool locked=!item.Locked;
            s.Edit(tx=>{foreach(var p in items){p.Locked=locked;if(!preview)Store.Instance(Entity(s,p.NativeHandle,tx),p,tx);}});
        });
        [CommandMethod("GENGYI","GYGROUP",CommandFlags.Modal)]public void Group()=>Run(s=>
        {
            NeedModelSpace(s);
            if(HasPreview(s))throw new ArgumentException("候选的原连接组已保存；请先结束预览，再确认已入图或原生复制设备的编组。");
            string action=CadGeometry.Keyword(s.Document.Editor,"插件连接组（不改变设备几何）","Group Ungroup","Group");
            var pick=s.Document.Editor.GetSelection(new PromptSelectionOptions{MessageForAdding="\n选择当前房间需要编组/解除编组的设备块："},new SelectionFilter(new[]{new TypedValue((int)DxfCode.Start,"INSERT")}));
            if(pick.Status!=PromptStatus.OK)throw new Cancelled();
            var handles=new HashSet<string>(pick.Value.GetObjectIds().Select(id=>id.Handle.ToString()));
            var items=s.Room.Placed.Where(p=>handles.Contains(p.NativeHandle)).ToList();
            if(items.Count!=handles.Count || items.Any(p=>p.Locked))throw new ArgumentException("选择必须全部是当前房间内未锁定的插件设备。");
            string group=action=="Group"?Guid.NewGuid().ToString("N"):"";
            s.Edit(tx=>{foreach(var p in items){p.GroupId=group;p.NativeCopyDetached=false;Store.Instance(Entity(s,p.NativeHandle,tx),p,tx);}});
        });
        [CommandMethod("GENGYI","GYDELETE",CommandFlags.Modal)]
        public void Delete()=>Run(s=>
        {
            bool preview=HasPreview(s);var p=preview?PickPreview(s):Pick(s);if(p.Locked)throw new ArgumentException("设备已锁定。");
            var scene=preview?s.Room.Candidates[s.Room.SelectedCandidate].Items:s.Room.Placed;
            ShoeStacking.RejectNewViolations(scene,scene.Where(i=>i.Id!=p.Id).ToList());
            s.Edit(tx=>{if(preview){var candidate=s.Room.Candidates[s.Room.SelectedCandidate];candidate.Items.RemoveAll(i=>i.Id==p.Id);candidate.Statistics=Validation.Evaluate(s.Project,s.Room,candidate.Scene(s.Room));}else Erase(s,p.NativeHandle,tx);});
        });
        [CommandMethod("GENGYI","GYLENGTH",CommandFlags.Modal)]
        public void Length()=>Run(s=>
        {
            bool preview=HasPreview(s);var old=preview?PickPreview(s):Pick(s);
            if(old.Locked || old.EquipmentId!="HANGER_CUSTOM")throw new ArgumentException("请选择未锁定的工装挂架。");
            var next=Serialization.Copy(old);next.Length=CadGeometry.Number(s.Document.Editor,"新挂架实际长度 mm：",old.Length);
            next.LocalHooks=Capacity.Hooks(new Placement{EquipmentId="HANGER_CUSTOM",Length=next.Length},s.Room.Options);
            Layout.ResolveMapping(s.Project,next);
            if(preview){SavePreviewEdits(s,new List<Placement>{next});return;}
            s.Edit(tx=>
            {
                if(next.MappingKey.Length==0 && old.Provenance=="image_reconstructed"){Blocks.Reconstruct(s,tx,new[]{next});Layout.ResolveMapping(s.Project,next);}
                var mapping=Blocks.Mapping(s,next);Erase(s,old.NativeHandle,tx);Blocks.Create(s.Document.Database,tx,s.Room,next,mapping,true);
            });
        });
        [CommandMethod("GENGYI","GYCROSS",CommandFlags.Modal)]
        public void Crossing()=>Run(s=>
        {
            bool preview=HasPreview(s);var old=preview?PickPreview(s):Pick(s);
            if(old.Locked || !old.Equipment.IsShoe || old.Equipment.Boxes!=1)throw new ArgumentException("只能为未锁定的单层鞋柜指定跨柜点；叠柜仍可提供存储。");
            var next=Serialization.Copy(old);next.CrossingEnabled=!next.CrossingEnabled;
            if(preview)SavePreviewEdits(s,new List<Placement>{next});
            else s.Edit(tx=>Store.Instance(Entity(s,old.NativeHandle,tx),next,tx));
        });
        [CommandMethod("GENGYI","GYTRANSFER",CommandFlags.Modal)]
        public void Transfer()=>Run(s=>
        {
            NeedBoundary(s);var option=new PromptEntityOptions("\n选择要转移到当前房间的 GENGYI 块：");option.SetRejectMessage("\n请选择块。");option.AddAllowedClass(typeof(BlockReference),true);
            var pick=s.Document.Editor.GetEntity(option);if(pick.Status!=PromptStatus.OK)throw new Cancelled();
            s.Edit(tx=>
            {
                var br=(BlockReference)tx.GetObject(pick.ObjectId,OpenMode.ForWrite);
                var item=Serialization.Read<Placement>(Store.Instance(br,tx)??throw new ArgumentException("此块不受插件管理。"));
                if(item.Locked)throw new ArgumentException("设备已锁定。");
                if(!s.Project.Mappings.TryGetValue(item.MappingKey,out var map))throw new ArgumentException("缺少块标定。");
                bool valid=s.Room.Mode==RoomMode.Shoe?item.Equipment.IsShoe||item.EquipmentId==Equipment.CornerId:s.Room.Mode==RoomMode.FirstChange?item.EquipmentId=="LOCKER12":item.EquipmentId=="HANGER_CUSTOM";
                if(!valid)throw new ArgumentException("该设备与当前房间模式不匹配。");
                var source=s.Project.Rooms.FirstOrDefault(r=>r.Id==item.RoomId);
                if(source?.Id==s.Room.Id)throw new ArgumentException("设备已属于当前房间。");
                if(source!=null)ShoeStacking.RejectNewViolations(source.Placed,source.Placed.Where(p=>p.NativeHandle!=br.Handle.ToString()).ToList());
                item.RoomId=s.Room.Id;CadGeometry.ReadTransform(s.Room,item,br,map);
                ShoeStacking.RejectNewViolations(s.Room.Placed,s.Room.Placed.Concat(new[]{item}).ToList());
                LockerAisles.RejectNewViolations(s.Room,s.Room.Placed,s.Room.Placed.Concat(new[]{item}).ToList());
                CheckAccessEdit(s,s.Room.Placed,s.Room.Placed.Concat(new[]{item}).ToList());
                Store.Instance(br,item,tx);
            });
        });
        [CommandMethod("GENGYI","GYCHECK",CommandFlags.Modal)]public void Check()=>Run(s=>
        {
            NeedBoundary(s);
            var scene=HasPreview(s)?s.Room.Candidates[s.Room.SelectedCandidate].Scene(s.Room):s.Room.Placed;
            var stats=Validation.Evaluate(s.Project,s.Room,scene);s.Report(true,stats);
            s.WitnessPath=null;
            if(s.Room.Mode==RoomMode.Shoe && stats.Checks["walking_without_crossing"]==CheckState.Fail)
            {
                s.EnableRoomGraphics();s.WitnessPath=BypassPath.Find(s.Room,scene);s.WitnessRoom=s.Room.Id;
                s.Document.Editor.WriteMessage(s.WitnessPath.Count>1?"\n红色临时线为满足当前人员宽度的绕行路径。":"\n连续拓扑已确认可绕行；显示用采样在预算内未找到路径，不能因此判通过。");
            }
        });
        [CommandMethod("GENGYI","GYSTATS",CommandFlags.Modal)]public void Stats()=>Run(s=>
        {foreach(var r in s.Project.Rooms)s.Document.Editor.WriteMessage("\n"+r.Name+" ["+r.Mode+"] "+r.Statistics.Summary);s.Report(true);});
        [CommandMethod("GENGYI","GYLOCATE",CommandFlags.Modal|CommandFlags.UsePickSet)]public void Locate()=>Run(s=>
        {
            NeedBoundary(s);var editor=s.Document.Editor;
            var value=editor.GetString("\n输入异常实例ID/前缀、柜组ID或原生句柄：");if(value.Status!=PromptStatus.OK)throw new Cancelled();
            string query=value.StringResult.Trim();if(query.Length==0)throw new Cancelled();
            var scene=HasPreview(s)?s.Room.Candidates[s.Room.SelectedCandidate].Scene(s.Room):s.Room.Placed;
            var matches=scene.Where(p=>p.Id.StartsWith(query,StringComparison.OrdinalIgnoreCase) || p.GroupId==query || p.NativeHandle==query).ToList();
            if(matches.Count==0)throw new ArgumentException("当前房间没有匹配的设备/柜组。");
            using(var view=editor.GetCurrentView())
            {
                var transform=Matrix3d.Rotation(-view.ViewTwist,view.ViewDirection,view.Target)*Matrix3d.Displacement(view.Target-Point3d.Origin)*Matrix3d.PlaneToWorld(view.ViewDirection);
                var points=matches.SelectMany(p=>Geometry.Outline(p)).Select(p=>CadGeometry.World(s.Room.Boundary!.Frame,p).TransformBy(transform.Inverse())).ToList();
                double w=points.Max(p=>p.X)-points.Min(p=>p.X),h=points.Max(p=>p.Y)-points.Min(p=>p.Y),ratio=view.Width/view.Height;
                view.Height=Math.Max(Math.Max(h,w/ratio)*1.5,500/s.Room.Boundary!.Frame.MillimetersPerDrawingUnit);view.Width=view.Height*ratio;
                view.CenterPoint=new Point2d((points.Min(p=>p.X)+points.Max(p=>p.X))/2,(points.Min(p=>p.Y)+points.Max(p=>p.Y))/2);editor.SetCurrentView(view);
            }
            var ids=matches.Where(p=>p.NativeHandle.Length>0).Select(p=>s.Document.Database.GetObjectId(false,new Handle(Convert.ToInt64(p.NativeHandle,16)),0)).ToArray();
            if(ids.Length>0)editor.SetImpliedSelection(ids);
        });
        [CommandMethod("GENGYI","GYEXPORT",CommandFlags.Modal)]public void Export()=>Run(s=>
        {
            var answer=s.Document.Editor.GetFileNameForSave(new PromptSaveFileOptions("\n导出当前图纸项目数据到本地 JSON："){Filter="JSON (*.json)|*.json"});
            if(answer.Status!=PromptStatus.OK)throw new Cancelled();File.WriteAllText(answer.StringResult,LayoutExport.Write(s.Project,s.Room),new System.Text.UTF8Encoding(false));
        });
        [CommandMethod("GENGYI","GYESTIMATE",CommandFlags.Modal)]public void Estimate()=>Run(s=>
        {
            var estimate=AreaPlanning.Estimate(s.Project.Population,s.Room);
            s.Document.Editor.WriteMessage("\n"+s.Room.Name+"："+estimate.Basis+"\n"+estimate.Message);
            foreach(var option in estimate.Options)s.Document.Editor.WriteMessage("\n"+option+$"；名义{option.Nominal}人，设备投影{option.ProjectionM2:0.##}㎡");
            s.Document.Editor.WriteMessage("\n"+estimate.Assumptions);
            if(Host.Panel==null)Host.Panel=new Palette();Host.Panel.Show(s);
        });
        internal static void NeedModelSpace(Session s)
        {
            using(var tx=s.Document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var table=(BlockTable)tx.GetObject(s.Document.Database.BlockTableId,OpenMode.ForRead);
                if(s.Document.Database.CurrentSpaceId!=table[BlockTableRecord.ModelSpace])throw new ArgumentException("请切换到模型空间或进入模型视口后选择/编辑设备，避免把纸空间坐标写入模型。");
            }
        }
        internal static void NeedBoundary(Session s){NeedModelSpace(s);if(s.Room.Boundary==null)throw new ArgumentException("请先为当前房间选择闭合边界。");}
        internal static bool HasPreview(Session s)=>s.Room.PreviewVisible && s.Room.Candidates.Count>0;
        private static Placement PickPreview(Session s)
        {
            var point=CadGeometry.Local(s.Room.Boundary!.Frame,CadGeometry.Point(s.Document.Editor,"点选候选预览中的设备："));
            var at=Geometry.Factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(point.X,point.Y));
            return s.Room.Candidates[s.Room.SelectedCandidate].Items.OrderBy(p=>Geometry.Footprint(p).Distance(at)).FirstOrDefault(p=>Geometry.Footprint(p).Distance(at)<200)
                ??throw new ArgumentException("此点没有候选设备；请在设备轮廓内点选。");
        }
        private static void SavePreviewEdits(Session s,List<Placement> copies)
        {
            var selected=s.Room.Candidates[s.Room.SelectedCandidate];
            var scene=selected.Scene(s.Room);
            var proposed=scene.Select(p=>copies.FirstOrDefault(q=>q.Id==p.Id)??p).ToList();
            ShoeStacking.RejectNewViolations(scene,proposed);
            LockerAisles.RejectNewViolations(s.Room,scene,proposed);
            CheckAccessEdit(s,scene,proposed);
            s.Edit(_=>
            {
                var candidate=s.Room.Candidates[s.Room.SelectedCandidate];
                candidate.Items=candidate.Items.Select(p=>copies.FirstOrDefault(q=>q.Id==p.Id)??p).ToList();
                candidate.Statistics=Validation.Evaluate(s.Project,s.Room,candidate.Scene(s.Room));
            });
        }
        internal static Placement Pick(Session s)
        {
            NeedModelSpace(s);
            var opt=new PromptEntityOptions("\n选择当前房间的 GENGYI 设备块：");opt.SetRejectMessage("\n请选择块参照。");opt.AddAllowedClass(typeof(BlockReference),true);
            var result=s.Document.Editor.GetEntity(opt);if(result.Status!=PromptStatus.OK)throw new Cancelled();
            string handle=result.ObjectId.Handle.ToString();
            return s.Room.Placed.FirstOrDefault(p=>p.NativeHandle==handle)??throw new ArgumentException("所选设备不属于当前房间。切换房间或使用 GYTRANSFER。");
        }
        internal static Entity Entity(Session s,string handle,Transaction tx) => (Entity)tx.GetObject(s.Document.Database.GetObjectId(false,new Handle(Convert.ToInt64(handle,16)),0),OpenMode.ForWrite);
        private static void Erase(Session s,string handle,Transaction tx)
        {if(handle.Length>0){var entity=Entity(s,handle,tx);if(!entity.IsErased)entity.Erase();}}
        internal static void CheckAccessEdit(Session s,IReadOnlyList<Placement> before,IReadOnlyList<Placement> after)
        {
            if(s.Room.Doors.Count==0 && s.Room.Passages.Count==0)return;
            RoomAccess.RejectNewViolations(s.Room,before,after);
            var old=Validation.Evaluate(s.Project,s.Room,before);
            var current=Validation.Evaluate(s.Project,s.Room,after);
            bool WalkFailure(Diagnostic issue)=>issue.Code=="DOOR_ACCESS_FAILED" || issue.Code=="DOOR_FRONT_UNREACHABLE";
            var oldFailures=new HashSet<(string,string)>(old.Issues.Where(WalkFailure).Select(i=>(i.InstanceId,i.Message)));
            var failure=current.Issues.FirstOrDefault(i=>WalkFailure(i) && !oldFailures.Contains((i.InstanceId,i.Message)));
            if(failure!=null)throw new ArgumentException(failure.Message);
        }
        private static void SaveEdits(Session s,List<Placement> copies)
        {
            var proposed=s.Room.Placed.Select(p=>copies.FirstOrDefault(q=>q.Id==p.Id)??p).ToList();
            ShoeStacking.RejectNewViolations(s.Room.Placed,proposed);
            LockerAisles.RejectNewViolations(s.Room,s.Room.Placed,proposed);
            CheckAccessEdit(s,s.Room.Placed,proposed);
            var stats=Validation.Evaluate(s.Project,s.Room,proposed);
            if(stats.Checks["geometry"]==CheckState.Fail || stats.Checks["boundary"]==CheckState.Fail)
                if(CadGeometry.Keyword(s.Document.Editor,"最终位置未通过几何/净距检查，保留为明显未通过草案","Keep Cancel","Cancel")!="Keep")throw new Cancelled();
            s.Edit(tx=>
            {
                foreach(var copy in copies)
                {
                    var old=s.Room.Placed.Single(p=>p.Id==copy.Id);
                    var entity=(BlockReference)Entity(s,copy.NativeHandle,tx);
                    var map=Blocks.Mapping(s,copy);
                    var before=CadGeometry.Transform(s.Room,old,map);var after=CadGeometry.Transform(s.Room,copy,map);
                    entity.TransformBy(after*before.Inverse());Store.Instance(entity,copy,tx);
                }
            });
        }
    }
}
