using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Gengyi.Core
{
    public sealed class EquipmentLabel
    {
        public string Title { get; set; } = "";
        public string Structure { get; set; } = "";
        public string Detail { get; set; } = "";
        public IReadOnlyList<string> Lines => new[] { Title, Structure, Detail }.Where(s => s.Length > 0).ToArray();
        public IReadOnlyList<string> CompactLines { get; set; } = Array.Empty<string>();
    }

    // The same physical description appears on previews, native block attributes and the palette.
    // A shoe cabinet's levels are per side; two stacked boxes are not two storage levels.
    public static class EquipmentPresentation
    {
        public static EquipmentLabel Describe(Placement item, LayoutOptions options)
        {
            var e = item.Equipment;
            string Mm(double value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "mm";
            if (e.IsShoe)
                return new EquipmentLabel
                {
                    Title = e.Id + " · " + e.Capacity + "人",
                    Structure = e.Boxes == 2 ? "上下2柜 / 每侧6层" : "单柜 / 每侧3层",
                    Detail = (e.Capacity * 2) + "格 · 高" + Mm(e.Height),
                    CompactLines = new[] { e.Id + " " + e.Capacity + "人", e.Boxes == 2 ? "双柜6层" : "单柜3层" }
                };
            if (e.Id == "LOCKER12")
                return new EquipmentLabel { Title = "12门衣柜 · 12人", Structure = "3列×4层", Detail = "高" + Mm(e.Height), CompactLines = new[] { "衣柜12人", "4层" } };
            if (e.Id == "HANGER_CUSTOM")
                return new EquipmentLabel { Title = "挂架 · " + Capacity.Nominal(item, options) + "挂位", Structure = "单列 · 长" + Mm(item.Length),
                    CompactLines = new[] { "挂架" + Capacity.Nominal(item, options) + "位", (item.Length/1000).ToString("0.###", CultureInfo.InvariantCulture) + "m" } };
            if (e.IsCorner)
                return new EquipmentLabel { Title = "R" + e.Length.ToString("0.###",CultureInfo.InvariantCulture), Structure = "转角 · 0人",
                    CompactLines = new[] { "R" + e.Length.ToString("0.###",CultureInfo.InvariantCulture) } };
            if (e.Id == "CUP100")
                return new EquipmentLabel { Title = "茶杯柜 · 100格", Detail = "高" + Mm(e.Height), CompactLines = new[] { "茶杯柜100格" } };
            var name = e.Id == "CHANGING_MIRROR" ? "更衣镜" : e.Id == "UV_DEVICE" ? "紫外线设备" :
                e.Id == "SHOE_ARCHIVE" ? "鞋间资料柜" : e.Id == "SHOE_DESK" ? "鞋间工作台" :
                e.Id == "SHOE_WATERHEATER" ? "鞋间热水器" : e.Id;
            return new EquipmentLabel { Title = name, CompactLines = new[] { name } };
        }
    }
}
