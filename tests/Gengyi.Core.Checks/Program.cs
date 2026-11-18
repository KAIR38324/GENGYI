using System;
using System.Collections.Generic;
using System.Linq;
using Gengyi.Core;
using Newtonsoft.Json.Linq;

static class Program
{
    static int passed;
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    static Room Make(RoomMode mode, double w=15000, double h=12000) => new Room
    { Id="test", Mode=mode, Boundary=new Boundary { Width=w,Height=h } };
    static List<Placement> ShoeLine(params string[] models)=>models.Select((id,i)=>new Placement
        {Id="cabinet-"+i,EquipmentId=id,Position=new V2(2000+i*2000,2000),CrossingEnabled=id=="S24"}).ToList();
    static void ShoeStackingChecks()
    {
        var legal=ShoeLine("S24","S48","S24");
        Assert(ShoeStacking.Violations(legal).Count==0,"24-48-24 with real end-face contact satisfies the mandatory low-neighbour rule");
        Assert(ShoeStacking.Violations(legal.AsEnumerable().Reverse().ToList()).Count==0,"stacking adjacency uses physical ports, not list order or group IDs");
        var rotated=Serialization.Copy(legal);foreach(var p in rotated){p.Position=p.Position.Rotate(90)+new V2(12000,1000);p.Rotation+=90;}
        Assert(ShoeStacking.Violations(rotated).Count==0,"24-48-24 adjacency survives whole-chain rotation");
        var reverse=Serialization.Copy(legal);reverse[0].Position+=new V2(2000,600);reverse[0].Rotation=180;
        Assert(ShoeStacking.Violations(reverse).Count==0,"opposite source block directions still recognise the same touching end face");
        Assert(ShoeStacking.Violations(ShoeLine("S48","S48","S48")).Select(i=>i.InstanceId).Distinct().Count()==3,"all-48 storage chains are explicitly rejected");
        Assert(ShoeStacking.Violations(ShoeLine("S24","S48","S48","S24")).Select(i=>i.InstanceId).Distinct().Count()==2,"two adjacent 48-person cabinets are both marked invalid");
        Assert(ShoeStacking.Violations(ShoeLine("S48","S24")).Any(i=>i.InstanceId=="cabinet-0"),"an end-position 48-person cabinet cannot claim a missing second low neighbour");
        foreach(string id in new[]{"S12","S18"})
        {
            var shortNeighbour=Serialization.Copy(legal);shortNeighbour[0].EquipmentId=id;shortNeighbour[0].Position.X=4000-shortNeighbour[0].Equipment.Length;
            Assert(ShoeStacking.Violations(shortNeighbour).Count==1,id+" touching the correct end cannot substitute for the required S24");
        }
        var broadside=Serialization.Copy(legal);broadside[0].Position=new V2(4000,2600);
        Assert(ShoeStacking.Violations(broadside).Count==1,"a low cabinet beside a long face is not a cabinet-chain neighbour");
        var gap=Serialization.Copy(legal);gap[0].Position.X-=1;
        Assert(ShoeStacking.Violations(gap).Count==1,"a detached S24 cannot satisfy the stacking rule through an empty gap");
        gap[0].Position.X+=.95;
        Assert(ShoeStacking.Violations(gap).Count==0,"contact tolerance works across spatial index bucket boundaries");
        var automatic=ShoeLine("S24","S24","S24","S24","S24");
        int upgraded=ShoeStacking.StackToTarget(automatic,1000);
        Assert(upgraded==2 && automatic.Select(i=>i.EquipmentId).SequenceEqual(new[]{"S24","S48","S24","S48","S24"}) && ShoeStacking.Violations(automatic).Count==0,"automatic stacking preserves low end cabinets and alternating low neighbours");
        Assert(ShoeStacking.MaximumStackableInRun(new[]{24,24,24,24,24,18,24,24,24})==3,"pre-boundary stack budget includes protected low ends and mixed-module breaks");
        var changed=Serialization.Copy(legal);changed[0].EquipmentId="S48";bool rejected=false;
        try{ShoeStacking.RejectNewViolations(legal,changed);}catch(ArgumentException){rejected=true;}
        Assert(rejected && legal[0].EquipmentId=="S24","changing a required low neighbour to S48 is rejected before drawing mutation");
        rejected=false;try{ShoeStacking.RejectNewViolations(legal,legal.Skip(1).ToList());}catch(ArgumentException){rejected=true;}
        Assert(rejected,"deleting a required S24 neighbour is rejected");
        var legacy=ShoeLine("S48","S48","S48");var repair=Serialization.Copy(legacy);repair[0].EquipmentId="S24";
        bool repaired=true;try{ShoeStacking.RejectNewViolations(legacy,repair);}catch(ArgumentException){repaired=false;}
        Assert(repaired && ShoeStacking.Violations(repair).Count<ShoeStacking.Violations(legacy).Count,"legacy invalid chains can be repaired one cabinet at a time");
        var room=Make(RoomMode.Shoe);var stats=Validation.Evaluate(Project.Create(),room,legacy);
        Assert(stats.Nominal==144 && stats.GeometricAvailable==0 && stats.Effective==null && stats.Checks[ShoeStacking.CheckKey]==CheckState.Fail,"illegal S48 keeps nominal storage visible but cannot contribute usable capacity or certify the room");
        Assert(Validation.Evaluate(Project.Create(),room,legal).Checks[ShoeStacking.CheckKey]==CheckState.Pass,"valid mixed storage is not rejected merely because it contains S48");
        var project=Project.Create();var estimate=AreaPlanning.Estimate(project.Population,project.Rooms[0]);
        Assert(estimate.Options.Any(o=>o.Items.Any(i=>i.EquipmentId=="S48")) && estimate.Options.All(o=>o.Nominal>=600 && ShoeStacking.Violations(o.Items).Count==0),"area previews use the same 24-48-24 rule and still meet the declared target");
        project.Rooms[0].Boundary=new Boundary{Width=10700,Height=10600};var generated=Layout.Generate(project,project.Rooms[0]);
        Assert(generated.Candidates.Any(c=>c.Items.Any(i=>i.EquipmentId=="S48") && c.Statistics.Nominal>=600) && generated.Candidates.All(c=>ShoeStacking.Violations(c.Items).Count==0),"rectangle generation provides genuine target-capacity alternating stacks without any all-48 option");
    }
    static double Distance(double[] a,double[] b)=>Math.Sqrt(a.Select((v,i)=>(v-b[i])*(v-b[i])).Sum());
    static void BoundaryEditingChecks()
    {
        var room=Make(RoomMode.Shoe);
        room.Boundary!.Frame=new Frame{Origin=new[]{100.0,200,300},XAxis=new[]{0.0,1,0},YAxis=new[]{0.0,0,1},MillimetersPerDrawingUnit=1000};
        var item=new Placement{Id="existing",EquipmentId="S24",Position=new V2(1000,2000),Rotation=90,NativeHandle="A1",Locked=true};
        room.Placed.Add(item);room.Obstacles.Add(new Region{NativeHandle="B1",Points=Geometry.Rect(4000,4000,1000,500)});
        room.Closures.Add(new Region{NativeHandle="B2",Points=Geometry.Rect(0,1000,500,4000)});
        room.DirtySeed=new V2(500,500);room.CleanSeed=new V2(6000,7000);
        room.Candidates.Add(new Candidate{Items=new List<Placement>{Serialization.Copy(item)}});room.PreviewVisible=true;
        var old=room.Boundary.Frame;
        var cabinetWorld=Geometry.Outline(item).Select(old.ToWorld).ToArray();
        var obstacleWorld=room.Obstacles[0].Points.Select(old.ToWorld).ToArray();
        var closureWorld=room.Closures[0].Points.Select(old.ToWorld).ToArray();
        var dirty=old.ToWorld(room.DirtySeed);var clean=old.ToWorld(room.CleanSeed);
        var chosen=new Boundary{NativeHandle="CUSTOM",Width=20000,Height=20000,
            Frame=new Frame{Origin=new[]{100.0,205,301},XAxis=new[]{0.0,0,1},YAxis=new[]{0.0,-1,0},MillimetersPerDrawingUnit=1000}};
        BoundaryEditing.Replace(room,chosen);
        var current=room.Boundary!.Frame;
        Assert(Geometry.Outline(item).Select(current.ToWorld).Zip(cabinetWorld,(a,b)=>Distance(a,b)<1e-8).All(v=>v),"reselecting a rotated boundary preserves every cabinet world-space corner in a tilted meter drawing");
        Assert(room.Obstacles[0].Points.Select(current.ToWorld).Zip(obstacleWorld,(a,b)=>Distance(a,b)<1e-8).All(v=>v) && room.Closures[0].Points.Select(current.ToWorld).Zip(closureWorld,(a,b)=>Distance(a,b)<1e-8).All(v=>v),"custom boundary reselection preserves actual obstacle and closure world positions");
        Assert(Distance(current.ToWorld(room.DirtySeed!),dirty)<1e-8 && Distance(current.ToWorld(room.CleanSeed!),clean)<1e-8,"dirty and clean entrance seeds keep their real positions after reselection");
        Assert(item.Id=="existing" && item.NativeHandle=="A1" && item.Locked && room.Placed.Count==1 && room.Boundary.NativeHandle=="CUSTOM","boundary changes retain existing managed block identity and lock state");
        Assert(room.Stale && room.Candidates.Count==0 && !room.PreviewVisible,"explicit reselection invalidates old drafts instead of reusing a layout for another boundary");
        string snapshot=Serialization.Write(room);var invalid=Serialization.Copy(chosen);invalid.Frame.Origin[0]+=1;
        bool rejected=false;try{BoundaryEditing.Replace(room,invalid);}catch(ArgumentException){rejected=true;}
        Assert(rejected && snapshot==Serialization.Write(room),"noncoplanar boundary reselection fails atomically without changing room state");
        var clockwise=Make(RoomMode.FirstChange);var cabinet=new Placement{EquipmentId="LOCKER12",Position=new V2(1000,2000),Rotation=90};clockwise.Placed.Add(cabinet);
        var before=Geometry.Outline(cabinet).Select(clockwise.Boundary!.Frame.ToWorld).ToArray();
        var reverse=new Boundary{Width=15000,Height=12000,Frame=new Frame{Origin=new[]{0.0,12000,0},XAxis=new[]{1.0,0,0},YAxis=new[]{0.0,-1,0}}};
        BoundaryEditing.Replace(clockwise,reverse);
        Assert(Geometry.Outline(cabinet).Select(clockwise.Boundary!.Frame.ToWorld).Zip(before,(a,b)=>Distance(a,b)<1e-7).All(v=>v) && cabinet.ScaleX==1 && cabinet.ScaleY==1,"clockwise user rectangles do not mirror or rescale existing cabinets");
        room.Candidates.Add(new Candidate{Items=new List<Placement>{Serialization.Copy(item)}});room.PreviewVisible=true;
        var candidateWorld=Geometry.Outline(room.Candidates[0].Items[0]).Select(current.ToWorld).ToArray();
        var shifted=Serialization.Copy(room.Boundary);shifted.Frame.Origin[1]+=1;
        BoundaryEditing.Replace(room,shifted,false);
        Assert(room.PreviewVisible && room.Candidates.Count==1 && Geometry.Outline(room.Candidates[0].Items[0]).Select(room.Boundary!.Frame.ToWorld).Zip(candidateWorld,(a,b)=>Distance(a,b)<1e-8).All(v=>v),"native boundary edits retain the preview world geometry while marking it stale");
        var empty=new Room();BoundaryEditing.Replace(empty,new Boundary{Width=12000,Height=8000,NativeHandle="USER"});
        Assert(empty.Boundary!.Width==12000 && empty.Boundary.Height==8000 && empty.Boundary.NativeHandle=="USER","a user's boundary is accepted directly without generating or accepting an area suggestion");
    }
    static void AreaAndIdentificationChecks()
    {
        var project=Project.Create();
        string original=Serialization.Write(project);
        foreach(var mode in new[]{RoomMode.Shoe,RoomMode.FirstChange,RoomMode.SecondChange})
        {
            var room=project.Rooms.First(r=>r.Mode==mode);
            var estimate=AreaPlanning.Estimate(project.Population,room);
            Assert(room.Boundary==null && estimate.Options.Count>=2 && estimate.Options.All(o=>o.Nominal>=estimate.Target && o.AreaM2>o.ProjectionM2),mode+" offers room dimensions before a boundary exists, separate from equipment projection");
            Assert(estimate.Options.All(o=>
            {
                var envelope=Geometry.RoomPolygon(new Boundary{Width=o.WidthMm,Height=o.HeightMm});
                var union=Geometry.Union(o.Items.Select(Geometry.Footprint));
                return envelope.Covers(union) && Math.Abs(union.Area-o.Items.Sum(i=>Geometry.Footprint(i).Area))<Geometry.AreaTolerance;
            }),mode+" estimates contain real-size equipment without overlapping or leaving the envelope");
            Assert(estimate.Options.Select(o=>o.ToString()).SequenceEqual(AreaPlanning.Estimate(project.Population,room).Options.Select(o=>o.ToString())),mode+" palette options and suggested rectangle command have deterministic ordering");
        }
        Assert(Serialization.Write(project)==original,"pre-boundary area preview does not alter room state or create a candidate");
        var shoes=project.Rooms[0];
        var shoeEstimate=AreaPlanning.Estimate(project.Population,shoes);
        Assert(shoeEstimate.Options.Any(o=>Math.Abs(o.WidthMm-10700)<.01 && Math.Abs(o.HeightMm-10600)<.01 && Math.Abs(o.AreaM2-113.42)<.001 && o.Nominal==600),"600-person estimate reproduces documented 6-row R600 planning template");
        Assert(shoeEstimate.Options.Any(o=>o.Items.Any(p=>p.EquipmentId=="S48") && o.Items.Any(p=>p.Equipment.IsShoe && p.CrossingEnabled)),"stacked estimate retains explicit low-cabinet crossing locations");
        shoes.Options.ShoeCapacities=new List<int>{18};shoes.Options.AllowStacking=false;
        Assert(AreaPlanning.Estimate(project.Population,shoes).Options.All(o=>o.Items.Where(i=>i.Equipment.IsShoe).All(i=>i.EquipmentId=="S18")),"area preview respects allowed real shoe modules and stacking opt-out");
        shoes.Options.PlanningAisleMm=2500;
        Assert(AreaPlanning.Estimate(project.Population,shoes).Options.Count==0,"unsupported shoe connector clearance produces an explanation instead of undersized dimensions");
        project=Project.Create();var second=project.Rooms.First(r=>r.Mode==RoomMode.SecondChange);
        second.Options.HangerMaxSegmentMm=350;second.Options.HookStartMarginMm=80;second.Options.HookEndMarginMm=80;
        var segmented=AreaPlanning.Estimate(project.Population,second);
        Assert(segmented.Target==150 && segmented.Options.All(o=>o.Items.Count==75 && o.Nominal==150 && Math.Abs(o.Items.Sum(i=>i.Length)-19500)<.01),"hanging area estimates account for endpoint losses on each short segment");
        second.ReserveOverride=.1m;
        Assert(AreaPlanning.Estimate(project.Population,second).Target==165 && AreaPlanning.Estimate(project.Population,project.Rooms[0]).Target==600,"area preview uses per-room population and reserve rules");
        project.Population=new Population{Registered=0,Shift=0};
        Assert(AreaPlanning.Estimate(project.Population,second).Options.Count==0,"zero-person room does not suggest a misleading nonzero minimum area");
        var single=EquipmentPresentation.Describe(new Placement{EquipmentId="S24"},new LayoutOptions());
        var stacked=EquipmentPresentation.Describe(new Placement{EquipmentId="S48"},new LayoutOptions());
        Assert(single.Title.Contains("24人") && single.Structure.Contains("每侧3层") && single.Detail.Contains("48格") && stacked.Title.Contains("48人") && stacked.Structure.Contains("上下2柜") && stacked.Structure.Contains("每侧6层") && stacked.Detail.Contains("1200mm"),"shoe labels distinguish people, physical cells, per-side levels and stacked height");
        var frozen=new Placement{EquipmentId="HANGER_CUSTOM",Length=1000,LocalHooks=new List<V2>{new V2(50,225),new V2(150,225)}};
        Assert(EquipmentPresentation.Describe(frozen,new LayoutOptions()).Title.Contains("2挂位") && EquipmentPresentation.Describe(new Placement{EquipmentId="LOCKER12"},new LayoutOptions()).Structure.Contains("4层"),"equipment identification follows actual hook inventory and locker storage levels");
        var hookMapping=new BlockMapping{EquipmentId="HANGER_CUSTOM",Length=1000,LocalHooks=Serialization.Copy(frozen.LocalHooks),Key="hanging-source",BlockName="original-hooks",Provenance="original_cad"};
        Assert(hookMapping.Accepts(frozen) && !hookMapping.Accepts(new Placement{EquipmentId="HANGER_CUSTOM",Length=1000,LocalHooks=new List<V2>{new V2(100,225),new V2(200,225)}}),"same-length hanger source must match physical hook positions, not only a mapping alias");
        project.Mappings[BlockMapping.For("HANGER_CUSTOM",1000)]=hookMapping;
        var incompatible=new Placement{EquipmentId="HANGER_CUSTOM",Length=1000,LocalHooks=new List<V2>{new V2(100,225),new V2(200,225)}};
        Layout.ResolveMapping(project,incompatible);
        Assert(incompatible.MappingKey=="" && incompatible.Provenance=="missing","incompatible source stays unbound until a matching block is explicitly connected or reconstructed");
    }
    static void Main(string[] args)
    {
        ShoeStackingChecks();
        BoundaryEditingChecks();
        AreaAndIdentificationChecks();
        var p=Project.Create();
        Assert(p.Rooms.Select(p.Population.For).SequenceEqual(new[]{600,300,150,300,150}),"three room populations 600/300/150");
        p.ChangePopulation(new Population{Registered=601,Shift=301});
        Assert(p.Rooms.Select(p.Population.For).SequenceEqual(new[]{601,301,151,300,150}),"odd roster/shift totals preserved");
        p=Project.Create();
        p.Rooms.Single(r=>r.Id=="male_second").ReserveOverride=.1m;
        Assert(p.Population.For(p.Rooms[0])==600 && p.Population.For(p.Rooms.Single(r=>r.Id=="male_first"))==300 && p.Population.For(p.Rooms.Single(r=>r.Id=="male_second"))==165,"room-specific hanging reserve does not change shoe or personal locker targets");
        p.Rooms.Single(r=>r.Id=="male_second").ReserveOverride=null;
        Assert(Equipment.Get("S24").Capacity==24 && Equipment.Get("S48").Boxes==2 && Equipment.Get("S48").Capacity==48,"shoe paired cells and stack boxes");
        Assert(Capacity.Run(7500,new[]{12,18,24})!.Sum()==90 && Capacity.Run(1250,new[]{12,18,24})==null,"whole module run decomposition");
        var hanger=new Placement{EquipmentId="HANGER_CUSTOM",Length=1000};
        var opt=new LayoutOptions();
        Assert(Capacity.Hooks(hanger,opt).Count==10 && Capacity.Hooks(new Placement{EquipmentId="HANGER_CUSTOM",Length=0},opt).Count==0,"hanger endpoints and zero segment");
        var room=Make(RoomMode.Shoe);
        var shoe=new Placement{EquipmentId="S24",Position=new V2(1000,1000),CrossingEnabled=true};
        var stat=Validation.Evaluate(p,room,new[]{shoe});
        Assert(stat.Nominal==24 && stat.Effective==null && stat.ProjectionM2==1.2,"unverified capacity is never certified");
        shoe.NativeCopyDetached=true;
        Assert(Validation.Evaluate(p,room,new[]{shoe}).Checks["connections"]==CheckState.RequiresInput,"native copied groups cannot silently remain linked to original assemblies");
        shoe.NativeCopyDetached=false;
        room.DirtySeed=new V2(500,500);room.CleanSeed=new V2(4000,4000);
        stat=Validation.Evaluate(p,room,new[]{shoe});
        Assert(stat.Checks["hygiene_without_crossing"]==CheckState.Fail,"open cabinet can be walked around");
        var shoe2=Serialization.Copy(shoe);shoe2.Id="second";shoe2.Position=new V2(1100,1000);
        stat=Validation.Evaluate(p,room,new[]{shoe,shoe2});
        Assert(stat.Checks["geometry"]==CheckState.Fail && stat.GeometricAvailable==0,"overlapping independent cabinets excluded");
        shoe.Position=new V2(999,1000);
        Assert(Validation.Evaluate(p,room,new[]{shoe}).Checks["boundary"]==CheckState.Fail,"999 mm boundary fails");
        room=Make(RoomMode.FirstChange,6000,6000);
        var locker=new Placement{EquipmentId="LOCKER12",Position=new V2(1140,500),Rotation=180};
        stat=Validation.Evaluate(p,room,new[]{locker});
        Assert(stat.Checks["boundary"]==CheckState.Pass && stat.GeometricAvailable==12,"locker wall back does not inherit shoe clearance");
        room.Obstacles.Add(new Region{Points=Geometry.Rect(0,500,1140,1000)});
        stat=Validation.Evaluate(p,room,new[]{locker});
        Assert(stat.GeometricAvailable==0,"blocked locker front loses twelve compartments");
        room=Make(RoomMode.Shoe,10000,6000);room.Options.ShoeBoundaryMm=1000;
        room.DirtySeed=new V2(5000,1000);room.CleanSeed=new V2(5000,5000);
        var barrier=new Placement{EquipmentId="S24",Position=new V2(4000,2700),CrossingEnabled=true};
        room.Closures.Add(new Region{Points=Geometry.Rect(0,2700,4000,600)});
        room.Closures.Add(new Region{Points=Geometry.Rect(6000,2700,4000,600)});
        stat=Validation.Evaluate(p,room,new[]{barrier});
        Assert(stat.Checks["hygiene_without_crossing"]==CheckState.Pass && stat.Checks["workflow_with_crossing"]==CheckState.Pass,"real end closures isolate and a low cabinet reconnects workflow");
        room.Closures.RemoveAt(1);
        Assert(Validation.Evaluate(p,room,new[]{barrier}).Checks["hygiene_without_crossing"]==CheckState.Fail,"removing closure reopens bypass");
        room.Closures.Add(new Region{Points=Geometry.Rect(6010,2700,3990,600)});
        Assert(Validation.Evaluate(p,room,new[]{barrier}).Checks["hygiene_without_crossing"]==CheckState.Fail,"ten millimeter crack is detected without coarse raster");
        room.Closures[1].Points=Geometry.Rect(6000,2700,4000,600);barrier.EquipmentId="S48";barrier.CrossingEnabled=false;
        Assert(Validation.Evaluate(p,room,new[]{barrier}).Checks["workflow_with_crossing"]==CheckState.Fail,"stacked storage does not invent crossing");
        var baseline=p.Rooms[0];baseline.Boundary=new Boundary{Width=10700,Height=10600};
        var before=Serialization.Write(baseline.Placed);
        var search=Layout.Generate(p,baseline);
        Assert(search.Candidates.Count>1 && search.Candidates.Any(c=>c.Statistics.Nominal>=600),"600-person multiple real shoe arrangements");
        Assert(search.Candidates.All(c=>c.Statistics.Checks["geometry"]==CheckState.Pass),"S sectors and connector cabinets do not overlap");
        Assert(search.Candidates.All(c=>c.Statistics.Effective==null) && Serialization.Write(baseline.Placed)==before,"candidate generation leaves placed drawing state intact");
        foreach(var mode in new[]{RoomMode.FirstChange,RoomMode.SecondChange})
        {
            var r=p.Rooms.First(r=>r.Mode==mode);r.Boundary=new Boundary{Width=20000,Height=16000};
            var generated=Layout.Generate(p,r);
            Assert(generated.Candidates.Count>1 && generated.Candidates.Any(c=>c.Statistics.Nominal>=p.Population.For(r)),mode+" generates multiple candidates meeting nominal target");
            Assert(generated.Candidates.Select(c=>Layout.Signature(c.Items)).Distinct().Count()==generated.Candidates.Count,mode+" has no translated duplicate candidates");
        }
        p.Rooms[1].Placed.Add(locker);p.CurrentRoomId=p.Rooms[2].Id;
        var reopened=Serialization.Read<Project>(Serialization.Write(p));
        Assert(reopened.Rooms[1].Placed.Count==1 && reopened.Rooms[2].Placed.Count==0,"room edits survive switch and serialization without leaking");
        var rotated=new Placement{Position=new V2(100,200),Rotation=90};
        Assert(rotated.Transform(new V2(2000,600)).Distance(new V2(-500,2200))<1e-7,"local orthogonal transform preserves dimensions");
        var frozen=new Placement{EquipmentId="HANGER_CUSTOM",Length=1000,LocalHooks=Capacity.Hooks(hanger,opt)};
        opt.HookPitchMm=200;
        Assert(Capacity.Hooks(frozen,opt).Count==10,"existing physical hook inventory does not change when planning pitch changes");
        var stale=new Placement{EquipmentId="S12",MappingKey="old-S24",BlockName="old-cabinet"};
        Layout.ResolveMapping(p,stale);
        Assert(stale.MappingKey=="" && stale.BlockName=="" && stale.Provenance=="missing","changing SKU cannot reuse a stale incompatible native block");
        var pairRoom=Make(RoomMode.Shoe,10000,6000);pairRoom.Options.ShoeBoundaryMm=0;
        var left=new Placement{Id="left",EquipmentId="S24",Position=new V2(2000,2000),GroupId="chain"};
        var right=new Placement{Id="right",EquipmentId="S24",Position=new V2(4000,2000),GroupId="chain"};
        Assert(Validation.Evaluate(p,pairRoom,new[]{left,right}).Checks["connections"]==CheckState.Pass,"exact face contact keeps cabinet chain connected");
        right.Position.X+=10;
        Assert(Validation.Evaluate(p,pairRoom,new[]{left,right}).Checks["connections"]==CheckState.Fail,"dragging a connector apart invalidates chain continuity");
        Assert(search.Candidates.All(c=>c.Statistics.Checks["connections"]==CheckState.Pass),"arc-sector shoe templates form connected native-footprint chains");
        room=Make(RoomMode.SecondChange,6000,6000);
        var h1=new Placement{Id="h1",EquipmentId="HANGER_CUSTOM",Length=1000,Position=new V2(1000,1000)};
        var h2=new Placement{Id="h2",EquipmentId="HANGER_CUSTOM",Length=1000,Position=new V2(1000,4000)};
        stat=Validation.Evaluate(p,room,new[]{h1,h2});
        Assert(stat.Nominal==20 && stat.GeometricAvailable==20 && stat.HangerSegments==2 && stat.Boxes==0,"two hanger segments count actual hooks once without inventing cabinet boxes");
        h1.PhysicalOutline=Geometry.Rect(0,0,1000,25);h1.LocalHooks=new List<V2>{new V2(1000,12.5)};
        h2.PhysicalOutline=Geometry.Rect(0,0,1000,25);h2.Position=new V2(2000,1000);h2.LocalHooks=new List<V2>{new V2(0,12.5)};
        stat=Validation.Evaluate(p,room,new[]{h1,h2});
        Assert(stat.Nominal==2 && stat.GeometricAvailable==1 && stat.Issues.Any(i=>i.Code=="DUPLICATE_HOOK"),"shared endpoint hook coordinates are counted once across separate segments");
        var invalidRoom=Make(RoomMode.FirstChange);
        invalidRoom.Obstacles.Add(new Region{Points=new List<V2>{new V2(0,0),new V2(1000,1000),new V2(1000,0),new V2(0,1000)}});
        Assert(Validation.Evaluate(p,invalidRoom,new[]{locker}).Effective==null,"invalid native constraint cannot certify capacity or stop diagnostics");
        var mapped=new Placement{EquipmentId="LOCKER12",Position=new V2(1140,500),Rotation=180,MappingKey="verified"};
        p.Mappings["verified"]=new BlockMapping{Key="verified",EquipmentId="LOCKER12",BlockName="OriginalLocker",Provenance="original_cad",FootprintVerified=true,ReviewStatus="approved"};
        var closedRoom=Make(RoomMode.FirstChange,6000,6000);closedRoom.Options.OperationDepthMm=1000;closedRoom.Options.DoorAndOperationVerified=true;
        Assert(Validation.Evaluate(p,closedRoom,new[]{mapped}).Effective==null,"verified geometry still requires an actual entrance for usable capacity");
        closedRoom.DirtySeed=new V2(3000,3000);
        Assert(Validation.Evaluate(p,closedRoom,new[]{mapped}).Effective==12,"verified source, operation and reachable entrance enable effective locker capacity");
        mapped.NativeGeometryUnverified=true;mapped.NativeIssue="dynamic state changed";
        Assert(Validation.Evaluate(p,closedRoom,new[]{mapped}).GeometricAvailable==0,"native dynamic change invalidates old cabinet geometry and capacity");
        var ct=new System.Threading.CancellationTokenSource();ct.Cancel();bool cancelled=false;
        try{Layout.Generate(p,baseline,ct.Token);}catch(OperationCanceledException){cancelled=true;}
        Assert(cancelled && Serialization.Write(baseline.Placed)==before,"cancelled solver leaves existing room state intact");
        var tilted=new Frame{Origin=new[]{100.0,200,300},XAxis=new[]{0.0,1,0},YAxis=new[]{0.0,0,1},MillimetersPerDrawingUnit=1000};
        var wcs=tilted.ToWorld(new V2(2000,3000));
        Assert(wcs.SequenceEqual(new[]{100.0,202,303}) && tilted.ToLocal(wcs).Distance(new V2(2000,3000))<1e-7,"tilted UCS in meter drawings round-trips millimeter local coordinates");
        bool offPlane=false;try{tilted.ToLocal(new[]{100.01,202,303});}catch(ArgumentException){offPlane=true;}
        Assert(offPlane,"off-plane picks are rejected instead of flattened");
        var pathRoom=Make(RoomMode.Shoe,6000,6000);pathRoom.DirtySeed=new V2(1000,1000);pathRoom.CleanSeed=new V2(5000,5000);
        pathRoom.Obstacles.Add(new Region{Points=Geometry.Rect(2500,1800,1000,2400)});
        var path=BypassPath.Find(pathRoom,Array.Empty<Placement>());
        Assert(path.Count>2 && path[0].Distance(pathRoom.DirtySeed)<1e-7 && path[path.Count-1].Distance(pathRoom.CleanSeed)<1e-7,"bypass witness routes around obstacles within the configured walking envelope");
        pathRoom.Closures.Add(new Region{Points=Geometry.Rect(0,2700,6000,600)});
        Assert(BypassPath.Find(pathRoom,Array.Empty<Placement>()).Count==0,"witness renderer never crosses a continuous physical barrier");
        var stackedSearch=Layout.Generate(p,baseline);
        Assert(stackedSearch.Candidates.Any(c=>c.Items.Any(i=>i.EquipmentId=="S48")),"allow-stacking search includes genuinely compact stacked alternatives");
        var noStacks=Serialization.Copy(baseline);noStacks.Options.AllowStacking=false;
        Assert(Layout.Generate(p,noStacks).Candidates.All(c=>c.Items.All(i=>i.EquipmentId!="S48")),"stacking opt-out never silently adds S48");
        var obstructed=Make(RoomMode.Shoe,22000,18000);obstructed.Obstacles.Add(new Region{Points=Geometry.Rect(1000,1000,1200,1200)});
        var fitted=Layout.Generate(p,obstructed);
        Assert(fitted.Candidates.Any(c=>c.Statistics.Checks["geometry"]==CheckState.Pass),"candidate fitting searches alternative obstacle-free offsets without adding translated duplicates");
        p=Project.Create();p.ChangePopulation(new Population{Registered=501,Shift=251,Reserve=.1m});
        Assert(p.Population.For(p.Rooms.Single(r=>r.Id=="male_first"))==277 && p.Population.For(p.Rooms.Single(r=>r.Id=="female_first"))==275,"reserve ceilings are applied after integer gender splitting");
        p=Project.Create();var fixtureDir=args.Length==2 && args[0]=="--fixtures"?args[1]:null;
        foreach(var mode in new[]{RoomMode.Shoe,RoomMode.FirstChange,RoomMode.SecondChange})
        {
            var r=p.Rooms.First(r=>r.Mode==mode);r.Boundary=new Boundary{Width=10000,Height=10000};
            r.Placed.Add(new Placement{RoomId=r.Id,EquipmentId=mode==RoomMode.Shoe?"S24":mode==RoomMode.FirstChange?"LOCKER12":"HANGER_CUSTOM",Position=new V2(2000,2000),Length=1000});
            string exported=LayoutExport.Write(p,r);var doc=JObject.Parse(exported);
            Assert((string?)doc["units"]=="mm" && doc["instances"]![0]!["effective_capacity"]!.Type==JTokenType.Null && (string?)doc["validation"]!["source_block"]=="requires_input",mode+" export preserves schema units and unverified status");
            if(fixtureDir!=null){System.IO.Directory.CreateDirectory(fixtureDir);System.IO.File.WriteAllText(System.IO.Path.Combine(fixtureDir,mode+".json"),exported);}
        }
        Console.WriteLine($"{passed} core checks passed");
        if(args.Length>0 && args[0]=="--json")
            Console.WriteLine(JObject.FromObject(new{passed,shoe=search.Candidates.Select(c=>c.Statistics).ToArray()}));
    }
}
