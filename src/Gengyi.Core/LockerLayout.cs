using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries.Utilities;
using Geo=NetTopologySuite.Geometries.Geometry;

namespace Gengyi.Core
{
    public static partial class Layout
    {
        private enum LockerRows { Walls, Paired, SingleFacing }

        internal static double[] LockerAngles(Boundary boundary)
        {
            var outline=Geometry.RoomOutline(boundary);
            return new[]{0.0}.Concat(outline.Select((p,i)=>outline[(i+1)%outline.Count]-p)
                .Where(segment=>segment.Distance(new V2())>=Equipment.Get("LOCKER12").Length)
                .OrderByDescending(segment=>segment.Distance(new V2()))
                .Select(segment=>Heading(segment)%90)).Select(angle=>Math.Round(angle,8)%90)
                .Distinct().Take(12).SelectMany(angle=>new[]{angle,angle+90}).ToArray();
        }

        private static IEnumerable<Candidate> Lockers(Room room,int target,Geo reserved,CancellationToken cancellation)
        {
            if(target==0){yield return new Candidate{Family="空房间"};yield break;}
            var boundary=room.Boundary!;
            var outline=Geometry.RoomPolygon(boundary);
            var solid=Geometry.Union(room.Obstacles.Concat(room.Closures).Select(Geometry.RegionFootprint));
            var walking=outline.Difference(solid);
            var storage=walking.Difference(reserved);
            bool hasEntry=room.Doors.Count>0 || room.DirtySeed!=null;
            var access=hasEntry?RoomAccess.WalkingSpace(outline,solid,room.Options.WalkWidthMm):
                new List<NetTopologySuite.Geometries.Polygon>();
            var entryComponents=new HashSet<int>();
            foreach(var zone in RoomAccess.Clearances(room,new List<Diagnostic>()).Where(z=>z.Door!=null))
                if(RoomAccess.TryOpening(solid,access,zone.Door!,zone.Shape,zone.Probe!,room.Options.WalkWidthMm,out int c))
                    entryComponents.Add(c);
            if(room.Doors.Count==0 && room.DirtySeed!=null)
                entryComponents.Add(Geometry.Component(access,room.DirtySeed));
            entryComponents.Remove(-1);
            double wallFront=Math.Max(room.Options.OperationDepthMm??room.Options.PlanningAisleMm,room.Options.WalkWidthMm+1);
            double aisle=Math.Max(wallFront,room.Options.PlanningAisleFor(room.Mode)), rear=room.Options.RearGapMm;
            int count=(target+11)/12;
            var equipment=Equipment.Get("LOCKER12");
            double length=equipment.Length, depth=equipment.Depth;

            var angleWallRows=new List<List<Placement>>();
            foreach(double angle in LockerAngles(boundary))
                foreach(bool reverse in new[]{false,true})
                {
                    var orient=AffineTransformation.RotationInstance(-angle*Math.PI/180);
                    var bounds=orient.Transform(outline).EnvelopeInternal;
                    double w=bounds.Width,h=bounds.Height;
                    orient.Translate(-bounds.MinX,-bounds.MinY);
                    if(reverse)orient.Compose(new AffineTransformation(-1,0,w,0,-1,h));
                    var inverse=orient.GetInverse();
                    Placement IntoRoom(double x,double y,bool up)
                    {
                        var point=new NetTopologySuite.Geometries.Coordinate();
                        inverse.Transform(new NetTopologySuite.Geometries.Coordinate(up?x+length:x,up?y+depth:y),point);
                        return Place(room,"LOCKER12",Math.Round(point.X,6),Math.Round(point.Y,6),
                            Geometry.Normalize(angle+(reverse?180:0)+(up?180:0)));
                    }
                    var bodySpace=orient.Transform(storage);
                    var frontSpace=orient.Transform(walking);
                    if(!Geometry.Orthogonal(angle))
                    {
                        // Rotating coordinates into a wall frame can leave sub-micron slivers.
                        bodySpace=bodySpace.Buffer(.00001,1);
                        frontSpace=frontSpace.Buffer(.00001,1);
                    }
                    var wall=orient.Transform(outline).Coordinates;
                    var wallLevels=wall.Zip(wall.Skip(1),
                        (a,b)=>new {A=a,B=b}).Where(edge=>Math.Abs(edge.A.Y-edge.B.Y)<.00001)
                        .Select(edge=>Math.Round(edge.A.Y,6)).Distinct().OrderBy(y=>y).Take(48).ToArray();
                    var runs=new Dictionary<(double Y,bool Up,double Front),(double Start,double End)[]>();
                    (double Start,double End)[] Runs(double y,bool up,double front)
                    {
                        if(runs.TryGetValue((y,up,front),out var cached))return cached;
                        cancellation.ThrowIfCancellationRequested();
                        var bodyBand=Geometry.Polygon(Geometry.Rect(0,y,w,depth));
                        var frontBand=Geometry.Polygon(Geometry.Rect(0,up?y+depth:y-front,w,front));
                        var gaps=Geometry.Components(bodyBand.Difference(bodySpace)).Concat(Geometry.Components(frontBand.Difference(frontSpace)))
                            .Select(p=>p.EnvelopeInternal).OrderBy(e=>e.MinX);
                        double x=0;
                        var available=new List<(double,double)>();
                        foreach(var gap in gaps)
                        {
                            if(gap.MinX-x+1e-6>=length)available.Add((x,gap.MinX));
                            x=Math.Max(x,gap.MaxX);
                        }
                        if(w-x+1e-6>=length)available.Add((x,w));
                        return runs[(y,up,front)]=available.ToArray();
                    }
                    foreach(var pattern in new[]{LockerRows.Walls,LockerRows.Paired,LockerRows.SingleFacing})
                    {
                        var candidate=new Candidate{Family=(pattern==LockerRows.Walls?"沿墙柜排":pattern==LockerRows.Paired?"沿墙与背靠背柜排":"沿墙与单面柜排")+
                            "/"+(angle==0?"横向":angle==90?"竖向":$"{angle:0.#}°")+(reverse?" · 对侧起排":"")};
                        var occupied=new List<(Geo Body,Geo Front)>();
                        var rows=new List<(double Y,bool Up)>();
                        if(pattern==LockerRows.Walls)
                        {
                            rows.AddRange(wallLevels.Where(y=>y+rear+depth+wallFront<=h+1e-6).Select(y=>(y+rear,true)));
                            rows.AddRange(wallLevels.Reverse().Where(y=>y-rear-depth-wallFront>=-1e-6).Select(y=>(y-rear-depth,false)));
                        }
                        else
                        {
                            double step=pattern==LockerRows.Paired?2*depth+aisle+rear:depth+aisle;
                            for(double y=rear;y+depth+aisle<=h+1e-6 && rows.Count<200;y+=step)
                            {
                                rows.Add((y,true));
                                if(pattern==LockerRows.Paired && y+2*depth+aisle<=h+1e-6)rows.Add((y+depth+aisle,false));
                            }
                        }
                        double endGap=pattern!=LockerRows.Walls && rows.Count>2?aisle:0;
                        bool Intersects(Geo a,Geo b)=>a.EnvelopeInternal.Intersects(b.EnvelopeInternal) && a.Intersection(b).Area>Geometry.AreaTolerance;
                        foreach(var row in rows)
                        {
                            foreach(var run in Runs(row.Y,row.Up,pattern==LockerRows.Walls?wallFront:aisle))
                                for(double x=run.Start;x+length<=Math.Min(w-endGap,run.End)+1e-6 && candidate.Items.Count<count;x+=length)
                                {
                                    var body=Geometry.Polygon(Geometry.Rect(x,row.Y,length,depth));
                                    var front=Geometry.Polygon(Geometry.Rect(x,row.Up?row.Y+depth:row.Y-aisle,length,aisle));
                                    if(pattern==LockerRows.Walls && occupied.Any(p=>Intersects(body,p.Body) || Intersects(body,p.Front) || Intersects(front,p.Body)))continue;
                                    var item=IntoRoom(x,row.Y,row.Up);
                                    double operating=room.Options.OperationDepthMm??room.Options.PlanningAisleMm;
                                    if(hasEntry && !Validation.Approach(item,0,length,false,operating,room.Options.WalkWidthMm,access)
                                        .Overlaps(entryComponents))continue;
                                    if(pattern==LockerRows.Walls)occupied.Add((body,front));
                                    candidate.Items.Add(item);
                                }
                            if(candidate.Items.Count>=count)break;
                        }
                        if(candidate.Items.Count>0)
                        {
                            if(pattern==LockerRows.Walls)angleWallRows.Add(candidate.Items);
                            yield return candidate;
                        }
                    }
                }
            var mixed=new Candidate{Family="组合墙向柜排"};
            var occupiedMixed=new List<(Geo Body,Geo Front)>();
            foreach(var item in angleWallRows.OrderByDescending(row=>row.Count).SelectMany(row=>row))
            {
                var body=Geometry.Footprint(item);
                var front=Geometry.Polygon(Geometry.Rect(.1,-aisle,length-.2,aisle-.1).Select(item.Transform));
                bool Hits(Geo a,Geo b)=>a.EnvelopeInternal.Intersects(b.EnvelopeInternal) && a.Intersection(b).Area>Geometry.AreaTolerance;
                if(occupiedMixed.Any(p=>Hits(body,p.Body) || Hits(body,p.Front) || Hits(front,p.Body)))continue;
                occupiedMixed.Add((body,front));mixed.Items.Add(item);
                if(mixed.Items.Count>=count)break;
            }
            if(mixed.Items.Count>0 && mixed.Items.Select(item=>Math.Round(item.Rotation%180,6)).Distinct().Count()>1)
                yield return mixed;
        }
    }
}
