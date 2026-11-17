using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Gengyi.Core;

namespace Gengyi.Cad.AutoCAD2024
{
    internal static class Store
    {
        internal const string ProjectKey="GENGYI_PROJECT_V1";
        internal const string InstanceKey="GENGYI_INSTANCE_V1";
        internal static string? Read(DBDictionary dictionary,string key,Transaction transaction)
        {
            if(!dictionary.Contains(key))return null;
            var record=transaction.GetObject(dictionary.GetAt(key),OpenMode.ForRead) as Xrecord;
            if(record==null)return null;
            using(var buffer=record.Data)
                return buffer==null?null:string.Concat(buffer.AsArray().Where(x=>x.TypeCode==(int)DxfCode.Text).Select(x=>x.Value as string));
        }
        internal static void Write(DBDictionary dictionary,string key,string text,Transaction transaction)
        {
            if(!dictionary.IsWriteEnabled)dictionary.UpgradeOpen();
            Xrecord record;
            if(dictionary.Contains(key))record=(Xrecord)transaction.GetObject(dictionary.GetAt(key),OpenMode.ForWrite);
            else{record=new Xrecord();dictionary.SetAt(key,record);transaction.AddNewlyCreatedDBObject(record,true);}
            var values=Enumerable.Range(0,(text.Length+239)/240).Select(i=>new TypedValue((int)DxfCode.Text,text.Substring(i*240,Math.Min(240,text.Length-i*240)))).ToArray();
            using(var buffer=new ResultBuffer(values))record.Data=buffer;
        }
        internal static string Pack(Project project)
        {
            var raw=Encoding.UTF8.GetBytes(Serialization.Write(project));
            using(var output=new MemoryStream())
            {
                using(var zip=new GZipStream(output,CompressionLevel.Optimal,true))zip.Write(raw,0,raw.Length);
                return Convert.ToBase64String(output.ToArray());
            }
        }
        internal static Project Unpack(string text)
        {
            using(var input=new MemoryStream(Convert.FromBase64String(text)))
            using(var zip=new GZipStream(input,CompressionMode.Decompress))
            using(var output=new MemoryStream())
            {
                var buffer=new byte[8192];int read;
                while((read=zip.Read(buffer,0,buffer.Length))>0)
                {
                    if(output.Length+read>32*1024*1024)throw new InvalidDataException("项目数据超过 32 MiB，请检查图纸数据。");
                    output.Write(buffer,0,read);
                }
                var result=Serialization.Read<Project>(Encoding.UTF8.GetString(output.ToArray()));
                if(result.SchemaVersion!="1.0" || result.Rooms.Count==0 || result.Rooms.Count>100)throw new InvalidDataException("不支持的项目数据版本。");
                result.Population.Validate();return result;
            }
        }
        internal static string? Instance(Entity entity,Transaction transaction)
        {
            if(entity.ExtensionDictionary.IsNull)return null;
            return Read((DBDictionary)transaction.GetObject(entity.ExtensionDictionary,OpenMode.ForRead),InstanceKey,transaction);
        }
        internal static void Instance(Entity entity,Placement item,Transaction transaction)
        {
            if(!entity.IsWriteEnabled)entity.UpgradeOpen();
            if(entity.ExtensionDictionary.IsNull)entity.CreateExtensionDictionary();
            Write((DBDictionary)transaction.GetObject(entity.ExtensionDictionary,OpenMode.ForWrite),InstanceKey,Serialization.Write(item),transaction);
        }
        internal static void Project(Database database,Project project,Transaction transaction) =>
            Write((DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId,OpenMode.ForWrite),ProjectKey,Pack(project),transaction);
    }
}
