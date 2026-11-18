using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Gengyi.Core;
using Newtonsoft.Json.Linq;
using Geometry=Gengyi.Core.Geometry;

namespace Gengyi.Cad.AutoCAD2024
{
    internal static class Blocks
    {
        internal static string Hash(byte[] bytes)
        {using(var sha=SHA256.Create())return string.Concat(sha.ComputeHash(bytes).Select(b=>b.ToString("x2")));}
        internal static ObjectId Layer(Database database,Transaction transaction,string name,short color)
        {
            var layers=(LayerTable)transaction.GetObject(database.LayerTableId,OpenMode.ForRead);
            if(layers.Has(name))return layers[name];
            layers.UpgradeOpen();var record=new LayerTableRecord{Name=name,Color=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci,color)};
            var id=layers.Add(record);transaction.AddNewlyCreatedDBObject(record,true);return id;
        }
        internal static BlockMapping Mapping(Session session,Placement item)
        {
            if(!session.Project.Mappings.TryGetValue(item.MappingKey,out var mapping))
                throw new ArgumentException(item.EquipmentId+" 缺少原块映射。请选取/导入原块，或明确选择据图重建模式。");
            if(mapping.Provenance=="legacy_generated" || mapping.Provenance=="missing")throw new ArgumentException("此来源不能作为生产设备块。");
            if(mapping.EquipmentId!=item.EquipmentId)throw new ArgumentException("块映射与设备型号不一致，禁止用缩放或旧映射冒充新柜型。");
            if(!mapping.Accepts(item))
                throw new ArgumentException(MappingMismatch(item));
            return mapping;
        }
        internal static string MappingMismatch(Placement item)
        {
            if(item.Equipment.IsShoe)return "鞋柜原块尚未按650mm总深（含50mm通风道）标定；旧600mm块仅作历史几何读取，请重新绑定或明确重建。";
            if(item.Equipment.IsCorner)return item.EquipmentId==Equipment.CornerId?
                "转角原块尚未按R650标定，请绑定实际R650原块或明确重建；不能缩放旧R600冒充新转角。":
                "旧R600仅作历史几何读取，当前方案要求R650，请绑定新转角或明确重建。";
            if(item.EquipmentId=="HANGER_CUSTOM")return "挂架 "+item.Length+" mm 的原块挂点与候选不一致；请为该长度/挂点绑定原块或明确重建，不能拉伸或虚计挂钩。";
            return "原块标定与当前设备尺寸不一致，请重新绑定并核对实际尺寸。";
        }
        internal static BlockReference Create(Database database,Transaction transaction,Room room,Placement item,BlockMapping mapping,bool append)
        {
            if(!mapping.Accepts(item))throw new ArgumentException(MappingMismatch(item));
            var table=(BlockTable)transaction.GetObject(database.BlockTableId,OpenMode.ForRead);
            if(!table.Has(mapping.BlockName))throw new ArgumentException("图中缺少块定义："+mapping.BlockName);
            var result=new BlockReference(Point3d.Origin,table[mapping.BlockName]);
            try
            {
                // Normalize any non-zero source block base point before assigning the exact transform.
                result.TransformBy(CadGeometry.Transform(room,item,mapping)*result.BlockTransform.Inverse());
                if(result.IsDynamicBlock)
                    foreach(DynamicBlockReferenceProperty property in result.DynamicBlockReferencePropertyCollection)
                        if(!property.ReadOnly && mapping.DynamicValues.TryGetValue(property.PropertyName,out var value))
                        {
                            var current=property.Value??throw new ArgumentException("原动态属性缺少有效值："+property.PropertyName);
                            property.Value=value is JToken token?token.ToObject(current.GetType()):Convert.ChangeType(value,current.GetType(),CultureInfo.InvariantCulture);
                        }
                if(append)
                {
                    var model=(BlockTableRecord)transaction.GetObject(table[BlockTableRecord.ModelSpace],OpenMode.ForWrite);
                    result.LayerId=Layer(database,transaction,"GY_"+room.Id,room.Mode==RoomMode.Shoe?(short)3:room.Mode==RoomMode.FirstChange?(short)2:(short)4);
                    ApplyStyle(result,mapping,transaction);
                    model.AppendEntity(result);transaction.AddNewlyCreatedDBObject(result,true);
                    foreach(ObjectId id in (BlockTableRecord)transaction.GetObject(result.BlockTableRecord,OpenMode.ForRead))
                    {
                        if(!(transaction.GetObject(id,OpenMode.ForRead) is AttributeDefinition definition) || definition.Constant)continue;
                        var attr=new AttributeReference();attr.SetAttributeFromBlock(definition,result.BlockTransform);
                        if(mapping.Attributes.TryGetValue(definition.Tag,out var text))attr.TextString=text;
                        if(new[]{"CAPACITY","PEOPLE","PERSONS","人数"}.Contains(definition.Tag.ToUpperInvariant()))attr.TextString=Capacity.Nominal(item,room.Options).ToString(CultureInfo.InvariantCulture);
                        if(new[]{"ID","柜号"}.Contains(definition.Tag.ToUpperInvariant()))attr.TextString=item.Id.Substring(0,8);
                        result.AttributeCollection.AppendAttribute(attr);transaction.AddNewlyCreatedDBObject(attr,true);
                    }
                    AppendDescriptions(result,database,transaction,room,item);
                    item.NativeHandle=result.Handle.ToString();item.BlockName=mapping.BlockName;item.Provenance=mapping.Provenance;
                    Store.Instance(result,item,transaction);
                }
                else ApplyStyle(result,mapping,transaction);
                return result;
            }
            catch{if(result.ObjectId.IsNull)result.Dispose();throw;}
        }
        private static void AppendDescriptions(BlockReference reference,Database database,Transaction transaction,Room room,Placement item,bool canPosition=true)
        {
            var layouts=LabelLayout(item,room.Options).ToDictionary(l=>"GY_DESCRIPTION_"+l.Index,StringComparer.OrdinalIgnoreCase);
            var existing=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(ObjectId id in reference.AttributeCollection)
            {
                var attribute=(AttributeReference)transaction.GetObject(id,OpenMode.ForRead);
                if(attribute.Tag.Equals("GY_STACK",StringComparison.OrdinalIgnoreCase) ||
                    (attribute.Tag.StartsWith("GY_DESCRIPTION_",StringComparison.OrdinalIgnoreCase) &&
                     (!layouts.ContainsKey(attribute.Tag) || existing.Contains(attribute.Tag))))
                {attribute.UpgradeOpen();attribute.Erase();continue;}
                if(!layouts.TryGetValue(attribute.Tag,out var layout))continue;
                existing.Add(attribute.Tag);attribute.UpgradeOpen();
                if(canPosition)ConfigureLabel(attribute,database,room,item,layout);else attribute.TextString=layout.Text;
                attribute.Invisible=false;attribute.LockPositionInBlock=true;attribute.AdjustAlignment(database);
            }
            foreach(var layout in layouts.Values)
            {
                string tag="GY_DESCRIPTION_"+layout.Index;
                if(existing.Contains(tag) || !canPosition)continue;
                // These belong to this reference, not the user's source block definition.
                // Native MOVE/COPY/ROTATE and DWG persistence carry them with the cabinet.
                var label=new AttributeReference{Tag=tag,Invisible=false,LockPositionInBlock=true};
                try
                {
                    ConfigureLabel(label,database,room,item,layout);
                    reference.AttributeCollection.AppendAttribute(label);transaction.AddNewlyCreatedDBObject(label,true);
                    label.AdjustAlignment(database);
                }
                catch{if(label.ObjectId.IsNull)label.Dispose();throw;}
            }
        }
        internal static int EnsureDescriptions(Session session)
        {
            // Explicit GYSETUP migration only. Idle remains read-only. ATTSYNC can remove
            // attributes without source definitions; opening the panel restores our labels.
            var missing=new List<(Room room,Placement item,ObjectId id,bool canPosition)>();
            using(var tx=session.Document.Database.TransactionManager.StartOpenCloseTransaction())
                foreach(var room in session.Project.Rooms.Where(r=>r.Boundary!=null))
                    foreach(var item in room.Placed.Where(p=>p.NativeHandle.Length>0))
                    {
                        var id=session.Document.Database.GetObjectId(false,new Handle(Convert.ToInt64(item.NativeHandle,16)),0);
                        if(id.IsErased || !(tx.GetObject(id,OpenMode.ForRead) is BlockReference reference))continue;
                        bool canPosition=!item.NativeGeometryUnverified || item.NativeIssue==MappingMismatch(item);
                        var labels=LabelLayout(item,room.Options).ToDictionary(l=>"GY_DESCRIPTION_"+l.Index,l=>l.Text,StringComparer.OrdinalIgnoreCase);
                        var attributes=reference.AttributeCollection.Cast<ObjectId>().Select(a=>(AttributeReference)tx.GetObject(a,OpenMode.ForRead)).ToList();
                        var own=attributes.Where(a=>a.Tag.StartsWith("GY_DESCRIPTION_",StringComparison.OrdinalIgnoreCase)).ToList();
                        bool changed=attributes.Any(a=>a.Tag.Equals("GY_STACK",StringComparison.OrdinalIgnoreCase)) ||
                            own.GroupBy(a=>a.Tag,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1) ||
                            own.Any(a=>!labels.TryGetValue(a.Tag,out var text) || text!=a.TextString) ||
                            (canPosition && labels.Keys.Any(tag=>!own.Any(a=>a.Tag.Equals(tag,StringComparison.OrdinalIgnoreCase))));
                        if(changed)missing.Add((room,item,id,canPosition));
                    }
            if(missing.Count>0)
                session.Edit(tx=>
                {
                    foreach(var entry in missing)
                        AppendDescriptions((BlockReference)tx.GetObject(entry.id,OpenMode.ForWrite),session.Document.Database,tx,entry.room,entry.item,entry.canPosition);
                });
            return missing.Count;
        }
        private sealed class LabelLine
        {
            internal int Index;
            internal string Text="";
            internal V2 Center=new V2();
            internal double Height;
        }
        private static IEnumerable<LabelLine> LabelLayout(Placement item,LayoutOptions options)
        {
            var e=item.Equipment;
            var description=EquipmentPresentation.Describe(item,options);
            var lines=description.CompactLines.ToArray();
            double minX=0,minY=0,width=e.Length,depth=e.Depth;
            if(item.PhysicalOutline!=null && item.PhysicalOutline.Count>0)
            {
                minX=item.PhysicalOutline.Min(p=>p.X);minY=item.PhysicalOutline.Min(p=>p.Y);
                width=item.PhysicalOutline.Max(p=>p.X)-minX;depth=item.PhysicalOutline.Max(p=>p.Y)-minY;
            }
            if(width<=0 || depth<=0)yield break;
            if(depth<250)lines=lines.Take(1).ToArray();
            double cx=minX+width/2,cy=minY+depth/2;
            if(e.IsCorner)
            {
                // The sector's bounding-box centre can lie outside the curved corner.
                cx=e.Length*.4;cy=e.Depth*.4;width=e.Length*.68;depth=e.Depth*.5;
            }
            double characters=lines.Max(s=>s.Sum(c=>c<128?.6:1));
            double height=Math.Min(90,Math.Min(width*.86/Math.Max(1,characters),depth*.78/(lines.Length*1.4)));
            for(int n=0;n<lines.Length;n++)
                yield return new LabelLine{Index=n,Text=lines[n],Center=new V2(cx,e.IsShoe && lines.Length==2?
                    minY+depth*(n==0?.75:.25):cy+(lines.Length-1-2*n)*height*.7),Height=height};
        }
        private static void ConfigureLabel(DBText label,Database database,Room room,Placement item,LabelLine layout)
        {
            label.SetDatabaseDefaults(database);label.Layer="0";label.ColorIndex=7;
            label.TextString=layout.Text;label.Height=layout.Height;
            label.Normal=Vector3d.ZAxis;label.Rotation=0;label.WidthFactor=1;label.Oblique=0;
            label.HorizontalMode=TextHorizontalMode.TextCenter;label.VerticalMode=TextVerticalMode.TextVerticalMid;
            label.Position=new Point3d(layout.Center.X,layout.Center.Y,0);label.AlignmentPoint=label.Position;
            label.TransformBy(CadGeometry.ToWorld(room.Boundary!.Frame)*
                Matrix3d.Displacement(new Vector3d(item.Position.X,item.Position.Y,0))*
                Matrix3d.Rotation(item.Rotation*Math.PI/180,Vector3d.ZAxis,Point3d.Origin));
        }
        internal static IEnumerable<DBText> Labels(Database database,Room room,Placement item)
        {
            foreach(var layout in LabelLayout(item,room.Options))
            {
                var text=new DBText();
                try{ConfigureLabel(text,database,room,item,layout);text.AdjustAlignment(database);}
                catch{text.Dispose();throw;}
                yield return text;
            }
        }
        internal static void InstallMapping(Project project,BlockMapping mapping)
        {
            string alias=BlockMapping.For(mapping.EquipmentId,mapping.Length);
            mapping.Key=alias+"#"+Hash(Encoding.UTF8.GetBytes(Serialization.Write(mapping))).Substring(0,16);
            project.Mappings[mapping.Key]=mapping;project.Mappings[alias]=mapping;
        }
        private static void ApplyStyle(BlockReference reference,BlockMapping mapping,Transaction transaction)
        {
            if(mapping.SourceRgb!=null && mapping.SourceRgb.Length==3)
                reference.Color=Autodesk.AutoCAD.Colors.Color.FromRgb((byte)mapping.SourceRgb[0],(byte)mapping.SourceRgb[1],(byte)mapping.SourceRgb[2]);
            else if(mapping.SourceColorIndex.HasValue)reference.ColorIndex=mapping.SourceColorIndex.Value;
            if(mapping.SourceLineWeight.HasValue)reference.LineWeight=(LineWeight)mapping.SourceLineWeight.Value;
            if(mapping.SourceLinetype.Length>0)
            {
                var table=(LinetypeTable)transaction.GetObject(reference.BlockTableRecord.Database.LinetypeTableId,OpenMode.ForRead);
                if(table.Has(mapping.SourceLinetype))reference.LinetypeId=table[mapping.SourceLinetype];
                else throw new ArgumentException("缺少源线型："+mapping.SourceLinetype);
            }
        }
        internal static string HashFile(string file)
        {using(var input=File.OpenRead(file))using(var sha=SHA256.Create())return string.Concat(sha.ComputeHash(input).Select(b=>b.ToString("x2")));}
        internal static void Reconstruct(Session session,Transaction transaction,IEnumerable<Placement> requested)
        {
            var database=session.Document.Database;
            var table=(BlockTable)transaction.GetObject(database.BlockTableId,OpenMode.ForWrite);
            foreach(var item in requested.GroupBy(p=>BlockMapping.For(p.EquipmentId,p.Length)).Select(g=>g.First()))
            {
                if(session.Project.Mappings.TryGetValue(BlockMapping.For(item.EquipmentId,item.Length),out var original) &&
                    original.Provenance=="original_cad" && original.Accepts(item) && table.Has(original.BlockName))continue;
                var e=item.Equipment;
                if(e.Length<=0 || e.Depth<=0)throw new ArgumentException(e.Id+"缺少原始外形，不能虚构重建；请导入实际块与占地。");
                if(e.IsCorner && e.Id!=Equipment.CornerId)throw new ArgumentException("旧R600只读兼容；请重新生成使用R650的候选后重建，不会自动缩放旧转角。");
                string signature=Serialization.Write(new{format="reconstructed-650-duct-v3",id=e.Id,length=e.Length,depth=e.Depth,height=e.Height,
                    radius=e.IsCorner?e.Length:0,duct=e.IsShoe?Equipment.ShoeDuctMm:0,hooks=item.LocalHooks});
                string name="GY_IMAGE_"+e.Id+"_"+Hash(Encoding.UTF8.GetBytes(signature)).Substring(0,12);
                if(!table.Has(name))
                {
                    var record=new BlockTableRecord{Name=name,Origin=Point3d.Origin,Units=UnitsValue.Millimeters};
                    table.Add(record);transaction.AddNewlyCreatedDBObject(record,true);
                    void Add(Entity entity){entity.Layer="0";record.AppendEntity(entity);transaction.AddNewlyCreatedDBObject(entity,true);}
                    if(e.IsCorner)
                    {
                        var pl=new Polyline();pl.AddVertexAt(0,new Point2d(0,0),0,0,0);
                        pl.AddVertexAt(1,new Point2d(e.Length,0),Math.Tan(Math.PI/8),0,0);pl.AddVertexAt(2,new Point2d(0,e.Depth),0,0,0);
                        pl.Closed=true;Add(pl);
                    }
                    else if(e.Id=="HANGER_CUSTOM")
                    {
                        Add(new Line(new Point3d(0,225,0),new Point3d(e.Length,225,0)));
                        foreach(var hook in item.LocalHooks??Capacity.Hooks(new Placement{EquipmentId=e.Id,Length=item.Length},session.Room.Options))
                            Add(new Line(new Point3d(hook.X,225,0),new Point3d(hook.X,275,0)));
                        for(double x=25;x<e.Length;x+=1000)Add(new Line(new Point3d(x,0,0),new Point3d(x,450,0)));
                    }
                    else
                    {
                        var pl=new Polyline();int n=0;foreach(var q in Geometry.Rect(0,0,e.Length,e.Depth))pl.AddVertexAt(n++,new Point2d(q.X,q.Y),0,0,0);pl.Closed=true;Add(pl);
                        if(e.IsShoe)
                        {
                            double bottom=(e.Depth-Equipment.ShoeDuctMm)/2,top=bottom+Equipment.ShoeDuctMm;
                            Add(new Line(new Point3d(0,bottom,0),new Point3d(e.Length,bottom,0)));
                            Add(new Line(new Point3d(0,top,0),new Point3d(e.Length,top,0)));
                            for(int col=1;col<e.Columns;col++)
                            {
                                double x=col*e.Length/e.Columns;
                                Add(new Line(new Point3d(x,0,0),new Point3d(x,bottom,0)));
                                Add(new Line(new Point3d(x,top,0),new Point3d(x,e.Depth,0)));
                            }
                        }
                        else for(int col=1;col<e.Columns;col++)Add(new Line(new Point3d(col*e.Length/e.Columns,0,0),new Point3d(col*e.Length/e.Columns,e.Depth,0)));
                    }
                }
                string evidence=e.Id=="LOCKER12"?"07":e.Id=="HANGER_CUSTOM"?"08":e.Id=="CUP100"?"06":"05";
                JObject source;
                using(var stream=typeof(Blocks).Assembly.GetManifestResourceStream("Gengyi.SourceEvidence")??throw new ArgumentException("缺少原图来源清单。"))
                using(var reader=new StreamReader(stream))
                    source=(JObject)JObject.Parse(reader.ReadToEnd())["images"]!.Single(image=>(string?)image["id"]==evidence);
                InstallMapping(session.Project,new BlockMapping{EquipmentId=e.Id,EquipmentDepthMm=e.Depth,Length=item.Length,BlockName=name,SourceBlockName=name,
                    SourceHash=(string)source["sha256"]!,GeometryHash=Hash(Encoding.UTF8.GetBytes(signature)),SourceEvidence=(string)source["file"]!,
                    Provenance="image_reconstructed",ReviewStatus="not_run",FootprintVerified=false,
                    PhysicalOutline=Geometry.Outline(new Placement{EquipmentId=e.Id,Length=item.Length}),
                    LocalHooks=item.LocalHooks==null?null:Serialization.Copy(item.LocalHooks)});
            }
        }
        internal static string CaptureHash(Database database,ObjectId source)
        {
            string path=Path.Combine(Path.GetTempPath(),"gengyi-block-"+Guid.NewGuid().ToString("N")+".dwg");
            try
            {
                using(var snapshot=database.Wblock(source)){snapshot.SaveAs(path,DwgVersion.AC1032);}
                return HashFile(path);
            }
            finally{if(File.Exists(path))File.Delete(path);}
        }
    }
}
