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

        private static IEnumerable<Candidate> Lockers(Room room,int target,Geo reserved,CancellationToken cancellation)
        {
            if(target==0){yield return new Candidate{Family="空房间"};yield break;}
            var boundary=room.Boundary!;
            var outline=Geometry.RoomPolygon(boundary);
            var solid=Geometry.Union(room.Obstacles.Concat(room.Closures).Select(Geometry.RegionFootprint));
            var walking=outline.Difference(solid);
            var storage=walking.Difference(reserved);
            double wallFront=Math.Max(room.Options.OperationDepthMm??room.Options.PlanningAisleMm,room.Options.WalkWidthMm+1);
            double aisle=Math.Max(wallFront,room.Options.PlanningAisleFor(room.Mode)), rear=room.Options.RearGapMm;
            int count=(target+11)/12;
            var equipment=Equipment.Get("LOCKER12");
            double length=equipment.Length, depth=equipment.Depth;

            foreach(bool vertical in new[]{false,true})
                foreach(bool reverse in new[]{false,true})
                {
                    double w=vertical?boundary.Height:boundary.Width,h=vertical?boundary.Width:boundary.Height;
                    var orient=vertical?new AffineTransformation(0,1,0,-1,0,boundary.Width):new AffineTransformation();
                    if(reverse)orient.Compose(new AffineTransformation(-1,0,w,0,-1,h));
                    var bodySpace=orient.Transform(storage);
                    var frontSpace=orient.Transform(walking);
                    var wall=orient.Transform(outline).Coordinates;
                    var wallLevels=wall.Zip(wall.Skip(1),
                        (a,b)=>new {A=a,B=b}).Where(edge=>Math.Abs(edge.A.Y-edge.B.Y)<1e-6)
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
                            "/"+(vertical?"竖向":"横向")+(reverse?" · 对侧起排":"")};
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
                                    if(pattern==LockerRows.Walls)occupied.Add((body,front));
                                    double y=reverse?h-row.Y-depth:row.Y;
                                    double left=reverse?w-x-length:x;
                                    bool up=row.Up!=reverse;
                                    candidate.Items.Add(up?Place(room,"LOCKER12",left+length,y+depth,180):Place(room,"LOCKER12",left,y));
                                }
                            if(candidate.Items.Count>=count)break;
                        }
                        if(vertical)RotateIntoBoundary(candidate,boundary.Width);
                        if(candidate.Items.Count>0)yield return candidate;
                    }
                }
        }
    }
}
