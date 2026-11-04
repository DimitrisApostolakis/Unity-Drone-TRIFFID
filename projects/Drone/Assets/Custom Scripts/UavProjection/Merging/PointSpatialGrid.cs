using System;
using System.Collections.Generic;

namespace Triffid.Uav
{
    /// <summary>Candidate index only: every cell can contain any number of independent objects.</summary>
    public sealed class PointSpatialGrid
    {
        private readonly double size;
        private readonly Dictionary<(long,long,long),List<int>> cells=new Dictionary<(long,long,long),List<int>>();
        public PointSpatialGrid(double cellMetres) { size=cellMetres; }
        private (long,long,long) Cell(MergePoint p) => (checked((long)Math.Floor(p.X/size)),checked((long)Math.Floor(p.Y/size)),checked((long)Math.Floor(p.Z/size)));
        public void Add(MergePoint p,int id)
        {
            var key=Cell(p);
            if(!cells.TryGetValue(key,out var list)) cells.Add(key,list=new List<int>());
            list.Add(id);
        }
        public IEnumerable<int> Nearby(MergePoint p,double radius)
        {
            var c=Cell(p); int n=(int)Math.Ceiling(radius/size);
            for(long x=c.Item1-n;x<=c.Item1+n;x++)
                for(long y=c.Item2-n;y<=c.Item2+n;y++)
                    for(long z=c.Item3-n;z<=c.Item3+n;z++)
                        if(cells.TryGetValue((x,y,z),out var list)) foreach(int id in list) yield return id;
        }
    }
}
