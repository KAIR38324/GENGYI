using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Geometries;
using Geo = NetTopologySuite.Geometries.Geometry;

namespace Gengyi.Core
{
    public static class Validation
    {
        public static Statistics Evaluate(Project project, Room room, IReadOnlyList<Placement> items)
        {
            room.Options.Validate();
            var result = new Statistics { Target = project.Population.For(room) };
            foreach (string key in new[] { "geometry", "boundary", "connections", "hygiene_without_crossing", "walking_without_crossing", "workflow_with_crossing", "front_access", "entry_reachability", "source_block", "operation", "installation" })
                result.Checks[key] = CheckState.NotRun;
            if (room.Boundary == null)
            {
                Issue(result, "BOUNDARY_MISSING", "", "尚未选定本房间矩形。");
                result.Checks["boundary"] = CheckState.RequiresInput;
                return result;
            }
            var boundary = Geometry.RoomPolygon(room.Boundary);
            result.BoundaryM2 = boundary.Area / 1000000;
            var physical = new List<Geo>();
            var invalid = new HashSet<string>();
            var obstacles = new List<Geo>();
            bool contextValid = room.ConstraintIssue.Length == 0;
            if (!contextValid) Issue(result, "BOUNDARY_CHANGED_INVALID", "", room.ConstraintIssue);
            foreach (var region in room.Obstacles.Concat(room.Closures))
                try { obstacles.Add(Geometry.Polygon(region.Points)); }
                catch (ArgumentException ex) { contextValid=false; Issue(result, "CONSTRAINT_INVALID", region.NativeHandle, ex.Message); }
            var blockUnion = Geometry.Union(obstacles);
            bool geometryPass = true, boundaryPass = true, sourcePass = true, cornerApprox = false;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var e = item.Equipment;
                int nominal = Capacity.Nominal(item, room.Options);
                if(item.NativeGeometryUnverified || !contextValid)result.GeometryUnverified+=nominal;
                result.Nominal += nominal;
                if(e.IsShoe)result.NominalShoeCells+=e.Capacity*2;
                result.Groups += nominal > 0 ? 1 : 0;
                result.Boxes += e.Boxes;
                if (e.IsShoe) { if (e.Boxes == 2) result.StackedShoe++; else result.SingleShoe++; }
                if (e.Id == "CORNER_R600") result.Corners++;
                if (e.Id == "HANGER_CUSTOM") { result.HangerLengthMm += item.Length; result.HangerSegments++; }
                else if(e.Id=="LOCKER12")result.LockerCabinets++;
                else if(!e.IsShoe && e.Id!="CORNER_R600")result.AuxiliaryCount++;
                Geo shape;
                try { shape = Geometry.Footprint(item); }
                catch (ArgumentException ex)
                {
                    shape = Geometry.Factory.CreatePolygon(); invalid.Add(item.Id); geometryPass = false;
                    Issue(result, "OUTLINE_MISSING", item.Id, ex.Message);
                }
                physical.Add(shape);
                if (item.NativeGeometryUnverified || !contextValid || Math.Abs(item.ScaleX - 1) > 1e-6 || Math.Abs(item.ScaleY - 1) > 1e-6 ||
                    (room.Options.Orthogonal && !Geometry.Orthogonal(item.Rotation)))
                {
                    invalid.Add(item.Id); geometryPass = false;
                    Issue(result, "TRANSFORM_INVALID", item.Id, item.NativeIssue.Length>0?item.NativeIssue:"设备被缩放、镜像、偏离正交或现场约束失效；不能按原柜型认定容量。");
                }
                if (!boundary.Covers(shape) || (!shape.IsEmpty && shape.Intersection(blockUnion).Area > Geometry.AreaTolerance))
                {
                    invalid.Add(item.Id); geometryPass = false;
                    Issue(result, "OUTSIDE_OR_OBSTACLE", item.Id, "柜体越界或与柱、门口/不可占区、固定封闭物重叠。");
                }
                if (room.Mode == RoomMode.Shoe && !shape.IsEmpty && shape.Distance(boundary.Boundary) + 1e-6 < room.Options.ShoeBoundaryMm)
                {
                    invalid.Add(item.Id); boundaryPass = false;
                    Issue(result, "BOUNDARY_CLEARANCE", item.Id, "实际边界距离 " + shape.Distance(boundary.Boundary).ToString("0.###") + " mm，小于 " + room.Options.ShoeBoundaryMm + " mm。");
                }
                for (int j = 0; j < i; j++)
                    if (shape.EnvelopeInternal.Intersects(physical[j].EnvelopeInternal) && shape.Intersection(physical[j]).Area > Geometry.AreaTolerance)
                    {
                        invalid.Add(item.Id); invalid.Add(items[j].Id); geometryPass = false;
                        Issue(result, "CABINET_OVERLAP", item.Id, "与 " + items[j].Id + " 发生实体重叠。");
                    }
                string key = item.MappingKey.Length > 0 ? item.MappingKey : BlockMapping.For(e.Id, item.Length);
                if (item.NativeGeometryUnverified || !project.Mappings.TryGetValue(key, out var mapping) || mapping.Provenance == "legacy_generated" ||
                    !mapping.FootprintVerified || mapping.ReviewStatus != "approved" || mapping.BlockName.Length == 0)
                    sourcePass = false;
                if (e.Id == "CORNER_R600" && item.PhysicalOutline == null) cornerApprox = true;
            }
            var cabinetUnion = Geometry.Union(physical);
            result.Checks["connections"] = CheckState.Pass;
            if(items.Any(p=>p.NativeCopyDetached))
            {
                result.Checks["connections"]=CheckState.RequiresInput;
                Issue(result,"COPIED_GROUP_PENDING","","原生复制已获得独立身份，不与原柜组绑定。请用 GYGROUP 确认复制设备的新连接组或明确解除编组。");
            }
            foreach (var group in items.Select((p,i)=>new { Item=p, Shape=physical[i] }).Where(x=>x.Item.GroupId.Length>0).GroupBy(x=>x.Item.GroupId))
            {
                if (Geometry.Components(Geometry.Union(group.Select(x=>x.Shape))).Count > 1)
                {
                    result.Checks["connections"]=CheckState.Fail;
                    Issue(result,"GROUP_DISCONNECTED",group.Key,"柜链已断开；连接柜、角件与横柜必须在真实接口处连续接合。");
                }
            }
            result.ProjectionM2 = cabinetUnion.Area / 1000000;
            var blockers=cabinetUnion.Union(blockUnion);
            var free = boundary.Difference(blockers);
            var components = Geometry.Components(free);
            var bodyFree=boundary.Buffer(-room.Options.WalkWidthMm/2,32).Difference(blockers.Buffer(room.Options.WalkWidthMm/2,32));
            var walking=Geometry.Components(bodyFree);
            int dirty = room.DirtySeed == null ? -1 : Geometry.Component(components, room.DirtySeed);
            int clean = room.CleanSeed == null ? -1 : Geometry.Component(components, room.CleanSeed);
            int dirtyWalk=room.DirtySeed==null?-1:Geometry.Component(walking,room.DirtySeed);
            int cleanWalk=room.CleanSeed==null?-1:Geometry.Component(walking,room.CleanSeed);
            bool seeds = room.DirtySeed != null && room.CleanSeed != null && dirty >= 0 && clean >= 0;
            bool isolation = seeds && dirty != clean;
            result.Checks["entry_reachability"] = room.Mode==RoomMode.Shoe ? (seeds && dirtyWalk>=0 && cleanWalk>=0?CheckState.Pass:CheckState.RequiresInput) :
                room.DirtySeed==null?CheckState.RequiresInput:dirtyWalk>=0?CheckState.Pass:CheckState.Fail;
            if(room.Mode!=RoomMode.Shoe && dirtyWalk<0)Issue(result,"ENTRY_PENDING","","需指定满足人员宽度的有效入口种子，才能确认柜前/取挂空间可由入口到达。");
            if (room.Mode == RoomMode.Shoe)
            {
                result.Checks["hygiene_without_crossing"] = !seeds ? CheckState.RequiresInput : isolation ? CheckState.Pass : CheckState.Fail;
                if (!seeds) Issue(result, "SEEDS_MISSING_OR_BLOCKED", "", "需指定有效的脏侧入口和净侧出口；种子不得位于柜/墙或边界上。");
                result.Checks["walking_without_crossing"]=dirtyWalk<0 || cleanWalk<0?CheckState.RequiresInput:dirtyWalk==cleanWalk?CheckState.Fail:CheckState.Pass;
                if(!isolation && seeds)
                    Issue(result,dirtyWalk>=0 && dirtyWalk==cleanWalk?"WALKING_BYPASS":"GEOMETRIC_OPENING","",
                        dirtyWalk>=0 && dirtyWalk==cleanWalk?"按配置的人员宽度，不跨柜仍可绕端到另一侧；S 外观不等于隔离。":"实体阻隔有几何开口；人员膨胀后过不去也不能冒充真实封闭，需核对封板/接口。");
                if (cornerApprox && isolation) result.Checks["hygiene_without_crossing"] = CheckState.ApproximateOnly;
            }
            else
            {
                result.Checks["hygiene_without_crossing"] = CheckState.Pass;
                result.Checks["walking_without_crossing"] = CheckState.Pass;
                result.Checks["workflow_with_crossing"] = CheckState.Pass;
            }
            var edges = new Dictionary<int, HashSet<int>>();
            bool allFronts = true;
            var hookSet = new HashSet<string>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i]; var e = item.Equipment;
                int usable = 0;
                if (!invalid.Contains(item.Id) && !physical[i].IsEmpty)
                {
                    var other = blockers.Difference(physical[i]);
                    if (e.Id == "HANGER_CUSTOM")
                    {
                        foreach (V2 hook in Capacity.Hooks(item, room.Options))
                        {
                            string key = Math.Round(hook.X, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Math.Round(hook.Y, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (!hookSet.Add(key)) { Issue(result, "DUPLICATE_HOOK", item.Id, "重合挂点只计一次。"); continue; }
                            // A hanging line has one set of hooks, even when both fronts are accessible.
                            V2 local = new V2((hook - item.Position).Rotate(-item.Rotation).X / item.ScaleX, 0);
                            double reachDepth=room.Options.OperationDepthMm ?? room.Options.PlanningAisleMm;
                            bool accessA = FrontClear(item, local.X - 25, 50, false, boundary, other, reachDepth);
                            bool accessB = FrontClear(item, local.X - 25, 50, true, boundary, other, reachDepth);
                            if(room.DirtySeed!=null)
                            {
                                accessA &= dirtyWalk>=0 && Approach(item,local.X-25,50,false,reachDepth,room.Options.WalkWidthMm,walking).Contains(dirtyWalk);
                                accessB &= dirtyWalk>=0 && Approach(item,local.X-25,50,true,reachDepth,room.Options.WalkWidthMm,walking).Contains(dirtyWalk);
                            }
                            bool access=accessA||accessB;
                            if (access) usable++;
                        }
                    }
                    else if (e.Capacity > 0)
                    {
                        for (int column = 0; column < e.Columns; column++)
                        {
                            double x = column * e.Length / e.Columns, width = e.Length / e.Columns;
                            double depth = room.Options.OperationDepthMm ?? room.Options.PlanningAisleMm;
                            bool a = FrontClear(item, x, width, false, boundary, other, depth);
                            bool b = e.IsShoe && FrontClear(item, x, width, true, boundary, other, depth);
                            if (e.IsShoe)
                            {
                                var faceA = item.Transform(new V2(x + width / 2, -Math.Min(depth / 2, 300)));
                                var faceB = item.Transform(new V2(x + width / 2, e.Depth + Math.Min(depth / 2, 300)));
                                int ca = Geometry.Component(components, faceA), cb = Geometry.Component(components, faceB);
                                if(seeds)
                                {
                                    var wa=Approach(item,x,width,false,depth,room.Options.WalkWidthMm,walking);
                                    var wb=Approach(item,x,width,true,depth,room.Options.WalkWidthMm,walking);
                                    bool reachable=dirtyWalk>=0 && cleanWalk>=0 && (wa.Contains(dirtyWalk) && wb.Contains(cleanWalk) || wa.Contains(cleanWalk) && wb.Contains(dirtyWalk));
                                    a &=reachable;b &=reachable;
                                }
                                if (a && b && (!seeds || isolation && (ca == dirty && cb == clean || ca == clean && cb == dirty))) usable += e.Levels;
                                if (a && b && ca >= 0 && cb >= 0 && ca != cb && item.CrossingEnabled && e.Boxes == 1)
                                {
                                    if (!edges.ContainsKey(ca)) edges[ca] = new HashSet<int>();
                                    if (!edges.ContainsKey(cb)) edges[cb] = new HashSet<int>();
                                    edges[ca].Add(cb); edges[cb].Add(ca);
                                }
                            }
                            else if (a && (room.DirtySeed==null || dirtyWalk>=0 && Approach(item,x,width,false,depth,room.Options.WalkWidthMm,walking).Contains(dirtyWalk))) usable += e.Levels;
                        }
                    }
                }
                result.GeometricAvailable += usable;
                result.GeometricByInstance[item.Id] = usable;
                int lost = Capacity.Nominal(item, room.Options) - usable;
                if (lost > 0)
                {
                    allFronts = false;
                    Issue(result, "CAPACITY_BLOCKED", item.Id, lost + " 个名义位因位置、操作面或脏净配对不可用。");
                }
            }
            if (room.Mode == RoomMode.Shoe)
            {
                var reached = new HashSet<int>(); var queue = new Queue<int>();
                if (dirty >= 0) { reached.Add(dirty); queue.Enqueue(dirty); }
                while (queue.Count > 0)
                {
                    int at = queue.Dequeue();
                    if (edges.TryGetValue(at, out var next)) foreach (int n in next) if (reached.Add(n)) queue.Enqueue(n);
                }
                result.Checks["workflow_with_crossing"] = !seeds ? CheckState.RequiresInput : isolation && reached.Contains(clean) ? CheckState.Pass : CheckState.Fail;
                if (seeds && !reached.Contains(clean)) Issue(result, "CROSSING_PATH_MISSING", "", "加入已允许的单层跨柜节点后仍无完整脏净转换；叠柜不会自动提供跨坐点。");
            }
            result.Blocked = result.Nominal - result.GeometricAvailable - result.GeometryUnverified;
            result.Checks["geometry"] = geometryPass ? CheckState.Pass : CheckState.Fail;
            result.Checks["boundary"] = boundaryPass ? CheckState.Pass : CheckState.Fail;
            result.Checks["front_access"] = allFronts ? CheckState.Pass : CheckState.Fail;
            result.Checks["source_block"] = sourcePass ? CheckState.Pass : CheckState.RequiresInput;
            result.Checks["operation"] = room.Options.OperationDepthMm.HasValue && room.Options.DoorAndOperationVerified ? CheckState.Pass : CheckState.RequiresInput;
            result.Checks["installation"] = room.Mode != RoomMode.SecondChange ||
                room.Options.InstallationVerified && room.Options.InstallationElevationMm.HasValue ? CheckState.Pass : CheckState.RequiresInput;
            bool auxiliaryMissing=room.Mode==RoomMode.SecondChange && (!items.Any(p=>p.EquipmentId=="CHANGING_MIRROR") || !items.Any(p=>p.EquipmentId=="UV_DEVICE"));
            result.Checks["auxiliary_operations"]=result.AuxiliaryCount>0 || auxiliaryMissing?CheckState.RequiresInput:CheckState.Pass;
            if(auxiliaryMissing)Issue(result,"SECOND_CHANGE_AUXILIARY_PENDING","","二更原表中的更衣镜/紫外线设备尚未全部落位；数量、外形待现场/原块确认，不增加挂位。");
            if(result.AuxiliaryCount>0)Issue(result,"AUXILIARY_ACTIVITY_PENDING","","辅助设备按真实占地参与冲突，但其独立安装/使用动作仍需现场核对；不增加容量。");
            if (!sourcePass) Issue(result, "SOURCE_PENDING", "", "存在缺块、重建待比对或占地未核的设备；不能认定原块样式已通过。");
            if (result.Checks["operation"] != CheckState.Pass) Issue(result, "OPERATION_PENDING", "", "当前操作净距是预排参数；需设置真实操作深度并核对柜门/更衣动作。");
            if (room.Mode == RoomMode.SecondChange && result.Checks["installation"] != CheckState.Pass)
                Issue(result, "INSTALLATION_PENDING", "", "吊挂标高、支撑与衣物操作占用待核；650 mm 是吊杆构件长度。");
            bool prerequisites = result.Checks["source_block"] == CheckState.Pass && result.Checks["operation"] == CheckState.Pass &&
                contextValid && result.Checks["entry_reachability"] == CheckState.Pass && result.Checks["connections"] == CheckState.Pass &&
                result.Checks["installation"] == CheckState.Pass && result.Checks["hygiene_without_crossing"] == CheckState.Pass &&
                result.Checks["workflow_with_crossing"] == CheckState.Pass;
            if (prerequisites) result.Effective = result.GeometricAvailable;
            return result;
        }

        private static bool FrontClear(Placement item, double x, double width, bool positive, Geo boundary, Geo other, double depth)
        {
            double y = positive ? item.Equipment.Depth : -depth;
            var points = Geometry.Rect(x + .1, y + (positive ? .1 : 0), Math.Max(.1, width - .2), depth - .1).Select(item.Transform);
            var zone = Geometry.Polygon(points);
            return boundary.Covers(zone) && zone.Intersection(other).Area <= Geometry.AreaTolerance;
        }
        private static HashSet<int> Approach(Placement item,double x,double width,bool positive,double depth,double walkWidth,IReadOnlyList<Polygon> components)
        {
            double offset=Math.Min(depth/2,walkWidth/2+1);
            double y=positive?item.Equipment.Depth+offset:-offset;
            var a=item.Transform(new V2(x+.1,y));var b=item.Transform(new V2(x+width-.1,y));
            var line=Geometry.Factory.CreateLineString(new[]{new Coordinate(a.X,a.Y),new Coordinate(b.X,b.Y)});
            var result=new HashSet<int>();
            for(int i=0;i<components.Count;i++)
                if(components[i].EnvelopeInternal.Intersects(line.EnvelopeInternal) && components[i].Intersection(line).Length>.1)result.Add(i);
            return result;
        }
        private static void Issue(Statistics result, string code, string id, string message) =>
            result.Issues.Add(new Diagnostic { Code = code, InstanceId = id, Message = message });
    }
}
