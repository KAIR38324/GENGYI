using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Gengyi.Core;

namespace Gengyi.Cad.AutoCAD2024
{
    internal sealed class Cancelled : Exception { }
    internal static class CadGeometry
    {
        internal static Point3d P(double[] p) => new Point3d(p[0], p[1], p[2]);
        internal static Vector3d V(double[] p) => new Vector3d(p[0], p[1], p[2]);
        internal static double[] A(Point3d p) => new[] { p.X, p.Y, p.Z };
        internal static double[] A(Vector3d p) => new[] { p.X, p.Y, p.Z };
        internal static Matrix3d ToWorld(Frame frame) => Matrix3d.AlignCoordinateSystem(Point3d.Origin,
            Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis,P(frame.Origin),V(frame.XAxis),V(frame.YAxis),V(frame.XAxis).CrossProduct(V(frame.YAxis)))
            * Matrix3d.Scaling(1 / frame.MillimetersPerDrawingUnit, Point3d.Origin);
        internal static Point3d World(Frame f, V2 p) => P(f.ToWorld(p));
        internal static V2 Local(Frame f, Point3d p)
        {
            return f.ToLocal(A(p));
        }
        internal static Matrix3d SourceToStandard(BlockMapping mapping) =>
            Matrix3d.Scaling(mapping.MillimetersPerSourceUnit,Point3d.Origin) *
            Matrix3d.Rotation(-mapping.SourceAngleDegrees*Math.PI/180,Vector3d.ZAxis,Point3d.Origin) *
            Matrix3d.Displacement(new Vector3d(-mapping.SourceAnchor.X,-mapping.SourceAnchor.Y,0));
        internal static Matrix3d Transform(Room room, Placement item, BlockMapping mapping) =>
            ToWorld(room.Boundary!.Frame) * Matrix3d.Displacement(new Vector3d(item.Position.X,item.Position.Y,0)) *
            Matrix3d.Rotation(item.Rotation*Math.PI/180,Vector3d.ZAxis,Point3d.Origin) * SourceToStandard(mapping);
        internal static void ReadTransform(Room room, Placement item, BlockReference reference, BlockMapping mapping)
        {
            var m=ToWorld(room.Boundary!.Frame).Inverse()*reference.BlockTransform*SourceToStandard(mapping).Inverse();
            var origin=Point3d.Origin.TransformBy(m);
            var x=Vector3d.XAxis.TransformBy(m);var y=Vector3d.YAxis.TransformBy(m);
            item.Position=new V2(origin.X,origin.Y);
            item.Rotation=Gengyi.Core.Geometry.Normalize(Math.Atan2(x.Y,x.X)*180/Math.PI);
            item.ScaleX=x.Length;item.ScaleY=y.Length*(x.CrossProduct(y).Z<0?-1:1);
            if(Math.Abs(origin.Z)>.1 || Math.Abs(x.Z)>.00001 || Math.Abs(y.Z)>.00001)
            {item.NativeGeometryUnverified=true;item.NativeIssue="设备已移离或倾斜于房间平面，需恢复共面后重核。";}
        }
        internal static double Units(Database database,Editor editor)
        {
            if(database.Insunits!=UnitsValue.Undefined)
                return UnitsConverter.GetConversionFactor(database.Insunits,UnitsValue.Millimeters);
            var answer=editor.GetDouble(new PromptDoubleOptions("\n图纸未设单位。输入每图纸单位对应毫米（米=1000，英寸=25.4）：")
            {AllowNegative=false,AllowZero=false});
            if(answer.Status!=PromptStatus.OK)throw new Cancelled();
            return answer.Value;
        }
        internal static Point3d Point(Editor editor,string message,Point3d? basePoint=null)
        {
            var options=new PromptPointOptions("\n"+message);
            if(basePoint.HasValue){options.BasePoint=basePoint.Value;options.UseBasePoint=true;}
            var result=editor.GetPoint(options);
            if(result.Status!=PromptStatus.OK)throw new Cancelled();
            return result.Value;
        }
        internal static double Number(Editor editor,string message,double value,bool zero=false)
        {
            var r=editor.GetDouble(new PromptDoubleOptions("\n"+message){DefaultValue=value,UseDefaultValue=true,AllowNegative=false,AllowZero=zero});
            if(r.Status!=PromptStatus.OK)throw new Cancelled();return r.Value;
        }
        internal static string Keyword(Editor editor,string message,string choices,string defaultValue)
        {
            var options=new PromptKeywordOptions("\n"+message+" ["+choices.Replace(' ','/')+"]",choices){AllowNone=true};
            options.Keywords.Default=defaultValue;
            var answer=editor.GetKeywords(options);
            if(answer.Status==PromptStatus.Cancel)throw new Cancelled();
            return answer.Status==PromptStatus.None?defaultValue:answer.StringResult;
        }
        internal static List<Point3d> PolylinePoints(Polyline polyline)
        {
            if(!polyline.Closed || polyline.NumberOfVertices<3)throw new ArgumentException("请选择闭合多段线。");
            var result=new List<Point3d>();
            for(int i=0;i<polyline.NumberOfVertices;i++)
            {
                if(Math.Abs(polyline.GetBulgeAt(i))<1e-10)result.Add(polyline.GetPoint3dAt(i));
                else
                {
                    var arc=polyline.GetArcSegmentAt(i);
                    double angle=4*Math.Atan(Math.Abs(polyline.GetBulgeAt(i)));
                    double step=2*Math.Acos(Math.Max(-1,1-.05/arc.Radius));
                    int count=Math.Max(2,(int)Math.Ceiling(angle/Math.Max(1e-6,step)));
                    if(count>20000)throw new ArgumentException("弧形轮廓过于复杂，请选择实际设备占地轮廓。");
                    for(int n=0;n<count;n++)result.Add(polyline.GetPointAtParameter(i+(double)n/count));
                }
            }
            return result;
        }
        internal static Polyline Polyline(IEnumerable<V2> points,Frame frame,short color=3)
        {
            var result=new Polyline();int i=0;
            foreach(var p in points)result.AddVertexAt(i++,new Point2d(p.X,p.Y),0,0,0);
            result.Closed=true;result.ColorIndex=color;result.TransformBy(ToWorld(frame));return result;
        }
    }
}
