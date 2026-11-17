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
            if(item.EquipmentId=="HANGER_CUSTOM" && (mapping.LocalHooks==null || item.LocalHooks==null ||
                mapping.LocalHooks.Count!=item.LocalHooks.Count || mapping.LocalHooks.Where((p,i)=>p.Distance(item.LocalHooks[i])>.001).Any()))
                throw new ArgumentException("挂架 "+item.Length+" mm 的原块挂点与候选不一致；请为该长度/挂点绑定原块或明确重建，不能拉伸或虚计挂钩。");
            return mapping;
        }
        internal static BlockReference Create(Database database,Transaction transaction,Room room,Placement item,BlockMapping mapping,bool append)
        {
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
                    if(item.EquipmentId=="S48")
                    {
                        var stack=new AttributeReference{Tag="GY_STACK",TextString="2层叠放 / 48人",Height=100,Position=new Point3d(0,700,0)};
                        stack.TransformBy(CadGeometry.ToWorld(room.Boundary!.Frame)*Matrix3d.Displacement(new Vector3d(item.Position.X,item.Position.Y,0))*Matrix3d.Rotation(item.Rotation*Math.PI/180,Vector3d.ZAxis,Point3d.Origin));
                        result.AttributeCollection.AppendAttribute(stack);transaction.AddNewlyCreatedDBObject(stack,true);
                    }
                    item.NativeHandle=result.Handle.ToString();item.BlockName=mapping.BlockName;item.Provenance=mapping.Provenance;
                    Store.Instance(result,item,transaction);
                }
                else ApplyStyle(result,mapping,transaction);
                return result;
            }
            catch{if(result.ObjectId.IsNull)result.Dispose();throw;}
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
                if(session.Project.Mappings.TryGetValue(BlockMapping.For(item.EquipmentId,item.Length),out var original) && original.Provenance=="original_cad")continue;
                var e=item.Equipment;
                if(e.Length<=0 || e.Depth<=0)throw new ArgumentException(e.Id+"缺少原始外形，不能虚构重建；请导入实际块与占地。");
                string signature=e.Id+"/"+item.Length+"/"+Serialization.Write(item.LocalHooks);
                string name="GY_IMAGE_"+e.Id+"_"+Hash(Encoding.UTF8.GetBytes(signature)).Substring(0,12);
                if(!table.Has(name))
                {
                    var record=new BlockTableRecord{Name=name,Origin=Point3d.Origin,Units=UnitsValue.Millimeters};
                    table.Add(record);transaction.AddNewlyCreatedDBObject(record,true);
                    void Add(Entity entity){entity.Layer="0";record.AppendEntity(entity);transaction.AddNewlyCreatedDBObject(entity,true);}
                    if(e.Id=="CORNER_R600")
                    {
                        var pl=new Polyline();pl.AddVertexAt(0,new Point2d(0,0),0,0,0);
                        pl.AddVertexAt(1,new Point2d(600,0),Math.Tan(Math.PI/8),0,0);pl.AddVertexAt(2,new Point2d(0,600),0,0,0);
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
                        for(int col=1;col<e.Columns;col++)Add(new Line(new Point3d(col*e.Length/e.Columns,0,0),new Point3d(col*e.Length/e.Columns,e.Depth,0)));
                        if(e.IsShoe)Add(new Line(new Point3d(0,300,0),new Point3d(e.Length,300,0)));
                    }
                }
                string evidence=e.Id=="LOCKER12"?"07":e.Id=="HANGER_CUSTOM"?"08":e.Id=="CUP100"?"06":"05";
                JObject source;
                using(var stream=typeof(Blocks).Assembly.GetManifestResourceStream("Gengyi.SourceEvidence")??throw new ArgumentException("缺少原图来源清单。"))
                using(var reader=new StreamReader(stream))
                    source=(JObject)JObject.Parse(reader.ReadToEnd())["images"]!.Single(image=>(string?)image["id"]==evidence);
                InstallMapping(session.Project,new BlockMapping{EquipmentId=e.Id,Length=item.Length,BlockName=name,SourceBlockName=name,
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
