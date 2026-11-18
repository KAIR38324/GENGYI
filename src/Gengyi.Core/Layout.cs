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
            var candidates = !rectangular ? PolygonCandidates(room,target,cancellation) : room.Mode == RoomMode.Shoe ? Shoes(room, target) : room.Mode == RoomMode.FirstChange ? Lockers(room, target) : Hangers(room, target);
            var signatures = new HashSet<string>();
            // Geometric enumeration does not mutate the room's placed entities or selected draft.
            var rough = new List<Candidate>();
            foreach (Candidate c in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                output.Enumerated++;
                if (clock.ElapsedMilliseconds > 5000) { output.TimedOut = true; break; }
                if (signatures.Add(Signature(c.Items))) rough.Add(c);
            }
            rough = rough.OrderBy(c => Math.Max(0, target - c.Items.Sum(p => Capacity.Nominal(p, room.Options))))
                .ThenBy(c => Math.Abs(c.Items.Sum(p => Capacity.Nominal(p, room.Options)) - target))
                .ThenBy(c => c.Items.Count).ToList();
            // Limit precise Boolean geometry work, retaining different families and orientations.
            var selected = rough.GroupBy(c => c.Family).SelectMany(g => g.Take(3)).Take(36).ToList();
            foreach (Candidate c in selected)
            {
                cancellation.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds > 10000 && output.Candidates.Count > 0) { output.TimedOut = true; break; }
                foreach (var p in c.Items) ResolveMapping(project, p);
                if(rectangular)FitAroundObstacles(room,c,cancellation);
                c.Statistics = Validation.Evaluate(project, room, c.Items);
                output.Candidates.Add(c);
            }
            output.Candidates = output.Candidates
                .OrderBy(c => c.Statistics.Checks["geometry"] == CheckState.Fail ? 1 : 0)
                .ThenBy(c => c.Statistics.Checks["boundary"] == CheckState.Fail ? 1 : 0)
                .ThenBy(c => c.Statistics.ConditionalDeficit)
                .ThenBy(c => c.Statistics.Blocked)
                .ThenBy(c => c.Statistics.NominalSurplus)
                .ThenBy(c => c.Statistics.ProjectionM2).Take(8).ToList();
            output.Message = output.Candidates.Count == 0 ? "当前候选家族没有找到可放下的柜链/挂架组合；请检查实际边界、净距与允许柜型。没有穷举任意构型，不能宣称全局无解。" :
                "得到 " + output.Candidates.Count + " 个不同预排；各项检查独立显示，待核预排不等于有效方案。";
            if(!rectangular)output.Message+=" 按实际轮廓保守分区排布，缺角和障碍不放柜；不足时显示差额，不把外包矩形当作房间，也不据此认定全局无解。";
            if (output.TimedOut) output.Message += " 搜索达到时间预算，未完成穷举，不能据此认定最优或无解。";
            return output;
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
        internal static Candidate ShoeTemplate(Room room, int rows, double run, int connector, List<int> chain, int stackedTarget = 0)
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
                bool right = row % 2 == 0;
                double cx = right ? margin + Equipment.CornerRadiusMm + run : margin + Equipment.CornerRadiusMm;
                c.Items.Add(Place(room, Equipment.CornerId, cx, y + Equipment.ShoeDepthMm, right ? 270 : 180, group: c.Id));
                c.Items.Add(Place(room, "S" + (connector * 12 / 1000), right ? cx + Equipment.CornerRadiusMm : cx, y + Equipment.ShoeDepthMm, 90, group: c.Id));
                c.Items.Add(Place(room, Equipment.CornerId, cx, y + Equipment.ShoeDepthMm + connector, right ? 0 : 90, group: c.Id));
            }
            ShoeStacking.StackToTarget(c.Items,stackedTarget);
            if (c.Items.Any(p => p.EquipmentId == "S48")) c.Family += "/局部叠放";
            return c;
        }

        private static IEnumerable<Candidate> Lockers(Room room, int target)
        {
            int count = (target + 11) / 12;
            if (count == 0) { yield return new Candidate { Family = "空房间" }; yield break; }
            var b = room.Boundary!;
            foreach (bool vertical in new[] { false, true })
            {
                double w = vertical ? b.Height : b.Width, h = vertical ? b.Width : b.Height;
                double rear = room.Options.RearGapMm, aisle = room.Options.PlanningAisleMm;
                var wall = new Candidate { Family = "沿墙柜排/" + (vertical ? "竖" : "横") };
                int perRow = Math.Max(0, (int)Math.Floor(w / 1140));
                for (int n = 0; n < perRow && wall.Items.Count < count && h >= 500 + rear; n++)
                    wall.Items.Add(Place(room, "LOCKER12", (n + 1) * 1140, rear + 500, 180));
                if (h >= 1000 + 2 * rear + aisle)
                    for (int n = 0; n < perRow && wall.Items.Count < count; n++)
                        wall.Items.Add(Place(room, "LOCKER12", n * 1140, h - rear - 500));
                if (w >= 1000 + 2 * rear + aisle)
                    for (double y = rear + 500 + aisle; y + 1140 <= h - rear - 500 - aisle && wall.Items.Count < count; y += 1140)
                    {
                        wall.Items.Add(Place(room, "LOCKER12", rear + 500, y, 90));
                        if (wall.Items.Count < count) wall.Items.Add(Place(room, "LOCKER12", w - rear - 500, y + 1140, 270));
                    }
                if (vertical) RotateIntoBoundary(wall, b.Width);
                if (wall.Items.Count > 0) yield return wall;
                foreach (bool paired in new[] { false, true })
                {
                    var rows = new Candidate { Family = (paired ? "背靠背岛柜" : "多列单面柜") + "/" + (vertical ? "竖" : "横") };
                    double step = (paired ? 1000 + rear : 500) + aisle;
                    for (double y = aisle; y + (paired ? 1000 + rear : 500) <= h && rows.Items.Count < count; y += step)
                        for (int n = 0; n < perRow && rows.Items.Count < count; n++)
                        {
                            rows.Items.Add(Place(room, "LOCKER12", n * 1140, y));
                            if (paired && rows.Items.Count < count)
                                rows.Items.Add(Place(room, "LOCKER12", (n + 1) * 1140, y + 1000 + rear, 180));
                        }
                    if (vertical) RotateIntoBoundary(rows, b.Width);
                    if (rows.Items.Count > 0) yield return rows;
                }
            }
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
        public static string Signature(IEnumerable<Placement> items)
        {
            var list = items.ToList();
            double x = list.Count == 0 ? 0 : list.Min(p => p.Position.X), y = list.Count == 0 ? 0 : list.Min(p => p.Position.Y);
            string N(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
            return string.Join(";", list.Select(p => p.EquipmentId + ":" + N(p.Position.X - x) + ":" + N(p.Position.Y - y) + ":" + N(p.Rotation) + ":" + N(p.Length)).OrderBy(s => s, StringComparer.Ordinal));
        }
        private static void FitAroundObstacles(Room room,Candidate candidate,CancellationToken cancellation)
        {
            if(room.Obstacles.Count==0 || candidate.Items.Count==0)return;
            var union=Geometry.Union(candidate.Items.Select(Geometry.Footprint));
            var obstacle=Geometry.Union(room.Obstacles.Select(Geometry.RegionFootprint));
            var envelope=union.EnvelopeInternal;var b=room.Boundary!;
            double margin=room.Mode==RoomMode.Shoe?room.Options.ShoeBoundaryMm:0;
            double minX=margin-envelope.MinX,maxX=b.Width-margin-envelope.MaxX;
            double minY=margin-envelope.MinY,maxY=b.Height-margin-envelope.MaxY;
            if(minX>maxX || minY>maxY)return;
            var xs=new List<double>{0,minX,maxX,(minX+maxX)/2};var ys=new List<double>{0,minY,maxY,(minY+maxY)/2};
            double operation=room.Options.OperationDepthMm??room.Options.PlanningAisleMm;
            foreach(var region in room.Obstacles)
            {
                var ob=Geometry.RegionFootprint(region).EnvelopeInternal;
                xs.Add(ob.MinX-envelope.MaxX-operation);xs.Add(ob.MaxX-envelope.MinX+operation);
                ys.Add(ob.MinY-envelope.MaxY-operation);ys.Add(ob.MaxY-envelope.MinY+operation);
            }
            V2 best=new V2();double bestScore=double.PositiveInfinity;
            foreach(double x in xs.Where(v=>v>=minX && v<=maxX).Distinct().Take(12))
                foreach(double y in ys.Where(v=>v>=minY && v<=maxY).Distinct().Take(12))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var moved=NetTopologySuite.Geometries.Utilities.AffineTransformation.TranslationInstance(x,y).Transform(union);
                    double collision=moved.Intersection(obstacle).Area;
                    double operating=moved.Buffer(operation/2,8).Intersection(obstacle).Area;
                    double score=collision*1e9+operating*100+Math.Abs(x)+Math.Abs(y);
                    if(score<bestScore){bestScore=score;best=new V2(x,y);}
                }
            foreach(var item in candidate.Items)item.Position+=best;
        }
    }
}
