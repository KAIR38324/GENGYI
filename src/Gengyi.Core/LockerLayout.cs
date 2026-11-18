using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private static bool LockerSceneValid(Room room,Statistics stats) =>
            !RoomAccess.Failed(stats) && stats.Checks["geometry"]==CheckState.Pass &&
            stats.Checks["boundary"]==CheckState.Pass && stats.Checks[LockerAisles.CheckKey]==CheckState.Pass &&
            (room.DirtySeed==null || stats.Checks[RoomAccess.EntryCheck]!=CheckState.Fail);

        private static SearchResult SearchLockers(Project project,Room room,int target,Geo reserved,bool supplement,CancellationToken cancellation)
        {
            var result=new SearchResult();
            var clock=Stopwatch.StartNew();
            var existing=supplement?room.Placed:new List<Placement>();
            var baseline=Validation.Evaluate(project,room,existing);
            if(!LockerSceneValid(room,baseline))
            {
                var issue=baseline.Issues.FirstOrDefault(i=>i.Code.StartsWith("DOOR_",StringComparison.Ordinal) ||
                    i.Code.StartsWith("PASSAGE_",StringComparison.Ordinal) || i.Code=="LOCKER_AISLE_CLEARANCE");
                result.Message=(supplement?"请先修正已入图设备或出入口。":"请先修正通行条件。")+
                    (issue?.Message??"请检查设备几何、边界及入口位置。");
                return result;
            }
            if(baseline.GeometricAvailable>=target)
            {
                if(!supplement)result.Candidates.Add(new Candidate{Family="空房间",Statistics=baseline});
                result.Message=supplement?$"已入图设备有 {baseline.GeometricAvailable} 个几何可用人位，已达到 {target} 人位目标。":"当前房间目标为 0 人位。";
                return result;
            }

            var equipment=Equipment.Get("LOCKER12");
            int needed=(int)Math.Ceiling(baseline.ConditionalDeficit/(double)equipment.Capacity);
            var pools=new List<Candidate>();
            var seen=new HashSet<string>();
            bool Full() => clock.ElapsedMilliseconds>=10000;
            foreach(var pool in LockerPools(room,existing,reserved,needed,cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                result.Enumerated++;
                if(seen.Add(Signature(pool.Items,true)))pools.Add(pool);
                if(clock.ElapsedMilliseconds>=3500){result.TimedOut=true;break;}
            }

            var checkedPlans=new HashSet<string>();
            Statistics Evaluate(List<Placement> added)
            {
                cancellation.ThrowIfCancellationRequested();
                return Validation.Evaluate(project,room,existing.Concat(added).ToList());
            }
            bool Preserves(Statistics stats) => LockerSceneValid(room,stats) && LockerAisles.PreservesCapacity(baseline,stats);

            // Give each direction and row family a turn before checking its alternative offsets.
            var groups=pools.OrderByDescending(p=>Math.Min(needed,p.Items.Count)).GroupBy(p=>p.Family).ToList();
            for(int pass=0;pass<3;pass++)
            {
                foreach(var group in groups)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(Full() || checkedPlans.Count>=32){result.TimedOut=true;break;}
                    var pool=group.Skip(pass).FirstOrDefault();
                    if(pool==null)continue;
                    foreach(var item in pool.Items)ResolveMapping(project,item);
                    var items=pool.Items.Take(needed).ToList();
                    if(!checkedPlans.Add(Signature(items,true)))continue;
                    var stats=Evaluate(items);
                    if(!Preserves(stats) || items.Any(item=>stats.GeometricByInstance[item.Id]==0))
                    {
                        var blocked=new HashSet<string>(stats.Issues.Where(i=>i.Code=="DOOR_FRONT_UNREACHABLE").Select(i=>i.InstanceId));
                        items.RemoveAll(item=>blocked.Contains(item.Id) || stats.GeometricByInstance[item.Id]==0);
                        stats=Evaluate(items);
                        // A dense first attempt can disconnect the door or reduce capacity in an installed row.
                        // Restart from the valid base and grow the same row pattern with access checks.
                        if(!Preserves(stats)){items.Clear();stats=baseline;}
                    }
                    if(stats.ConditionalDeficit>0 && !Full())
                    {
                        var remaining=pool.Items.Skip(needed).Concat(pool.Items.Take(needed))
                            .Where(item=>!items.Contains(item)).ToList();
                        int extra=(int)Math.Ceiling(stats.ConditionalDeficit/(double)equipment.Capacity);
                        var batch=items.Concat(remaining.Take(extra)).ToList();
                        if(batch.Count>items.Count)
                        {
                            var combined=Evaluate(batch);
                            if(Preserves(combined) && batch.All(item=>combined.GeometricByInstance[item.Id]>0) &&
                                combined.GeometricAvailable>stats.GeometricAvailable)
                            {items=batch;stats=combined;}
                        }
                        foreach(var item in remaining.Where(item=>!items.Contains(item)).Take(8))
                        {
                            if(Full() || stats.ConditionalDeficit==0)break;
                            items.Add(item);
                            var combined=Evaluate(items);
                            if(Preserves(combined) && combined.GeometricByInstance[item.Id]>0 &&
                                combined.GeometricAvailable>stats.GeometricAvailable)
                                stats=combined;
                            else items.RemoveAt(items.Count-1);
                        }
                    }
                    if(items.Count>0 && stats.GeometricAvailable>baseline.GeometricAvailable)
                        result.Candidates.Add(new Candidate{Family=pool.Family,Items=items,Statistics=stats,AppendToPlaced=supplement});
                }
                if(Full() || checkedPlans.Count>=32)break;
            }

            result.Candidates=result.Candidates.OrderBy(c=>c.Statistics.ConditionalDeficit)
                .ThenBy(c=>c.Statistics.Blocked).ThenBy(c=>c.Statistics.NominalSurplus).ThenBy(c=>c.Items.Count)
                .GroupBy(c=>Signature(c.Items,true)).Select(g=>g.First()).Take(8).ToList();
            if(result.Candidates.Count==0)
                result.Message=$"在当前边界、障碍和通行条件下，未找到可增加的人位。当前条件缺口 {baseline.ConditionalDeficit} 人位。";
            else
            {
                var first=result.Candidates[0];
                result.Message=supplement
                    ?$"找到 {result.Candidates.Count} 个补排方案。首选新增 {first.Items.Count} 柜，增加 {first.Statistics.GeometricAvailable-baseline.GeometricAvailable} 个几何可用人位，条件缺口 {first.Statistics.ConditionalDeficit}。"
                    :$"找到 {result.Candidates.Count} 个一更预排方案。首选 {first.Items.Count} 柜，几何可用 {first.Statistics.GeometricAvailable} 人位，条件缺口 {first.Statistics.ConditionalDeficit}。";
            }
            if(result.TimedOut || Full())
            {
                result.TimedOut=true;
                result.Message+=" 搜索达到预算，仍可能存在其他可行排法。";
            }
            return result;
        }

        private static IEnumerable<Candidate> LockerPools(Room room,IReadOnlyList<Placement> existing,Geo reserved,int needed,CancellationToken cancellation)
        {
            var boundary=room.Boundary!;
            var outline=Geometry.RoomPolygon(boundary);
            var existingBodies=existing.Select(Geometry.Footprint).ToList();
            var solid=Geometry.Union(room.Obstacles.Concat(room.Closures).Select(Geometry.RegionFootprint).Concat(existingBodies));
            var walking=outline.Difference(solid);
            double wallFront=Math.Max(room.Options.OperationDepthMm??room.Options.PlanningAisleMm,room.Options.WalkWidthMm+2);
            double aisle=Math.Max(wallFront,room.Options.PlanningAisleFor(room.Mode)), rear=room.Options.RearGapMm;
            var existingFronts=existing.Where(p=>p.EquipmentId=="LOCKER12").Select(p=>LockerAisles.Front(p,aisle)).ToList();
            var storage=walking.Difference(Geometry.Union(existingFronts.Append(reserved)));
            var features=Geometry.Components(solid).Concat(Geometry.Components(reserved)).ToList();
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
            var equipment=Equipment.Get("LOCKER12");
            double length=equipment.Length, depth=equipment.Depth;
            int limit=needed+12;
            bool Hits(Geo a,Geo b)=>a.EnvelopeInternal.Intersects(b.EnvelopeInternal) && a.Intersection(b).Area>Geometry.AreaTolerance;

            var angleWallRows=new List<List<Placement>>();
            foreach(double angle in LockerAngles(boundary))
                foreach(bool reverse in new[]{false,true})
                {
                    cancellation.ThrowIfCancellationRequested();
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
                    var wallLevels=wall.Zip(wall.Skip(1),(a,b)=>(A:a,B:b)).Where(edge=>Math.Abs(edge.A.Y-edge.B.Y)<.00001)
                        .Select(edge=>Math.Round(edge.A.Y,6)).Distinct().OrderBy(y=>y).Take(48).ToArray();
                    var featureLevels=features.Select(p=>orient.Transform(p).EnvelopeInternal.MaxY).Concat(wallLevels).ToArray();
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

                    Candidate Pool(LockerRows pattern,IReadOnlyList<(double Y,bool Up)> rows,double sidePassage)
                    {
                        string direction=angle==0?"横向":angle==90?"竖向":$"{angle:0.#}°";
                        string name=pattern==LockerRows.Walls?"沿墙柜排":pattern==LockerRows.Paired?"沿墙与背靠背柜排":"沿墙与单面柜排";
                        var candidate=new Candidate{Family=name+"/"+direction+(reverse?" · 对侧起排":"")};
                        var occupied=new List<(Geo Body,Geo Front)>();
                        double frontDepth=pattern==LockerRows.Walls?wallFront:aisle;
                        foreach(var row in rows)
                        {
                            foreach(var run in Runs(row.Y,row.Up,frontDepth))
                                for(double x=run.Start;x+length<=Math.Min(w-sidePassage,run.End)+1e-6 && candidate.Items.Count<limit;x+=length)
                                {
                                    var item=IntoRoom(x,row.Y,row.Up);
                                    if(hasEntry && !Validation.Approach(item,0,length,false,wallFront,room.Options.WalkWidthMm,access)
                                        .Overlaps(entryComponents))continue;
                                    if(pattern==LockerRows.Walls)
                                    {
                                        var body=Geometry.Polygon(Geometry.Rect(x,row.Y,length,depth));
                                        var front=Geometry.Polygon(Geometry.Rect(x,row.Up?row.Y+depth:row.Y-aisle,length,aisle));
                                        if(occupied.Any(p=>Hits(body,p.Body) || Hits(body,p.Front) || Hits(front,p.Body)))continue;
                                        occupied.Add((body,front));
                                    }
                                    candidate.Items.Add(item);
                                }
                            if(candidate.Items.Count>=limit)break;
                        }
                        return candidate;
                    }

                    var wallRows=wallLevels.Where(y=>y+rear+depth+wallFront<=h+1e-6).Select(y=>(y+rear,true))
                        .Concat(wallLevels.Reverse().Where(y=>y-rear-depth-wallFront>=-1e-6).Select(y=>(y-rear-depth,false))).ToList();
                    var wallPool=Pool(LockerRows.Walls,wallRows,0);
                    if(wallPool.Items.Count>0){angleWallRows.Add(wallPool.Items);yield return wallPool;}
                    foreach(var pattern in new[]{LockerRows.Paired,LockerRows.SingleFacing})
                    {
                        double step=pattern==LockerRows.Paired?2*depth+aisle+rear:depth+aisle;
                        var offsets=new[]{0.0}.Concat(featureLevels.Select(y=>Math.Round((y%step+step)%step,6)))
                            .Select(y=>Math.Abs(y-step)<1e-6?0:y).Distinct().Take(8);
                        foreach(double offset in offsets)
                        {
                            var rows=new List<(double Y,bool Up)>();
                            for(double y=rear+offset;y+depth+aisle<=h+1e-6 && rows.Count<200;y+=step)
                            {
                                rows.Add((y,true));
                                if(pattern==LockerRows.Paired && y+2*depth+aisle<=h+1e-6)rows.Add((y+depth+aisle,false));
                            }
                            // End access fits the configured walking width; opposed fronts keep their own clearance.
                            double sidePassage=rows.Count>2?room.Options.WalkWidthMm+2:0;
                            var candidate=Pool(pattern,rows,sidePassage);
                            if(candidate.Items.Count>0)yield return candidate;
                            if(sidePassage>0 && (!hasEntry || candidate.Items.Count<limit))
                            {
                                var alternate=Pool(pattern,rows,0);
                                if(alternate.Items.Count>candidate.Items.Count)yield return alternate;
                            }
                        }
                    }
                }
            var mixed=new Candidate{Family="组合墙向柜排"};
            var occupiedMixed=new List<(Geo Body,Geo Front)>();
            foreach(var item in angleWallRows.OrderByDescending(row=>row.Count).SelectMany(row=>row))
            {
                cancellation.ThrowIfCancellationRequested();
                var body=Geometry.Footprint(item);
                var front=LockerAisles.Front(item,aisle);
                if(occupiedMixed.Any(p=>Hits(body,p.Body) || Hits(body,p.Front) || Hits(front,p.Body)))continue;
                occupiedMixed.Add((body,front));mixed.Items.Add(item);
                if(mixed.Items.Count>=limit)break;
            }
            if(mixed.Items.Count>0 && mixed.Items.Select(item=>Math.Round(item.Rotation%180,6)).Distinct().Count()>1)
                yield return mixed;
        }
    }
}
