using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Gengyi.Core
{
    public enum RoomMode { Shoe, FirstChange, SecondChange }
    public enum CheckState { NotRun, Pass, Fail, RequiresInput, ApproximateOnly }

    public sealed class V2
    {
        public double X { get; set; }
        public double Y { get; set; }
        public V2() { }
        public V2(double x, double y) { X = x; Y = y; }
        public V2 Rotate(double degrees)
        {
            double a = degrees * Math.PI / 180;
            return new V2(X * Math.Cos(a) - Y * Math.Sin(a), X * Math.Sin(a) + Y * Math.Cos(a));
        }
        public static V2 operator +(V2 a, V2 b) => new V2(a.X + b.X, a.Y + b.Y);
        public static V2 operator -(V2 a, V2 b) => new V2(a.X - b.X, a.Y - b.Y);
        public static V2 operator *(V2 a, double s) => new V2(a.X * s, a.Y * s);
        public double Distance(V2 p) => Math.Sqrt((X - p.X) * (X - p.X) + (Y - p.Y) * (Y - p.Y));
    }

    public sealed class Frame
    {
        public double[] Origin { get; set; } = new double[] { 0, 0, 0 };
        public double[] XAxis { get; set; } = new double[] { 1, 0, 0 };
        public double[] YAxis { get; set; } = new double[] { 0, 1, 0 };
        public double MillimetersPerDrawingUnit { get; set; } = 1;
        public double[] ToWorld(V2 p)
        {
            return Enumerable.Range(0,3).Select(i=>Origin[i]+(p.X*XAxis[i]+p.Y*YAxis[i])/MillimetersPerDrawingUnit).ToArray();
        }
        public V2 ToLocal(double[] p)
        {
            var d=Enumerable.Range(0,3).Select(i=>(p[i]-Origin[i])*MillimetersPerDrawingUnit).ToArray();
            var n=new[]{XAxis[1]*YAxis[2]-XAxis[2]*YAxis[1],XAxis[2]*YAxis[0]-XAxis[0]*YAxis[2],XAxis[0]*YAxis[1]-XAxis[1]*YAxis[0]};
            if(Math.Abs(Enumerable.Range(0,3).Sum(i=>d[i]*n[i]))>.1)throw new ArgumentException("所选对象与房间平面不共面。");
            return new V2(Enumerable.Range(0,3).Sum(i=>d[i]*XAxis[i]),Enumerable.Range(0,3).Sum(i=>d[i]*YAxis[i]));
        }
    }

    public sealed class Boundary
    {
        public Frame Frame { get; set; } = new Frame();
        public string NativeHandle { get; set; } = "";
        public double Width { get; set; }
        public double Height { get; set; }
        // Null preserves legacy rectangular drawings. Non-null is the actual planning outline.
        public List<V2>? Outline { get; set; }
        public double? SourceAreaMm2 { get; set; }
        public double CurveToleranceMm { get; set; }
        [JsonIgnore] public double AreaM2 => (SourceAreaMm2 ?? Geometry.RoomPolygon(this).Area)/1000000;
    }

    public sealed class Region
    {
        public string NativeHandle { get; set; } = "";
        public List<V2> Points { get; set; } = new List<V2>();
        public double CurveToleranceMm { get; set; }
    }

    public sealed class Population
    {
        public int Registered { get; set; } = 600;
        public int Shift { get; set; } = 300;
        public decimal MaleRatio { get; set; } = .5m;
        public decimal Reserve { get; set; }
        public void Validate()
        {
            if (Registered < 0 || Registered > 10000 || Shift < 0 || Shift > Registered)
                throw new ArgumentException("在册人数须为 0–10000，当班人数须在 0 与在册人数之间。");
            if (MaleRatio < 0 || MaleRatio > 1 || Reserve < 0 || Reserve > 1)
                throw new ArgumentException("男女比例与备用率须在 0–100% 之间。");
        }
        public int For(Room room)
        {
            Validate();
            if (room.CustomPeople < 0 || room.CustomPeople > 10000) throw new ArgumentException("自定义房间人数须在 0–10000 之间。");
            if(room.ReserveOverride < 0 || room.ReserveOverride > 1)throw new ArgumentException("房间独立备用率须在 0–100% 之间。");
            int total = room.Mode == RoomMode.SecondChange ? Shift : Registered;
            int male = (int)Math.Floor(total * MaleRatio + .5m);
            int people = room.Gender == "male" ? male : room.Gender == "female" ? total - male : total;
            return (int)Math.Ceiling((room.CustomPeople ?? people) * (1 + (room.ReserveOverride??Reserve)));
        }
    }

    public sealed class LayoutOptions
    {
        public bool AllowStacking { get; set; } = true;
        public List<int> ShoeCapacities { get; set; } = new List<int> { 12, 18, 24 };
        public double ShoeBoundaryMm { get; set; } = 1000;
        public double PlanningAisleMm { get; set; } = 1000;
        public double WalkWidthMm { get; set; } = 600;
        public double RearGapMm { get; set; }
        public double? OperationDepthMm { get; set; }
        public bool DoorAndOperationVerified { get; set; }
        public bool InstallationVerified { get; set; }
        public double HangerMaxSegmentMm { get; set; } = 3000;
        public double HangerSegmentGapMm { get; set; } = 100;
        public int HookPitchMm { get; set; } = 100;
        public int HookStartMarginMm { get; set; } = 50;
        public int HookEndMarginMm { get; set; } = 50;
        public double? InstallationElevationMm { get; set; }
        public bool Orthogonal { get; set; } = true;
        public double PlanningAisleFor(RoomMode mode)=>Math.Max(Math.Max(PlanningAisleMm,OperationDepthMm??0),
            mode==RoomMode.FirstChange?LockerAisles.MinimumMm:0);
        public void Validate()
        {
            if (ShoeBoundaryMm < 0 || PlanningAisleMm < 100 || WalkWidthMm < 100 || WalkWidthMm > 2000 || RearGapMm < 0 ||
                HangerMaxSegmentMm < 100 || HangerMaxSegmentMm > 50000 || HangerSegmentGapMm < 0 || HookPitchMm <= 0 ||
                HookStartMarginMm < 0 || HookEndMarginMm < 0 || OperationDepthMm < 1 ||
                InstallationElevationMm < 0 || ShoeCapacities.Count == 0 ||
                ShoeCapacities.Any(c => c != 12 && c != 18 && c != 24))
                throw new ArgumentException("间距、挂钩节距/端距或允许柜型参数无效。");
            foreach(double value in new[]{ShoeBoundaryMm,PlanningAisleMm,WalkWidthMm,RearGapMm,HangerMaxSegmentMm,HangerSegmentGapMm,OperationDepthMm??1,InstallationElevationMm??0})
                if(double.IsNaN(value) || double.IsInfinity(value))throw new ArgumentException("几何参数必须是有限数值。");
        }
    }

    public sealed class Equipment
    {
        public const double ShoeDepthMm = 650;
        public const double ShoeDuctMm = 50;
        public const double ShoeSideDepthMm = (ShoeDepthMm-ShoeDuctMm)/2;
        public const double ShoeHeightMm = 600;
        public const double CornerRadiusMm = 650;
        public const string CornerId = "CORNER_R650";
        public const string LegacyCornerId = "CORNER_R600";
        public string Id { get; set; } = "";
        public double Length { get; set; }
        public double Depth { get; set; }
        public double Height { get; set; }
        public int Capacity { get; set; }
        public int Boxes { get; set; } = 1;
        public int Columns { get; set; }
        public int Levels { get; set; }
        public bool IsShoe => Id.StartsWith("S", StringComparison.Ordinal) && Id != "SHOE_DESK" && Id != "SHOE_ARCHIVE" && Id != "SHOE_WATERHEATER";
        public bool IsCorner => Id == CornerId || Id == LegacyCornerId;
        public static Equipment Get(string id, double length = 0)
        {
            if (id == "S12" || id == "S18" || id == "S24" || id == "S48")
            {
                int people = int.Parse(id.Substring(1));
                int boxes = people == 48 ? 2 : 1;
                int columns = people / boxes / 3;
                return new Equipment { Id = id, Length = columns * 250, Depth = ShoeDepthMm, Height = ShoeHeightMm * boxes,
                    Capacity = people, Columns = columns, Levels = 3 * boxes, Boxes = boxes };
            }
            if (id == "LOCKER12") return new Equipment { Id = id, Length = 1140, Depth = 500, Height = 2095, Capacity = 12, Columns = 3, Levels = 4 };
            if (id == "HANGER_CUSTOM") return new Equipment { Id = id, Length = length, Depth = 450, Height = 650, Boxes = 0 };
            if (id == CornerId) return new Equipment { Id = id, Length = CornerRadiusMm, Depth = CornerRadiusMm, Height = ShoeHeightMm, Boxes = 0 };
            // Keep the legacy radius for reading old drawings; it is never silently enlarged.
            if (id == LegacyCornerId) return new Equipment { Id = id, Length = 600, Depth = 600, Height = ShoeHeightMm, Boxes = 0 };
            if (id == "CUP100") return new Equipment { Id = id, Length = 1500, Depth = 150, Height = 1800, Boxes = 0 };
            if (id == "CHANGING_MIRROR") return new Equipment { Id = id, Length = 1400, Height = 1600, Boxes = 0 };
            if (id == "UV_DEVICE" || id == "SHOE_ARCHIVE" || id == "SHOE_DESK" || id == "SHOE_WATERHEATER")
                return new Equipment { Id = id, Boxes = 0 };
            throw new ArgumentException("未知设备型号：" + id);
        }
    }

    public sealed class Placement
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string RoomId { get; set; } = "";
        public string GroupId { get; set; } = "";
        public string EquipmentId { get; set; } = "S24";
        public double Length { get; set; }
        public V2 Position { get; set; } = new V2();
        public double Rotation { get; set; }
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public bool CrossingEnabled { get; set; }
        public bool Locked { get; set; }
        public bool NativeGeometryUnverified { get; set; }
        public bool NativeCopyDetached { get; set; }
        public string NativeIssue { get; set; } = "";
        public string NativeHandle { get; set; } = "";
        public string MappingKey { get; set; } = "";
        public string BlockName { get; set; } = "";
        public string Provenance { get; set; } = "missing";
        public List<V2>? PhysicalOutline { get; set; }
        public List<V2>? LocalHooks { get; set; }
        [JsonIgnore] public Equipment Equipment => global::Gengyi.Core.Equipment.Get(EquipmentId, Length);
        public V2 Transform(V2 p) => Position + new V2(p.X * ScaleX, p.Y * ScaleY).Rotate(Rotation);
    }

    public sealed class BlockMapping
    {
        public string Key { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string BlockName { get; set; } = "";
        public string SourceBlockName { get; set; } = "";
        public string SourceHash { get; set; } = "";
        public string GeometryHash { get; set; } = "";
        public string SourceEvidence { get; set; } = "";
        public int[]? SourceRgb { get; set; }
        public short? SourceColorIndex { get; set; }
        public string SourceLinetype { get; set; } = "";
        public int? SourceLineWeight { get; set; }
        public string Provenance { get; set; } = "missing";
        public string ReviewStatus { get; set; } = "not_run";
        public V2 SourceAnchor { get; set; } = new V2();
        public double SourceAngleDegrees { get; set; }
        public double MillimetersPerSourceUnit { get; set; } = 1;
        public double Length { get; set; }
        public double EquipmentDepthMm { get; set; }
        public List<V2>? PhysicalOutline { get; set; }
        public bool FootprintVerified { get; set; }
        public List<V2>? LocalHooks { get; set; }
        public Dictionary<string, object> DynamicValues { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
        public bool Accepts(Placement item)
        {
            if(EquipmentId != item.EquipmentId)return false;
            var equipment=item.Equipment;
            if(equipment.Id == Equipment.LegacyCornerId)return false;
            if((equipment.IsShoe || equipment.IsCorner) && !(EquipmentDepthMm>0 && Math.Abs(EquipmentDepthMm-equipment.Depth)<=.0005))return false;
            return item.EquipmentId != "HANGER_CUSTOM" || Math.Abs(Length-item.Length) <= .0005 &&
                LocalHooks != null && item.LocalHooks != null && LocalHooks.Count == item.LocalHooks.Count &&
                !LocalHooks.Where((p,i) => p.Distance(item.LocalHooks[i]) > .001).Any();
        }
        public static string For(string equipmentId, double length) => equipmentId == "HANGER_CUSTOM"
            ? equipmentId + "@" + length.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : equipmentId;
    }

    public sealed class Diagnostic
    {
        public string Code { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public sealed class Statistics
    {
        public int Target { get; set; }
        public int Groups { get; set; }
        public int Boxes { get; set; }
        public int Corners { get; set; }
        public int SingleShoe { get; set; }
        public int StackedShoe { get; set; }
        public int HangerSegments { get; set; }
        public int LockerCabinets { get; set; }
        public int AuxiliaryCount { get; set; }
        public int Nominal { get; set; }
        public int NominalShoeCells { get; set; }
        public int GeometricAvailable { get; set; }
        public int? Effective { get; set; }
        public int Blocked { get; set; }
        public int GeometryUnverified { get; set; }
        public Dictionary<string,int> GeometricByInstance { get; set; } = new Dictionary<string,int>();
        public int Unverified => Effective.HasValue ? 0 : GeometricAvailable + GeometryUnverified;
        public int? Deficit => Effective.HasValue ? Math.Max(0, Target - Effective.Value) : (int?)null;
        public int ConditionalDeficit => Math.Max(0, Target - GeometricAvailable);
        public int NominalSurplus => Math.Max(0, Nominal - Target);
        public double ProjectionM2 { get; set; }
        public double BoundaryM2 { get; set; }
        public double HangerLengthMm { get; set; }
        public Dictionary<string, CheckState> Checks { get; set; } = new Dictionary<string, CheckState>();
        public List<Diagnostic> Issues { get; set; } = new List<Diagnostic>();
        public string Summary => $"目标{Target} / 名义{Nominal} / 有效{(Effective.HasValue ? Effective.ToString() : "待核")} / 几何可用{GeometricAvailable} / 条件缺口{ConditionalDeficit}";
    }

    public sealed class Candidate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Family { get; set; } = "";
        public List<Placement> Items { get; set; } = new List<Placement>();
        public Statistics Statistics { get; set; } = new Statistics();
        public override string ToString() => Family + " | " + Statistics.Summary;
    }

    public sealed class Room
    {
        public string Id { get; set; } = "";
        public string PlanningPreviewSignature { get; set; } = "";
        public int PlanningPreviewOption { get; set; } = -1;
        public string Name { get; set; } = "";
        public string Gender { get; set; } = "shared";
        public RoomMode Mode { get; set; }
        public int? CustomPeople { get; set; }
        public decimal? ReserveOverride { get; set; }
        public Boundary? Boundary { get; set; }
        public List<Region> Obstacles { get; set; } = new List<Region>();
        public List<Region> Closures { get; set; } = new List<Region>();
        public V2? DirtySeed { get; set; }
        public V2? CleanSeed { get; set; }
        public LayoutOptions Options { get; set; } = new LayoutOptions();
        public List<Candidate> Candidates { get; set; } = new List<Candidate>();
        public int SelectedCandidate { get; set; }
        public List<Placement> Placed { get; set; } = new List<Placement>();
        public bool PreviewVisible { get; set; }
        public bool Stale { get; set; }
        public string ConstraintIssue { get; set; } = "";
        public List<string> AnnotationHandles { get; set; } = new List<string>();
        public Statistics Statistics { get; set; } = new Statistics();
    }

    public sealed class Project
    {
        public const string CurrentSchemaVersion="2.0";
        public string SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public Population Population { get; set; } = new Population();
        public string CurrentRoomId { get; set; } = "shoe_shared";
        public Dictionary<string, BlockMapping> Mappings { get; set; } = new Dictionary<string, BlockMapping>();
        public List<Room> Rooms { get; set; } = new List<Room>();
        [JsonIgnore] public Room Current => Rooms.Single(r => r.Id == CurrentRoomId);
        public void UpgradeSchema()
        {
            if(SchemaVersion!="1.0" && SchemaVersion!=CurrentSchemaVersion)throw new ArgumentException("不支持的项目数据版本。");
            SchemaVersion=CurrentSchemaVersion;
        }
        public static Project Create()
        {
            var p = new Project();
            p.Rooms.Add(new Room { Id = "shoe_shared", Name = "共用换鞋间", Mode = RoomMode.Shoe });
            foreach (string sex in new[] { "male", "female" })
            {
                string prefix = sex == "male" ? "男" : "女";
                p.Rooms.Add(new Room { Id = sex + "_first", Name = prefix + "一更", Gender = sex, Mode = RoomMode.FirstChange });
                p.Rooms.Add(new Room { Id = sex + "_second", Name = prefix + "二更", Gender = sex, Mode = RoomMode.SecondChange });
            }
            return p;
        }
        public void ChangePopulation(Population population)
        {
            population.Validate();
            foreach (Room room in Rooms)
                if (Population.For(room) != population.For(room)) room.Stale = true;
            Population = population;
        }
    }

    public static class Serialization
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        { TypeNameHandling = TypeNameHandling.None, MaxDepth = 96, FloatParseHandling = FloatParseHandling.Double };
        public static string Write<T>(T value) => JsonConvert.SerializeObject(value, Formatting.None, Settings);
        public static T Read<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings) ?? throw new ArgumentException("数据为空或无效。");
        public static T Copy<T>(T value) => Read<T>(Write(value));
    }
}
