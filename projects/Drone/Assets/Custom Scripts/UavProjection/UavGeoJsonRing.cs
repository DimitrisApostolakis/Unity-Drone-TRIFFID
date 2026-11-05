using System;
using Newtonsoft.Json.Linq;

namespace Triffid.Uav
{
    public static class UavGeoJsonRing
    {
        // Coordinates are WGS84 lon/lat/alt. Exterior is CCW; holes are CW.
        public static bool TryClose(JArray ring, bool exterior)
        {
            if (ring.Count < 3) return false;
            double ox = (double)ring[0][0], oy = (double)ring[0][1], area = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                JToken a = ring[i], b = ring[(i + 1) % ring.Count];
                if ((double)a[0] == (double)b[0] && (double)a[1] == (double)b[1]) return false;
                area += ((double)a[0] - ox) * ((double)b[1] - oy) - ((double)b[0] - ox) * ((double)a[1] - oy);
                for (int j = i + 2; j < ring.Count; j++)
                {
                    if (i == 0 && j == ring.Count - 1) continue;
                    if (Intersects(a, b, ring[j], ring[(j + 1) % ring.Count])) return false;
                }
            }
            if (Math.Abs(area) < 1e-20) return false;
            if ((area > 0) != exterior)
            {
                for (int a = 0, b = ring.Count - 1; a < b; a++, b--)
                {
                    JToken first = ring[a].DeepClone(), last = ring[b].DeepClone();
                    ring[a] = last; ring[b] = first;
                }
            }
            ring.Add(ring[0].DeepClone());
            return true;
        }
        private static double Cross(JToken a, JToken b, JToken c) =>
            ((double)b[0] - (double)a[0]) * ((double)c[1] - (double)a[1]) -
            ((double)b[1] - (double)a[1]) * ((double)c[0] - (double)a[0]);
        private static bool OnSegment(JToken a, JToken b, JToken p) =>
            (double)p[0] >= Math.Min((double)a[0], (double)b[0]) &&
            (double)p[0] <= Math.Max((double)a[0], (double)b[0]) &&
            (double)p[1] >= Math.Min((double)a[1], (double)b[1]) &&
            (double)p[1] <= Math.Max((double)a[1], (double)b[1]);
        private static bool Intersects(JToken a, JToken b, JToken c, JToken d)
        {
            double ac = Cross(a, b, c), ad = Cross(a, b, d), ca = Cross(c, d, a), cb = Cross(c, d, b);
            if (((ac > 0 && ad < 0) || (ac < 0 && ad > 0)) &&
                ((ca > 0 && cb < 0) || (ca < 0 && cb > 0))) return true;
            return (ac == 0 && OnSegment(a, b, c)) || (ad == 0 && OnSegment(a, b, d)) ||
                (ca == 0 && OnSegment(c, d, a)) || (cb == 0 && OnSegment(c, d, b));
        }
    }
}
