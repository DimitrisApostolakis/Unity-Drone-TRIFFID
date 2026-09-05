using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Creates one provisional WGS84 footprint per physical-object candidate by associating local
/// detections from different views through shared collider triangles. Raw SurfaceHits are never
/// modified or discarded by this exporter.
/// </summary>
public static class SurfaceHitPolygonExporter
{
    public sealed class Options
    {
        public string ClassFilter = "building";
        public int MinimumSharedTriangles = 1;
        public float MinimumTriangleOverlapRatio = 0.1f;
        public int MinimumViewsPerSupportedTriangle = 2;
    }

    public readonly struct ExportSummary
    {
        public readonly int DetectionCount;
        public readonly int AssociationCount;
        public readonly int CandidateCount;
        public readonly int ExportedPolygonCount;
        public readonly int OmittedCandidateCount;

        public ExportSummary(
            int detectionCount,
            int associationCount,
            int candidateCount,
            int exportedPolygonCount,
            int omittedCandidateCount)
        {
            DetectionCount = detectionCount;
            AssociationCount = associationCount;
            CandidateCount = candidateCount;
            ExportedPolygonCount = exportedPolygonCount;
            OmittedCandidateCount = omittedCandidateCount;
        }
    }

    public static bool TryExport(
        IReadOnlyList<SurfaceHit> hits,
        SrtDroneRaycastPlayer player,
        string outputPath,
        Options options,
        out ExportSummary summary,
        out string error)
    {
        summary = default;
        error = string.Empty;
        if (hits == null || hits.Count == 0)
            return Fail("There are no surface hits to polygonize.", out error);
        if (player == null)
            return Fail("SrtDroneRaycastPlayer is missing.", out error);
        if (options == null)
            return Fail("Polygon export options are missing.", out error);
        if (string.IsNullOrWhiteSpace(options.ClassFilter))
            return Fail("Polygon class filter is empty.", out error);
        if (options.MinimumSharedTriangles < 1)
            return Fail("Minimum shared triangles must be at least one.", out error);
        if (options.MinimumTriangleOverlapRatio <= 0f ||
            options.MinimumTriangleOverlapRatio > 1f)
        {
            return Fail("Triangle overlap ratio must be greater than zero and at most one.", out error);
        }
        if (options.MinimumViewsPerSupportedTriangle < 1)
            return Fail("Minimum views per supported triangle must be at least one.", out error);
        if (string.IsNullOrWhiteSpace(outputPath))
            return Fail("Polygon output path is empty.", out error);

        List<DetectionNode> detections = BuildDetections(hits, options.ClassFilter);
        if (detections.Count == 0)
        {
            return Fail(
                $"No surface hits have class '{options.ClassFilter}'.",
                out error);
        }

        var unionFind = new UnionFind(detections.Count);
        int associationCount = 0;
        for (int leftIndex = 0; leftIndex < detections.Count; leftIndex++)
        {
            DetectionNode left = detections[leftIndex];
            for (int rightIndex = leftIndex + 1; rightIndex < detections.Count; rightIndex++)
            {
                DetectionNode right = detections[rightIndex];
                if (left.ViewIndex == right.ViewIndex ||
                    !string.Equals(
                        left.ClassName, right.ClassName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int minimumTriangleCount = Math.Min(
                    left.TriangleKeys.Count, right.TriangleKeys.Count);
                if (minimumTriangleCount == 0)
                    continue;
                int sharedTriangleCount = CountIntersection(
                    left.TriangleKeys, right.TriangleKeys);
                float overlapRatio = (float)sharedTriangleCount / minimumTriangleCount;
                if (sharedTriangleCount < options.MinimumSharedTriangles ||
                    overlapRatio < options.MinimumTriangleOverlapRatio)
                {
                    continue;
                }

                unionFind.Union(leftIndex, rightIndex);
                associationCount++;
            }
        }

        var groupsByRoot = new Dictionary<int, List<DetectionNode>>();
        for (int i = 0; i < detections.Count; i++)
        {
            int root = unionFind.Find(i);
            if (!groupsByRoot.TryGetValue(root, out List<DetectionNode> group))
            {
                group = new List<DetectionNode>();
                groupsByRoot.Add(root, group);
            }
            group.Add(detections[i]);
        }

        var groups = new List<List<DetectionNode>>(groupsByRoot.Values);
        groups.Sort(CompareGroups);
        var features = new JArray();
        int omittedCount = 0;
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            if (!TryBuildFeature(
                    groups[groupIndex],
                    groupIndex,
                    player,
                    options,
                    out JObject feature))
            {
                omittedCount++;
                continue;
            }
            features.Add(feature);
        }

        var metadata = new JObject
        {
            ["provisional"] = true,
            ["class_filter"] = options.ClassFilter,
            ["association_method"] = "shared_collider_triangles_transitive_graph",
            ["polygon_method"] = "horizontal_convex_hull",
            ["minimum_shared_triangles"] = options.MinimumSharedTriangles,
            ["minimum_triangle_overlap_ratio"] = options.MinimumTriangleOverlapRatio,
            ["minimum_views_per_supported_triangle"] =
                options.MinimumViewsPerSupportedTriangle,
            ["detection_count"] = detections.Count,
            ["association_count"] = associationCount,
            ["candidate_count"] = groups.Count,
            ["exported_polygon_count"] = features.Count,
            ["omitted_candidate_count"] = omittedCount
        };
        var rootObject = new JObject
        {
            ["type"] = "FeatureCollection",
            ["generated_at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["metadata"] = metadata,
            ["features"] = features
        };

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (string.IsNullOrWhiteSpace(directory))
                return Fail("Polygon output directory could not be resolved.", out error);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                outputPath,
                rootObject.ToString(Formatting.Indented));
        }
        catch (Exception exception)
        {
            return Fail($"Could not write polygon GeoJSON: {exception.Message}", out error);
        }

        summary = new ExportSummary(
            detections.Count,
            associationCount,
            groups.Count,
            features.Count,
            omittedCount);
        return true;
    }

    private static List<DetectionNode> BuildDetections(
        IReadOnlyList<SurfaceHit> hits, string classFilter)
    {
        var byId = new Dictionary<string, DetectionNode>(StringComparer.Ordinal);
        for (int i = 0; i < hits.Count; i++)
        {
            SurfaceHit hit = hits[i];
            if (!string.Equals(hit.ClassName, classFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            string detectionId = hit.LocalDetectionId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(detectionId))
                continue;
            if (!byId.TryGetValue(detectionId, out DetectionNode detection))
            {
                detection = new DetectionNode(
                    detectionId, hit.ClassName, hit.ViewIndex, hit.FrameIndex);
                byId.Add(detectionId, detection);
            }
            detection.Hits.Add(hit);
            if (hit.TriangleIndex >= 0)
            {
                detection.TriangleKeys.Add(new TriangleKey(
                    hit.ColliderInstanceId, hit.TriangleIndex));
            }
        }

        var detections = new List<DetectionNode>(byId.Values);
        detections.Sort(DetectionNode.Compare);
        return detections;
    }

    private static bool TryBuildFeature(
        List<DetectionNode> group,
        int groupIndex,
        SrtDroneRaycastPlayer player,
        Options options,
        out JObject feature)
    {
        feature = null;
        var allHits = new List<SurfaceHit>();
        var detectionIds = new List<string>();
        var viewIndices = new HashSet<int>();
        var viewsByTriangle = new Dictionary<TriangleKey, HashSet<int>>();
        for (int i = 0; i < group.Count; i++)
        {
            DetectionNode detection = group[i];
            detectionIds.Add(detection.Id);
            viewIndices.Add(detection.ViewIndex);
            allHits.AddRange(detection.Hits);
            for (int hitIndex = 0; hitIndex < detection.Hits.Count; hitIndex++)
            {
                SurfaceHit hit = detection.Hits[hitIndex];
                if (hit.TriangleIndex < 0)
                    continue;
                var key = new TriangleKey(hit.ColliderInstanceId, hit.TriangleIndex);
                if (!viewsByTriangle.TryGetValue(key, out HashSet<int> views))
                {
                    views = new HashSet<int>();
                    viewsByTriangle.Add(key, views);
                }
                views.Add(hit.ViewIndex);
            }
        }

        var supportedHits = new List<SurfaceHit>();
        for (int i = 0; i < allHits.Count; i++)
        {
            SurfaceHit hit = allHits[i];
            if (hit.TriangleIndex < 0)
                continue;
            var key = new TriangleKey(hit.ColliderInstanceId, hit.TriangleIndex);
            if (viewsByTriangle.TryGetValue(key, out HashSet<int> views) &&
                views.Count >= options.MinimumViewsPerSupportedTriangle)
            {
                supportedHits.Add(hit);
            }
        }

        bool usedMultiViewSupport = supportedHits.Count >= 3;
        List<SurfaceHit> polygonHits = usedMultiViewSupport ? supportedHits : allHits;
        List<GeoPoint> geoPoints = ConvertToGeoPoints(polygonHits, player);
        List<GeoPoint> hull = BuildConvexHull(geoPoints);
        if (hull.Count < 3 && usedMultiViewSupport)
        {
            usedMultiViewSupport = false;
            polygonHits = allHits;
            geoPoints = ConvertToGeoPoints(polygonHits, player);
            hull = BuildConvexHull(geoPoints);
        }
        if (hull.Count < 3)
            return false;

        string className = group[0].ClassName;
        string featureId = $"{SanitizeId(className)}_{groupIndex:D3}";
        var ring = new JArray();
        for (int i = 0; i < hull.Count; i++)
            ring.Add(new JArray(hull[i].Longitude, hull[i].Latitude));
        ring.Add(new JArray(hull[0].Longitude, hull[0].Latitude));

        detectionIds.Sort(StringComparer.Ordinal);
        var sortedViews = new List<int>(viewIndices);
        sortedViews.Sort();
        feature = new JObject
        {
            ["type"] = "Feature",
            ["id"] = featureId,
            ["properties"] = new JObject
            {
                ["class"] = className,
                ["source"] = "mask_raycast_multiview",
                ["provisional"] = true,
                ["association_method"] = "shared_collider_triangles_transitive_graph",
                ["polygon_method"] = "horizontal_convex_hull",
                ["detection_ids"] = new JArray(detectionIds),
                ["view_indices"] = new JArray(sortedViews),
                ["raw_hit_count"] = allHits.Count,
                ["polygon_hit_count"] = polygonHits.Count,
                ["used_multiview_triangle_support"] = usedMultiViewSupport
            },
            ["geometry"] = new JObject
            {
                ["type"] = "Polygon",
                ["coordinates"] = new JArray(ring)
            }
        };
        return true;
    }

    private static List<GeoPoint> ConvertToGeoPoints(
        List<SurfaceHit> hits, SrtDroneRaycastPlayer player)
    {
        var points = new List<GeoPoint>(hits.Count);
        var unique = new HashSet<GeoPoint>();
        for (int i = 0; i < hits.Count; i++)
        {
            if (!player.TryConvertWorldToWgs84(
                    hits[i].WorldPoint,
                    out double longitude,
                    out double latitude,
                    out double altitude))
            {
                continue;
            }
            var point = new GeoPoint(longitude, latitude);
            if (unique.Add(point))
                points.Add(point);
        }
        return points;
    }

    private static List<GeoPoint> BuildConvexHull(List<GeoPoint> points)
    {
        if (points.Count < 3)
            return points;
        points.Sort(GeoPoint.Compare);
        var lower = new List<GeoPoint>();
        for (int i = 0; i < points.Count; i++)
        {
            while (lower.Count >= 2 && Cross(
                       lower[lower.Count - 2], lower[lower.Count - 1], points[i]) <= 0.0)
            {
                lower.RemoveAt(lower.Count - 1);
            }
            lower.Add(points[i]);
        }

        var upper = new List<GeoPoint>();
        for (int i = points.Count - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 && Cross(
                       upper[upper.Count - 2], upper[upper.Count - 1], points[i]) <= 0.0)
            {
                upper.RemoveAt(upper.Count - 1);
            }
            upper.Add(points[i]);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(GeoPoint origin, GeoPoint left, GeoPoint right)
    {
        return (left.Longitude - origin.Longitude) * (right.Latitude - origin.Latitude) -
               (left.Latitude - origin.Latitude) * (right.Longitude - origin.Longitude);
    }

    private static int CountIntersection(
        HashSet<TriangleKey> left, HashSet<TriangleKey> right)
    {
        HashSet<TriangleKey> smaller = left.Count <= right.Count ? left : right;
        HashSet<TriangleKey> larger = left.Count <= right.Count ? right : left;
        int count = 0;
        foreach (TriangleKey key in smaller)
        {
            if (larger.Contains(key))
                count++;
        }
        return count;
    }

    private static int CompareGroups(List<DetectionNode> left, List<DetectionNode> right)
    {
        string leftId = FindMinimumId(left);
        string rightId = FindMinimumId(right);
        return string.Compare(leftId, rightId, StringComparison.Ordinal);
    }

    private static string FindMinimumId(List<DetectionNode> group)
    {
        string minimum = group[0].Id;
        for (int i = 1; i < group.Count; i++)
        {
            if (string.Compare(group[i].Id, minimum, StringComparison.Ordinal) < 0)
                minimum = group[i].Id;
        }
        return minimum;
    }

    private static string SanitizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "object";
        var characters = value.Trim().ToLowerInvariant().ToCharArray();
        for (int i = 0; i < characters.Length; i++)
        {
            if (!char.IsLetterOrDigit(characters[i]) && characters[i] != '_')
                characters[i] = '_';
        }
        return new string(characters);
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private sealed class DetectionNode
    {
        public readonly string Id;
        public readonly string ClassName;
        public readonly int ViewIndex;
        public readonly int FrameIndex;
        public readonly List<SurfaceHit> Hits = new List<SurfaceHit>();
        public readonly HashSet<TriangleKey> TriangleKeys = new HashSet<TriangleKey>();

        public DetectionNode(string id, string className, int viewIndex, int frameIndex)
        {
            Id = id;
            ClassName = className;
            ViewIndex = viewIndex;
            FrameIndex = frameIndex;
        }

        public static int Compare(DetectionNode left, DetectionNode right)
        {
            int viewComparison = left.ViewIndex.CompareTo(right.ViewIndex);
            return viewComparison != 0
                ? viewComparison
                : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
        }
    }

    private readonly struct TriangleKey : IEquatable<TriangleKey>
    {
        private readonly int colliderInstanceId;
        private readonly int triangleIndex;

        public TriangleKey(int colliderInstanceId, int triangleIndex)
        {
            this.colliderInstanceId = colliderInstanceId;
            this.triangleIndex = triangleIndex;
        }

        public bool Equals(TriangleKey other)
        {
            return colliderInstanceId == other.colliderInstanceId &&
                   triangleIndex == other.triangleIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is TriangleKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (colliderInstanceId * 397) ^ triangleIndex;
            }
        }
    }

    private readonly struct GeoPoint : IEquatable<GeoPoint>
    {
        public readonly double Longitude;
        public readonly double Latitude;

        public GeoPoint(double longitude, double latitude)
        {
            Longitude = longitude;
            Latitude = latitude;
        }

        public bool Equals(GeoPoint other)
        {
            return Longitude.Equals(other.Longitude) && Latitude.Equals(other.Latitude);
        }

        public override bool Equals(object obj)
        {
            return obj is GeoPoint other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Longitude.GetHashCode() * 397) ^ Latitude.GetHashCode();
            }
        }

        public static int Compare(GeoPoint left, GeoPoint right)
        {
            int longitudeComparison = left.Longitude.CompareTo(right.Longitude);
            return longitudeComparison != 0
                ? longitudeComparison
                : left.Latitude.CompareTo(right.Latitude);
        }
    }

    private sealed class UnionFind
    {
        private readonly int[] parent;
        private readonly byte[] rank;

        public UnionFind(int count)
        {
            parent = new int[count];
            rank = new byte[count];
            for (int i = 0; i < count; i++)
                parent[i] = i;
        }

        public int Find(int value)
        {
            if (parent[value] != value)
                parent[value] = Find(parent[value]);
            return parent[value];
        }

        public void Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot == rightRoot)
                return;
            if (rank[leftRoot] < rank[rightRoot])
                parent[leftRoot] = rightRoot;
            else if (rank[leftRoot] > rank[rightRoot])
                parent[rightRoot] = leftRoot;
            else
            {
                parent[rightRoot] = leftRoot;
                rank[leftRoot]++;
            }
        }
    }
}
