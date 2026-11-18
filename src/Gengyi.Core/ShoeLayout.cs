using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Geometries.Utilities;
using Geo = NetTopologySuite.Geometries.Geometry;

namespace Gengyi.Core
{
    public static partial class Layout
    {
        private static IEnumerable<Candidate> ShoeChains(Room room,Geo outline,Geo reserved,int target,CancellationToken cancellation)
        {
            if(target==0){yield return new Candidate{Family="空房间"};yield break;}
            var closures=room.Closures.Select(Geometry.RegionFootprint).ToList();
            var solids=Geometry.Union(room.Obstacles.Select(Geometry.RegionFootprint).Concat(closures));
            var occupied=solids.Union(reserved);
            var usable=outline.Buffer(-room.Options.ShoeBoundaryMm,16).Difference(occupied);
            var wall=outline.Boundary;
            foreach(bool vertical in new[]{false,true})
            {
                var orientation=new AffineTransformation(0,1,0,-1,0,room.Boundary!.Width);
                var faces=vertical?closures.Select(g=>orientation.Transform(g)).ToList():closures;
                foreach(var candidate in ShoeChainsInOutline(room,vertical?orientation.Transform(usable):usable,wall,faces,target,vertical,cancellation))
                    yield return candidate;
            }
        }

        private static IEnumerable<Candidate> ShoeChainsInOutline(Room room,Geo usable,Geo wall,IReadOnlyList<Geo> closures,int target,bool vertical,CancellationToken cancellation)
        {
            if(usable.IsEmpty)yield break;
            var bounds=usable.EnvelopeInternal;
            double radius=Equipment.CornerRadiusMm,depth=Equipment.ShoeDepthMm;
            if(bounds.Width<1000+2*radius || bounds.Height<3*depth+2000)yield break;

            var prepared=PreparedGeometryFactory.Prepare(usable.Buffer(.000001,1));
            var features=usable.Coordinates.Select(c=>Math.Round(c.Y,6)).Distinct().OrderBy(y=>y).ToArray();
            var levels=features.Length<=24?features:Enumerable.Range(0,24)
                .Select(i=>features[(int)Math.Round(i*(features.Length-1)/23.0)]).ToArray();
            var xBands=new Dictionary<(double Low,double High),List<(double Start,double End)>>();
            var chains=new Dictionary<double,List<int>?>();
            var closureXs=closures.SelectMany(g=>new[]{g.EnvelopeInternal.MinX,g.EnvelopeInternal.MaxX}).Distinct().ToArray();
            var closureLevels=closures.SelectMany(g=>new[]{g.EnvelopeInternal.MinY,g.EnvelopeInternal.MaxY-Equipment.ShoeDepthMm}).ToArray();
            int Contacts(double x,double y)
            {
                var line=Geometry.Factory.CreateLineString(new[]{new NetTopologySuite.Geometries.Coordinate(x,y),
                    new NetTopologySuite.Geometries.Coordinate(x,y+Equipment.ShoeDepthMm)});
                return closures.Any(c=>c.EnvelopeInternal.Intersects(line.EnvelopeInternal) &&
                    c.Intersection(line).Length+.001>=Equipment.ShoeDepthMm)?1:0;
            }
            double FindTerminal(IReadOnlyList<(double Start,double End)> bands,double anchor,bool onRight,double desired,double y)
            {
                double best=0;int contacts=-1;
                foreach(var band in bands.Where(span=>span.Start<=anchor+.000001 && span.End+.000001>=anchor))
                {
                    double limit=onRight?anchor-band.Start:band.End-anchor;
                    double fitted=LargestRun(Math.Min(limit,Math.Max(1000,desired)),room.Options.ShoeCapacities);
                    double longest=LargestRun(limit,room.Options.ShoeCapacities);
                    foreach(double run in new[]{fitted,longest}.Where(n=>n>=1000).Distinct())
                    {
                        int contact=Contacts(anchor+(onRight?-run:run),y);
                        if(contact>contacts || contact==contacts && CompareShoeCapacity((int)(run*.012),(int)(best*.012),(int)(desired*.012))<0)
                        {best=run;contacts=contact;}
                    }
                }
                return best;
            }
            List<(double Start,double End)> Runs(double low,double high)
            {
                var key=(Math.Round(low,6),Math.Round(high,6));
                if(xBands.TryGetValue(key,out var cached))return cached;
                cancellation.ThrowIfCancellationRequested();
                var band=Geometry.Polygon(Geometry.Rect(bounds.MinX,low,bounds.Width,high-low));
                // The projection of each missing piece excludes its entire x-range from this band.
                var gaps=prepared.Covers(band)?Enumerable.Empty<Geo>():Geometry.Components(band.Difference(usable));
                var runs=new List<(double,double)>();
                double x=bounds.MinX;
                foreach(var gap in gaps.Select(p=>p.EnvelopeInternal).OrderBy(e=>e.MinX))
                {
                    if(gap.MinX>x+.000001)runs.Add((x,gap.MinX));
                    x=Math.Max(x,gap.MaxX);
                }
                if(bounds.MaxX>x+.000001)runs.Add((x,bounds.MaxX));
                xBands[key]=runs;
                return runs;
            }

            var templateRoom=new Room{Id=room.Id,Mode=room.Mode,Options=new LayoutOptions{ShoeBoundaryMm=0}};
            var allowed=room.Options.ShoeCapacities;
            var tiles=new List<(double Start,double End)>{(bounds.MinX,bounds.MaxX)};
            foreach(int connector in allowed.Distinct().Select(c=>c/3*250).Where(mm=>mm>=room.Options.PlanningAisleFor(RoomMode.Shoe)))
            {
                double pitch=depth+connector;
                int maxRows=Math.Min(24,(int)Math.Floor((bounds.Height+connector+.000001)/pitch));
                for(int rows=3;rows<=maxRows;rows++)
                {
                    double maxY=bounds.MaxY-((rows-1)*pitch+depth);
                    double midY=(bounds.MinY+maxY)/2;
                    var yOffsets=levels.SelectMany(feature=>Enumerable.Range(0,rows).SelectMany(i=>new[]{feature-i*pitch,feature-i*pitch-depth}))
                        .Concat(closureLevels.SelectMany(y=>new[]{y,y-(rows-1)*pitch}))
                        .Append(bounds.MinY).Append(midY).Append(maxY)
                        .Where(y=>y>=bounds.MinY-.000001 && y<=maxY+.000001)
                        .Select(y=>Math.Round(y,6)).Distinct().OrderBy(y=>y).ToArray();
                    if(yOffsets.Length>24)
                    {
                        var sample=yOffsets;
                        yOffsets=Enumerable.Range(0,24).Select(i=>sample[(int)Math.Round(i*(sample.Length-1)/23.0)])
                            .Concat(closureLevels.SelectMany(y=>new[]{y,y-(rows-1)*pitch}).Where(y=>y>=bounds.MinY && y<=maxY))
                            .Distinct().ToArray();
                    }

                    var accepted=new HashSet<string>();
                    int[] fits={0,0};
                    double AlignTerminals(double y)=>closureLevels.Length==0?0:
                        closureLevels.Min(c=>Math.Abs(c-y))+closureLevels.Min(c=>Math.Abs(c-y-(rows-1)*pitch));
                    foreach(double y in yOffsets.OrderBy(AlignTerminals).ThenBy(y=>Math.Min(y-bounds.MinY,maxY-y)))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if(fits.All(count=>count==2))break;
                        var common=tiles;
                        var evenTurns=tiles;
                        var oddTurns=tiles;
                        for(int i=0;i<rows && common.Count>0;i++)
                        {
                            double at=y+i*pitch;
                            if(i>0 && i<rows-1)common=IntersectRuns(common,Runs(at,at+depth));
                            if(i==rows-1)continue;
                            if(i%2==0)evenTurns=IntersectRuns(evenTurns,Runs(at+depth,at+pitch));
                            else oddTurns=IntersectRuns(oddTurns,Runs(at+depth,at+pitch));
                        }
                        if(common.Count==0 || evenTurns.Count==0 || oddTurns.Count==0)continue;

                        var firstBand=Runs(y,y+depth);
                        double lastY=y+(rows-1)*pitch;
                        var lastBand=Runs(lastY,lastY+depth);
                        if(firstBand.Count==0 || lastBand.Count==0)continue;
                        for(int side=0;side<fits.Length;side++)
                        {
                            if(fits[side]==2)continue;
                            bool firstRight=side==0;
                            bool lastRight=((rows-2)%2==0)==firstRight;
                            IReadOnlyList<(double Start,double End)> TerminalAnchors(
                                IReadOnlyList<(double Start,double End)> bands,bool right)=>
                                bands.Select(span=>(span.Start+(right?1000:0),span.End-(right?0:1000)))
                                    .Where(span=>span.Item1<=span.Item2+.000001).ToList();
                            var leftTurns=firstRight?oddTurns:evenTurns;
                            var rightTurns=firstRight?evenTurns:oddTurns;
                            Candidate? best=null;
                            int bestCapacity=0;
                            foreach(var runBand in common)
                            {
                                var left=IntersectRuns(leftTurns.Select(span=>(span.Start+radius,span.End))
                                    .Where(span=>span.Item1<=span.End).ToList(),new[]{runBand});
                                var right=IntersectRuns(rightTurns.Select(span=>(span.Start,span.End-radius))
                                    .Where(span=>span.Start<=span.Item2).ToList(),new[]{runBand});
                                if(firstRight)right=IntersectRuns(right,TerminalAnchors(firstBand,true));
                                else left=IntersectRuns(left,TerminalAnchors(firstBand,false));
                                if(lastRight)right=IntersectRuns(right,TerminalAnchors(lastBand,true));
                                else left=IntersectRuns(left,TerminalAnchors(lastBand,false));
                                foreach(var l in left)
                                    foreach(var r in right)
                                    {
                                        double shortest=Math.Max(1000,Math.Ceiling((r.Start-l.End-.000001)/500)*500);
                                        double longest=Math.Floor((r.End-l.Start+.000001)/500)*500;
                                        if(shortest>longest)continue;
                                        double needed=(target-(rows-1)*connector*.012)/(rows*.012);
                                        double preferred=Math.Min(longest,Math.Max(shortest,Math.Ceiling(needed/500)*500));
                                        // Each supported module is a multiple of 500 mm; four
                                        // adjacent lengths cover every supported set of sizes.
                                        var lengths=new[]{preferred,longest,shortest,Math.Max(shortest,Math.Floor(preferred/750)*500)}
                                            .SelectMany(n=>new[]{n,n-500,n+500,n-1000,n+1000,n-1500,n+1500})
                                            .Where(n=>n>=shortest && n<=longest).Distinct();
                                        foreach(double run in lengths)
                                        {
                                            cancellation.ThrowIfCancellationRequested();
                                            if(!chains.TryGetValue(run,out var chain))chains[run]=chain=Capacity.Run(run,allowed);
                                            if(chain==null)continue;
                                            double minX=Math.Max(l.Start,r.Start-run);
                                            double maxX=Math.Min(l.End,r.End-run);
                                            var anchors=new[]{minX,maxX,(minX+maxX)/2}
                                                .Concat(closureXs.SelectMany(face=>new[]{
                                                    face+Math.Ceiling((minX-face-.000001)/500)*500,
                                                    face+Math.Floor((maxX-face+.000001)/500)*500}))
                                                .Where(x=>x>=minX-.000001 && x<=maxX+.000001);
                                            foreach(double x in anchors.Distinct())
                                            {
                                                double firstAnchor=firstRight?x+run:x, lastAnchor=lastRight?x+run:x;
                                                double firstLength=FindTerminal(firstBand,firstAnchor,firstRight,run,y);
                                                double lastLength=FindTerminal(lastBand,lastAnchor,lastRight,run,lastY);
                                                if(firstLength<=0 || lastLength<=0)continue;
                                                int contact=Contacts(firstAnchor+(firstRight?-firstLength:firstLength),y)+
                                                    Contacts(lastAnchor+(lastRight?-lastLength:lastLength),lastY);
                                                if(best!=null && contact<best.ShoeClosureContacts)continue;
                                                if(!chains.TryGetValue(firstLength,out var first))chains[firstLength]=first=Capacity.Run(firstLength,allowed);
                                                if(!chains.TryGetValue(lastLength,out var last))chains[lastLength]=last=Capacity.Run(lastLength,allowed);
                                                if(first==null || last==null)continue;
                                                int lowCapacity=chain.Sum()*(rows-2)+first.Sum()+last.Sum()+(rows-1)*connector*12/1000;
                                                int stackTarget=room.Options.AllowStacking && lowCapacity<target?target:0;
                                                int stackable=ShoeStacking.MaximumStackableInRun(chain)*(rows-2)+
                                                    ShoeStacking.MaximumStackableInRun(first)+ShoeStacking.MaximumStackableInRun(last);
                                                int stacks=stackTarget==0?0:Math.Min((target-lowCapacity+23)/24,stackable);
                                                if(best!=null && contact==best.ShoeClosureContacts &&
                                                    CompareShoeCapacity(bestCapacity,lowCapacity+24*stacks,target)<=0)continue;
                                                var candidate=ShoeTemplate(templateRoom,rows,run,connector,chain,stackTarget,firstRight,first,last);
                                                candidate.ShoeClosureContacts=contact;
                                                var shift=new V2(x-radius,y);
                                                foreach(var item in candidate.Items)item.Position+=shift;
                                                if(!candidate.Items.All(item=>prepared.Covers(Geometry.Footprint(item))))continue;
                                                if(vertical)RotateIntoBoundary(candidate,room.Boundary!.Width);
                                                // The buffered bands guide the search. Measure the
                                                // actual footprint against the selected wall as well.
                                                if(candidate.Items.Any(item=>Geometry.Footprint(item).Distance(wall)+.000001<room.Options.ShoeBoundaryMm))continue;
                                                int capacity=candidate.Items.Sum(p=>p.Equipment.Capacity);
                                                if(best!=null && contact==best.ShoeClosureContacts &&
                                                    CompareShoeCapacity(bestCapacity,capacity,target)<=0)continue;
                                                candidate.Family+=vertical?"/竖向":"/横向";
                                                best=candidate;bestCapacity=capacity;
                                            }
                                        }
                                    }
                            }
                            if(best!=null && accepted.Add(Signature(best.Items,true)))
                            {
                                fits[side]++;
                                yield return best;
                            }
                        }
                    }
                }
            }
        }

        private static List<(double Start,double End)> IntersectRuns(IReadOnlyList<(double Start,double End)> a,
            IReadOnlyList<(double Start,double End)> b)
        {
            var output=new List<(double,double)>();
            int i=0,j=0;
            while(i<a.Count && j<b.Count)
            {
                double start=Math.Max(a[i].Start,b[j].Start),end=Math.Min(a[i].End,b[j].End);
                if(end+.000001>=start)output.Add((start,Math.Max(start,end)));
                if(a[i].End<b[j].End)i++;else j++;
            }
            return output;
        }

        private static int CompareShoeCapacity(int a,int b,int target)
        {
            int shortage=Math.Max(0,target-a).CompareTo(Math.Max(0,target-b));
            return shortage!=0?shortage:Math.Abs(target-a).CompareTo(Math.Abs(target-b));
        }
    }
}
