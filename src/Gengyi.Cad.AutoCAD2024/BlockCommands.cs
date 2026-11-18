using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Gengyi.Core;
using Geometry=Gengyi.Core.Geometry;
using Layout=Gengyi.Core.Layout;

namespace Gengyi.Cad.AutoCAD2024
{
    public sealed class BlockCommands
    {
        private static string EquipmentChoice(Session s,string message="绑定设备型号")=>CadGeometry.Keyword(s.Document.Editor,message,
            "S12 S18 S24 S48 "+Equipment.CornerId+" LOCKER12 HANGER_CUSTOM CUP100 CHANGING_MIRROR UV_DEVICE SHOE_ARCHIVE SHOE_DESK SHOE_WATERHEATER",
            s.Room.Mode==RoomMode.Shoe?"S24":s.Room.Mode==RoomMode.FirstChange?"LOCKER12":"HANGER_CUSTOM");

        [CommandMethod("GENGYI","GYBLOCK",CommandFlags.Modal)]
        public void Bind()=>Commands.Run(s=>
        {
            var editor=s.Document.Editor;string equipment=EquipmentChoice(s);
            double length=equipment=="HANGER_CUSTOM"?CadGeometry.Number(editor,"该挂架实际总长 mm：",1000):0;
            var e=Equipment.Get(equipment,length);
            double calibrationLength=e.Length>0?e.Length:CadGeometry.Number(editor,"沿设备长轴的实际长度 mm（来自现场/产品图）：",1000);
            var options=new PromptEntityOptions("\n选择用户原始设备块实例（保留动态属性和嵌套）：");options.SetRejectMessage("\n请选择块参照。");options.AddAllowedClass(typeof(BlockReference),true);
            var pick=editor.GetEntity(options);if(pick.Status!=PromptStatus.OK)throw new Cancelled();
            Point3d a=CadGeometry.Point(editor,"点选设备物理原点（标注不参与占地）：");
            Point3d b=CadGeometry.Point(editor,"沿设备长轴点选另一端，已知长度 "+calibrationLength+" mm：",a);
            double worldMillimeters=s.Document.Database.Insunits!=UnitsValue.Undefined?
                UnitsConverter.GetConversionFactor(s.Document.Database.Insunits,UnitsValue.Millimeters):
                s.Room.Boundary?.Frame.MillimetersPerDrawingUnit??CadGeometry.Units(s.Document.Database,editor);
            if(Math.Abs((b-a).Length*worldMillimeters-calibrationLength)>.5)
                throw new ArgumentException("所选原块的实际长度 "+((b-a).Length*worldMillimeters).ToString("0.###")+"mm，与当前型号 "+calibrationLength+"mm 不一致；不会通过重新声明尺寸或缩放将旧块冒充新型号。");
            Point3d? depthPoint=e.Depth>0 && equipment!="HANGER_CUSTOM"?CadGeometry.Point(editor,
                e.IsCorner?"沿另一径向点选转角实际深度端点，须为650mm：":e.IsShoe?
                "点选鞋柜深度另一端（含50mm通风道，总深650mm）：":"点选设备物理深度另一侧，须为"+e.Depth+"mm：",a):(Point3d?)null;
            BlockMapping mapping;
            Matrix3d sourceToWorld;
            using(var tx=s.Document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var reference=(BlockReference)tx.GetObject(pick.ObjectId,OpenMode.ForRead);
                var bx=Vector3d.XAxis.TransformBy(reference.BlockTransform);var by=Vector3d.YAxis.TransformBy(reference.BlockTransform);var bz=Vector3d.ZAxis.TransformBy(reference.BlockTransform);
                if(bx.CrossProduct(by).DotProduct(bz)<0 || Math.Abs(bx.Length-by.Length)>1e-7*Math.Max(bx.Length,by.Length))
                    throw new ArgumentException("源实例经过镜像或非等比缩放，请选择未变形的原块实例；默认不静默继承镜像。");
                var anchor=a.TransformBy(reference.BlockTransform.Inverse());var end=b.TransformBy(reference.BlockTransform.Inverse());
                var axis=end-anchor;
                if(axis.Length<1e-8 || Math.Abs(axis.Z)>1e-7 || Math.Abs(anchor.Z)>1e-6)throw new ArgumentException("长轴标定点须位于设备块的 XY 平面内，且不能重合。");
                var definition=(BlockTableRecord)tx.GetObject(reference.IsDynamicBlock?reference.DynamicBlockTableRecord:reference.BlockTableRecord,OpenMode.ForRead);
                if(definition.IsFromExternalReference)throw new ArgumentException("请先使用本地真实块定义；不能将外部参照直接冒充独立设备块。");
                if(e.IsCorner && definition.Name.StartsWith("GY_IMAGE_"+Equipment.LegacyCornerId,StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("所选块是旧R600重建定义，不能重新声明为R650；请选实际R650原块或明确重建新转角。");
                mapping=new BlockMapping{EquipmentId=equipment,EquipmentDepthMm=e.Depth,Length=length,BlockName=definition.Name,SourceBlockName=definition.Name,
                    SourceAnchor=new V2(anchor.X,anchor.Y),SourceAngleDegrees=Math.Atan2(axis.Y,axis.X)*180/Math.PI,
                    MillimetersPerSourceUnit=calibrationLength/axis.Length,SourceHash=Blocks.CaptureHash(s.Document.Database,definition.ObjectId),
                    Provenance=definition.Name.StartsWith("GY_IMAGE_",StringComparison.Ordinal)?"image_reconstructed":"original_cad"};
                if(reference.IsDynamicBlock)
                    foreach(DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
                        if(!property.ReadOnly)mapping.DynamicValues[property.PropertyName]=property.Value;
                foreach(ObjectId id in reference.AttributeCollection)
                    if(tx.GetObject(id,OpenMode.ForRead) is AttributeReference attribute)mapping.Attributes[attribute.Tag]=attribute.TextString;
                sourceToWorld=reference.BlockTransform;
                var sourceLayer=(LayerTableRecord)tx.GetObject(reference.LayerId,OpenMode.ForRead);
                var color=reference.Color.ColorMethod==Autodesk.AutoCAD.Colors.ColorMethod.ByLayer?sourceLayer.Color:reference.Color;
                if(color.ColorMethod==Autodesk.AutoCAD.Colors.ColorMethod.ByColor)mapping.SourceRgb=new[]{(int)color.Red,(int)color.Green,(int)color.Blue};
                else mapping.SourceColorIndex=color.ColorIndex;
                mapping.SourceLineWeight=(int)(reference.LineWeight==LineWeight.ByLayer?sourceLayer.LineWeight:reference.LineWeight);
                mapping.SourceLinetype=((LinetypeTableRecord)tx.GetObject(string.Equals(reference.Linetype,"ByLayer",StringComparison.OrdinalIgnoreCase)?sourceLayer.LinetypeObjectId:reference.LinetypeId,OpenMode.ForRead)).Name;
                mapping.SourceEvidence="selected_block_local_snapshot";
                if(depthPoint.HasValue)
                {
                    var measured=depthPoint.Value.TransformBy(CadGeometry.SourceToStandard(mapping)*sourceToWorld.Inverse());
                    if(Math.Abs(measured.Z)>.5 || Math.Abs(Math.Abs(measured.Y)-e.Depth)>.5)throw new ArgumentException("原块实测深度 "+Math.Abs(measured.Y).ToString("0.###")+" mm，与参数 "+e.Depth+" mm 不一致。请核对单位/型号，不能缩放原块伪造一致。");
                    var depthAxis=bx.CrossProduct(by).GetNormal().CrossProduct((b-a).GetNormal()).GetNormal();
                    double actual=Math.Abs((depthPoint.Value-a).DotProduct(depthAxis))*worldMillimeters;
                    if(Math.Abs(actual-e.Depth)>.5)throw new ArgumentException("原块当前实际深度 "+actual.ToString("0.###")+"mm，当前型号要求 "+e.Depth+"mm；不会把旧尺寸改标为650mm。");
                }
            }
            string shape=equipment=="HANGER_CUSTOM"?"Outline":CadGeometry.Keyword(editor,"占地轮廓来源（不使用含文字的整体 Extents）","Outline Nominal",e.Length>0 && e.Depth>0?"Nominal":"Outline");
            if(shape=="Outline")
            {
                var opt=new PromptEntityOptions("\n选择已在图上核对的物理占地闭合多段线：");opt.SetRejectMessage("\n需要闭合多段线。");opt.AddAllowedClass(typeof(Polyline),true);
                var outline=editor.GetEntity(opt);if(outline.Status!=PromptStatus.OK)throw new Cancelled();
                using(var tx=s.Document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    var pl=(Polyline)tx.GetObject(outline.ObjectId,OpenMode.ForRead);var canonical=CadGeometry.SourceToStandard(mapping)*sourceToWorld.Inverse();
                    double millimetersPerUnit=Math.Max(Vector3d.XAxis.TransformBy(canonical).Length,
                        Math.Max(Vector3d.YAxis.TransformBy(canonical).Length,Vector3d.ZAxis.TransformBy(canonical).Length));
                    mapping.PhysicalOutline=CadGeometry.PolylinePoints(pl,.05/millimetersPerUnit).Select(p=>p.TransformBy(canonical)).Select(p=>new V2(p.X,p.Y)).ToList();
                    Geometry.Polygon(mapping.PhysicalOutline);
                }
            }
            else if(e.Length<=0 || e.Depth<=0)throw new ArgumentException("此设备没有可靠的名义占地，必须选择真实轮廓。");
            if(equipment=="HANGER_CUSTOM")mapping.LocalHooks=Capacity.Hooks(new Placement{EquipmentId=equipment,Length=length},s.Room.Options);
            s.Edit(_=>{Blocks.InstallMapping(s.Project,mapping);s.Room.PreviewVisible=false;});
            var item=new Placement{EquipmentId=equipment,Length=length,PhysicalOutline=mapping.PhysicalOutline};
            var overlay=new Polyline();int index=0;
            foreach(var p in Geometry.Outline(item))overlay.AddVertexAt(index++,new Point2d(p.X,p.Y),0,0,0);
            overlay.Closed=true;overlay.ColorIndex=6;overlay.TransformBy(sourceToWorld*CadGeometry.SourceToStandard(mapping).Inverse());
            s.Preview.Clear();s.Preview.Overlay(overlay);
            editor.WriteMessage("\n已保存版本化原块映射，并叠显独立物理轮廓。源状态："+mapping.Provenance+"；图面/实物参数尚待 GYVERIFY 核对。原图和原块未被重画。");
            if(s.Document.Database.Insunits==UnitsValue.Undefined)editor.WriteMessage("\n图纸单位未设：本次按已确认的每图纸单位 "+worldMillimeters.ToString("0.###")+"mm核对长度和深度，没有修改图纸单位；原块样式与制造条件仍需比对。");
        });

        [CommandMethod("GENGYI","GYIMPORT",CommandFlags.Modal)]
        public void Import()=>Commands.Run(s=>
        {
            Commands.NeedModelSpace(s);
            var editor=s.Document.Editor;
            var selected=editor.GetFileNameForOpen(new PromptOpenFileOptions("\n选择本地原始设备 DWG/DXF（仅读取所选文件，不上传）："){Filter="CAD (*.dwg;*.dxf)|*.dwg;*.dxf"});
            if(selected.Status!=PromptStatus.OK)throw new Cancelled();
            string file=selected.StringResult;
            if(file.Replace('\\','/').IndexOf("references/legacy/",StringComparison.OrdinalIgnoreCase)>=0 || Path.GetFileName(file).IndexOf("F_R3",StringComparison.OrdinalIgnoreCase)>=0)
                throw new ArgumentException("旧 F-R3 是被否定的线条符号，不能导入作原块源。");
            string hash=Blocks.HashFile(file);
            using(var source=new Database(false,true))
            {
                if(string.Equals(Path.GetExtension(file),".dxf",StringComparison.OrdinalIgnoreCase))source.DxfIn(file,null);
                else{source.ReadDwgFile(file,FileOpenMode.OpenForReadAndAllShare,true,null);source.CloseInput(true);}
                List<string> names;
                using(var tx=source.TransactionManager.StartOpenCloseTransaction())
                    names=((BlockTable)tx.GetObject(source.BlockTableId,OpenMode.ForRead)).Cast<ObjectId>()
                        .Select(id=>(BlockTableRecord)tx.GetObject(id,OpenMode.ForRead)).Where(r=>!r.IsLayout && !r.IsAnonymous && !r.IsFromExternalReference).Select(r=>r.Name).ToList();
                if(names.Count==0)throw new ArgumentException("所选文件没有可导入的原生设备块；组合线需先在原 CAD 中按原样建块。");
                editor.WriteMessage("\n所选文件的可用块："+string.Join("、",names.Take(100)));
                var name=editor.GetString(new PromptStringOptions("\n输入要导入的原块名："){AllowSpaces=true});
                if(name.Status!=PromptStatus.OK)throw new Cancelled();
                string actual=names.FirstOrDefault(n=>string.Equals(n,name.StringResult,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("所选文件没有该块。");
                var point=CadGeometry.Point(editor,"指定原块样本位置（用于随后标定，不计入布置容量）：");
                double srcUnits=source.Insunits==UnitsValue.Undefined?CadGeometry.Number(editor,"原文件单位未设，每原单位对应多少 mm：",1):UnitsConverter.GetConversionFactor(source.Insunits,UnitsValue.Millimeters);
                double destUnits=CadGeometry.Units(s.Document.Database,editor);ObjectId original;
                using(var tx=source.TransactionManager.StartTransaction())
                {
                    var table=(BlockTable)tx.GetObject(source.BlockTableId,OpenMode.ForRead);original=table[actual];
                    VerifyDependencies(original,tx,new HashSet<ObjectId>());
                    var root=(BlockTableRecord)tx.GetObject(original,OpenMode.ForWrite);
                    root.Name="GY_SOURCE_"+hash.Substring(0,12)+"_"+Guid.NewGuid().ToString("N").Substring(0,8);tx.Commit();
                }
                s.Edit(tx=>
                {
                    var ids=new ObjectIdCollection(new[]{original});var map=new IdMapping();
                    source.WblockCloneObjects(ids,s.Document.Database.BlockTableId,map,DuplicateRecordCloning.MangleName,false);
                    ObjectId imported=map[original].Value;
                    var table=(BlockTable)tx.GetObject(s.Document.Database.BlockTableId,OpenMode.ForRead);
                    var model=(BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                    var sample=new BlockReference(point,imported){ScaleFactors=new Scale3d(srcUnits/destUnits),LayerId=Blocks.Layer(s.Document.Database,tx,"GY_BLOCK_SAMPLES",7)};
                    model.AppendEntity(sample);tx.AddNewlyCreatedDBObject(sample,true);
                });
                editor.WriteMessage("\n已保留嵌套依赖导入原块样本，重名定义采用隔离命名，没有替换现有同名块。运行 GYBLOCK 选择该样本标定并绑定设备。");
            }
        });

        private static void VerifyDependencies(ObjectId id,Transaction tx,HashSet<ObjectId> visited)
        {
            if(!visited.Add(id))return;
            var record=(BlockTableRecord)tx.GetObject(id,OpenMode.ForRead);
            if(record.IsFromExternalReference)throw new ArgumentException("原块包含外部参照 "+record.Name+"；请在原文件中明确处理后再导入。");
            foreach(ObjectId entityId in record)
            {
                var entity=tx.GetObject(entityId,OpenMode.ForRead);
                if(entity.GetRXClass().Name.IndexOf("Proxy",StringComparison.OrdinalIgnoreCase)>=0)throw new ArgumentException("原块包含代理对象，当前导入无法证明完整；请用目标 CAD 可识别的原生实体。");
                if(entity is BlockReference nested)VerifyDependencies(nested.BlockTableRecord,tx,visited);
            }
        }

        [CommandMethod("GENGYI","GYVERIFY",CommandFlags.Modal)]
        public void Verify()=>Commands.Run(s=>
        {
            string equipment=EquipmentChoice(s,"选择已完成图面与尺寸比对的设备");
            double length=equipment=="HANGER_CUSTOM"?CadGeometry.Number(s.Document.Editor,"实际挂架长度 mm：",1000):0;
            string key=BlockMapping.For(equipment,length);
            if(!s.Project.Mappings.TryGetValue(key,out var map))throw new ArgumentException("请先绑定设备原块。");
            var sample=new Placement{EquipmentId=equipment,Length=length,LocalHooks=map.LocalHooks};
            if(!map.Accepts(sample))throw new ArgumentException(Blocks.MappingMismatch(sample));
            if(CadGeometry.Keyword(s.Document.Editor,"请在 CAD 中完成原图/原块与实例的同尺度核对：轮廓、列数、圆弧、方向、实际尺寸、物理占地；挂架还须核对实际挂点。已全部核对一致","Yes No","No")!="Yes")throw new Cancelled();
            s.Edit(_=>
            {
                map.ReviewStatus="approved";map.FootprintVerified=true;s.Project.Mappings[key]=map;s.Project.Mappings[map.Key]=map;
            });
            s.Document.Editor.WriteMessage("\n记录为用户在宿主内完成比对。来源仍为 "+map.Provenance+"，重建块不会改标 original_cad。操作动作与安装条件仍单独校核。");
        });

        [CommandMethod("GENGYI","GYADD",CommandFlags.Modal)]
        public void Add()=>Commands.Run(s=>
        {
            Commands.NeedBoundary(s);string id=EquipmentChoice(s,"手动添加设备（辅助设备容量为0）");
            bool allowed=s.Room.Mode==RoomMode.Shoe?new[]{"S12","S18","S24","S48",Equipment.CornerId,"CUP100","SHOE_ARCHIVE","SHOE_DESK","SHOE_WATERHEATER"}.Contains(id):
                s.Room.Mode==RoomMode.FirstChange?id=="LOCKER12":new[]{"HANGER_CUSTOM","CHANGING_MIRROR","UV_DEVICE"}.Contains(id);
            if(!allowed)throw new ArgumentException("该设备不属于当前房间模式。");
            double length=id=="HANGER_CUSTOM"?CadGeometry.Number(s.Document.Editor,"实际挂架总长 mm：",1000):0;
            string key=BlockMapping.For(id,length);
            s.Project.Mappings.TryGetValue(key,out var mapping);
            var position=CadGeometry.Local(s.Room.Boundary!.Frame,CadGeometry.Point(s.Document.Editor,"设备落位点："));
            var item=new Placement{RoomId=s.Room.Id,EquipmentId=id,Position=position,Length=length,CrossingEnabled=id=="S12"||id=="S18"||id=="S24",LocalHooks=mapping?.LocalHooks};
            if(id=="HANGER_CUSTOM" && item.LocalHooks==null)item.LocalHooks=Capacity.Hooks(item,s.Room.Options).Select(p=>p-position).ToList();
            Layout.ResolveMapping(s.Project,item);Geometry.Footprint(item);
            var scene=Commands.HasPreview(s)?s.Room.Candidates[s.Room.SelectedCandidate].Items:s.Room.Placed;
            ShoeStacking.RejectNewViolations(scene,scene.Concat(new[]{item}).ToList());
            if(Commands.HasPreview(s))
                s.Edit(_=>{var candidate=s.Room.Candidates[s.Room.SelectedCandidate];candidate.Items.Add(item);candidate.Statistics=Validation.Evaluate(s.Project,s.Room,candidate.Items);});
            else
            {
                mapping=Blocks.Mapping(s,item);
                s.Edit(tx=>Blocks.Create(s.Document.Database,tx,s.Room,item,mapping,true));
            }
        });
    }
}
