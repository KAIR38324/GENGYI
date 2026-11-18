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
            foreach (string key in new[] { "geometry", "boundary", "connections", ShoeStacking.CheckKey, LockerAisles.CheckKey, "hygiene_without_crossing", "walking_without_crossing", "workflow_with_crossing", "front_access", RoomAccess.EntryCheck, RoomAccess.ClearanceCheck, "source_block", "operation", "installation" })
                result.Checks[key] = CheckState.NotRun;
            if (room.Boundary == null)
            {
                Issue(result, "BOUNDARY_MISSING", "", "尚未选定本房间边界。");
                result.Checks["boundary"] = CheckState.RequiresInput;
                return result;
            }
            var boundary = Geometry.RoomPolygon(room.Boundary);
            var fitBoundary=room.Mode==RoomMode.FirstChange?boundary.Buffer(.00001,1):boundary;
            result.BoundaryM2 = room.Boundary.AreaM2;
            var physical = new List<Geo>();
            var invalid = new HashSet<string>();
            var stackingIssues=ShoeStacking.Violations(items);
            result.Checks[ShoeStacking.CheckKey]=stackingIssues.Count==0?CheckState.Pass:CheckState.Fail;
            foreach(var issue in stackingIssues){invalid.Add(issue.InstanceId);result.Issues.Add(issue);}
            var lockerAisleIssues=LockerAisles.Violations(room,items);
            result.Checks[LockerAisles.CheckKey]=lockerAisleIssues.Count==0?CheckState.Pass:CheckState.Fail;
            foreach(var issue in lockerAisleIssues){invalid.Add(issue.InstanceId);result.Issues.Add(issue);}
            var obstacles = new List<Geo>();
            bool contextValid = room.ConstraintIssue.Length == 0;
            if (!contextValid) Issue(result, "BOUNDARY_CHANGED_INVALID", "", room.ConstraintIssue);
            foreach (var region in room.Obstacles.Concat(room.Closures))
                try { obstacles.Add(Geometry.RegionFootprint(region)); }
                catch (ArgumentException ex) { contextValid=false; Issue(result, "CONSTRAINT_INVALID", region.NativeHandle, ex.Message); }
            var blockUnion = Geometry.Union(obstacles);
            var accessIssues = new List<Diagnostic>();
            var clearances = RoomAccess.Clearances(room,accessIssues);
            result.Issues.AddRange(accessIssues);
            bool clearancePass = accessIssues.Count==0;
            foreach(var passage in clearances.Where(z=>z.Door==null))
                if(passage.Shape.Intersection(blockUnion).Area>Geometry.AreaTolerance)
                {
                    clearancePass=false;
                    Issue(result,"PASSAGE_FIXED_BLOCKER","",passage.Label+"与固定障碍重叠，请核对通行轮廓。");
                }
            var lockerAngles=room.Mode==RoomMode.FirstChange?Layout.LockerAngles(room.Boundary):Array.Empty<double>();
            bool doorsPass = clearances.Count(z=>z.Door!=null)==room.Doors.Count;
            bool geometryPass = true, boundaryPass = true, sourcePass = true, cornerApprox = false;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var e = item.Equipment;
                string key = item.MappingKey.Length > 0 ? item.MappingKey : BlockMapping.For(e.Id, item.Length);
                bool hasMapping=project.Mappings.TryGetValue(key,out var mapping);
                bool obsoleteDimensions=e.Id==Equipment.LegacyCornerId || (e.IsShoe || e.IsCorner) && hasMapping &&
                    !mapping!.Accepts(item) && (item.MappingKey.Length>0 || item.NativeHandle.Length>0);
                int nominal = Capacity.Nominal(item, room.Options);
                if(item.NativeGeometryUnverified || !contextValid || obsoleteDimensions)result.GeometryUnverified+=nominal;
                result.Nominal += nominal;
                if(e.IsShoe)result.NominalShoeCells+=e.Capacity*2;
                result.Groups += nominal > 0 ? 1 : 0;
                result.Boxes += e.Boxes;
                if (e.IsShoe) { if (e.Boxes == 2) result.StackedShoe++; else result.SingleShoe++; }
                if (e.IsCorner) result.Corners++;
                if (e.Id == "HANGER_CUSTOM") { result.HangerLengthMm += item.Length; result.HangerSegments++; }
                else if(e.Id=="LOCKER12")result.LockerCabinets++;
                else if(!e.IsShoe && !e.IsCorner)result.AuxiliaryCount++;
                Geo shape;
                try { shape = Geometry.Footprint(item); }
                catch (ArgumentException ex)
                {
                    shape = Geometry.Factory.CreatePolygon(); invalid.Add(item.Id); geometryPass = false;
                    Issue(result, "OUTLINE_MISSING", item.Id, ex.Message);
                }
                physical.Add(shape);
                if(obsoleteDimensions)
                {
                    invalid.Add(item.Id);geometryPass=false;
                    Issue(result,"SHOE_DIMENSION_OUTDATED",item.Id,
                        $"设备源块不符合当前{Equipment.ShoeDepthMm:0}mm占地/{Equipment.ShoeDuctMm:0}mm中间通风道及R{Equipment.CornerRadiusMm:0}转角参数；保留旧实体原状，请重新绑定或按新参数重建，不能按新尺寸计为有效。");
                }
                if (item.NativeGeometryUnverified || !contextValid || Math.Abs(item.ScaleX - 1) > 1e-6 || Math.Abs(item.ScaleY - 1) > 1e-6 ||
                    (room.Options.Orthogonal && !Geometry.Orthogonal(item.Rotation) &&
                        !(item.EquipmentId=="LOCKER12" && lockerAngles.Any(angle=>Geometry.Orthogonal(item.Rotation-angle)))))
                {
                    invalid.Add(item.Id); geometryPass = false;
                    Issue(result, "TRANSFORM_INVALID", item.Id, item.NativeIssue.Length>0?item.NativeIssue:"设备被缩放、镜像、偏离正交或现场约束失效；不能按原柜型认定容量。");
                }
                if (!fitBoundary.Covers(shape) || (!shape.IsEmpty && shape.Intersection(blockUnion).Area > Geometry.AreaTolerance))
                {
                    invalid.Add(item.Id); geometryPass = false;
                    Issue(result, "OUTSIDE_OR_OBSTACLE", item.Id, "柜体越界或与柱、门口/不可占区、固定封闭物重叠。");
                }
                if (room.Mode == RoomMode.Shoe && !shape.IsEmpty && shape.Distance(boundary.Boundary) + 1e-6 < room.Options.ShoeBoundaryMm)
                {
                    invalid.Add(item.Id); boundaryPass = false;
                    Issue(result, "BOUNDARY_CLEARANCE", item.Id, "实际边界距离 " + shape.Distance(boundary.Boundary).ToString("0.###") + " mm，小于 " + room.Options.ShoeBoundaryMm + " mm。");
                }
                foreach(var zone in clearances)
                    if (shape.EnvelopeInternal.Intersects(zone.Shape.EnvelopeInternal) &&
                        shape.Intersection(zone.Shape).Area > Geometry.AreaTolerance)
                    {
                        invalid.Add(item.Id);clearancePass=false;
                        if(zone.Door!=null)doorsPass=false;
                        Issue(result,zone.Door==null?"PASSAGE_OCCUPIED":"DOOR_BLOCKED",item.Id,"设备占用"+zone.Label+"的室内净空。");
                    }
                for (int j = 0; j < i; j++)
                    if (shape.EnvelopeInternal.Intersects(physical[j].EnvelopeInternal) && shape.Intersection(physical[j]).Area > Geometry.AreaTolerance)
                    {
                        invalid.Add(item.Id); invalid.Add(items[j].Id); geometryPass = false;
                        Issue(result, "CABINET_OVERLAP", item.Id, "与 " + items[j].Id + " 发生实体重叠。");
                    }
                if (item.NativeGeometryUnverified || !hasMapping || !mapping!.Accepts(item) || mapping.Provenance == "legacy_generated" ||
                    !mapping.FootprintVerified || mapping.ReviewStatus != "approved" || mapping.BlockName.Length == 0)
                    sourcePass = false;
                if (e.IsCorner && item.PhysicalOutline == null) cornerApprox = true;
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
            var walking=RoomAccess.WalkingSpace(boundary,blockers,room.Options.WalkWidthMm);
            var dirtyAreas = new HashSet<int>();
            var cleanAreas = new HashSet<int>();
            var dirtyWalks = new HashSet<int>();
            var cleanWalks = new HashSet<int>();
            foreach(var zone in clearances.Where(z=>z.Door!=null))
            {
                var door = zone.Door!;
                var probe = zone.Probe!;
                int area = Geometry.Component(components,probe);
                if(area<0 || !RoomAccess.TryOpening(blockers,walking,door,zone.Shape,probe,room.Options.WalkWidthMm,out int walk))
                {
                    doorsPass=false;clearancePass=false;
                    Issue(result,"DOOR_ACCESS_FAILED","",zone.Label+"的门宽或门内接入区无法连到室内步行空间，请核对门宽、障碍和柜排。");
                    continue;
                }
                (door.Side==DoorSide.Clean && room.Mode==RoomMode.Shoe?cleanAreas:dirtyAreas).Add(area);
                (door.Side==DoorSide.Clean && room.Mode==RoomMode.Shoe?cleanWalks:dirtyWalks).Add(walk);
            }
            if(room.Doors.Count==0)
            {
                void AddLegacy(V2? seed,HashSet<int> areas,HashSet<int> walks)
                {
                    if(seed==null)return;
                    int area=Geometry.Component(components,seed),walk=Geometry.Component(walking,seed);
                    if(area>=0)areas.Add(area);
                    if(walk>=0)walks.Add(walk);
                }
                AddLegacy(room.DirtySeed,dirtyAreas,dirtyWalks);
                AddLegacy(room.CleanSeed,cleanAreas,cleanWalks);
            }
            bool shoe = room.Mode==RoomMode.Shoe;
            bool hasEntries = room.Doors.Count>0 || room.DirtySeed!=null;
            bool seeds = dirtyAreas.Count>0 && cleanAreas.Count>0;
            bool isolation = seeds && !dirtyAreas.Overlaps(cleanAreas);
            bool walkSeeds = dirtyWalks.Count>0 && (!shoe || cleanWalks.Count>0);
            bool walkBypass = dirtyWalks.Overlaps(cleanWalks);
            result.Checks[RoomAccess.EntryCheck] = !doorsPass?CheckState.Fail:
                walkSeeds?CheckState.Pass:hasEntries && !shoe?CheckState.Fail:CheckState.RequiresInput;
            if(!hasEntries)Issue(result,"ENTRY_PENDING","","请设置房间出入口，才能校核柜前或取挂空间是否可达。");
            if (shoe)
            {
                result.Checks["hygiene_without_crossing"] = !seeds ? CheckState.RequiresInput : isolation ? CheckState.Pass : CheckState.Fail;
                if (!seeds) Issue(result, "SEEDS_MISSING_OR_BLOCKED", "", "请分别设置有效的脏侧出入口和净侧出入口。");
                result.Checks["walking_without_crossing"]=!walkSeeds?CheckState.RequiresInput:walkBypass?CheckState.Fail:CheckState.Pass;
                if(!isolation && seeds)
                    Issue(result,walkBypass?"WALKING_BYPASS":"GEOMETRIC_OPENING","",
                        walkBypass?"按配置的人员宽度，不跨柜仍可绕端到另一侧；S 外观不等于隔离。":"实体阻隔有几何开口；人员膨胀后过不去也不能冒充真实封闭，需核对封板/接口。");
                bool curvedTopology=room.Boundary.CurveToleranceMm>0 || room.Obstacles.Concat(room.Closures).Any(r=>r.CurveToleranceMm>0);
                if ((cornerApprox || curvedTopology) && isolation) result.Checks["hygiene_without_crossing"] = CheckState.ApproximateOnly;
                if(curvedTopology)
                {
                    if(result.Checks["walking_without_crossing"]==CheckState.Pass)result.Checks["walking_without_crossing"]=CheckState.ApproximateOnly;
                    Issue(result,"CURVED_TOPOLOGY_PENDING","","曲线边界/封闭物采用保守离散轮廓；临界缝隙可能受近似影响，不能直接认定脏净隔离已通过，需核对真实曲线与端部接口。");
                }
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
                bool reachableFront = false;
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
                            if(hasEntries)
                            {
                                accessA &= Approach(item,local.X-25,50,false,reachDepth,room.Options.WalkWidthMm,walking).Overlaps(dirtyWalks);
                                accessB &= Approach(item,local.X-25,50,true,reachDepth,room.Options.WalkWidthMm,walking).Overlaps(dirtyWalks);
                            }
                            bool access=accessA||accessB;
                            if (access){usable++;reachableFront=true;}
                        }
                    }
                    else if (e.Capacity > 0)
                    {
                        for (int column = 0; column < e.Columns; column++)
                        {
                            double x = column * e.Length / e.Columns, width = e.Length / e.Columns;
                            double depth = room.Options.OperationDepthMm ?? room.Options.PlanningAisleMm;
                            bool a = FrontClear(item, x, width, false, fitBoundary, other, depth);
                            bool b = e.IsShoe && FrontClear(item, x, width, true, boundary, other, depth);
                            if (e.IsShoe)
                            {
                                var faceA = item.Transform(new V2(x + width / 2, -Math.Min(depth / 2, 300)));
                                var faceB = item.Transform(new V2(x + width / 2, e.Depth + Math.Min(depth / 2, 300)));
                                int ca = Geometry.Component(components, faceA), cb = Geometry.Component(components, faceB);
                                if(seeds || room.Doors.Count>0)
                                {
                                    var wa=Approach(item,x,width,false,depth,room.Options.WalkWidthMm,walking);
                                    var wb=Approach(item,x,width,true,depth,room.Options.WalkWidthMm,walking);
                                    if(a && b && ca>=0 && cb>=0 && ca!=cb && item.CrossingEnabled && e.Boxes==1)
                                        foreach(int from in wa)
                                            foreach(int to in wb.Where(component=>component!=from))
                                            {
                                                if(!edges.ContainsKey(from))edges[from]=new HashSet<int>();
                                                if(!edges.ContainsKey(to))edges[to]=new HashSet<int>();
                                                edges[from].Add(to);edges[to].Add(from);
                                            }
                                    bool aDirty=wa.Overlaps(dirtyWalks), bDirty=wb.Overlaps(dirtyWalks);
                                    bool aClean=wa.Overlaps(cleanWalks), bClean=wb.Overlaps(cleanWalks);
                                    reachableFront |= a && (aDirty || aClean) || b && (bDirty || bClean);
                                    bool reachable=seeds?aDirty && bClean || aClean && bDirty:
                                        (aDirty || aClean) && (bDirty || bClean);
                                    a &=reachable;b &=reachable;
                                }
                                if (a && b && (!seeds || isolation && (dirtyAreas.Contains(ca) && cleanAreas.Contains(cb) ||
                                    cleanAreas.Contains(ca) && dirtyAreas.Contains(cb)))) usable += e.Levels;
                            }
                            else if (a && (!hasEntries || Approach(item,x,width,false,
                                e.Id=="LOCKER12"?Math.Max(depth,room.Options.WalkWidthMm/2+1):depth,
                                room.Options.WalkWidthMm,walking).Overlaps(dirtyWalks)))
                            {usable+=e.Levels;reachableFront=true;}
                        }
                    }
                }
                if(room.Doors.Count>0 && (dirtyWalks.Count>0 || cleanWalks.Count>0) && !reachableFront && Capacity.Nominal(item,room.Options)>0 && !invalid.Contains(item.Id))
                {
                    clearancePass=false;
                    Issue(result,"DOOR_FRONT_UNREACHABLE",item.Id,"设备的操作面无法通向任何有效出入口。");
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
                bool ReachesEachSide(HashSet<int> from, HashSet<int> to)
                {
                    foreach(int entry in from)
                    {
                        var reached = new HashSet<int>{entry}; var queue = new Queue<int>();
                        queue.Enqueue(entry);
                        while (queue.Count > 0)
                        {
                            int at = queue.Dequeue();
                            if (edges.TryGetValue(at, out var next)) foreach (int n in next) if (reached.Add(n)) queue.Enqueue(n);
                        }
                        if(!reached.Overlaps(to))return false;
                    }
                    return true;
                }
                bool crosses = walkSeeds && doorsPass && ReachesEachSide(dirtyWalks,cleanWalks) && ReachesEachSide(cleanWalks,dirtyWalks);
                result.Checks["workflow_with_crossing"] = !seeds ? CheckState.RequiresInput : isolation && crosses ? CheckState.Pass : CheckState.Fail;
                if (seeds && !crosses) Issue(result, "CROSSING_PATH_MISSING", "", "有出入口无法经允许的单层跨柜节点完成脏净转换。");
            }
            result.Checks[RoomAccess.ClearanceCheck]=clearancePass?CheckState.Pass:CheckState.Fail;
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
                contextValid && clearancePass && result.Checks[RoomAccess.EntryCheck] == CheckState.Pass && result.Checks["connections"] == CheckState.Pass &&
                result.Checks["installation"] == CheckState.Pass && result.Checks["hygiene_without_crossing"] == CheckState.Pass &&
                result.Checks["workflow_with_crossing"] == CheckState.Pass && result.Checks[ShoeStacking.CheckKey] == CheckState.Pass &&
                result.Checks[LockerAisles.CheckKey] == CheckState.Pass;
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
        internal static HashSet<int> Approach(Placement item,double x,double width,bool positive,double depth,double walkWidth,IReadOnlyList<Polygon> components)
        {
            double offset=Math.Min(Math.Max(depth/2,walkWidth/2+1),depth);
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
