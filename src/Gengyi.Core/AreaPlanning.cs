using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Gengyi.Core
{
    public sealed class RectangleEstimate
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double AreaM2 => WidthMm * HeightMm / 1000000;
        public double ProjectionM2 { get; set; }
        public int Nominal { get; set; }
        public List<Placement> Items { get; set; } = new List<Placement>();
        public override string ToString() => $"{WidthMm/1000:0.##} × {HeightMm/1000:0.##} m · {AreaM2:0.##} ㎡ · {Name}";
    }

    public sealed class AreaEstimate
    {
        public int Target { get; set; }
        public string Basis { get; set; } = "";
        public string Assumptions { get; set; } = "";
        public string Message { get; set; } = "";
        public List<RectangleEstimate> Options { get; set; } = new List<RectangleEstimate>();
    }

    /// <summary>Pre-boundary planning envelopes, never a certificate of effective capacity.</summary>
    public static class AreaPlanning
    {
        public static bool IsPreviewCurrent(Population population,Room room,int option)=>option>=0 &&
            room.PlanningPreviewOption==option && room.PlanningPreviewSignature.Length>0 && room.PlanningPreviewSignature==Signature(population,room);
        public static string Signature(Population population,Room room)
        {
            var options=Serialization.Copy(room.Options);
            options.ShoeCapacities=options.ShoeCapacities.Distinct().OrderBy(c=>c).ToList();
            string Number(decimal value)=>value.ToString("G29",CultureInfo.InvariantCulture);
            return Serialization.Write(new
            {
                room.Id,room.Mode,room.Gender,room.CustomPeople,
                ReserveOverride=room.ReserveOverride.HasValue?Number(room.ReserveOverride.Value):null,
                population.Registered,population.Shift,MaleRatio=Number(population.MaleRatio),Reserve=Number(population.Reserve),Options=options,
                Equipment.ShoeDepthMm,Equipment.ShoeDuctMm,Equipment.CornerRadiusMm
            });
        }
        private sealed class Draft
        {
            internal string Name = "";
            internal double W, H;
            internal bool Stacked;
            internal Func<List<Placement>> Build = null!;
            internal double Area => W * H;
            internal double Ratio => W / H;
        }

        public static AreaEstimate Estimate(Population population, Room room)
        {
            population.Validate(); room.Options.Validate();
            int target = population.For(room);
            var o = room.Options;
            double aisle = Math.Max(o.PlanningAisleMm, o.OperationDepthMm ?? 0);
            var output = new AreaEstimate
            {
                Target = target,
                Basis = (room.CustomPeople.HasValue ? "本房自定义人数" : room.Mode == RoomMode.SecondChange ? "按当班人数" : "按在册人数") +
                    $"；备用 {(room.ReserveOverride ?? population.Reserve)*100:0.##}%；目标 {target} 人",
                Assumptions = room.Mode == RoomMode.Shoe
                    ? $"鞋柜深{Equipment.ShoeDepthMm:0} mm（含中间{Equipment.ShoeDuctMm:0} mm通风道），含四周 {o.ShoeBoundaryMm:0} mm 边距与假定 R{Equipment.CornerRadiusMm:0} 扇形角；连接净距至少 {aisle:0} mm。48人柜必须24—48—24交错。门柱、封闭与脏净绕行待选框后校核。"
                    : $"按操作/通行净距 {aisle:0} mm、背距 {o.RearGapMm:0} mm 预排；门柱、更衣动作及辅助设备另核。" +
                        (room.Mode == RoomMode.SecondChange ? $" 挂钩节距 {o.HookPitchMm}、端距 {o.HookStartMarginMm}/{o.HookEndMarginMm} mm；450 mm 仅架体深度，衣物与安装待核。" : "")
            };
            if (target == 0)
            {
                output.Message = "当前目标为 0 人；请先输入需要服务的人数。";
                return output;
            }

            var drafts = room.Mode == RoomMode.Shoe ? Shoes(room, target, aisle) : Rows(room, target, aisle);
            // Three distinct low-cabinet/row proportions, plus one explicitly marked stacking alternative.
            // Ordering depends only on dimensions and options, so GYRECT Suggested matches the palette.
            var selected = new List<Draft>();
            var ordinary = drafts.Where(d => !d.Stacked).OrderBy(d => d.Area).ThenBy(d => d.W).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
            bool Pick(IEnumerable<Draft> choices)
            {
                var next = choices.FirstOrDefault(d => !selected.Any(s => s.W == d.W && s.H == d.H && s.Stacked == d.Stacked));
                if (next != null) selected.Add(next);
                return next != null;
            }
            Pick(ordinary.OrderBy(d => d.Area * (1 + .35 * Math.Abs(Math.Log(d.Ratio)))));
            Pick(ordinary.Where(d => d.Ratio >= 1.35));
            Pick(ordinary.Where(d => d.Ratio <= 1/1.35));
            while (selected.Count < 3 && Pick(ordinary)) { }
            Pick(drafts.Where(d => d.Stacked).OrderBy(d => d.Area * (1 + .35 * Math.Abs(Math.Log(d.Ratio)))).ThenBy(d => d.W));
            foreach (var draft in selected)
            {
                var items = draft.Build();
                output.Options.Add(new RectangleEstimate
                {
                    Name = draft.Name, WidthMm = draft.W, HeightMm = draft.H, Items = items,
                    Nominal = items.Sum(p => Capacity.Nominal(p, o)),
                    ProjectionM2 = items.Sum(p => Geometry.Footprint(p).Area) / 1000000
                });
            }
            output.Message = output.Options.Count == 0
                ? "当前柜型/净距参数没有可用的预估模板，请调整参数后再选框。"
                : $"建议矩形约 {output.Options.Min(c => c.AreaM2):0.##}–{output.Options.Max(c => c.AreaM2):0.##} ㎡（可切换长宽）。这是所列预排范围，非最小面积或使用合格结论。";
            return output;
        }

        private static List<Draft> Shoes(Room room, int target, double aisle)
        {
            var output = new List<Draft>();
            var o = room.Options;
            var chains = new Dictionary<double, List<int>?>();
            foreach (int connector in o.ShoeCapacities.Distinct().OrderBy(c => c).Select(c => c/3*250).Where(c => c >= aisle))
                for (int rows = 3; rows <= 24; rows++)
                    foreach (bool stacked in o.AllowStacking && o.ShoeCapacities.Contains(24) ? new[] { false, true } : new[] { false })
                    {
                        double start = Math.Ceiling(Math.Max(1000, (target - (rows-1)*connector*.012) / (rows*.012*(stacked ? 1.5 : 1))) / 500) * 500;
                        for (double run = start; run <= start + 8000; run += 500)
                        {
                            if (!chains.TryGetValue(run, out var chain)) chains[run] = chain = Capacity.Run(run, o.ShoeCapacities);
                            if (chain == null) continue;
                            int nominal = chain.Sum()*rows + (rows-1)*connector*12/1000;
                            int stackable = ShoeStacking.MaximumStackableInRun(chain)*rows;
                            if (nominal + (stacked ? 24*stackable : 0) < target) continue;
                            if (stacked && nominal >= target) break;
                            int r = rows, con = connector; double len = run;
                            output.Add(new Draft
                            {
                                W = run + 2*Equipment.CornerRadiusMm + 2*o.ShoeBoundaryMm,
                                H = rows*Equipment.ShoeDepthMm + (rows-1)*connector + 2*o.ShoeBoundaryMm,
                                Stacked = stacked,
                                Name = $"S形{rows}排 · {(stacked ? "24—48—24交错" : "单柜3层")}",
                                Build = () => Layout.ShoeTemplate(room, r, len, con, chain, stacked ? target : 0).Items
                            });
                            break;
                        }
                    }
            return output;
        }

        private static List<Draft> Rows(Room room, int target, double aisle)
        {
            var o = room.Options;
            var parts = new List<double>();
            if (room.Mode == RoomMode.FirstChange)
                parts.AddRange(Enumerable.Repeat(1140.0, (target+11)/12));
            else
            {
                int maxHooks = o.HangerMaxSegmentMm < o.HookStartMarginMm + o.HookEndMarginMm ? 0 :
                    (int)Math.Floor((o.HangerMaxSegmentMm-o.HookStartMarginMm-o.HookEndMarginMm)/o.HookPitchMm)+1;
                if (maxHooks <= 0) return new List<Draft>();
                for (int remaining = target; remaining > 0; remaining -= Math.Min(remaining, maxHooks))
                {
                    int hooks = Math.Min(remaining, maxHooks);
                    parts.Add(Math.Max(1, (hooks-1)*o.HookPitchMm + o.HookStartMarginMm + o.HookEndMarginMm));
                }
            }
            var output = new List<Draft>();
            double depth = room.Mode == RoomMode.FirstChange ? 500 : 450;
            double gap = room.Mode == RoomMode.FirstChange ? 0 : o.HangerSegmentGapMm;
            for (int columns = 1; columns <= Math.Min(parts.Count, 64); columns++)
            {
                int n = columns, rows = (parts.Count+columns-1)/columns;
                double run = 0;
                for (int row = 0; row < rows; row++)
                    run = Math.Max(run, parts.Skip(row*columns).Take(columns).Sum() + (Math.Min(columns,parts.Count-row*columns)-1)*gap);
                output.Add(new Draft
                {
                    W = run + aisle, H = rows*(depth+o.RearGapMm+aisle),
                    Name = room.Mode == RoomMode.FirstChange ? $"{rows}排 · {parts.Count}个12门4层柜" : $"{rows}排 · {parts.Count}段挂架",
                    Build = () =>
                    {
                        var items = new List<Placement>();
                        for (int i = 0; i < parts.Count; i++)
                        {
                            int row = i/n, col = i%n;
                            double x = parts.Skip(row*n).Take(col).Sum()+col*gap;
                            items.Add(Layout.Place(room, room.Mode == RoomMode.FirstChange ? "LOCKER12" : "HANGER_CUSTOM",
                                x, aisle+row*(depth+o.RearGapMm+aisle), length: room.Mode == RoomMode.FirstChange ? 0 : parts[i]));
                        }
                        return items;
                    }
                });
            }
            return output;
        }
    }
}
