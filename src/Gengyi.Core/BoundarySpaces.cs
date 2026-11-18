using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using Geo=NetTopologySuite.Geometries.Geometry;

namespace Gengyi.Core
{
    internal sealed class BoundarySpace
    {
        internal double X,Y,Width,Height;
        internal double Area=>Width*Height;
    }

    internal static class BoundarySpaces
    {
        // Cells are accepted only when the complete rectangle is covered by the true
        // usable polygon. The grid never substitutes a bounding box for the boundary.
        internal static List<BoundarySpace> Partition(Geo usable,CancellationToken cancellation,Geo? actualRoom=null,double boundaryClearance=0)
        {
            var result=new List<BoundarySpace>();
            if(usable.IsEmpty || usable.Area<=Geometry.AreaTolerance)return result;
            var bounds=usable.EnvelopeInternal;
            var xs=Axis(usable.Coordinates.Select(c=>c.X),bounds.MinX,bounds.MaxX);
            var ys=Axis(usable.Coordinates.Select(c=>c.Y),bounds.MinY,bounds.MaxY);
            if(xs.Count<2 || ys.Count<2)return result;
            int width=xs.Count-1,height=ys.Count-1;
            var cells=new bool[height,width];var prepared=PreparedGeometryFactory.Prepare(usable.Buffer(.000001,1));
            var actual=actualRoom==null?null:PreparedGeometryFactory.Prepare(actualRoom);
            var wall=actualRoom?.Boundary;
            bool Covered(Geo shape)=>prepared.Covers(shape) && (actual==null || actual.Covers(shape)) &&
                (wall==null || boundaryClearance<=0 || shape.Distance(wall)+.000001>=boundaryClearance);
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                cancellation.ThrowIfCancellationRequested();
                cells[y,x]=Covered(Geometry.Polygon(Geometry.Rect(xs[x],ys[y],xs[x+1]-xs[x],ys[y+1]-ys[y])));
            }
            for(int n=0;n<24;n++)
            {
                cancellation.ThrowIfCancellationRequested();
                double bestArea=0;int left=0,right=0,bottom=0,top=0;
                for(int y0=0;y0<height;y0++)
                {
                    var across=Enumerable.Repeat(true,width).ToArray();
                    for(int y1=y0;y1<height;y1++)
                    {
                        for(int x=0;x<width;x++)across[x]&=cells[y1,x];
                        for(int x0=0;x0<width;)
                        {
                            if(!across[x0]){x0++;continue;}
                            int x1=x0+1;while(x1<width && across[x1])x1++;
                            double area=(xs[x1]-xs[x0])*(ys[y1+1]-ys[y0]);
                            if(area>bestArea+.01){bestArea=area;left=x0;right=x1;bottom=y0;top=y1+1;}
                            x0=x1;
                        }
                    }
                }
                if(bestArea<10000)break;
                var space=new BoundarySpace{X=xs[left],Y=ys[bottom],Width=xs[right]-xs[left],Height=ys[top]-ys[bottom]};
                // A second continuous check protects against any grid/index arithmetic drift.
                if(!Covered(Geometry.Polygon(Geometry.Rect(space.X,space.Y,space.Width,space.Height))))break;
                result.Add(space);
                for(int y=bottom;y<top;y++)for(int x=left;x<right;x++)cells[y,x]=false;
            }
            return result.OrderByDescending(r=>r.Area).ThenBy(r=>r.Y).ThenBy(r=>r.X).ToList();
        }

        private static List<double> Axis(IEnumerable<double> source,double min,double max)
        {
            if(max-min<1)return new List<double>();
            var feature=source.Where(v=>v>min+.001 && v<max-.001).Select(v=>Math.Round(v,6)).Distinct().OrderBy(v=>v).ToList();
            var values=new List<double>{min,max};
            // Keep significant outline changes without retaining thousands of arc chords.
            double spacing=Math.Max(100,(max-min)/40),last=min;
            foreach(double value in feature)
                if(value-last>=spacing){values.Add(value);last=value;}
            double step=Math.Max(500,(max-min)/24);
            for(double value=min+step;value<max-.001;value+=step)values.Add(value);
            var all=new List<double>();
            foreach(double value in values.OrderBy(v=>v))
                if(all.Count==0 || value-all[all.Count-1]>.001)all.Add(value);
            if(all.Count<=48)return all;
            var sampled=new List<double>{min};
            for(int n=1;n<47;n++)sampled.Add(all[(int)Math.Round(n*(all.Count-1)/47.0)]);
            sampled.Add(max);return sampled.Distinct().OrderBy(v=>v).ToList();
        }
    }
}
