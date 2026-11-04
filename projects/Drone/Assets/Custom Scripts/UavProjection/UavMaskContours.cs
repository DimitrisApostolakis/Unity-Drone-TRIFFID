using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Triffid.Uav
{
    /// <summary>Exact pixel-edge boundaries. Keeps holes and separates diagonal-only islands.</summary>
    public static class UavMaskContours
    {
        public sealed class Polygon
        {
            public List<Vector2> Exterior;
            public readonly List<List<Vector2>> Holes = new List<List<Vector2>>();
        }
        private readonly struct Edge
        {
            public readonly Vector2Int Start, End;
            public readonly int Direction;
            public Edge(int x, int y, int dx, int dy, int direction)
            { Start = new Vector2Int(x, y); End = new Vector2Int(x + dx, y + dy); Direction = direction; }
        }

        public static List<Polygon> Trace(UavBinaryMask mask, int maximumVertices)
        {
            var edges = new List<Edge>();
            var outgoing = new Dictionary<Vector2Int, List<int>>();
            Action<Edge> add = edge =>
            {
                if (edges.Count >= 2000000) throw new InvalidDataException("Mask boundary exceeds the edge memory budget.");
                if (!outgoing.TryGetValue(edge.Start, out List<int> list))
                    outgoing.Add(edge.Start, list = new List<int>(2));
                list.Add(edges.Count);
                edges.Add(edge);
            };
            for (int y = 0; y < mask.Height; y++)
                for (int x = 0; x < mask.Width; x++)
                {
                    if (!mask.Contains(x, y)) continue;
                    if (!mask.Contains(x, y - 1)) add(new Edge(x, y, 1, 0, 0));
                    if (!mask.Contains(x + 1, y)) add(new Edge(x + 1, y, 0, 1, 1));
                    if (!mask.Contains(x, y + 1)) add(new Edge(x + 1, y + 1, -1, 0, 2));
                    if (!mask.Contains(x - 1, y)) add(new Edge(x, y + 1, 0, -1, 3));
                }
            var used = new bool[edges.Count];
            var polygons = new List<Polygon>();
            var holes = new List<List<Vector2>>();
            int vertices = 0;
            for (int seed = 0; seed < edges.Count; seed++)
            {
                if (used[seed]) continue;
                var ring = new List<Vector2>();
                int current = seed;
                while (true)
                {
                    Edge edge = edges[current];
                    if (used[current]) throw new InvalidDataException("Non-simple mask boundary.");
                    used[current] = true;
                    ring.Add(new Vector2(edge.Start.x + mask.X, edge.Start.y + mask.Y));
                    if (edge.End == edges[seed].Start) break;
                    int best = -1, bestRank = int.MaxValue;
                    foreach (int candidate in outgoing[edge.End])
                    {
                        if (used[candidate]) continue;
                        int turn = (edges[candidate].Direction - edge.Direction + 4) % 4;
                        int rank = turn == 1 ? 0 : turn == 0 ? 1 : turn == 3 ? 2 : 3;
                        if (rank < bestRank) { best = candidate; bestRank = rank; }
                    }
                    if (best < 0) throw new InvalidDataException("Open mask boundary.");
                    current = best;
                }
                ring = RemoveCollinear(ring);
                vertices += ring.Count;
                if (vertices > maximumVertices)
                    throw new InvalidDataException($"Mask exceeds {maximumVertices} contour vertices; raise the budget or clean its mask.");
                if (SignedArea(ring) > 0) polygons.Add(new Polygon { Exterior = ring });
                else holes.Add(ring);
            }
            foreach (List<Vector2> hole in holes)
            {
                Polygon owner = null;
                double smallest = double.MaxValue;
                foreach (Polygon polygon in polygons)
                {
                    double area = SignedArea(polygon.Exterior);
                    if (area < smallest && Contains(polygon.Exterior, hole[0])) { owner = polygon; smallest = area; }
                }
                if (owner == null) throw new InvalidDataException("Mask hole has no containing exterior.");
                owner.Holes.Add(hole);
            }
            return polygons;
        }

        private static List<Vector2> RemoveCollinear(List<Vector2> ring)
        {
            var result = new List<Vector2>();
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 a = ring[(i + ring.Count - 1) % ring.Count], b = ring[i], c = ring[(i + 1) % ring.Count];
                Vector2 u = b - a, v = c - b;
                if (u.x * v.y != u.y * v.x || Vector2.Dot(u, v) <= 0f) result.Add(b);
            }
            if (result.Count < 3) throw new InvalidDataException("Degenerate mask ring.");
            return result;
        }
        public static double SignedArea(List<Vector2> ring)
        {
            double sum = 0;
            for (int i = 0; i < ring.Count; i++)
            { Vector2 a = ring[i], b = ring[(i + 1) % ring.Count]; sum += (double)a.x * b.y - (double)b.x * a.y; }
            return sum / 2;
        }
        private static bool Contains(List<Vector2> ring, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                Vector2 a = ring[i], b = ring[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }
    }
}
