using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using Geo = NetTopologySuite.Geometries.Geometry;

namespace Gengyi.Core
{
    public static class Geometry
    {
        public static readonly GeometryFactory Factory = new GeometryFactory();
        public const double AreaTolerance = .01; // mm², arithmetic tolerance, not a construction gap.
        public static Polygon Polygon(IEnumerable<V2> points)
        {
            var p = points.Select(v =>
            {
                if (double.IsNaN(v.X) || double.IsInfinity(v.X) || double.IsNaN(v.Y) || double.IsInfinity(v.Y) || Math.Abs(v.X)>1e9 || Math.Abs(v.Y)>1e9)
                    throw new ArgumentException("轮廓坐标无效或超出计算范围。");
                return new Coordinate(Math.Round(v.X,6), Math.Round(v.Y,6));
            }).ToList();
            if (p.Count < 3) throw new ArgumentException("闭合轮廓至少需要三个顶点。");
            if (!p[0].Equals2D(p[p.Count - 1])) p.Add(p[0].Copy());
            var result = Factory.CreatePolygon(p.ToArray());
            if (!result.IsValid || result.Area <= AreaTolerance) throw new ArgumentException("轮廓自交或面积为零。");
            return result;
        }
        public static List<V2> Rect(double x, double y, double w, double h) => new List<V2>
        { new V2(x,y),new V2(x+w,y),new V2(x+w,y+h),new V2(x,y+h) };
        public static List<V2> Outline(Placement item)
        {
            if (item.PhysicalOutline != null && item.PhysicalOutline.Count >= 3)
                return item.PhysicalOutline.Select(item.Transform).ToList();
            var e = item.Equipment;
            if (!e.IsCorner)
            {
                if (e.Length <= 0 || e.Depth <= 0) throw new ArgumentException(e.Id + "缺少真实占地轮廓，不能虚构尺寸。");
                return Rect(0, 0, e.Length, e.Depth).Select(item.Transform).ToList();
            }
            // Current R650 quarter sector; 64 chords, maximum chord error <0.049 mm.
            // Legacy R600 keeps its original radius when reopening an older drawing.
            // This is an explicit reconstruction assumption, never the original CAD contour.
            var points = new List<V2> { item.Transform(new V2()) };
            for (int i = 0; i <= 64; i++)
                points.Add(item.Transform(new V2(e.Length * Math.Cos(i * Math.PI / 128), e.Length * Math.Sin(i * Math.PI / 128))));
            return points;
        }
        public static Geo Footprint(Placement item) => Polygon(Outline(item));
        public static Geo Union(IEnumerable<Geo> shapes)
        {
            var list = shapes.ToList();
            return list.Count == 0 ? Factory.CreatePolygon() : UnaryUnionOp.Union(list);
        }
        public static List<V2> RoomOutline(Boundary b) => b.Outline==null?Rect(0,0,b.Width,b.Height):
            b.Outline.Select(p=>new V2(p.X,p.Y)).ToList();
        public static Geo RoomPolygon(Boundary b) => Polygon(RoomOutline(b));
        public static Geo RegionFootprint(Region region)
        {
            if(double.IsNaN(region.CurveToleranceMm) || double.IsInfinity(region.CurveToleranceMm) || region.CurveToleranceMm<0)
                throw new ArgumentException("现场曲线轮廓的误差标定无效。");
            var shape=Polygon(region.Points);
            return region.CurveToleranceMm==0?shape:shape.Buffer(region.CurveToleranceMm/Math.Cos(Math.PI/64),16);
        }
        public static List<Polygon> Components(Geo free)
        {
            var output = new List<Polygon>();
            if (free is Polygon p && !p.IsEmpty) output.Add(p);
            else for (int i = 0; i < free.NumGeometries; i++)
                if (!ReferenceEquals(free.GetGeometryN(i), free)) output.AddRange(Components(free.GetGeometryN(i)));
            return output;
        }
        public static int Component(IReadOnlyList<Polygon> components, V2 p)
        {
            var point = Factory.CreatePoint(new Coordinate(p.X, p.Y));
            for (int i = 0; i < components.Count; i++) if (components[i].Contains(point)) return i;
            return -1;
        }
        public static bool Orthogonal(double degrees) => Math.Abs(degrees / 90 - Math.Round(degrees / 90)) < 1e-7;
        public static double Normalize(double degrees) => (degrees % 360 + 360) % 360;
    }

    public static class Capacity
    {
        public static List<V2> Hooks(Placement item, LayoutOptions options)
        {
            if (item.LocalHooks != null) return item.LocalHooks.Select(item.Transform).ToList();
            if (item.EquipmentId != "HANGER_CUSTOM" || item.Length <= 0 ||
                item.Length < options.HookStartMarginMm + options.HookEndMarginMm) return new List<V2>();
            options.Validate();
            var result = new List<V2>();
            for (double x = options.HookStartMarginMm; x <= item.Length - options.HookEndMarginMm + 1e-8; x += options.HookPitchMm)
                result.Add(item.Transform(new V2(x, 225)));
            return result;
        }
        public static int Nominal(Placement item, LayoutOptions options) => item.EquipmentId == "HANGER_CUSTOM"
            ? Hooks(item, options).Count : item.Equipment.Capacity;
        public static List<int>? Run(double length, IReadOnlyCollection<int> allowed)
        {
            if (length < 0 || Math.Abs(length / 250 - Math.Round(length / 250)) > 1e-7) return null;
            int end = (int)Math.Round(length / 250);
            var paths = new Dictionary<int, List<int>> { [0] = new List<int>() };
            for (int n = 1; n <= end; n++)
                foreach (int cap in allowed.OrderByDescending(c => c))
                {
                    int size = cap / 3;
                    if (!paths.TryGetValue(n - size, out var prev)) continue;
                    var next = new List<int>(prev) { cap };
                    if (!paths.TryGetValue(n, out var current) || next.Count < current.Count) paths[n] = next;
                }
            return paths.TryGetValue(end, out var result) ? result : null;
        }
    }
}
