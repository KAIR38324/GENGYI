using System;
using System.Collections.Generic;
using System.Linq;

namespace Gengyi.Core
{
    public static class ShoeStacking
    {
        public const string CheckKey="stacking_neighbors";
        private const double ContactToleranceMm=.1;
        private sealed class Port
        {
            internal int Index,Side;
            internal V2 A=new V2(),B=new V2(),Mid=new V2(),Outward=new V2();
        }
        private sealed class Neighbours
        {
            internal readonly IReadOnlyList<Placement> Items;
            private readonly List<int>[] ends;
            internal Neighbours(IReadOnlyList<Placement> items)
            {
                Items=items;ends=Enumerable.Range(0,items.Count*2).Select(_=>new List<int>()).ToArray();
                var buckets=new Dictionary<(long x,long y),List<Port>>();
                var ports=new List<Port>();
                for(int i=0;i<items.Count;i++)
                {
                    var p=items[i];var e=p.Equipment;if(!e.IsShoe)continue;
                    var axis=p.Transform(new V2(e.Length,0))-p.Transform(new V2());
                    double length=axis.Distance(new V2());
                    if(double.IsNaN(length) || double.IsInfinity(length) || length<1e-8)continue;
                    for(int side=0;side<2;side++)
                    {
                        var a=p.Transform(new V2(side*e.Length,0));var b=p.Transform(new V2(side*e.Length,e.Depth));
                        var mid=(a+b)*.5;
                        if(double.IsNaN(mid.X) || double.IsNaN(mid.Y) || double.IsInfinity(mid.X) || double.IsInfinity(mid.Y))continue;
                        var port=new Port{Index=i,Side=side,A=a,B=b,Mid=mid,Outward=axis*((side==0?-1:1)/length)};
                        var key=Key(mid);if(!buckets.TryGetValue(key,out var list))buckets[key]=list=new List<Port>();
                        list.Add(port);ports.Add(port);
                    }
                }
                foreach(var port in ports)
                {
                    var key=Key(port.Mid);
                    // Neighbouring buckets avoid quantization errors near a bucket edge.
                    for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++)
                        if(buckets.TryGetValue((key.x+dx,key.y+dy),out var candidates))
                            foreach(var other in candidates)
                            {
                                if(other.Index==port.Index || port.Outward.X*other.Outward.X+port.Outward.Y*other.Outward.Y>-.999999)continue;
                                bool contact=port.A.Distance(other.A)<=ContactToleranceMm && port.B.Distance(other.B)<=ContactToleranceMm ||
                                    port.A.Distance(other.B)<=ContactToleranceMm && port.B.Distance(other.A)<=ContactToleranceMm;
                                var at=ends[port.Index*2+port.Side];
                                if(contact && !at.Contains(other.Index))at.Add(other.Index);
                            }
                }
            }
            private static (long x,long y) Key(V2 p)=>((long)Math.Floor(p.X),(long)Math.Floor(p.Y));
            internal bool HasLowNeighbour(int index,int side)
            {
                var at=ends[index*2+side];
                return at.Count==1 && Items[at[0]].EquipmentId=="S24";
            }
            internal bool CanStack(int index)=>HasLowNeighbour(index,0) && HasLowNeighbour(index,1);
        }

        public static List<Diagnostic> Violations(IReadOnlyList<Placement> items)
        {
            var result=new List<Diagnostic>();
            if(!items.Any(p=>p.EquipmentId=="S48"))return result;
            var neighbours=new Neighbours(items);
            for(int i=0;i<items.Count;i++)
                if(items[i].EquipmentId=="S48")
                    for(int side=0;side<2;side++)
                        if(!neighbours.HasLowNeighbour(i,side))result.Add(new Diagnostic
                        {
                            Code=side==0?"STACK_NEIGHBOR_START":"STACK_NEIGHBOR_END",InstanceId=items[i].Id,
                            Message="S48 的"+(side==0?"一端":"另一端")+"未相接 S24 单柜；每个48人柜必须按 24—48—24 布置，不能相邻叠柜或放在柜链端部。"
                        });
            return result;
        }

        public static void RequireValid(IReadOnlyList<Placement> items)
        {
            var issue=Violations(items).FirstOrDefault();
            if(issue!=null)throw new ArgumentException(issue.Message);
        }

        // Permit repairing legacy drafts one cabinet at a time, but never create another bad end.
        public static void RejectNewViolations(IReadOnlyList<Placement> before,IReadOnlyList<Placement> after)
        {
            var existing=new HashSet<string>(Violations(before).Select(d=>d.InstanceId+"/"+d.Code));
            var issue=Violations(after).FirstOrDefault(d=>!existing.Contains(d.InstanceId+"/"+d.Code));
            if(issue!=null)throw new ArgumentException(issue.Message+" 请先保留两侧24人柜，再调整型号或位置。");
        }

        public static int StackToTarget(IReadOnlyList<Placement> items,int target)
        {
            int nominal=items.Sum(p=>p.Equipment.Capacity),stacked=0;
            if(nominal>=target)return stacked;
            var neighbours=new Neighbours(items);
            for(int i=items.Count-1;i>=0 && nominal<target;i--)
                if(items[i].EquipmentId=="S24" && !items[i].Locked && neighbours.CanStack(i))
                {
                    items[i].EquipmentId="S48";items[i].CrossingEnabled=false;nominal+=24;stacked++;
                }
            return stacked;
        }

        // One run always begins and ends with a low S24; mixed 12/18 modules break the run.
        public static int MaximumStackableInRun(IReadOnlyList<int> capacities)
        {
            int total=0,run=0;
            foreach(int cap in capacities)
                if(cap==24)run++;
                else{total+=Math.Max(0,(run-1)/2);run=0;}
            return total+Math.Max(0,(run-1)/2);
        }
    }
}
