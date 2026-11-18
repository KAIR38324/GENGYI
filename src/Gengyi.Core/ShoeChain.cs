using System;
using System.Collections.Generic;
using System.Linq;

namespace Gengyi.Core
{
    public static partial class Layout
    {
        private const double PortTolerance = .1;

        public static bool IsShoeLayout(IEnumerable<Placement> items)
        {
            var groups=items.Where(item=>item.Equipment.IsShoe || item.Equipment.IsCorner)
                .GroupBy(item=>item.GroupId).ToList();
            return groups.Count>0 && groups.All(group=>group.Key.Length>0 && IsOpenSerpentine(group.ToList()));
        }

        private static bool IsOpenSerpentine(IReadOnlyList<Placement> items)
        {
            if(items.Count<9 || items.Any(item=>item.Equipment.IsCorner && item.EquipmentId!=Equipment.CornerId))return false;
            var ports=items.SelectMany(Ports).ToList();
            var matches=new int[ports.Count];
            for(int i=0;i<matches.Length;i++)matches[i]=-1;
            (long,long) Key(V2 p)=>((long)Math.Floor(p.X),(long)Math.Floor(p.Y));
            var buckets=ports.Select((port,index)=>(port,index)).ToLookup(entry=>Key(entry.port.Middle));
            for(int i=0;i<ports.Count;i++)
            {
                var p=ports[i];var key=Key(p.Middle);
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)
                    foreach(var entry in buckets[(key.Item1+x,key.Item2+y)])
                    {
                        if(entry.index/2==i/2 || p.Normal.X*entry.port.Normal.X+p.Normal.Y*entry.port.Normal.Y>-.999999)continue;
                        var q=entry.port;
                        bool connects=p.A.Distance(q.A)<=PortTolerance && p.B.Distance(q.B)<=PortTolerance ||
                            p.A.Distance(q.B)<=PortTolerance && p.B.Distance(q.A)<=PortTolerance;
                        if(!connects)continue;
                        if(matches[i]>=0)return false;
                        matches[i]=entry.index;
                    }
            }
            var ends=Enumerable.Range(0,matches.Length).Where(i=>matches[i]<0).ToList();
            if(ends.Count!=2 || ends.Any(i=>items[i/2].Equipment.IsCorner))return false;
            var turns=new List<double>();
            int visited=0,previousTurn=-2;
            for(int at=ends[0];at>=0;at=matches[at^1])
            {
                if(visited>=items.Count)return false;
                if(items[at/2].Equipment.IsCorner)
                {
                    if(visited==previousTurn+1)return false; // Each pair of corners contains a real connecting cabinet.
                    previousTurn=visited;
                    var a=ports[at].Normal;var b=ports[at^1].Normal;
                    turns.Add(Math.Sign(a.Y*b.X-a.X*b.Y));
                }
                visited++;
            }
            if(visited!=items.Count || turns.Count<4 || turns.Count%2!=0)return false;
            return Enumerable.Range(0,turns.Count).All(i=>turns[i]==(i/2%2==0?turns[0]:-turns[0]));
        }

        private static IEnumerable<(V2 A,V2 B,V2 Normal,V2 Middle)> Ports(Placement item)
        {
            (V2,V2,V2,V2) Port(V2 a,V2 b,V2 normal)
            {
                var A=item.Transform(a);var B=item.Transform(b);
                return (A,B,normal.Rotate(item.Rotation),(A+B)*.5);
            }
            var e=item.Equipment;
            if(e.IsCorner)
            {
                yield return Port(new V2(),new V2(e.Length,0),new V2(0,-1));
                yield return Port(new V2(),new V2(0,e.Depth),new V2(-1,0));
            }
            else
            {
                yield return Port(new V2(),new V2(0,e.Depth),new V2(-1,0));
                yield return Port(new V2(e.Length,0),new V2(e.Length,e.Depth),new V2(1,0));
            }
        }
    }
}
