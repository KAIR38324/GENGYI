using System;
using System.Collections.Generic;
using System.Linq;
using Geo=NetTopologySuite.Geometries.Geometry;

namespace Gengyi.Core
{
    public static class LockerAisles
    {
        public const double MinimumMm=1100;
        public const string CheckKey="locker_aisle";

        internal static Geo Front(Placement item,double depth) =>
            Geometry.Polygon(Geometry.Rect(.1,-depth,item.Equipment.Length-.2,depth-.1).Select(item.Transform));

        public static bool PreservesCapacity(Statistics before,Statistics after) =>
            before.GeometricByInstance.All(pair=>after.GeometricByInstance.TryGetValue(pair.Key,out int usable) && usable>=pair.Value);

        // A shared aisle may serve both fronts. Back-to-back and side-by-side
        // cabinet contacts are not aisles and do not require a rear/side gap.
        private static List<(Placement A,Placement B,double Gap)> Pairs(Room room,IReadOnlyList<Placement> items)
        {
            var result=new List<(Placement A,Placement B,double Gap)>();
            if(room.Mode!=RoomMode.FirstChange)return result;
            var lockers=new List<(Placement Item,Geo Body,Geo Front)>();
            foreach(var item in items.Where(p=>p.EquipmentId=="LOCKER12"))
                try
                {
                    lockers.Add((item,Geometry.Footprint(item),Front(item,MinimumMm)));
                }
                catch(ArgumentException){ /* Geometry validation reports malformed source outlines/transforms. */ }
            for(int i=0;i<lockers.Count;i++)
                for(int j=0;j<i;j++)
                {
                    var a=lockers[i];var b=lockers[j];
                    bool blocked=a.Front.EnvelopeInternal.Intersects(b.Body.EnvelopeInternal) && a.Front.Intersection(b.Body).Area>Geometry.AreaTolerance ||
                        b.Front.EnvelopeInternal.Intersects(a.Body.EnvelopeInternal) && b.Front.Intersection(a.Body).Area>Geometry.AreaTolerance;
                    if(!blocked)continue;
                    result.Add((a.Item,b.Item,a.Body.Distance(b.Body)));
                }
            return result;
        }

        public static List<Diagnostic> Violations(Room room,IReadOnlyList<Placement> items)
        {
            var result=new List<Diagnostic>();
            foreach(var pair in Pairs(room,items))
            {
                string gap=pair.Gap.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);
                foreach(var side in new[]{(pair.A,pair.B),(pair.B,pair.A)})result.Add(new Diagnostic
                    {
                        Code="LOCKER_AISLE_CLEARANCE",InstanceId=side.Item1.Id,
                        Message="与衣柜 "+side.Item2.Id+" 之间的柜前通道净距不足1100 mm（柜体最短净距 "+gap+" mm）；按柜面之间的空距计算，不含两侧柜深。"
                    });
            }
            return result;
        }

        public static void RequireValid(Room room,IReadOnlyList<Placement> items)
        {
            var issue=Violations(room,items).FirstOrDefault();
            if(issue!=null)throw new ArgumentException(issue.Message);
        }

        public static void RejectNewViolations(Room room,IReadOnlyList<Placement> before,IReadOnlyList<Placement> after)
        {
            if(room.Mode!=RoomMode.FirstChange)return;
            string Key(Placement a,Placement b)=>string.CompareOrdinal(a.Id,b.Id)<0?a.Id+"/"+b.Id:b.Id+"/"+a.Id;
            var existing=Pairs(room,before).ToDictionary(p=>Key(p.A,p.B),p=>p.Gap);
            foreach(var pair in Pairs(room,after))
                if(!existing.TryGetValue(Key(pair.A,pair.B),out double gap) || pair.Gap+1e-6<gap)
                    throw new ArgumentException("衣柜 "+pair.A.Id+" 与 "+pair.B.Id+" 之间的通道不足1100 mm；不能新增或缩窄不合格通道，请先调整柜排。");
        }
    }
}
