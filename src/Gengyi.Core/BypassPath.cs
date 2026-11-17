using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NetTopologySuite.Geometries;

namespace Gengyi.Core
{
    public static class BypassPath
    {
        // A displayed witness only: failure to sample a path never overrides continuous topology.
        // Every returned segment is checked against the continuous body-clearance polygon.
        public static List<V2> Find(Room room,IReadOnlyList<Placement> items)
        {
            if(room.Boundary==null || room.DirtySeed==null || room.CleanSeed==null)return new List<V2>();
            double radius=room.Options.WalkWidthMm/2;
            var blockers=Geometry.Union(items.Select(Geometry.Footprint).Concat(room.Obstacles.Concat(room.Closures).Select(r=>(NetTopologySuite.Geometries.Geometry)Geometry.Polygon(r.Points))));
            var free=Geometry.RoomPolygon(room.Boundary).Buffer(-radius,32).Difference(blockers.Buffer(radius,32));
            var start=room.DirtySeed;var end=room.CleanSeed;
            bool Clear(V2 a,V2 b)=>free.Covers(Geometry.Factory.CreateLineString(new[]{new Coordinate(a.X,a.Y),new Coordinate(b.X,b.Y)}));
            if(Clear(start,end))return new List<V2>{start,end};
            double step=Math.Max(50,Math.Max(room.Boundary.Width,room.Boundary.Height)/160);
            int nx=(int)Math.Ceiling(room.Boundary.Width/step),ny=(int)Math.Ceiling(room.Boundary.Height/step);
            int Key(int x,int y)=>x*(ny+1)+y;
            V2 Point(int key)=>new V2((key/(ny+1))*step,(key%(ny+1))*step);
            var open=new SortedSet<(double Score,int Key)>();var costs=new Dictionary<int,double>();var parents=new Dictionary<int,int>();
            int sx=(int)Math.Round(start.X/step),sy=(int)Math.Round(start.Y/step);
            for(int x=Math.Max(0,sx-1);x<=Math.Min(nx,sx+1);x++)for(int y=Math.Max(0,sy-1);y<=Math.Min(ny,sy+1);y++)
            {
                int key=Key(x,y);var p=Point(key);if(!Clear(start,p))continue;
                costs[key]=start.Distance(p);parents[key]=-1;open.Add((costs[key]+p.Distance(end),key));
            }
            var clock=Stopwatch.StartNew();
            while(open.Count>0 && clock.ElapsedMilliseconds<2500)
            {
                var current=open.Min;open.Remove(current);var at=Point(current.Key);
                if(Clear(at,end))
                {
                    var path=new List<V2>{end};int key=current.Key;
                    while(key>=0){path.Add(Point(key));key=parents[key];}path.Add(start);path.Reverse();
                    var simplified=new List<V2>{path[0]};int index=0;
                    while(index<path.Count-1)
                    {
                        int next=path.Count-1;while(next>index+1 && !Clear(path[index],path[next]))next--;
                        simplified.Add(path[next]);index=next;
                    }
                    return simplified;
                }
                int cx=current.Key/(ny+1),cy=current.Key%(ny+1);
                for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++)
                {
                    int x=cx+dx,y=cy+dy;if(dx==0 && dy==0 || x<0 || y<0 || x>nx || y>ny)continue;
                    int key=Key(x,y);var p=Point(key);double cost=costs[current.Key]+at.Distance(p);
                    if(costs.TryGetValue(key,out double old) && old<=cost)continue;
                    if(!Clear(at,p))continue;
                    if(costs.ContainsKey(key))open.Remove((old+p.Distance(end),key));
                    costs[key]=cost;parents[key]=current.Key;open.Add((cost+p.Distance(end),key));
                }
            }
            return new List<V2>();
        }
    }
}
