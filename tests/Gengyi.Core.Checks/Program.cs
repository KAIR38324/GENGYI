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
    static void UpdatedDimensionsChecks()
    {
        var single=Equipment.Get("S24");var stacked=Equipment.Get("S48");var locker=Equipment.Get("LOCKER12");
        Assert(single.Length==2000 && single.Depth==650 && single.Height==600 && stacked.Depth==650 && stacked.Height==1200 && Equipment.ShoeDuctMm==50,"650-wide shoe cabinets retain 600/1200 heights and a 50 millimetre central ventilation duct");
        Assert(locker.Length==1140 && locker.Depth==500 && locker.Height==2095 && locker.Capacity==12 && locker.Levels==4,"first-change lockers are exactly 1140 by 500 by 2095 millimetres for twelve people");
        var corner=new Placement{EquipmentId=Equipment.CornerId};var shape=Geometry.Footprint(corner);
        Assert(corner.Equipment.Length==650 && shape.EnvelopeInternal.Width==650 && shape.EnvelopeInternal.Height==650 && Math.Abs(shape.Area-Math.PI*650*650/4)<40,"new corners use a real R650 quarter arc with a matching 650 millimetre port");
        var oldMapping=new BlockMapping{Key="old-600",EquipmentId="S24",EquipmentDepthMm=600,BlockName="Old600",Provenance="original_cad",FootprintVerified=true,ReviewStatus="approved",PhysicalOutline=Geometry.Rect(0,0,2000,600)};
        var current=new Placement{EquipmentId="S24",MappingKey="old-600",PhysicalOutline=Serialization.Copy(oldMapping.PhysicalOutline),Position=new V2(2000,2000)};
        var project=Project.Create();project.Mappings["old-600"]=oldMapping;
        var stats=Validation.Evaluate(project,Make(RoomMode.Shoe),new[]{current});
        Assert(!oldMapping.Accepts(current) && stats.GeometricAvailable==0 && stats.Effective==null && stats.Checks["geometry"]==CheckState.Fail,"old verified 600-wide source geometry cannot silently certify the new 650-wide cabinet");
        oldMapping.EquipmentDepthMm=650;oldMapping.PhysicalOutline=Geometry.Rect(0,0,2000,650);
        Assert(oldMapping.Accepts(current),"new source bindings explicitly record the updated shoe depth");
        var oldCorner=new Placement{EquipmentId=Equipment.LegacyCornerId,Position=new V2(2000,2000)};
        Assert(oldCorner.Equipment.Depth==600 && Validation.Evaluate(project,Make(RoomMode.Shoe),new[]{oldCorner}).Checks["geometry"]==CheckState.Fail,"legacy R600 remains readable at its old size but cannot be mistaken for the updated R650");
        Assert(EquipmentPresentation.Describe(new Placement{EquipmentId="S48"},new LayoutOptions()).CompactLines.Count<=2 &&
            EquipmentPresentation.Describe(new Placement{EquipmentId="S48"},new LayoutOptions()).CompactLines.All(s=>!s.Contains("格") && !s.Contains("mm")) &&
            EquipmentPresentation.Describe(corner,new LayoutOptions()).CompactLines.SequenceEqual(new[]{"R650"}),"on-drawing labels stay compact while retaining cabinet type, people and essential levels");
    }
    static List<V2> LOutline()=>new List<V2>{new V2(0,0),new V2(18000,0),new V2(18000,8000),new V2(8000,8000),new V2(8000,18000),new V2(0,18000)};
    static void LockerAisleChecks()
    {
        var project=Project.Create();var room=Make(RoomMode.FirstChange,6000,6000);
        room.Options.PlanningAisleMm=100;room.Options.OperationDepthMm=100;room.Options.DoorAndOperationVerified=true;
        var facing=new List<Placement>
        {
            new Placement{Id="lower",EquipmentId="LOCKER12",Position=new V2(2140,1500),Rotation=180},
            new Placement{Id="upper",EquipmentId="LOCKER12",Position=new V2(1000,2600)}
        };
        var stats=Validation.Evaluate(project,room,facing);
        Assert(stats.Checks[LockerAisles.CheckKey]==CheckState.Pass && stats.GeometricAvailable==24,
            "two facing locker fronts share one exact 1100 millimetre aisle, without doubling it or including cabinet depths");
        var tight=Serialization.Copy(facing);tight[1].Position.Y-=.1;
        stats=Validation.Evaluate(project,room,tight);
        Assert(stats.Checks["geometry"]==CheckState.Pass && stats.Checks[LockerAisles.CheckKey]==CheckState.Fail && stats.GeometricAvailable==0 && stats.Effective==null &&
            stats.Issues.Where(d=>d.Code=="LOCKER_AISLE_CLEARANCE").Select(d=>d.InstanceId).Distinct().Count()==2,
            "1099.9 millimetres fails the locker aisle rule for both cabinets even with smaller verified planning/operation inputs");
        bool rejected=false;try{LockerAisles.RejectNewViolations(room,facing,tight);}catch(ArgumentException){rejected=true;}
        Assert(rejected,"plugin moves and rotations reject a new undersized locker aisle");
        var narrower=Serialization.Copy(tight);narrower[1].Position.Y=2500;
        rejected=false;try{LockerAisles.RejectNewViolations(room,tight,narrower);}catch(ArgumentException){rejected=true;}
        Assert(rejected,"legacy undersized locker aisles cannot be made narrower by reusing already-invalid cabinet IDs");
        bool repairAllowed=true;try{LockerAisles.RejectNewViolations(room,narrower,tight);LockerAisles.RejectNewViolations(room,tight,facing);}catch(ArgumentException){repairAllowed=false;}
        Assert(repairAllowed,"legacy locker aisles may be widened incrementally until the 1100 millimetre minimum is restored");
        rejected=false;try{LockerAisles.RequireValid(room,tight);}catch(ArgumentException){rejected=true;}
        Assert(rejected,"commit and append cannot certify or insert an undersized locker aisle as a pending draft");
        var rotated=Serialization.Copy(tight);
        foreach(var item in rotated){item.Position=item.Position.Rotate(90)+new V2(5000,0);item.Rotation=Geometry.Normalize(item.Rotation+90);}
        Assert(LockerAisles.Violations(room,rotated).Select(d=>d.InstanceId).Distinct().Count()==2,
            "locker aisle width follows physical rotated fronts rather than global bounding-box axes");
        var touching=new List<Placement>
        {
            new Placement{Id="a",EquipmentId="LOCKER12",Position=new V2(1000,2500)},
            new Placement{Id="b",EquipmentId="LOCKER12",Position=new V2(2140,3500),Rotation=180}
        };
        Assert(LockerAisles.Violations(room,touching).Count==0,"back-to-back locker bodies may touch without a fictitious 1100 millimetre rear aisle");
        touching[1].Position=new V2(2140,2500);touching[1].Rotation=0;
        Assert(LockerAisles.Violations(room,touching).Count==0,"side-by-side lockers may form one continuous row without a fictitious side aisle");
        var single=new Placement{EquipmentId="LOCKER12",Position=new V2(1000,1000)};
        room.Options.OperationDepthMm=1000;
        Assert(Validation.Evaluate(project,room,new[]{single}).GeometricAvailable==12,
            "the inter-cabinet minimum does not impose an unrelated 1100 millimetre wall setback on a lone cabinet");
        Assert(LockerAisles.Violations(Make(RoomMode.Shoe),tight).Count==0,"the first-change aisle rule does not change shoe-room rules");
        room.Options.PlanningAisleMm=1000;room.Options.OperationDepthMm=null;room.CustomPeople=43;
        var estimate=AreaPlanning.Estimate(project.Population,room);
        Assert(estimate.Assumptions.Contains("1100") && estimate.Options.Count>0 && estimate.Options.All(option=>
        {
            var preview=Serialization.Copy(room);preview.Boundary=new Boundary{Width=option.WidthMm,Height=option.HeightMm};
            return Validation.Evaluate(project,preview,option.Items).GeometricAvailable>=96 && LockerAisles.Violations(preview,option.Items).Count==0;
        }),"pre-boundary locker area previews reserve the 1100 millimetre aisle with enough usable cabinet capacity");
        room.Boundary=new Boundary{Width=6000,Height=2100};
        var generated=Layout.Generate(project,room);
        Assert(generated.Candidates.Count>0 && generated.Candidates.All(c=>c.Statistics.Checks[LockerAisles.CheckKey]==CheckState.Pass) && generated.Candidates.Any(c=>c.Statistics.GeometricAvailable>=96),
            "rectangular generation fits two 500 millimetre locker banks around an exact 1100 millimetre shared aisle");
        room.Boundary=new Boundary{Width=6000,Height=1500};room.CustomPeople=20;
        Assert(Layout.Generate(project,room).Candidates.Any(c=>c.Statistics.GeometricAvailable>=project.Population.For(room) &&
            c.Items.All(i=>Geometry.Footprint(i).Distance(Geometry.RoomPolygon(room.Boundary).Boundary)<.00001)),
            "a single wall-backed bank can use a 1000 millimetre operating zone without a second facing bank");
        room.Boundary=BoundaryEditing.FromOutline(LOutline(),new Frame());room.CustomPeople=136;
        generated=Layout.Generate(project,room);
        Assert(generated.Candidates.Count>1 && generated.Candidates.All(c=>c.Statistics.Checks[LockerAisles.CheckKey]==CheckState.Pass) && generated.Candidates.Any(c=>c.Statistics.GeometricAvailable>=300),
            "irregular first-change layouts preserve the cabinet aisle minimum across real room partitions");
    }
    static void RoomAccessChecks()
    {
        var project=Project.Create();
        var room=Make(RoomMode.FirstChange,6000,2100);
        room.CustomPeople=40;
        room.Doors.Add(new DoorOpening{Start=new V2(0,600),End=new V2(0,1500)});
        room.Doors.Add(new DoorOpening{Start=new V2(6000,600),End=new V2(6000,1500)});
        room.Passages.Add(new Region{Points=Geometry.Rect(0,500,6000,1100)});
        var cabinet=new Placement{EquipmentId="LOCKER12",Position=new V2(2280,500),Rotation=180};
        var check=Validation.Evaluate(project,room,new[]{cabinet});
        Assert(check.Checks[RoomAccess.EntryCheck]==CheckState.Pass && !RoomAccess.Failed(check) && check.GeometricAvailable==12,
            "both 900 mm doors accept 600 mm people, and a reserved aisle connects to the front of a flush-wall cabinet");
        var blocked=Serialization.Copy(cabinet);blocked.Position=new V2(6000,1200);
        check=Validation.Evaluate(project,room,new[]{cabinet,blocked});
        Assert(RoomAccess.Failed(check) && check.Checks[RoomAccess.EntryCheck]==CheckState.Fail &&
            check.Issues.Any(i=>i.Code=="DOOR_BLOCKED" && i.Message.Contains("2")),
            "an open first door does not mask the blocked second door");
        bool editRejected=false;
        try{RoomAccess.RejectNewViolations(room,new[]{cabinet},new[]{cabinet,blocked});}catch(ArgumentException){editRejected=true;}
        Assert(editRejected,"manual placement cannot make a new door or passage overlap");
        var generated=Layout.Generate(project,room);
        Assert(generated.Candidates.Count>0 && generated.Candidates.All(c=>!RoomAccess.Failed(c.Statistics)) &&
            generated.Candidates.Any(c=>c.Statistics.GeometricAvailable>=project.Population.For(room) &&
                c.Items.All(i=>Geometry.Footprint(i).Distance(Geometry.RoomPolygon(room.Boundary!).Boundary)<.00001)),
            "automatic placement keeps both doors and the shared aisle clear while fitting enough wall-backed cabinets");

        var shortfall=Serialization.Copy(room);shortfall.CustomPeople=1000;
        var limited=Layout.Generate(project,shortfall);
        Assert(limited.Candidates.Count>0 && limited.Candidates.All(c=>!RoomAccess.Failed(c.Statistics) &&
            c.Statistics.ConditionalDeficit>0),"an undersized room reports a shortfall instead of using door and passage space");
        var frontIsland=Make(RoomMode.FirstChange,6000,5000);
        frontIsland.Doors.Add(new DoorOpening{Start=new V2(1000,0),End=new V2(1900,0)});
        frontIsland.Obstacles.Add(new Region{Points=Geometry.Rect(0,2500,6000,150)});
        var isolated=new Placement{EquipmentId="LOCKER12",Position=new V2(1000,4500)};
        var isolatedStats=Validation.Evaluate(project,frontIsland,new[]{isolated});
        Assert(isolatedStats.Checks[RoomAccess.EntryCheck]==CheckState.Pass && isolatedStats.GeometricAvailable==0 &&
            isolatedStats.Issues.Any(i=>i.Code=="DOOR_FRONT_UNREACHABLE"),
            "a clear door alone does not certify a cabinet disconnected from it");
        var narrow=Serialization.Copy(room);narrow.Doors[1].End.Y=1150;
        Assert(Validation.Evaluate(project,narrow,new[]{cabinet}).Checks[RoomAccess.EntryCheck]==CheckState.Fail,"the narrower second door is checked against the selected walking width");
        var transformed=new Boundary{Width=2100,Height=6000,Frame=new Frame{Origin=new[]{6000d,0,0},
            XAxis=new[]{0d,1,0},YAxis=new[]{-1d,0,0}}};
        var oldFrame=room.Boundary!.Frame;
        var doorWorld=oldFrame.ToWorld(room.Doors[1].Start);
        var passageWorld=oldFrame.ToWorld(room.Passages[0].Points[1]);
        BoundaryEditing.Replace(room,transformed);
        var copy=Serialization.Copy(room);
        Assert(copy.Boundary!.Frame.ToWorld(copy.Doors[1].Start).SequenceEqual(doorWorld) &&
            copy.Boundary.Frame.ToWorld(copy.Passages[0].Points[1]).SequenceEqual(passageWorld) &&
            Validation.Evaluate(project,copy,Array.Empty<Placement>()).Checks[RoomAccess.EntryCheck]==CheckState.Pass,
            "moving the room coordinate frame and serialization preserve the world coordinates of both access inputs");

        var shoe=Make(RoomMode.Shoe,10000,6000);
        shoe.Doors.Add(new DoorOpening{Start=new V2(2000,0),End=new V2(2900,0),Side=DoorSide.Dirty});
        shoe.Doors.Add(new DoorOpening{Start=new V2(7000,0),End=new V2(7900,0),Side=DoorSide.Dirty});
        shoe.Doors.Add(new DoorOpening{Start=new V2(4500,6000),End=new V2(5400,6000),Side=DoorSide.Clean});
        shoe.Closures.Add(new Region{Points=Geometry.Rect(0,2700,4000,Equipment.ShoeDepthMm)});
        shoe.Closures.Add(new Region{Points=Geometry.Rect(6000,2700,4000,Equipment.ShoeDepthMm)});
        var shoeCabinet=new Placement{EquipmentId="S24",Position=new V2(4000,2700),CrossingEnabled=true};
        check=Validation.Evaluate(project,shoe,new[]{shoeCabinet});
        Assert(check.Checks["hygiene_without_crossing"]==CheckState.Pass && check.Checks["workflow_with_crossing"]==CheckState.Pass &&
            check.GeometricAvailable==24,"multiple shoe-room doors preserve the physical dirty/clean separation and low-cabinet crossing");
        shoe.Closures.RemoveAt(1);
        Assert(Validation.Evaluate(project,shoe,new[]{shoeCabinet}).Checks["hygiene_without_crossing"]==CheckState.Fail &&
            BypassPath.Find(shoe,new[]{shoeCabinet}).Count>1,
            "the bypass checker can show a path between explicit dirty and clean doors when the barrier is broken");

        project.SchemaVersion="2.0";
        project.Population.Registered=601;project.Population.Shift=300;project.Population.Reserve=.1m;
        var first=project.Rooms.First(r=>r.Mode==RoomMode.FirstChange);first.ReserveOverride=.25m;
        first.Candidates.Add(new Candidate());first.PreviewVisible=true;
        project.UpgradeSchema();
        Assert(project.Population.PeoplePerShift==300 && project.Population.ShiftCount==2 && project.Population.For(first)==330 &&
            project.Population.Registered==601 && project.Population.Reserve==.1m && first.ReserveOverride==.25m &&
            !first.PreviewVisible && first.Candidates.Count==0,"migration keeps raw legacy fields, applies the fixed factor once and discards old targets");
        first.Candidates.Add(new Candidate());project.ChangePopulation(new Population{ShiftCount=1});
        Assert(first.Candidates.Count==0 && project.Population.For(first)==165 && project.Population.Registered==601,
            "switching shifts invalidates candidates while retaining the original drawing inputs");
    }

    static void TrackAndSearchChecks()
    {
        var project=Project.Create();
        var shoe=Make(RoomMode.Shoe,11500,8500);
        var vertices=new[]{new V2(2000,2000),new V2(8325,2000),new V2(8325,3650),new V2(1675,3650),
            new V2(1675,5300),new V2(8000,5300)};
        Assert(Layout.TrackPoint(shoe,new[]{vertices[0]},new V2(8335,2100)).Distance(vertices[1])<.0001,
            "the first shoe-track point snaps to whole modules with room for the next R650 turn");
        var low=Layout.Track(shoe,vertices,312,false,Array.Empty<V2>(),out string note,false);
        var stacked=Layout.Track(shoe,vertices,312,false,Array.Empty<V2>(),out _,true);
        var stats=Validation.Evaluate(project,shoe,stacked.Items);
        Assert(note.Length==0 && Layout.IsShoeLayout(low.Items) && Layout.IsShoeLayout(stacked.Items) && stats.Nominal==312 &&
            low.Items.Sum(p=>p.Equipment.Capacity)==240 && stats.Corners==4 &&
            stats.Checks["geometry"]==CheckState.Pass && stats.Checks["connections"]==CheckState.Pass &&
            ShoeStacking.Violations(stacked.Items).Count==0,
            "a five-segment shoe track builds an actual S with 240 single-level places and 72 legal stacking places");
        var centre=low.Items.Single(p=>p.Equipment.IsShoe && p.Position.Distance(new V2(4000,1675))<.001);
        var guide=centre.Transform(new V2(1000,325));
        var locked=Layout.Track(shoe,vertices,312,false,new[]{guide},out _,true);
        Assert(locked.Items.Any(p=>p.Locked && p.EquipmentId=="S24" && p.CrossingEnabled) &&
            locked.Items.Sum(p=>p.Equipment.Capacity)==288,"protecting the middle cabinet retains a single-level crossing and removes 24 stacking places");
        Assert(!Layout.IsShoeLayout(Layout.Track(shoe,vertices.Take(4).ToArray(),80,false,Array.Empty<V2>(),out _).Items),
            "a partly drawn track can provide a preview while the completed shoe layout still requires an open S");
        var loop=vertices.ToList();
        loop[loop.Count-1]=new V2(1675,-3000); // This leg does not continue the open S.
        Assert(!Layout.IsShoeLayout(Layout.Track(shoe,loop,0,false,Array.Empty<V2>(),out _).Items),
            "invalid parallel or incomplete legs are never accepted as a full S shape");

        var first=Make(RoomMode.FirstChange,12000,5000);
        var existing=new Placement{EquipmentId="LOCKER12",Position=new V2(4600,4500)};
        first.Placed.Add(existing);
        var wall=new[]{new V2(2000,0),new V2(4300,0)};
        var firstTrack=Layout.Track(first,wall,24,false,Array.Empty<V2>(),out _);
        var flipped=Layout.Track(first,wall,24,true,Array.Empty<V2>(),out _);
        Assert(firstTrack.Items.Count==2 && firstTrack.AppendToPlaced && firstTrack.Scene(first).Count==3 &&
            Validation.Evaluate(project,first,firstTrack.Scene(first)).GeometricAvailable==36 &&
            flipped.Items.All(p=>GeoMinY(p)<0),"locker track follows cabinet backs, flips sides and validates existing plus added cabinets");
        Assert(Serialization.Copy(firstTrack).AppendToPlaced,"saving a track draft preserves its append semantics");
        double GeoMinY(Placement p)=>Geometry.Footprint(p).EnvelopeInternal.MinY;

        first.Placed.Clear();
        first.CustomPeople=5;
        first.Doors.Add(new DoorOpening{Start=new V2(5550,0),End=new V2(6450,0)});
        first.Obstacles.Add(new Region{Points=Geometry.Rect(3900,0,200,5000)});
        first.Obstacles.Add(new Region{Points=Geometry.Rect(7900,0,200,5000)});
        Assert(Layout.Generate(project,first).Candidates.Any(c=>c.Statistics.GeometricAvailable>=12),
            "one-change generation reaches the middle compartment connected to the door before spending its target in isolated compartments");
        var skew=Make(RoomMode.FirstChange);
        skew.Boundary=BoundaryEditing.FromOutline(Geometry.Rect(0,0,6500,2150).Select(p=>p.Rotate(45)).ToList(),new Frame());
        skew.CustomPeople=50;
        var oriented=Layout.Generate(project,skew);
        Assert(oriented.Candidates.Any(c=>c.Statistics.GeometricAvailable>=110 && c.Items.All(p=>Geometry.Orthogonal(p.Rotation-45))),
            "first-change generation follows sloped straight walls even when the drawing axes are unchanged");

        var stepped=Make(RoomMode.Shoe,7850,5950);
        stepped.CustomPeople=68;stepped.Options.AllowStacking=false;
        stepped.Obstacles.Add(new Region{Points=Geometry.Rect(0,0,3150,1650)});
        stepped.Obstacles.Add(new Region{Points=Geometry.Rect(4650,4300,3200,1650)});
        Assert(Layout.Generate(project,stepped).Candidates.Any(c=>c.Statistics.GeometricAvailable>=150),
            "allowing different end-run lengths recovers the 150-place stepped shoe-room layout");
        var aligned=Make(RoomMode.Shoe,6800,7700);
        aligned.CustomPeople=60;aligned.Options.AllowStacking=false;
        aligned.Doors.Add(new DoorOpening{Start=new V2(1150,0),End=new V2(2050,0),Side=DoorSide.Dirty});
        aligned.Doors.Add(new DoorOpening{Start=new V2(4750,7700),End=new V2(5650,7700),Side=DoorSide.Clean});
        aligned.Closures.Add(new Region{Points=Geometry.Rect(0,1750,1750,650)});
        aligned.Closures.Add(new Region{Points=Geometry.Rect(4750,5050,2050,650)});
        Assert(Layout.Generate(project,aligned).Candidates.First().Statistics.Checks["workflow_with_crossing"]==CheckState.Pass,
            "existing partitions guide terminal offsets and proven access is preferred when ranking shoe plans");

        var loopItems=low.Items.Where(p=>p.Equipment.IsShoe && p.Rotation%180==0 && p.Position.Y<=3325 ||
            p.Equipment.IsCorner && p.Position.Y<=3325).ToList();
        Assert(!Layout.IsShoeLayout(loopItems),"a broken chain cannot pass the two-open-end S rule");
        var actualLoop=Make(RoomMode.Shoe,6800,6000);
        Placement P(string id,double x,double y,double rotation=0)=>new Placement{EquipmentId=id,Position=new V2(x,y),Rotation=rotation,GroupId="loop"};
        var closed=new[]{P("S18",1750,1000),P("S18",3250,1000),P("S18",1750,2650),P("S18",3250,2650),
            P(Equipment.CornerId,4750,1650,270),P("S12",5400,1650,90),P(Equipment.CornerId,4750,2650),
            P(Equipment.CornerId,1750,1650,180),P("S12",1750,1650,90),P(Equipment.CornerId,1750,2650,90)};
        Assert(!Layout.IsShoeLayout(closed) && Validation.Evaluate(project,actualLoop,closed).Checks["connections"]==CheckState.Pass,
            "four R650 corners can form a closed loop but port topology rejects it as an S");

        var opening=Make(RoomMode.FirstChange,5000,3500);
        opening.Doors.Add(new DoorOpening{Start=new V2(1000,0),End=new V2(1900,0)});
        opening.Obstacles.Add(new Region{Points=Geometry.Rect(1000,800,900,500)});
        Assert(Validation.Evaluate(project,opening,Array.Empty<Placement>()).Checks[RoomAccess.EntryCheck]==CheckState.Pass,
            "a 900 mm opening can lead into a turn at 800 mm with a 600 mm walking width");
        opening.Passages.Add(new Region{Points=Geometry.Rect(1000,650,900,2000)});
        Assert(Validation.Evaluate(project,opening,Array.Empty<Placement>()).Issues.Any(i=>i.Code=="PASSAGE_FIXED_BLOCKER"),
            "a fixed obstruction in the designated passage is reported");
        var secondDoor=Make(RoomMode.Shoe,10000,6000);
        secondDoor.Doors.Add(new DoorOpening{Start=new V2(1000,0),End=new V2(1900,0),Side=DoorSide.Dirty});
        secondDoor.Doors.Add(new DoorOpening{Start=new V2(8700,0),End=new V2(9600,0),Side=DoorSide.Dirty});
        secondDoor.Doors.Add(new DoorOpening{Start=new V2(4600,6000),End=new V2(5500,6000),Side=DoorSide.Clean});
        foreach(var rect in new[]{Geometry.Rect(0,2700,4000,650),Geometry.Rect(6000,2700,4000,650),Geometry.Rect(8000,0,100,2200)})
            secondDoor.Closures.Add(new Region{Points=rect});
        var crossing=new Placement{EquipmentId="S24",Position=new V2(4000,2700),CrossingEnabled=true};
        Assert(Validation.Evaluate(project,secondDoor,new[]{crossing}).Checks["workflow_with_crossing"]==CheckState.Fail,
            "every selected door must reach a crossing through its own body-width walking component");
    }

    static void ShoeShapeWithDoorsChecks()
    {
        var project=Project.Create();
        project.ChangePopulation(new Population{ShiftCount=1,PeoplePerShift=300});
        var room=project.Current;
        room.Options.AllowStacking=false;
        room.Boundary=BoundaryEditing.FromOutline(new[]{new V2(0,0),new V2(30000,0),new V2(30000,3500),
            new V2(7500,3500),new V2(7500,9000),new V2(0,9000)},new Frame());
        room.Doors.Add(new DoorOpening{Start=new V2(1700,0),End=new V2(2600,0),Side=DoorSide.Dirty});
        room.Doors.Add(new DoorOpening{Start=new V2(1700,9000),End=new V2(2600,9000),Side=DoorSide.Clean});

        bool Complete(Candidate c)=>Layout.IsShoeLayout(c.Items) && c.Items.GroupBy(p=>p.GroupId).Count()==1 &&
            c.Statistics.Corners>=4 && c.Statistics.Checks["connections"]==CheckState.Pass &&
            c.Statistics.Checks["geometry"]==CheckState.Pass && c.Statistics.Checks["boundary"]==CheckState.Pass &&
            c.Statistics.Checks[RoomAccess.EntryCheck]==CheckState.Pass && !RoomAccess.Failed(c.Statistics) &&
            c.Items.All(p=>p.ScaleX==1 && p.ScaleY==1 && p.EquipmentId!="S48");
        var atDoors=Layout.Generate(project,room);
        Assert(atDoors.Candidates.Count>0 && atDoors.Candidates.All(Complete),
            "a wide branch with two doors yields only connected S chains with alternating R650 turns even when a long wing dominates the floor area");
        Assert(atDoors.Candidates.All(c=>c.Statistics.Checks["hygiene_without_crossing"]==CheckState.Fail && c.Statistics.Effective==null),
            "geometrically fitting a serpentine chain does not certify dirty/clean isolation when both doors can walk around it");

        var mixed=Serialization.Copy(atDoors.Candidates[0].Items);
        mixed.Add(new Placement{RoomId=room.Id,EquipmentId="S24",GroupId="straight-run",Position=new V2(15000,1400)});
        Assert(!Layout.IsShoeLayout(mixed) && !Layout.IsShoeLayout(mixed.Where(p=>p.GroupId=="straight-run")),
            "old straight and mixed S/straight drafts are no longer accepted as an S-shaped layout");

        room.Boundary=BoundaryEditing.FromOutline(new[]{new V2(0,0),new V2(6600,0),new V2(6600,4200),
            new V2(6370,4200),new V2(6370,5950),new V2(0,5950)},new Frame());
        room.Doors.Clear();
        room.Doors.Add(new DoorOpening{Start=new V2(0,1700),End=new V2(0,2600),Side=DoorSide.Dirty});
        room.Doors.Add(new DoorOpening{Start=new V2(1600,5950),End=new V2(2500,5950),Side=DoorSide.Clean});
        room.Passages.Add(new Region{Points=Geometry.Rect(0,1700,4000,900)});
        var passage=Geometry.RegionFootprint(room.Passages[0]);
        var constrained=Layout.Generate(project,room);
        Assert(constrained.Candidates.Count>0 && constrained.Candidates.All(Complete) &&
            constrained.Candidates.Any(c=>c.Family.Contains("横向") && c.Statistics.Nominal<project.Population.For(room) &&
                c.Items.All(p=>Geometry.Footprint(p).Intersection(passage).Area<=Geometry.AreaTolerance) &&
                Geometry.Factory.ToGeometry(Geometry.Union(c.Items.Select(Geometry.Footprint)).EnvelopeInternal)
                    .Intersection(passage).Area>Geometry.AreaTolerance),
            "the real cabinet and corner footprints fit around a reserved aisle that cuts through the S chain's enclosing rectangle");
    }

    static void IrregularBoundaryAndWorkflowChecks()
    {
        var outline=LOutline();var boundary=BoundaryEditing.FromOutline(outline,new Frame(),"USER-L");
        Assert(boundary.Width==18000 && boundary.Height==18000 && Math.Abs(boundary.AreaM2-224)<1e-8,"concave room reports actual 224 square metres instead of its 324 square metre bounding box");
        var room=Make(RoomMode.Shoe);room.Boundary=boundary;
        var notch=new Placement{EquipmentId="S24",Position=new V2(12000,12000)};
        var stats=Validation.Evaluate(Project.Create(),room,new[]{notch});
        Assert(stats.Nominal==24 && stats.GeometricAvailable==0 && stats.Checks["geometry"]==CheckState.Fail,"equipment inside the envelope but in the missing corner cannot count as available capacity");
        var near=new Placement{EquipmentId="S24",Position=new V2(5500,12000)};
        Assert(Validation.Evaluate(Project.Create(),room,new[]{near}).Checks["boundary"]==CheckState.Fail,"shoe boundary distance is measured to the concave inner wall");
        near.Position.X=5000;
        Assert(Validation.Evaluate(Project.Create(),room,new[]{near}).Checks["boundary"]==CheckState.Pass,"a true 1000 millimetre clearance to an inner concave wall is accepted");
        var first=Make(RoomMode.FirstChange);first.Boundary=Serialization.Copy(boundary);
        var locker=new Placement{EquipmentId="LOCKER12",Position=new V2(9000,7500),Rotation=180};
        stats=Validation.Evaluate(Project.Create(),first,new[]{locker});
        Assert(stats.Checks["geometry"]==CheckState.Pass && stats.GeometricAvailable==0,"a concave missing corner blocks an operation zone even when the whole locker body fits");
        var changed=BoundaryEditing.FromOutline(new List<V2>{new V2(0,0),new V2(18000,0),new V2(18000,8000),new V2(7000,8000),new V2(7000,18000),new V2(0,18000)},new Frame());
        Assert(boundary.Width==changed.Width && boundary.Height==changed.Height && boundary.AreaM2!=changed.AreaM2 &&
            Geometry.RoomPolygon(boundary).Covers(Geometry.Factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(7500,12000))) &&
            !Geometry.RoomPolygon(changed).Covers(Geometry.Factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(7500,12000))),"changing a notch changes the real room even when both envelope dimensions stay the same");
        var frame=new Frame{Origin=new[]{100.0,200,300},XAxis=new[]{0.0,1,0},YAxis=new[]{0.0,0,1},MillimetersPerDrawingUnit=1000};
        var negative=outline.Select(p=>p-new V2(5000,3000)).ToList();var normalized=BoundaryEditing.FromOutline(negative,frame);
        Assert(Geometry.RoomOutline(normalized).Select(normalized.Frame.ToWorld).Zip(negative.Select(frame.ToWorld),(a,b)=>Distance(a,b)<1e-8).All(v=>v) &&
            Geometry.RoomOutline(normalized).Min(p=>p.X)==0 && Geometry.RoomOutline(normalized).Min(p=>p.Y)==0,"negative polygon coordinates normalize without moving their world outline in a tilted meter drawing");
        var reverse=Serialization.Copy(normalized);reverse.Frame.YAxis=reverse.Frame.YAxis.Select(v=>-v).ToArray();
        var before=Geometry.RoomOutline(reverse).Select(reverse.Frame.ToWorld).ToArray();var aligned=BoundaryEditing.AlignOrientation(reverse,normalized.Frame);
        Assert(Geometry.RoomOutline(aligned).Select(aligned.Frame.ToWorld).Zip(before,(a,b)=>Distance(a,b)<1e-8).All(v=>v),"normal reversal transforms irregular vertices along with the frame, preserving the physical room");
        bool rejected=false;try{BoundaryEditing.FromOutline(new[]{new V2(0,0),new V2(1000,1000),new V2(1000,0),new V2(0,1000)},new Frame());}catch(ArgumentException){rejected=true;}
        Assert(rejected,"self-intersecting room outlines are rejected instead of replaced by their envelope");
        var legacy=Serialization.Read<Boundary>("{\"Width\":12000,\"Height\":8000}");
        Assert(legacy.Outline==null && Math.Abs(legacy.AreaM2-96)<1e-8,"existing rectangular drawings remain readable without polygon fields");
        var oldProject=Project.Create();oldProject.SchemaVersion="1.0";oldProject.Population.Registered=501;oldProject.Rooms[0].Boundary=legacy;oldProject.UpgradeSchema();
        Assert(oldProject.SchemaVersion==Project.CurrentSchemaVersion && oldProject.Population.Registered==501 && oldProject.Rooms[0].Boundary!.Outline==null,"legacy project upgrade preserves rectangle and population while protecting the new polygon format from older readers");
        oldProject.SchemaVersion="future";string oldSnapshot=Serialization.Write(oldProject);rejected=false;
        try{oldProject.UpgradeSchema();}catch(ArgumentException){rejected=true;}
        Assert(rejected && oldSnapshot==Serialization.Write(oldProject),"unknown project formats are rejected without changing saved fields");
        var curved=BoundaryEditing.FromOutline(Geometry.Rect(0,0,9999.9,9999.9),new Frame());curved.SourceAreaMm2=100000000;curved.CurveToleranceMm=.05;
        first.Boundary=curved;
        Assert(Math.Abs(curved.AreaM2-100)<1e-8 && Geometry.RoomPolygon(curved).Area<100000000 && Math.Abs(Validation.Evaluate(Project.Create(),first,Array.Empty<Placement>()).BoundaryM2-100)<1e-8,"native curve measurement is reported separately from the conservative planning outline");
        var project=Project.Create();var r=project.Rooms[0];
        string signature=AreaPlanning.Signature(project.Population,r);r.PlanningPreviewSignature=signature;r.PlanningPreviewOption=0;
        BoundaryEditing.Replace(r,boundary);
        Assert(AreaPlanning.IsPreviewCurrent(project.Population,r,0) && !AreaPlanning.IsPreviewCurrent(project.Population,r,1),"pre-boundary preview remains current after choosing the actual outline, but not after changing reference option");
        project.Population.MaleRatio=.50m;project.Population.Reserve=.00m;r.Options.ShoeCapacities.Reverse();
        Assert(AreaPlanning.IsPreviewCurrent(project.Population,r,0),"equivalent decimal input scales and cabinet option order do not invalidate a completed preview");
        r.Options.AllowStacking=!r.Options.AllowStacking;
        Assert(!AreaPlanning.IsPreviewCurrent(project.Population,r,0),"changing the stacking decision requires a new pre-boundary preview");
        r.Options.AllowStacking=!r.Options.AllowStacking;project.Population.PeoplePerShift++;
        Assert(!AreaPlanning.IsPreviewCurrent(project.Population,r,0),"changing the people target invalidates the old planning preview");
        project.Population.PeoplePerShift--;r.Candidates.Add(new Candidate());
        Assert(AreaPlanning.IsPreviewCurrent(project.Population,r,0),"preview completion does not depend on random candidate or equipment IDs");
        r.PreviewVisible=true;
        var restored=Serialization.Read<Project>(Serialization.Write(project));
        Assert(restored.Rooms[0].Boundary!.Outline!.Count==6 && restored.Rooms[0].PlanningPreviewOption==0 && AreaPlanning.IsPreviewCurrent(restored.Population,restored.Rooms[0],0),"actual polygon and workflow state survive save and reopen together");
        var legacyPreview=Serialization.Read<Room>("{\"PreviewVisible\":true,\"SelectedCandidate\":0,\"Candidates\":[{}]}");
        Assert(!restored.Rooms[0].PreviewVisible && restored.Rooms[0].Candidates.Count==1 &&
            !legacyPreview.PreviewVisible && legacyPreview.Candidates.Count==1,
            "candidate data survives reopening without restoring transient visibility from current or legacy drawing data");
        string exported=LayoutExport.Write(project,r);var document=JObject.Parse(exported);
        Assert((int)document["project_state"]!["Rooms"]![0]!["Boundary"]!["Outline"]!.Count()==6 && Math.Abs((double)document["statistics"]!["BoundaryM2"]!-224)<1e-8,"export retains the real concave outline and actual measured room area");
        var triangle=BoundaryEditing.FromOutline(new[]{new V2(0,0),new V2(18000,0),new V2(12000,16000),new V2(0,16000)},new Frame());
        room.Boundary=triangle;near.Position=new V2(10500,12000);
        Assert(Validation.Evaluate(Project.Create(),room,new[]{near}).Checks["boundary"]==CheckState.Fail,"clearance to a sloping wall uses the true diagonal distance");
        room.Boundary=boundary;room.DirtySeed=new V2(15000,4000);room.CleanSeed=new V2(4000,15000);
        var path=BypassPath.Find(room,Array.Empty<Placement>());var walking=Geometry.RoomPolygon(boundary).Buffer(-room.Options.WalkWidthMm/2,32);
        Assert(path.Count>2 && path.Zip(path.Skip(1),(a,b)=>walking.Covers(Geometry.Factory.CreateLineString(new[]{new NetTopologySuite.Geometries.Coordinate(a.X,a.Y),new NetTopologySuite.Geometries.Coordinate(b.X,b.Y)}))).All(v=>v),"walking witnesses bend around a missing corner instead of taking a bounding-box shortcut");
        foreach(var mode in new[]{RoomMode.Shoe,RoomMode.FirstChange,RoomMode.SecondChange})
        {
            project=Project.Create();r=project.Rooms.First(rm=>rm.Mode==mode);r.Boundary=Serialization.Copy(boundary);
            string snapshot=Serialization.Write(project);var generated=Layout.Generate(project,r);
            Assert(generated.Candidates.Count>1 && generated.Candidates.Any(c=>c.Statistics.GeometricAvailable>=project.Population.For(r)),mode+" has multiple concave-room arrangements with enough geometrically usable capacity");
            Assert(generated.Candidates.All(c=>c.Statistics.Checks["geometry"]==CheckState.Pass && c.Statistics.Checks["boundary"]==CheckState.Pass && c.Items.All(i=>Geometry.RoomPolygon(r.Boundary).Covers(Geometry.Footprint(i)) && i.ScaleX==1 && i.ScaleY==1)),mode+" complete equipment footprints remain in the real room without shrinking or clipping");
            Assert(snapshot==Serialization.Write(project),mode+" polygon search does not mutate saved room or preview state");
        }
        project=Project.Create();r=project.Rooms[0];r.Boundary=Serialization.Copy(boundary);r.Options.AllowStacking=false;
        var lowOnly=Layout.Generate(project,r);
        Assert(lowOnly.Candidates.Count>0 && lowOnly.Candidates.All(c=>c.Items.All(i=>i.EquipmentId!="S48")),"the visible no-stacking choice is respected by irregular-room generation");
        r.Boundary=triangle;
        var sloped=Layout.Generate(project,r);
        Assert(sloped.Candidates.Count>0 && sloped.Candidates.All(c=>c.Items.All(i=>Geometry.RoomPolygon(triangle).Covers(Geometry.Footprint(i)) && Geometry.Footprint(i).Distance(Geometry.RoomPolygon(triangle).Boundary)+1e-6>=1000)),"sloped-room candidates preserve full footprints and true 1000 millimetre wall clearance");
        foreach(var mode in new[]{RoomMode.Shoe,RoomMode.FirstChange,RoomMode.SecondChange})
        {
            project=Project.Create();r=project.Rooms.First(rm=>rm.Mode==mode);r.Boundary=Serialization.Copy(boundary);
            r.Obstacles.Add(new Region{Points=Geometry.Rect(4000,3000,2000,3000)});
            var obstacle=Geometry.Polygon(r.Obstacles[0].Points);var result=Layout.Generate(project,r);
            Assert(result.Candidates.Count>0 && result.Candidates.All(c=>c.Items.All(i=>Geometry.RoomPolygon(boundary).Covers(Geometry.Footprint(i)) && Geometry.Footprint(i).Intersection(obstacle).Area<=Geometry.AreaTolerance)),mode+" polygon planning avoids actual interior obstacles as well as the outer missing corner");
        }
        project=Project.Create();r=project.Rooms[0];r.Boundary=BoundaryEditing.FromOutline(new[]{new V2(0,0),new V2(7000,0),new V2(7000,3000),new V2(3000,3000),new V2(3000,7000),new V2(0,7000)},new Frame());
        var small=Layout.Generate(project,r);
        Assert(small.Candidates.Count==0 && small.Message.Contains("完整 S 形"),
            "an undersized irregular room reports that no complete S shape fits without offering long straight runs");
        var cancelled=new System.Threading.CancellationTokenSource();cancelled.Cancel();string state=Serialization.Write(project);bool stopped=false;
        try{Layout.Generate(project,r,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
        Assert(stopped && state==Serialization.Write(project),"cancelled irregular-room generation leaves every saved field untouched");
        var curveRoom=Make(RoomMode.Shoe,6000,6000);curveRoom.Boundary!.CurveToleranceMm=.05;
        curveRoom.DirtySeed=new V2(1000,1000);curveRoom.CleanSeed=new V2(5000,5000);
        curveRoom.Closures.Add(new Region{Points=Geometry.Rect(0,2700,2000,Equipment.ShoeDepthMm)});curveRoom.Closures.Add(new Region{Points=Geometry.Rect(4000,2700,2000,Equipment.ShoeDepthMm)});
        var cross=new Placement{EquipmentId="S24",Position=new V2(2000,2700),CrossingEnabled=true};
        stats=Validation.Evaluate(Project.Create(),curveRoom,new[]{cross});
        Assert(stats.Checks["hygiene_without_crossing"]==CheckState.ApproximateOnly && stats.Effective==null,"conservatively approximated curve boundaries cannot falsely certify complete dirty-clean isolation");
        curveRoom.Boundary.CurveToleranceMm=0;curveRoom.Closures[0].CurveToleranceMm=.05;
        Assert(Validation.Evaluate(Project.Create(),curveRoom,new[]{cross}).Checks["hygiene_without_crossing"]==CheckState.ApproximateOnly,"curve approximation on fixed closures is also kept pending for topological certification");
        first=Make(RoomMode.FirstChange);first.Obstacles.Add(new Region{Points=Geometry.Rect(0,0,1000,1000),CurveToleranceMm=.05});
        Assert(Validation.Evaluate(Project.Create(),first,new[]{new Placement{EquipmentId="LOCKER12",Position=new V2(1000.02,100)}}).Checks["geometry"]==CheckState.Fail,"curved obstacle collision proxies cover the known chord error instead of understating occupied space");
    }
    static List<Placement> ShoeLine(params string[] models)=>models.Select((id,i)=>new Placement
        {Id="cabinet-"+i,EquipmentId=id,Position=new V2(2000+i*2000,2000),CrossingEnabled=id=="S24"}).ToList();
    static void ShoeStackingChecks()
    {
        var legal=ShoeLine("S24","S48","S24");
        Assert(ShoeStacking.Violations(legal).Count==0,"24-48-24 with real end-face contact satisfies the mandatory low-neighbour rule");
        Assert(ShoeStacking.Violations(legal.AsEnumerable().Reverse().ToList()).Count==0,"stacking adjacency uses physical ports, not list order or group IDs");
        var rotated=Serialization.Copy(legal);foreach(var p in rotated){p.Position=p.Position.Rotate(90)+new V2(12000,1000);p.Rotation+=90;}
        Assert(ShoeStacking.Violations(rotated).Count==0,"24-48-24 adjacency survives whole-chain rotation");
        var reverse=Serialization.Copy(legal);reverse[0].Position+=new V2(2000,Equipment.ShoeDepthMm);reverse[0].Rotation=180;
        Assert(ShoeStacking.Violations(reverse).Count==0,"opposite source block directions still recognise the same touching end face");
        Assert(ShoeStacking.Violations(ShoeLine("S48","S48","S48")).Select(i=>i.InstanceId).Distinct().Count()==3,"all-48 storage chains are explicitly rejected");
        Assert(ShoeStacking.Violations(ShoeLine("S24","S48","S48","S24")).Select(i=>i.InstanceId).Distinct().Count()==2,"two adjacent 48-person cabinets are both marked invalid");
        Assert(ShoeStacking.Violations(ShoeLine("S48","S24")).Any(i=>i.InstanceId=="cabinet-0"),"an end-position 48-person cabinet cannot claim a missing second low neighbour");
        foreach(string id in new[]{"S12","S18"})
        {
            var shortNeighbour=Serialization.Copy(legal);shortNeighbour[0].EquipmentId=id;shortNeighbour[0].Position.X=4000-shortNeighbour[0].Equipment.Length;
            Assert(ShoeStacking.Violations(shortNeighbour).Count==1,id+" touching the correct end cannot substitute for the required S24");
        }
        var broadside=Serialization.Copy(legal);broadside[0].Position=new V2(4000,2000+Equipment.ShoeDepthMm);
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
        var project=Project.Create();project.ChangePopulation(new Population{ShiftCount=1,PeoplePerShift=545});
        var estimate=AreaPlanning.Estimate(project.Population,project.Rooms[0]);
        Assert(estimate.Options.Any(o=>o.Items.Any(i=>i.EquipmentId=="S48")) && estimate.Options.All(o=>o.Nominal>=600 && ShoeStacking.Violations(o.Items).Count==0),"area previews use the same 24-48-24 rule and still meet the declared target");
        project.Rooms[0].Boundary=new Boundary{Width=10800,Height=10900};var generated=Layout.Generate(project,project.Rooms[0]);
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
        var shoeEstimate=AreaPlanning.Estimate(new Population{ShiftCount=1,PeoplePerShift=545},shoes);
        Assert(shoeEstimate.Options.Any(o=>Math.Abs(o.WidthMm-10800)<.01 && Math.Abs(o.HeightMm-10900)<.01 && Math.Abs(o.AreaM2-117.72)<.001 && o.Nominal==600),"600-person estimate reproduces documented 6-row R650 planning template");
        Assert(shoeEstimate.Options.Any(o=>o.Items.Any(p=>p.EquipmentId=="S48") && o.Items.Any(p=>p.Equipment.IsShoe && p.CrossingEnabled)),"stacked estimate retains explicit low-cabinet crossing locations");
        shoes.Options.ShoeCapacities=new List<int>{18};shoes.Options.AllowStacking=false;
        Assert(AreaPlanning.Estimate(project.Population,shoes).Options.All(o=>o.Items.Where(i=>i.Equipment.IsShoe).All(i=>i.EquipmentId=="S18")),"area preview respects allowed real shoe modules and stacking opt-out");
        shoes.Options.PlanningAisleMm=2500;
        Assert(AreaPlanning.Estimate(project.Population,shoes).Options.Count==0,"unsupported shoe connector clearance produces an explanation instead of undersized dimensions");
        project=Project.Create();var second=project.Rooms.First(r=>r.Mode==RoomMode.SecondChange);
        second.Options.HangerMaxSegmentMm=350;second.Options.HookStartMarginMm=80;second.Options.HookEndMarginMm=80;
        var segmented=AreaPlanning.Estimate(project.Population,second);
        Assert(segmented.Target==165 && segmented.Options.All(o=>o.Items.Count==83 && o.Nominal==165 && Math.Abs(o.Items.Sum(i=>i.Length)-21480)<.01),"hanging area estimates account for endpoint losses on each short segment");
        second.ReserveOverride=.1m;
        Assert(AreaPlanning.Estimate(project.Population,second).Target==165 && AreaPlanning.Estimate(project.Population,project.Rooms[0]).Target==660,"area preview uses the fixed factor and ignores archived room reserves");
        project.Population=new Population{PeoplePerShift=0};
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
        UpdatedDimensionsChecks();
        LockerAisleChecks();
        RoomAccessChecks();
        TrackAndSearchChecks();
        ShoeShapeWithDoorsChecks();
        IrregularBoundaryAndWorkflowChecks();
        ShoeStackingChecks();
        BoundaryEditingChecks();
        AreaAndIdentificationChecks();
        var p=Project.Create();
        Assert(p.Rooms.Select(p.Population.For).SequenceEqual(new[]{660,330,165,330,165}),"double shift scales storage while second-change capacity stays on one shift");
        p.ChangePopulation(new Population{ShiftCount=1});
        Assert(p.Rooms.Select(p.Population.For).SequenceEqual(new[]{330,165,165,165,165}),"single shift at 300 people uses 330 shared places and 165 per gender");
        p.ChangePopulation(new Population{ShiftCount=1,PeoplePerShift=301});
        Assert(p.Rooms.Select(p.Population.BaseFor).SequenceEqual(new[]{301,151,151,150,150}) &&
            p.Rooms.Select(p.Population.For).SequenceEqual(new[]{332,167,167,165,165}),"split odd headcount before rounding each room up");
        p=Project.Create();
        Assert(Equipment.Get("S24").Capacity==24 && Equipment.Get("S48").Boxes==2 && Equipment.Get("S48").Capacity==48,"shoe paired cells and stack boxes");
        Assert(Capacity.Run(7500,new[]{12,18,24})!.Sum()==90 && Capacity.Run(1250,new[]{12,18,24})==null,"whole module run decomposition");
        var hanger=new Placement{EquipmentId="HANGER_CUSTOM",Length=1000};
        var opt=new LayoutOptions();
        Assert(Capacity.Hooks(hanger,opt).Count==10 && Capacity.Hooks(new Placement{EquipmentId="HANGER_CUSTOM",Length=0},opt).Count==0,"hanger endpoints and zero segment");
        var room=Make(RoomMode.Shoe);
        var shoe=new Placement{EquipmentId="S24",Position=new V2(1000,1000),CrossingEnabled=true};
        var stat=Validation.Evaluate(p,room,new[]{shoe});
        Assert(stat.Nominal==24 && stat.Effective==null && stat.ProjectionM2==1.3,"unverified capacity is never certified");
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
        room.Closures.Add(new Region{Points=Geometry.Rect(0,2700,4000,Equipment.ShoeDepthMm)});
        room.Closures.Add(new Region{Points=Geometry.Rect(6000,2700,4000,Equipment.ShoeDepthMm)});
        stat=Validation.Evaluate(p,room,new[]{barrier});
        Assert(stat.Checks["hygiene_without_crossing"]==CheckState.Pass && stat.Checks["workflow_with_crossing"]==CheckState.Pass,"real end closures isolate and a low cabinet reconnects workflow");
        room.Closures.RemoveAt(1);
        Assert(Validation.Evaluate(p,room,new[]{barrier}).Checks["hygiene_without_crossing"]==CheckState.Fail,"removing closure reopens bypass");
        room.Closures.Add(new Region{Points=Geometry.Rect(6010,2700,3990,Equipment.ShoeDepthMm)});
        Assert(Validation.Evaluate(p,room,new[]{barrier}).Checks["hygiene_without_crossing"]==CheckState.Fail,"ten millimeter crack is detected without coarse raster");
        room.Closures[1].Points=Geometry.Rect(6000,2700,4000,Equipment.ShoeDepthMm);barrier.EquipmentId="S48";barrier.CrossingEnabled=false;
        Assert(Validation.Evaluate(p,room,new[]{barrier}).Checks["workflow_with_crossing"]==CheckState.Fail,"stacked storage does not invent crossing");
        var baseline=p.Rooms[0];baseline.Boundary=new Boundary{Width=11800,Height=11900};
        var before=Serialization.Write(baseline.Placed);
        var search=Layout.Generate(p,baseline);
        Assert(search.Candidates.Count>1 && search.Candidates.Any(c=>c.Statistics.Nominal>=660),"660-person multiple real shoe arrangements");
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
        p=Project.Create();p.ChangePopulation(new Population{ShiftCount=1,PeoplePerShift=501});
        Assert(p.Population.For(p.Rooms.Single(r=>r.Id=="male_first"))==277 && p.Population.For(p.Rooms.Single(r=>r.Id=="female_first"))==275,"reserve ceilings are applied after integer gender splitting");
        p=Project.Create();var fixtureDir=args.Length==2 && args[0]=="--fixtures"?args[1]:null;
        foreach(var mode in new[]{RoomMode.Shoe,RoomMode.FirstChange,RoomMode.SecondChange})
        {
            var r=p.Rooms.First(r=>r.Mode==mode);r.Boundary=new Boundary{Width=10000,Height=10000};
            r.Placed.Add(new Placement{RoomId=r.Id,EquipmentId=mode==RoomMode.Shoe?"S24":mode==RoomMode.FirstChange?"LOCKER12":"HANGER_CUSTOM",Position=new V2(2000,2000),Length=1000});
            if(mode==RoomMode.Shoe)r.Placed.Add(new Placement{RoomId=r.Id,EquipmentId=Equipment.CornerId,Position=new V2(4000,2000)});
            string exported=LayoutExport.Write(p,r);var doc=JObject.Parse(exported);
            Assert((string?)doc["units"]=="mm" && doc["instances"]![0]!["effective_capacity"]!.Type==JTokenType.Null && (string?)doc["validation"]!["source_block"]=="requires_input",mode+" export preserves schema units and unverified status");
            if(fixtureDir!=null){System.IO.Directory.CreateDirectory(fixtureDir);System.IO.File.WriteAllText(System.IO.Path.Combine(fixtureDir,mode+".json"),exported);}
        }
        Console.WriteLine($"{passed} core checks passed");
        if(args.Length>0 && args[0]=="--json")
            Console.WriteLine(JObject.FromObject(new{passed,shoe=search.Candidates.Select(c=>c.Statistics).ToArray()}));
    }
}
