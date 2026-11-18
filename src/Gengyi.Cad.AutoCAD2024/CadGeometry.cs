using System;
using System.Collections.Generic;
using System.Linq;
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
        internal static Frame UcsFrame(Matrix3d ucs,double units)=>new Frame{Origin=A(ucs.CoordinateSystem3d.Origin),
            XAxis=A(ucs.CoordinateSystem3d.Xaxis.GetNormal()),YAxis=A(ucs.CoordinateSystem3d.Yaxis.GetNormal()),MillimetersPerDrawingUnit=units};
        internal static Boundary ReadBoundary(Polyline pl,double units,Frame? reference=null)
        {
            if(!pl.Closed || pl.NumberOfVertices<3)throw new ArgumentException("请选择闭合的房间多段线，至少三个顶点；开口轮廓不能作为房间边界。");
            if(units<=0 || double.IsNaN(units) || double.IsInfinity(units))throw new ArgumentException("房间单位标定无效。");
            var world=PolylinePoints(pl,.05/units);var origin=world[0];var normal=pl.Normal.GetNormal();
            var x=world.Skip(1).Select(p=>p-origin).FirstOrDefault(v=>v.Length>1e-8);
            if(x.Length<=1e-8)throw new ArgumentException("房间边界不能退化成一个点。");
            x=x.GetNormal();var y=normal.CrossProduct(x).GetNormal();
            if(reference!=null)
            {
                var rx=V(reference.XAxis);var ry=V(reference.YAxis);
                if(Math.Abs(Math.Abs(rx.CrossProduct(ry).GetNormal().DotProduct(normal))-1)<1e-7)
                {x=rx;y=ry;}
            }
            var frame=new Frame{Origin=A(origin),XAxis=A(x),YAxis=A(y),MillimetersPerDrawingUnit=units};
            var points=world.Select(p=>Local(frame,p)).ToList();
            var polygon=Gengyi.Core.Geometry.Polygon(points); // Reject self-intersections before any offset.
            bool curved=Enumerable.Range(0,pl.NumberOfVertices).Any(i=>Math.Abs(pl.GetBulgeAt(i))>1e-10);
            if(curved)
            {
                // Chords of re-entrant arcs may extend beyond the real room. Offset inward
                // by at least their maximum error, including the buffer's arc approximation.
                const int quadrants=16;
                double inset=.05/Math.Cos(Math.PI/(4*quadrants));
                var usable=polygon.Buffer(-inset,quadrants);
                if(!(usable is NetTopologySuite.Geometries.Polygon single) || single.IsEmpty || single.NumInteriorRings>0)
                    throw new ArgumentException("弧边按0.05mm误差保守内缩后无法形成单个有效房间，请检查过窄或自接触的轮廓。");
                if(single.ExteriorRing.NumPoints>20001)throw new ArgumentException("弧边保守轮廓超过20000点，请简化多段线后重试。");
                points=single.ExteriorRing.Coordinates.Take(single.ExteriorRing.NumPoints-1).Select(p=>new V2(p.X,p.Y)).ToList();
            }
            var boundary=BoundaryEditing.FromOutline(points,frame,pl.Handle.ToString());
            boundary.SourceAreaMm2=Math.Abs(pl.Area)*units*units;boundary.CurveToleranceMm=curved?.05:0;
            return boundary;
        }
        // Kept for older command callers; actual validation now uses the complete room outline.
        internal static Boundary ReadRectangle(Polyline pl,double units,Frame? reference=null)=>ReadBoundary(pl,units,reference);
        internal static bool SameBoundary(Boundary a,Boundary b)
        {
            if(Math.Abs(a.Width-b.Width)>1e-5 || Math.Abs(a.Height-b.Height)>1e-5 ||
                Math.Abs(a.AreaM2-b.AreaM2)>1e-8 || Math.Abs(a.CurveToleranceMm-b.CurveToleranceMm)>1e-8 ||
                Math.Abs(a.Frame.MillimetersPerDrawingUnit-b.Frame.MillimetersPerDrawingUnit)>1e-10)return false;
            if(Enumerable.Range(0,3).Any(i=>Math.Abs(a.Frame.Origin[i]-b.Frame.Origin[i])*a.Frame.MillimetersPerDrawingUnit>1e-5 ||
                Math.Abs(a.Frame.XAxis[i]-b.Frame.XAxis[i])>1e-9 || Math.Abs(a.Frame.YAxis[i]-b.Frame.YAxis[i])>1e-9))return false;
            var pa=Gengyi.Core.Geometry.RoomOutline(a);var pb=Gengyi.Core.Geometry.RoomOutline(b);
            return pa.Count==pb.Count && !pa.Where((p,i)=>p.Distance(pb[i])>1e-5).Any();
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
        internal static List<Point3d> PolylinePoints(Polyline polyline,double maximumChordError=.05)
        {
            if(!polyline.Closed || polyline.NumberOfVertices<3)throw new ArgumentException("请选择闭合多段线。");
            if(maximumChordError<=0 || double.IsNaN(maximumChordError) || double.IsInfinity(maximumChordError))throw new ArgumentException("轮廓离散误差必须是有效正数。");
            const int maxPoints=20000;
            if(polyline.NumberOfVertices>maxPoints)throw new ArgumentException("轮廓超过20000个顶点，请简化多段线后重试。");
            var result=new List<Point3d>();
            for(int i=0;i<polyline.NumberOfVertices;i++)
            {
                if(Math.Abs(polyline.GetBulgeAt(i))<1e-10)result.Add(polyline.GetPoint3dAt(i));
                else
                {
                    var arc=polyline.GetArcSegmentAt(i);
                    double angle=4*Math.Atan(Math.Abs(polyline.GetBulgeAt(i)));
                    double step=2*Math.Acos(Math.Max(-1,1-maximumChordError/arc.Radius));
                    int count=Math.Max(2,(int)Math.Ceiling(angle/Math.Max(1e-6,step)));
                    if((long)result.Count+count>maxPoints)throw new ArgumentException("轮廓离散后超过20000点，请简化多段线后重试。");
                    for(int n=0;n<count;n++)result.Add(polyline.GetPointAtParameter(i+(double)n/count));
                }
                if(result.Count>maxPoints)throw new ArgumentException("轮廓离散后超过20000点，请简化多段线后重试。");
            }
            return result;
        }
        internal static Polyline PreviewPolyline(Database database,IEnumerable<V2> points,Frame frame,short color)
        {
            var result=new Polyline();
            try
            {
                result.SetDatabaseDefaults(database);
                result.Layer="0";result.Linetype="Continuous";result.ColorIndex=color;
                int i=0;
                foreach(var p in points)result.AddVertexAt(i++,new Point2d(p.X,p.Y),0,0,0);
                result.Closed=true;result.TransformBy(ToWorld(frame));return result;
            }
            catch{result.Dispose();throw;}
        }
    }
}
