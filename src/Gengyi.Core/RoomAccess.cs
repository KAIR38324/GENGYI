using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using Geo = NetTopologySuite.Geometries.Geometry;

namespace Gengyi.Core
{
    public static class RoomAccess
    {
        public const string ClearanceCheck = "passage_clearance";
        public const string EntryCheck = "entry_reachability";

        public static List<V2> Landing(Boundary boundary, DoorOpening door, double walkWidth)
        {
            var a = door.Start;
            var b = door.End;
            var room = Geometry.RoomPolygon(boundary);
            var opening = Geometry.Factory.CreateLineString(new[] { new Coordinate(a.X,a.Y), new Coordinate(b.X,b.Y) });
            double tolerance = .001+2*boundary.CurveToleranceMm;
            double width = opening.Length;
            if (width < 1 || !room.Boundary.Buffer(tolerance).Covers(opening))
                throw new ArgumentException("请在同一段直墙上选取两个门边点。");
            var normal = new V2(-(b.Y-a.Y)/width,(b.X-a.X)/width);
            // The opening remains full width. Extend it beyond the wall inset used for walking.
            double depth = walkWidth+2;
            var covered = room.Buffer(tolerance);
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                var inset = normal*(sign*depth);
                var points = new List<V2> { a,b,b+inset,a+inset };
                if (covered.Covers(Geometry.Polygon(points))) return points;
            }
            throw new ArgumentException("该门的室内接入区超出房间边界，请核对门边点和门内空间。");
        }

        internal static List<(string Label, DoorOpening? Door, Geo Shape, V2? Probe)> Clearances(Room room, List<Diagnostic> issues)
        {
            var zones = new List<(string,DoorOpening?,Geo,V2?)>();
            var boundary = room.Boundary!;
            for (int i=0; i<room.Doors.Count; i++)
            {
                string name = "出入口 " + (i+1);
                var door = room.Doors[i];
                try
                {
                    var points = Landing(boundary,door,room.Options.WalkWidthMm);
                    var middle=(points[0]+points[1])*.5;
                    var inward=(points[2]+points[3])*.5-middle;
                    var probe=middle+inward*((room.Options.WalkWidthMm/2+.5)/inward.Distance(new V2()));
                    zones.Add((name,door,Geometry.Polygon(points),probe));
                }
                catch (ArgumentException ex)
                {
                    issues.Add(new Diagnostic { Code="DOOR_INVALID", Message=name+"："+ex.Message });
                }
            }
            for (int i=0; i<room.Passages.Count; i++)
            {
                string name = "通行保留区 " + (i+1);
                var region = room.Passages[i];
                try
                {
                    var shape=Geometry.RegionFootprint(region);
                    if (!Geometry.RoomPolygon(boundary).Buffer(.001+2*boundary.CurveToleranceMm).Covers(Geometry.Polygon(region.Points)))
                        throw new ArgumentException("所选轮廓超出房间边界，请重新设置。");
                    zones.Add((name,null,shape,null));
                }
                catch (ArgumentException ex)
                {
                    issues.Add(new Diagnostic { Code="PASSAGE_INVALID", Message=name+"："+ex.Message });
                }
            }
            return zones;
        }

        internal static List<Polygon> WalkingSpace(Geo boundary,Geo blockers,double width) =>
            Geometry.Components(boundary.Buffer(-width/2,32).Difference(blockers.Buffer(width/2,32)));

        internal static bool TryOpening(Geo blockers,IReadOnlyList<Polygon> walking,DoorOpening door,Geo zone,V2 probe,
            double width,out int component)
        {
            component=Geometry.Component(walking,probe);
            return door.Start.Distance(door.End)+.000001>=width && component>=0 &&
                zone.Intersection(blockers).Area<=Geometry.AreaTolerance &&
                walking[component].Difference(zone).Area>Geometry.AreaTolerance;
        }

        internal static Geo ReservedSpace(Room room)
        {
            var issues = new List<Diagnostic>();
            var zones = Clearances(room,issues);
            if (issues.Count>0) throw new ArgumentException(issues[0].Message);
            return Geometry.Union(zones.Select(zone=>zone.Shape));
        }

        public static void RequireValid(Statistics stats)
        {
            if (Failed(stats)) throw new ArgumentException(stats.Issues.First(i=>i.Code.StartsWith("DOOR_",StringComparison.Ordinal) ||
                i.Code.StartsWith("PASSAGE_",StringComparison.Ordinal)).Message);
        }

        public static bool Failed(Statistics stats) =>
            stats.Checks.TryGetValue(ClearanceCheck,out var clearance) && clearance==CheckState.Fail;

        public static void RejectNewViolations(Room room,IReadOnlyList<Placement> before,IReadOnlyList<Placement> after)
        {
            if(room.Doors.Count==0 && room.Passages.Count==0)return;
            var zones=Clearances(room,new List<Diagnostic>());
            var previous=before.ToDictionary(item=>item.Id);
            foreach(var item in after)
            {
                Placement? old;
                try
                {
                    var shape=Geometry.Footprint(item);
                    foreach(var zone in zones)
                    {
                        double overlap=shape.Intersection(zone.Shape).Area;
                        if(overlap<=Geometry.AreaTolerance)continue;
                        if(previous.TryGetValue(item.Id,out old) && overlap<=Geometry.Footprint(old).Intersection(zone.Shape).Area+Geometry.AreaTolerance)continue;
                        throw new ArgumentException("设备占用"+zone.Label+"，请保留通行净空。");
                    }
                }
                catch(ArgumentException) when(item.Equipment.Length<=0 && item.PhysicalOutline==null)
                { /* The main geometry check reports equipment with missing footprints. */ }
            }
        }
    }
}
