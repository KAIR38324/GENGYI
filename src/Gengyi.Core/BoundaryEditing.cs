using System;
using System.Collections.Generic;
using System.Linq;

namespace Gengyi.Core
{
    public static class BoundaryEditing
    {
        public static Boundary FromOutline(IReadOnlyList<V2> points,Frame frame,string nativeHandle="")
        {
            ValidateFrame(frame);
            var polygon=Geometry.Polygon(points);var extent=polygon.EnvelopeInternal;
            if(extent.Width<1 || extent.Height<1)throw new ArgumentException("房间轮廓外包尺寸须至少1mm。");
            var result=new Boundary{NativeHandle=nativeHandle,Width=extent.Width,Height=extent.Height,Frame=Serialization.Copy(frame)};
            result.Frame.Origin=frame.ToWorld(new V2(extent.MinX,extent.MinY));
            result.Outline=polygon.ExteriorRing.Coordinates.Take(polygon.ExteriorRing.NumPoints-1)
                .Select(p=>new V2(p.X-extent.MinX,p.Y-extent.MinY)).ToList();
            return result;
        }

        // A clockwise CAD polyline must not make existing equipment look mirrored.
        public static Boundary AlignOrientation(Boundary boundary, Frame? reference)
        {
            ValidateFrame(boundary.Frame);
            if(boundary.CurveToleranceMm<0 || !Finite(boundary.CurveToleranceMm) || boundary.SourceAreaMm2.HasValue &&
                (!Finite(boundary.SourceAreaMm2.Value) || boundary.SourceAreaMm2.Value<=Geometry.AreaTolerance))
                throw new ArgumentException("房间面积或曲线误差标定无效。");
            Boundary copy;
            if(boundary.Outline!=null)
            {
                copy=FromOutline(boundary.Outline,boundary.Frame,boundary.NativeHandle);
                copy.SourceAreaMm2=boundary.SourceAreaMm2;copy.CurveToleranceMm=boundary.CurveToleranceMm;
            }
            else
            {
                if(boundary.Width<1 || boundary.Height<1 || !Finite(boundary.Width) || !Finite(boundary.Height))
                    throw new ArgumentException("边界外包长宽必须至少 1 mm，且为有效数值。");
                copy=Serialization.Copy(boundary);
            }
            if(reference==null)return copy;
            ValidateFrame(reference);
            var normal=Normal(reference);var other=Normal(copy.Frame);
            double dot=normal.Select((v,i)=>v*other[i]).Sum();
            if(Math.Abs(Math.Abs(dot)-1)>1e-7)throw new ArgumentException("新边界与原房间平面不平行，不能保持现有设备和入口的位置。");
            reference.ToLocal(copy.Frame.Origin); // Reject a parallel plane at a different elevation, too.
            if(dot<0)
            {
                copy.Frame.Origin=copy.Frame.ToWorld(new V2(0,copy.Height));
                copy.Frame.YAxis=copy.Frame.YAxis.Select(v=>-v).ToArray();
                if(copy.Outline!=null)copy.Outline=copy.Outline.Select(p=>new V2(p.X,copy.Height-p.Y)).ToList();
            }
            return copy;
        }

        public static void Replace(Room room, Boundary replacement, bool discardCandidates=true)
        {
            var next=AlignOrientation(replacement,room.Boundary?.Frame);
            var previous=room.Boundary?.Frame;
            var points=new List<Action>();
            if(previous!=null)
            {
                V2 Map(V2 p)=>next.Frame.ToLocal(previous.ToWorld(p));
                var items=room.Placed.Concat(discardCandidates?Enumerable.Empty<Placement>():room.Candidates.SelectMany(c=>c.Items));
                foreach(var item in items)
                {
                    var position=Map(item.Position);
                    double angle=item.Rotation*Math.PI/180;
                    var axis=Map(item.Position+new V2(Math.Cos(angle),Math.Sin(angle)))-position;
                    double rotation=Geometry.Normalize(Math.Atan2(axis.Y,axis.X)*180/Math.PI);
                    points.Add(()=>{item.Position=position;item.Rotation=rotation;});
                }
                foreach(var region in room.Obstacles.Concat(room.Closures))
                {
                    var mapped=region.Points.Select(Map).ToList();
                    points.Add(()=>region.Points=mapped);
                }
                var dirty=room.DirtySeed==null?null:Map(room.DirtySeed);
                var clean=room.CleanSeed==null?null:Map(room.CleanSeed);
                points.Add(()=>{room.DirtySeed=dirty;room.CleanSeed=clean;});
            }
            else if(room.Placed.Count>0)throw new ArgumentException("原房间缺少坐标基准，不能自动重定位已有设备。");
            // Validate and prepare every conversion before changing any room state.
            foreach(var apply in points)apply();
            room.Boundary=next;room.Stale=true;room.ConstraintIssue="";
            if(discardCandidates){room.Candidates.Clear();room.SelectedCandidate=0;room.PreviewVisible=false;}
        }

        private static bool Finite(double value)=>!double.IsNaN(value) && !double.IsInfinity(value);
        private static double[] Normal(Frame frame)=>new[]{frame.XAxis[1]*frame.YAxis[2]-frame.XAxis[2]*frame.YAxis[1],
            frame.XAxis[2]*frame.YAxis[0]-frame.XAxis[0]*frame.YAxis[2],frame.XAxis[0]*frame.YAxis[1]-frame.XAxis[1]*frame.YAxis[0]};
        private static void ValidateFrame(Frame frame)
        {
            if(frame.Origin.Length!=3 || frame.XAxis.Length!=3 || frame.YAxis.Length!=3 ||
                !frame.Origin.Concat(frame.XAxis).Concat(frame.YAxis).All(Finite) ||
                !Finite(frame.MillimetersPerDrawingUnit) || frame.MillimetersPerDrawingUnit<=0 ||
                Math.Abs(frame.XAxis.Sum(v=>v*v)-1)>1e-7 || Math.Abs(frame.YAxis.Sum(v=>v*v)-1)>1e-7 ||
                Math.Abs(frame.XAxis.Select((v,i)=>v*frame.YAxis[i]).Sum())>1e-7)
                throw new ArgumentException("房间坐标和单位标定无效。");
        }
    }
}
