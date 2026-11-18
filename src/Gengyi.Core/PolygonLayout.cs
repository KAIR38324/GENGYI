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
        private static IEnumerable<Candidate> PolygonHangers(Room room,int target,Geo reserved,CancellationToken cancellation)
        {
            if(target==0){yield return new Candidate{Family="不规则空房间"};yield break;}
            var boundary=room.Boundary!;
            double aisle=room.Options.PlanningAisleFor(room.Mode);
            Geo actualRoom=Geometry.RoomPolygon(boundary);
            var fixedObjects=Geometry.Union(room.Obstacles.Concat(room.Closures).Select(Geometry.RegionFootprint));
            var usable=actualRoom.Difference(fixedObjects.Union(reserved));
            foreach(bool vertical in new[]{false,true})
            {
                var transform=new AffineTransformation(0,1,0,-1,0,boundary.Width);
                var oriented=vertical?transform.Transform(usable):usable;
                var actualOriented=vertical?transform.Transform(actualRoom):actualRoom;
                var spaces=BoundarySpaces.Partition(oriented,cancellation,actualOriented);
                foreach(bool edges in new[]{false,true})
                {
                    var candidate=new Candidate{Family="分区/"+(edges?"边列挂架":"中排挂架")+"/"+(vertical?"竖向":"横向")};
                    int remaining=target;
                    foreach(var space in spaces)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if(remaining<=0)break;
                        var items=HangersInSpace(room,space,remaining,edges,aisle);
                        candidate.Items.AddRange(items);
                        remaining-=items.Sum(p=>Capacity.Nominal(p,room.Options));
                    }
                    if(vertical)RotateIntoBoundary(candidate,boundary.Width);
                    if(candidate.Items.Count>0)yield return candidate;
                }
            }
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
