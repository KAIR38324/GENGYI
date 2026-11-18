using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Gengyi.Core
{
    public sealed class SearchResult
    {
        public List<Candidate> Candidates { get; set; } = new List<Candidate>();
        public bool TimedOut { get; set; }
        public int Enumerated { get; set; }
        public string Message { get; set; } = "";
    }

    public static partial class Layout
    {
        public static SearchResult Generate(Project project, Room room, CancellationToken cancellation = default)
        {
            project.Population.Validate(); room.Options.Validate();
            if (room.Boundary == null) throw new ArgumentException("请先为当前房间选择边界。");
            if (room.ConstraintIssue.Length > 0) throw new ArgumentException(room.ConstraintIssue);
            if (room.Boundary.Width <= 0 || room.Boundary.Height <= 0) throw new ArgumentException("房间边界面积必须为正。");
            var output = new SearchResult();
            var clock = Stopwatch.StartNew();
            int target = project.Population.For(room);
            var actualBoundary=Geometry.RoomPolygon(room.Boundary);
            var boxBoundary=Geometry.Polygon(Geometry.Rect(0,0,room.Boundary.Width,room.Boundary.Height));
            bool rectangular=actualBoundary.SymmetricDifference(boxBoundary).Area<=Geometry.AreaTolerance;
            var reserved=RoomAccess.ReservedSpace(room);
            bool hasAccess=room.Doors.Count>0 || room.Passages.Count>0;
            bool shoe=room.Mode==RoomMode.Shoe;
            bool constrainedShoe=shoe && (!rectangular || hasAccess || room.Obstacles.Count>0 || room.Closures.Count>0);
            var candidates = room.Mode switch
            {
                RoomMode.Shoe => constrainedShoe?ShoeChains(room,actualBoundary,reserved,target,cancellation):Shoes(room,target),
                RoomMode.FirstChange => Lockers(room,target,reserved,cancellation),
                _ => rectangular?Hangers(room,target):PolygonHangers(room,target,reserved,cancellation)
            };
            bool translate=rectangular && room.Mode==RoomMode.SecondChange;
            var drafts=candidates.Select(candidate=>(Candidate:candidate,Translate:translate));
            if(translate && hasAccess)
                drafts=drafts.Concat(PolygonHangers(room,target,reserved,cancellation).Select(candidate=>(candidate,false)));
            var signatures = new HashSet<string>();
            // Geometric enumeration does not mutate the room's placed entities or selected draft.
            var rough = new List<(Candidate Candidate,bool Translate)>();
            foreach (var draft in drafts)
            {
                cancellation.ThrowIfCancellationRequested();
                output.Enumerated++;
                if (clock.ElapsedMilliseconds > 5000) { output.TimedOut = true; break; }
                if (signatures.Add(Signature(draft.Candidate.Items,hasAccess))) rough.Add(draft);
            }
            rough = rough.OrderBy(d => Math.Max(0, target - d.Candidate.Items.Sum(p => Capacity.Nominal(p, room.Options))))
                .ThenBy(d => Math.Abs(d.Candidate.Items.Sum(p => Capacity.Nominal(p, room.Options)) - target))
                .ThenBy(d => d.Candidate.Items.Count).ToList();
            // Limit precise Boolean geometry work, retaining different families and orientations.
            var selected = rough.GroupBy(d=>d.Candidate.Family).SelectMany(g=>g.Take(3)).Take(36);
            foreach (var draft in selected)
            {
                var c=draft.Candidate;
                cancellation.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds > 10000 && output.Candidates.Count > 0) { output.TimedOut = true; break; }
                foreach (var p in c.Items) ResolveMapping(project, p);
                if(draft.Translate)FitAroundObstacles(room,c,reserved,cancellation);
                if(hasAccess)
                {
                    RemovePlacementsAndGroups(c.Items,item=>Geometry.Footprint(item).Intersection(reserved).Area>Geometry.AreaTolerance);
                }
                c.Statistics = Validation.Evaluate(project, room, c.Items);
                if(hasAccess)
                {
                    var unreachable=new HashSet<string>(c.Statistics.Issues.Where(i=>i.Code=="DOOR_FRONT_UNREACHABLE").Select(i=>i.InstanceId));
                    if(RemovePlacementsAndGroups(c.Items,item=>unreachable.Contains(item.Id) || room.Mode!=RoomMode.Shoe &&
                        c.Statistics.GeometricByInstance.TryGetValue(item.Id,out int usable) && usable==0)>0)
                        c.Statistics=Validation.Evaluate(project,room,c.Items);
                }
                bool complete=!shoe || target==0 || IsShoeLayout(c.Items) && c.Statistics.Checks["connections"]==CheckState.Pass &&
                    c.Statistics.Checks["geometry"]==CheckState.Pass && c.Statistics.Checks["boundary"]==CheckState.Pass;
                if(complete && !RoomAccess.Failed(c.Statistics) && (c.Items.Count>0 || target==0))output.Candidates.Add(c);
            }
            if(hasAccess && output.Candidates.Count==0)
            {
                var empty=new Candidate{Family="当前无可放设备"};
                empty.Statistics=Validation.Evaluate(project,room,empty.Items);
                if(!RoomAccess.Failed(empty.Statistics)){if(!shoe)output.Candidates.Add(empty);}
                else output.Message=empty.Statistics.Issues.First(i=>i.Code.StartsWith("DOOR_",StringComparison.Ordinal) ||
                    i.Code.StartsWith("PASSAGE_",StringComparison.Ordinal)).Message;
            }
            output.Candidates = output.Candidates.GroupBy(c=>Signature(c.Items,hasAccess)).Select(g=>g.First())
                .OrderBy(c => c.Statistics.Checks["geometry"] == CheckState.Fail ? 1 : 0)
                .ThenBy(c => c.Statistics.Checks["boundary"] == CheckState.Fail ? 1 : 0)
                .ThenBy(c => c.Statistics.ConditionalDeficit)
                .ThenBy(c => shoe ? Math.Max(0,target-c.Statistics.Nominal) : 0)
                .ThenBy(c => c.Statistics.Blocked)
                .ThenBy(c => c.Statistics.NominalSurplus)
                .ThenBy(c => c.Statistics.ProjectionM2).Take(8).ToList();
            if(output.Message.Length==0)
                output.Message=output.Candidates.Count==0
                    ? shoe?"当前边界、门位和净距下未找到完整 S 形柜链。S 形需要至少三排柜及交替的 R650 圆弧转角。"
                        :"当前边界和净距下未找到可布置的设备，请核对房间与参数。"
                    : "得到 "+output.Candidates.Count+(shoe && target>0?" 个带 R650 圆弧转角的 S 形预排。":" 个不同预排。");
            if(hasAccess && output.Candidates.Count>0)
                output.Message+=$" 已避让所选出入口与通行区，首选方案的条件缺口为 {output.Candidates[0].Statistics.ConditionalDeficit}。";
            if (output.TimedOut) output.Message += " 搜索达到时间预算，未完成穷举，不能据此认定最优或无解。";
            return output;
        }

        private static int RemovePlacementsAndGroups(List<Placement> items,Func<Placement,bool> exclude)
        {
            var blocked=new HashSet<Placement>(items.Where(exclude));
            var groups=new HashSet<string>(blocked.Where(item=>item.GroupId.Length>0).Select(item=>item.GroupId));
            return items.RemoveAll(item=>blocked.Contains(item) || groups.Contains(item.GroupId));
        }

        public static void ResolveMapping(Project project, Placement p)
        {
            string key = BlockMapping.For(p.EquipmentId, p.Length);
            p.MappingKey = ""; p.BlockName = ""; p.Provenance = "missing"; p.PhysicalOutline = null;
            if (project.Mappings.TryGetValue(key, out var mapping) && mapping.Accepts(p))
            {
                p.MappingKey = mapping.Key.Length > 0 ? mapping.Key : key; p.BlockName = mapping.BlockName; p.Provenance = mapping.Provenance;
                p.PhysicalOutline = mapping.PhysicalOutline == null ? null : Serialization.Copy(mapping.PhysicalOutline);
            }
        }

        internal static Placement Place(Room room, string id, double x, double y, double rotation = 0, double length = 0, string group = "")
        {
            var p = new Placement { RoomId = room.Id, EquipmentId = id, Position = new V2(x,y), Rotation = rotation,
                Length = length, GroupId = group, CrossingEnabled = id == "S12" || id == "S18" || id == "S24" };
            if (id == "HANGER_CUSTOM") p.LocalHooks = Capacity.Hooks(new Placement { EquipmentId = id, Length = length }, room.Options);
            return p;
        }

        private static IEnumerable<Candidate> Shoes(Room room, int target)
        {
            if (target == 0) { yield return new Candidate { Family = "空房间" }; yield break; }
            var b = room.Boundary!;
            double margin = room.Options.ShoeBoundaryMm;
            foreach (bool vertical in new[] { false, true })
            {
                double width = (vertical ? b.Height : b.Width) - 2 * margin;
                double height = (vertical ? b.Width : b.Height) - 2 * margin;
                foreach (int connector in room.Options.ShoeCapacities.Select(c => c / 3 * 250).Distinct())
                {
                    int maxRows = Math.Min(24, (int)Math.Floor((height + connector) / (Equipment.ShoeDepthMm + connector)));
                    for (int rows = 3; rows <= maxRows; rows++)
                    {
                        double maxRun = Math.Floor((width - 2*Equipment.CornerRadiusMm) / 500) * 500;
                        if (maxRun < 1000) continue;
                        double desired = Math.Ceiling(Math.Max(1000, (target - (rows - 1) * connector * .012) / (rows * .012)) / 500) * 500;
                        var runs=new List<double>{Math.Min(desired,maxRun),Math.Min(desired+500,maxRun),maxRun};
                        if(room.Options.AllowStacking)
                        {
                            double stackedRun=Math.Ceiling(Math.Max(1000,(target-(rows-1)*connector*.012)/(rows*.018))/500)*500;
                            foreach(double extra in new[]{0.0,500,1000,2000})runs.Add(Math.Min(maxRun,stackedRun+extra));
                        }
                        foreach (double run in runs.Distinct())
                        {
                            var chain = Capacity.Run(run, room.Options.ShoeCapacities);
                            if (chain == null) continue;
                            foreach (bool stack in room.Options.AllowStacking ? new[] { false, true } : new[] { false })
                            {
                                var c = ShoeTemplate(room, rows, run, connector, chain, stack ? target : 0);
                                c.Family += "/" + (vertical ? "竖" : "横");
                                if (stack && !c.Items.Any(p => p.EquipmentId == "S48")) continue;
                                if (vertical) RotateIntoBoundary(c, b.Width);
                                yield return c;
                            }
                        }
                    }
                }
            }
        }

        // Shared by pre-boundary area planning and the actual rectangle solver.
        // R650 is the current quarter-sector assumption, not an original block.
        internal static Candidate ShoeTemplate(Room room, int rows, double run, int connector, List<int> chain, int stackedTarget = 0, bool firstTurnRight = true)
        {
            var c = new Candidate { Family = $"S形{rows}排/{connector}连接" };
            double margin = room.Options.ShoeBoundaryMm;
            for (int row = 0; row < rows; row++)
            {
                double x = margin + Equipment.CornerRadiusMm, y = margin + row * (Equipment.ShoeDepthMm + connector);
                foreach (int cap in chain)
                {
                    c.Items.Add(Place(room, "S" + cap, x, y, group: c.Id));
                    x += cap / 3 * 250;
                }
                if (row == rows - 1) continue;
                bool right = (row % 2 == 0) == firstTurnRight;
                double cx = right ? margin + Equipment.CornerRadiusMm + run : margin + Equipment.CornerRadiusMm;
                c.Items.Add(Place(room, Equipment.CornerId, cx, y + Equipment.ShoeDepthMm, right ? 270 : 180, group: c.Id));
                c.Items.Add(Place(room, "S" + (connector * 12 / 1000), right ? cx + Equipment.CornerRadiusMm : cx, y + Equipment.ShoeDepthMm, 90, group: c.Id));
                c.Items.Add(Place(room, Equipment.CornerId, cx, y + Equipment.ShoeDepthMm + connector, right ? 0 : 90, group: c.Id));
            }
            ShoeStacking.StackToTarget(c.Items,stackedTarget);
            if (c.Items.Any(p => p.EquipmentId == "S48")) c.Family += "/局部叠放";
            return c;
        }

        private static IEnumerable<Candidate> Hangers(Room room, int target)
        {
            if (target == 0) { yield return new Candidate { Family = "空房间" }; yield break; }
            var b = room.Boundary!; var o = room.Options;
            foreach (bool vertical in new[] { false, true })
            {
                double w = vertical ? b.Height : b.Width, h = vertical ? b.Width : b.Height;
                foreach (bool walls in new[] { true, false })
                {
                    var c = new Candidate { Family = (walls ? "沿墙分段挂架" : "多段中岛挂架") + "/" + (vertical ? "竖" : "横") };
                    int remaining = target;
                    var ys = walls ? new[] { 0.0, h - 450 } :
                        Enumerable.Range(0, Math.Max(0, (int)Math.Floor(h / (450 + o.PlanningAisleMm))))
                            .Select(n => o.PlanningAisleMm / 2 + n * (450 + o.PlanningAisleMm)).ToArray();
                    foreach (double y in ys.Where(y => y >= 0 && y + 450 <= h).Distinct())
                    {
                        double x = 0;
                        while (remaining > 0 && x + o.HookStartMarginMm + o.HookEndMarginMm <= w)
                        {
                            double max = Math.Min(o.HangerMaxSegmentMm, w - x);
                            int fit = max < o.HookStartMarginMm + o.HookEndMarginMm ? 0 :
                                (int)Math.Floor((max - o.HookStartMarginMm - o.HookEndMarginMm) / o.HookPitchMm) + 1;
                            int hooks = Math.Min(remaining, fit);
                            if (hooks <= 0) break;
                            double length = (hooks - 1) * o.HookPitchMm + o.HookStartMarginMm + o.HookEndMarginMm;
                            if (length <= 0) length = 1; // zero margins still require a positive physical segment.
                            c.Items.Add(Place(room, "HANGER_CUSTOM", x, y, length: length));
                            remaining -= hooks; x += length + o.HangerSegmentGapMm;
                        }
                        if (remaining <= 0) break;
                    }
                    if (vertical) RotateIntoBoundary(c, b.Width);
                    if (c.Items.Count > 0) yield return c;
                }
            }
        }

        private static void RotateIntoBoundary(Candidate c, double roomWidth)
        {
            foreach (var p in c.Items)
            {
                p.Position = new V2(roomWidth - p.Position.Y, p.Position.X);
                p.Rotation = Geometry.Normalize(p.Rotation + 90);
            }
        }
        public static string Signature(IEnumerable<Placement> items,bool absolute=false)
        {
            var list = items.ToList();
            double x = absolute || list.Count == 0 ? 0 : list.Min(p => p.Position.X), y = absolute || list.Count == 0 ? 0 : list.Min(p => p.Position.Y);
            string N(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
            return string.Join(";", list.Select(p => p.EquipmentId + ":" + N(p.Position.X - x) + ":" + N(p.Position.Y - y) + ":" + N(p.Rotation) + ":" + N(p.Length)).OrderBy(s => s, StringComparer.Ordinal));
        }
        private static void FitAroundObstacles(Room room,Candidate candidate,NetTopologySuite.Geometries.Geometry reserved,CancellationToken cancellation)
        {
            var fixedObjects=room.Obstacles.Concat(room.Closures).Select(Geometry.RegionFootprint).ToList();
            if((fixedObjects.Count==0 && reserved.IsEmpty) || candidate.Items.Count==0)return;
            var union=Geometry.Union(candidate.Items.Select(Geometry.Footprint));
            var obstacle=Geometry.Union(fixedObjects);
            var excluded=obstacle.Union(reserved);
            var envelope=union.EnvelopeInternal;var b=room.Boundary!;
            double margin=room.Mode==RoomMode.Shoe?room.Options.ShoeBoundaryMm:0;
            double minX=margin-envelope.MinX,maxX=b.Width-margin-envelope.MaxX;
            double minY=margin-envelope.MinY,maxY=b.Height-margin-envelope.MaxY;
            if(minX>maxX || minY>maxY)return;
            var xs=new List<double>{0,minX,maxX,(minX+maxX)/2};var ys=new List<double>{0,minY,maxY,(minY+maxY)/2};
            double operation=room.Options.OperationDepthMm??room.Options.PlanningAisleMm;
            foreach(var restriction in fixedObjects.Concat(Geometry.Components(reserved)))
            {
                var ob=restriction.EnvelopeInternal;
                xs.Add(ob.MinX-envelope.MaxX);xs.Add(ob.MaxX-envelope.MinX);
                ys.Add(ob.MinY-envelope.MaxY);ys.Add(ob.MaxY-envelope.MinY);
                xs.Add(ob.MinX-envelope.MaxX-operation);xs.Add(ob.MaxX-envelope.MinX+operation);
                ys.Add(ob.MinY-envelope.MaxY-operation);ys.Add(ob.MaxY-envelope.MinY+operation);
            }
            V2 best=new V2();double bestScore=double.PositiveInfinity;
            foreach(double x in xs.Where(v=>v>=minX && v<=maxX).Distinct().Take(12))
                foreach(double y in ys.Where(v=>v>=minY && v<=maxY).Distinct().Take(12))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var moved=NetTopologySuite.Geometries.Utilities.AffineTransformation.TranslationInstance(x,y).Transform(union);
                    double collision=moved.Intersection(excluded).Area;
                    double operating=moved.Buffer(operation/2,8).Intersection(obstacle).Area;
                    double score=collision*1e9+operating*100+Math.Abs(x)+Math.Abs(y);
                    if(score<bestScore){bestScore=score;best=new V2(x,y);}
                }
            foreach(var item in candidate.Items)item.Position+=best;
        }
    }
}
