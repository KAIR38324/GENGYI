using System;
using System.Collections.Generic;
using System.Linq;

namespace Gengyi.Core
{
    public static partial class Layout
    {
        private static readonly V2[] RoomAxes={new V2(1,0),new V2(0,1),new V2(-1,0),new V2(0,-1)};

        public static V2 TrackPoint(Room room,IReadOnlyList<V2> vertices,V2 cursor)
        {
            var start=vertices[vertices.Count-1];
            var delta=cursor-start;
            if(room.Mode==RoomMode.FirstChange && !room.Options.Orthogonal)return cursor;
            IEnumerable<V2> axes=RoomAxes;
            if(room.Mode==RoomMode.Shoe && vertices.Count>1)
            {
                var before=start-vertices[vertices.Count-2];
                axes=axes.Where(a=>Math.Abs(Dot(a,before))<.000001);
            }
            else if(room.Mode==RoomMode.FirstChange && room.Boundary!=null)
                axes=LockerAngles(room.Boundary).SelectMany(angle=>new[]{new V2(1,0).Rotate(angle),new V2(-1,0).Rotate(angle)});
            var axis=axes.OrderByDescending(a=>Dot(delta,a)).First();
            double length=Math.Max(0,Dot(delta,axis));
            if(room.Mode==RoomMode.Shoe)
            {
                double allowance=Equipment.CornerRadiusMm*(vertices.Count>1?1:.5);
                double run=LargestRun(length-allowance,room.Options.ShoeCapacities);
                if(run>0)length=run+allowance;
                else
                {
                    double minimum=room.Options.ShoeCapacities.Min()*250/3;
                    if(length>=minimum)length=minimum+allowance;
                }
            }
            return start+axis*length;
        }

        public static Candidate Track(Room room,IReadOnlyList<V2> vertices,int remaining,bool flip,IReadOnlyList<V2> keepLow,out string note,bool? allowStacking=null)
        {
            if(room.Mode==RoomMode.SecondChange)throw new ArgumentException("轨迹布置支持换鞋间和一更。");
            var result=new Candidate{Family=room.Mode==RoomMode.Shoe?"轨迹 S 形鞋柜":"轨迹衣柜",AppendToPlaced=true};
            note="";
            if(vertices.Count<2)return result;
            var directions=new List<V2>();
            for(int i=1;i<vertices.Count;i++)
            {
                var delta=vertices[i]-vertices[i-1];
                double length=vertices[i-1].Distance(vertices[i]);
                if(length<=.000001){note="请继续拉长轨迹。";return result;}
                directions.Add(delta*(1/length));
            }
            if(room.Mode==RoomMode.FirstChange)
            {
                var e=Equipment.Get("LOCKER12");
                for(int i=0;i<directions.Count;i++)
                {
                    var direction=directions[i];var back=vertices[i];double length=back.Distance(vertices[i+1]);
                    if(flip){direction=direction*(-1);back=vertices[i+1];}
                    var normal=Left(direction);
                    for(double x=0;x+e.Length<=length+.000001;x+=e.Length)
                    {
                        // Local +Y points from the cabinet front toward its back.
                        var front=back+direction*(x+e.Length)+normal*e.Depth;
                        result.Items.Add(Place(room,e.Id,front.X,front.Y,Heading(direction*(-1))));
                    }
                }
                if(result.Items.Count==0)note="一更每个柜需要完整的 1140 mm 柜背线。";
                return result;
            }

            double half=Equipment.CornerRadiusMm/2;
            for(int i=0;i<directions.Count;i++)
            {
                var direction=directions[i];
                if(!Geometry.Orthogonal(Heading(direction)) || i>0 && Math.Abs(Dot(directions[i-1],direction))>.000001)
                {note="鞋柜轨迹需要按顺序作 90° 转向。";break;}
                var start=vertices[i]+direction*(i==0?0:half);
                var end=vertices[i+1]-direction*(i==directions.Count-1?0:half);
                double length=Dot(end-start,direction);
                double run=LargestRun(length,room.Options.ShoeCapacities);
                if(run<=0 || i<directions.Count-1 && Math.Abs(length-run)>.001)
                {note="转角之间需要放下完整柜型，请拉长这一段。";break;}
                if(i>0 && i<directions.Count-1 && i%2==1 && run+.001<room.Options.PlanningAisleFor(room.Mode))
                {note="连接柜长度不足以形成所设排间净距。";break;}
                if(i>0)
                {
                    var incoming=directions[i-1];
                    var corner=vertices[i]-incoming*half+direction*half;
                    var a=direction*(-1);var b=incoming;
                    var first=a.X*b.Y-a.Y*b.X>0?a:b;
                    result.Items.Add(Place(room,Equipment.CornerId,corner.X,corner.Y,Heading(first),group:result.Id));
                }
                foreach(int capacity in Capacity.Run(run,room.Options.ShoeCapacities)!)
                {
                    var p=start-Left(direction)*half;
                    result.Items.Add(Place(room,"S"+capacity,p.X,p.Y,Heading(direction),group:result.Id));
                    start+=direction*(capacity/3*250);
                }
            }
            foreach(var item in result.Items.Where(p=>p.Equipment.IsShoe))
                if(keepLow.Any(point=>Geometry.Footprint(item).Covers(Geometry.Factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(point.X,point.Y)))))
                    item.Locked=true;
            if(allowStacking??room.Options.AllowStacking)ShoeStacking.StackToTarget(result.Items,remaining);
            if(note.Length==0 && !IsShoeLayout(result.Items))note="继续画出至少三排，左右交替转弯。";
            return result;
        }

        private static double LargestRun(double length,IReadOnlyCollection<int> sizes)
        {
            double snapped=Math.Floor((length+.000001)/500)*500;
            for(int i=0;i<4 && snapped>=1000;i++,snapped-=500)
                if(Capacity.Run(snapped,sizes)!=null)return snapped;
            return 0;
        }
        private static double Dot(V2 a,V2 b)=>a.X*b.X+a.Y*b.Y;
        private static V2 Left(V2 a)=>new V2(-a.Y,a.X);
        private static double Heading(V2 a)=>Geometry.Normalize(Math.Atan2(a.Y,a.X)*180/Math.PI);
    }
}
