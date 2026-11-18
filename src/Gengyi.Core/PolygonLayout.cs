using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries.Utilities;
using Geo=NetTopologySuite.Geometries.Geometry;

namespace Gengyi.Core
{
    public static partial class Layout
    {
        private static IEnumerable<Candidate> PolygonCandidates(Room room,int target,CancellationToken cancellation)
        {
            if(target==0){yield return new Candidate{Family="不规则空房间"};yield break;}
            var boundary=room.Boundary!;
            double aisle=room.Options.PlanningAisleFor(room.Mode);
            Geo actualRoom=Geometry.RoomPolygon(boundary),usable=actualRoom;
            if(room.Mode==RoomMode.Shoe)usable=usable.Buffer(-room.Options.ShoeBoundaryMm,16);
            var fixedObjects=Geometry.Union(room.Obstacles.Concat(room.Closures).Select(Geometry.RegionFootprint));
            if(!fixedObjects.IsEmpty)usable=usable.Difference(fixedObjects);
            foreach(bool vertical in new[]{false,true})
            {
                cancellation.ThrowIfCancellationRequested();
                var transform=new AffineTransformation(0,1,0,-1,0,boundary.Width);
                var oriented=vertical?transform.Transform(usable):usable;
                var actualOriented=vertical?transform.Transform(actualRoom):actualRoom;
                var spaces=BoundarySpaces.Partition(oriented,cancellation,actualOriented,room.Mode==RoomMode.Shoe?room.Options.ShoeBoundaryMm:0);
                foreach(bool alternative in new[]{false,true})
                    foreach(bool stacking in room.Mode==RoomMode.Shoe && room.Options.AllowStacking?new[]{false,true}:new[]{false})
                    {
                        var c=new Candidate{Family="不规则分区/"+(room.Mode==RoomMode.Shoe?(alternative?"直段低柜":"S柜链"):room.Mode==RoomMode.FirstChange?(alternative?"背靠背衣柜":"单面衣柜"):(alternative?"边列挂架":"中排挂架"))+
                            "/"+(vertical?"竖向":"横向")+(stacking?"/24—48—24":"")};
                        int remaining=target;
                        foreach(var space in spaces)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if(remaining<=0)break;
                            List<Placement> items;
                            if(room.Mode==RoomMode.Shoe)items=ShoesInSpace(room,space,remaining,stacking,alternative,aisle,cancellation);
                            else if(room.Mode==RoomMode.FirstChange)items=LockersInSpace(room,space,remaining,alternative,aisle);
                            else items=HangersInSpace(room,space,remaining,alternative,aisle);
                            foreach(var item in items)
                            {
                                if(vertical){item.Position=new V2(boundary.Width-item.Position.Y,item.Position.X);item.Rotation=Geometry.Normalize(item.Rotation+90);}
                                c.Items.Add(item);
                            }
                            remaining-=items.Sum(p=>Capacity.Nominal(p,room.Options));
                        }
                        if(room.Mode==RoomMode.Shoe)
                        {
                            bool corners=c.Items.Any(p=>p.Equipment.IsCorner);
                            bool lines=c.Items.GroupBy(p=>p.GroupId).Any(g=>!g.Any(p=>p.Equipment.IsCorner));
                            if(!corners)c.Family=c.Family.Replace("S柜链","直段低柜");
                            else if(lines)c.Family=c.Family.Replace("S柜链","S组+直段");
                            if(!c.Items.Any(p=>p.EquipmentId=="S48"))c.Family=c.Family.Replace("/24—48—24","");
                        }
                        if(c.Items.Count>0)yield return c;
                    }
            }
        }

        private static List<Placement> ShoesInSpace(Room room,BoundarySpace space,int target,bool stacking,bool straight,double aisle,CancellationToken cancellation)
        {
            var local=PlanningRoom(room,space.Width,space.Height);
            local.Options.ShoeBoundaryMm=0;local.Options.AllowStacking=stacking;
            var choices=new List<Candidate>();
            if(!straight)
                foreach(var candidate in Shoes(local,target))
                {
                    cancellation.ThrowIfCancellationRequested();
                    choices.Add(candidate);
                }
            // Narrow parts of an L/U room can still contain real connected low-cabinet
            // runs. They are separate groups, never a cropped or falsely connected S chain.
            foreach(bool rotated in new[]{false,true})
            {
                double w=rotated?space.Height:space.Width,h=rotated?space.Width:space.Height;
                double maxRun=Math.Floor(w/500)*500;
                if(maxRun<1000 || h<Equipment.ShoeDepthMm)continue;
                int rows=Math.Min(40,1+(int)Math.Floor((h-Equipment.ShoeDepthMm)/(Equipment.ShoeDepthMm+aisle)));
                var line=new Candidate{Family="分区直段"};int remaining=target;
                for(int row=0;row<rows && remaining>0;row++)
                {
                    double desired=Math.Min(maxRun,Math.Max(1000,Math.Ceiling(remaining/.012/500)*500));
                    List<int>? chain=null;
                    for(double run=desired;run<=maxRun && chain==null;run+=500)chain=Capacity.Run(run,room.Options.ShoeCapacities);
                    for(double run=maxRun;run>=1000 && chain==null;run-=500)chain=Capacity.Run(run,room.Options.ShoeCapacities);
                    if(chain==null)continue;
                    string group=Guid.NewGuid().ToString("N");double x=0,y=row*(Equipment.ShoeDepthMm+aisle);
                    foreach(int cap in chain){line.Items.Add(Place(room,"S"+cap,x,y,group:group));x+=cap/3*250;}
                    if(stacking)ShoeStacking.StackToTarget(line.Items,target);
                    remaining=target-line.Items.Sum(p=>p.Equipment.Capacity);
                }
                if(rotated)RotateIntoBoundary(line,space.Width);
                if(line.Items.Count>0)choices.Add(line);
            }
            var selected=choices.OrderBy(c=>Math.Max(0,target-c.Items.Sum(p=>p.Equipment.Capacity)))
                .ThenBy(c=>!straight && c.Family.StartsWith("S形",StringComparison.Ordinal)?0:1)
                .ThenBy(c=>Math.Max(0,c.Items.Sum(p=>p.Equipment.Capacity)-target))
                .ThenBy(c=>c.Items.Count).FirstOrDefault();
            if(selected==null)return new List<Placement>();
            foreach(var item in selected.Items)item.Position+=new V2(space.X,space.Y);
            return selected.Items;
        }

        private static List<Placement> LockersInSpace(Room room,BoundarySpace space,int target,bool paired,double aisle)
        {
            var choices=new List<List<Placement>>();int needed=(target+11)/12;
            foreach(bool rotated in new[]{false,true})
            {
                double w=rotated?space.Height:space.Width,h=rotated?space.Width:space.Height;
                int columns=Math.Max(0,(int)Math.Floor((w-aisle)/1140));
                var items=new List<Placement>();double rear=room.Options.RearGapMm;
                double depth=paired?1000+rear:500+rear,step=depth+aisle;
                for(double y=aisle;y+depth+(paired?aisle:0)<=h+.000001 && items.Count<needed;y+=step)
                    for(int col=0;col<columns && items.Count<needed;col++)
                    {
                        items.Add(Place(room,"LOCKER12",col*1140,y));
                        if(paired && items.Count<needed)items.Add(Place(room,"LOCKER12",(col+1)*1140,y+1000+rear,180));
                    }
                PositionInSpace(items,space,rotated);choices.Add(items);
            }
            return choices.OrderByDescending(c=>c.Count).First();
        }

        private static List<Placement> HangersInSpace(Room room,BoundarySpace space,int target,bool edges,double aisle)
        {
            var choices=new List<List<Placement>>();var o=room.Options;
            foreach(bool rotated in new[]{false,true})
            {
                double w=(rotated?space.Height:space.Width)-aisle,h=rotated?space.Width:space.Height;
                var items=new List<Placement>();int remaining=target;
                var ys=edges?(h>=450+aisle?(h>=900+aisle?new[]{0.0,h-450}:new[]{0.0}):Array.Empty<double>()):
                    Enumerable.Range(0,Math.Max(0,Math.Min(100,(int)Math.Floor(h/(450+aisle)))))
                        .Select(row=>aisle+row*(450+aisle)).Where(y=>y+450<=h+.000001).ToArray();
                foreach(double y in ys)
                {
                    double x=0;
                    while(remaining>0 && x+o.HookStartMarginMm+o.HookEndMarginMm<=w)
                    {
                        double max=Math.Min(o.HangerMaxSegmentMm,w-x);
                        int hooks=max<o.HookStartMarginMm+o.HookEndMarginMm?0:(int)Math.Floor((max-o.HookStartMarginMm-o.HookEndMarginMm)/o.HookPitchMm)+1;
                        hooks=Math.Min(remaining,hooks);if(hooks<=0)break;
                        double length=Math.Max(1,(hooks-1)*o.HookPitchMm+o.HookStartMarginMm+o.HookEndMarginMm);
                        items.Add(Place(room,"HANGER_CUSTOM",x,y,length:length));remaining-=hooks;x+=length+o.HangerSegmentGapMm;
                    }
                    if(remaining<=0)break;
                }
                PositionInSpace(items,space,rotated);choices.Add(items);
            }
            return choices.OrderByDescending(c=>c.Sum(p=>Capacity.Nominal(p,o))).First();
        }

        private static Room PlanningRoom(Room room,double width,double height)=>new Room{Id=room.Id,Mode=room.Mode,
            Boundary=new Boundary{Width=width,Height=height},Options=Serialization.Copy(room.Options)};

        private static void PositionInSpace(List<Placement> items,BoundarySpace space,bool rotated)
        {
            foreach(var item in items)
            {
                if(rotated){item.Position=new V2(space.Width-item.Position.Y,item.Position.X);item.Rotation=Geometry.Normalize(item.Rotation+90);}
                item.Position+=new V2(space.X,space.Y);
            }
        }
    }
}
