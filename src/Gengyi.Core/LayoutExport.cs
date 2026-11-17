using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gengyi.Core
{
    public static class LayoutExport
    {
        private static string State(CheckState state) => state == CheckState.RequiresInput ? "requires_input" :
            state == CheckState.ApproximateOnly ? "approximate_only" : state == CheckState.NotRun ? "not_run" : state.ToString().ToLowerInvariant();
        public static string Write(Project project, Room room)
        {
            var stats = Validation.Evaluate(project,room,room.Placed);
            var items = new JArray();
            foreach(var p in room.Placed)
            {
                project.Mappings.TryGetValue(p.MappingKey,out var map);
                if(p.Provenance=="original_cad" && (map==null || map.SourceHash.Length!=64 || map.SourceBlockName.Length==0))
                    throw new ArgumentException("原块来源不完整，不能导出为 original_cad："+p.Id);
                items.Add(JObject.FromObject(new {
                    id=p.Id,equipment_id=p.EquipmentId,insert_mm=new[]{p.Position.X,p.Position.Y,0},
                    rotation_deg=Geometry.Normalize(p.Rotation),rotation_mode=room.Options.Orthogonal && Geometry.Orthogonal(p.Rotation)?"orthogonal":"free",
                    block_provenance=p.Provenance,nominal_capacity=Capacity.Nominal(p,room.Options),
                    effective_capacity=stats.Effective.HasValue && stats.GeometricByInstance.TryGetValue(p.Id,out int usable)?(int?)usable:null,
                    capacity_validation=stats.Effective.HasValue?"pass":"requires_input",native_handle=p.NativeHandle,
                    physical_box_count=p.Equipment.Boxes,source_file_sha256=map?.SourceHash,source_block_name=map?.SourceBlockName,
                    physical_cells=p.Equipment.IsShoe?p.Equipment.Capacity*2:0,
                    physical_footprint_local_mm=p.PhysicalOutline,actual_hook_positions_local_mm=p.LocalHooks,
                    crossing_enabled=p.CrossingEnabled,scale=new[]{p.ScaleX,p.ScaleY},group_id=p.GroupId
                }));
            }
            var root = new JObject {
                ["schema_version"]="1.0",["units"]="mm",["room_instance_id"]=room.Id,
                ["room_mode"]=room.Mode==RoomMode.Shoe?"shoe":room.Mode==RoomMode.FirstChange?"first_change":"second_change",
                ["gender_scope"]=room.Gender,["instances"]=items,
                ["validation"]=JObject.FromObject(stats.Checks.ToDictionary(x=>x.Key,x=>State(x.Value))),
                ["statistics"]=JObject.FromObject(stats),["project_state"]=JObject.FromObject(project)
            };
            return root.ToString(Formatting.Indented);
        }
    }
}
