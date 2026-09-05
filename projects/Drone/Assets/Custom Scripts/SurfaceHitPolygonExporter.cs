using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Clusters georeferenced raycast hits in a local metric plane and creates one provisional
/// WGS84 footprint from the outer points of each dense cluster. Raw SurfaceHits are never
/// modified or discarded by this exporter.
/// </summary>
public static class SurfaceHitPolygonExporter
{
    private const double EarthRadiusMeters = 6378137.0;
    private const int Unvisited = -2;
    private const int Noise = -1;

    public sealed class Options
    {
        public string ClassFilter = "building";
        public float DbscanEpsilonMeters = 2f;
        public int DbscanMinimumPoints = 5;
    }

    public readonly struct ExportSummary
    {
        public readonly int InputHitCount;
        public readonly int GeoreferencedHitCount;
        public readonly int ClusterCount;
        public readonly int NoiseHitCount;
        public readonly int ExportedPolygonCount;
        public readonly int OmittedClusterCount;

        public ExportSummary(
            int inputHitCount,
            int georeferencedHitCount,
            int clusterCount,
            int noiseHitCount,
            int exportedPolygonCount,
            int omittedClusterCount)
        {
            InputHitCount = inputHitCount;
            GeoreferencedHitCount = georeferencedHitCount;
            ClusterCount = clusterCount;
            NoiseHitCount = noiseHitCount;
            ExportedPolygonCount = exportedPolygonCount;
            OmittedClusterCount = omittedClusterCount;
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
        if (options.DbscanEpsilonMeters <= 0f)
            return Fail("DBSCAN epsilon must be greater than zero metres.", out error);
        if (options.DbscanMinimumPoints < 3)
            return Fail("DBSCAN minimum points must be at least three.", out error);
        if (string.IsNullOrWhiteSpace(outputPath))
            return Fail("Polygon output path is empty.", out error);

        int inputHitCount = CountClassHits(hits, options.ClassFilter);
        if (inputHitCount == 0)
            return Fail($"No surface hits have class '{options.ClassFilter}'.", out error);

        List<ClusterPoint> points = BuildClusterPoints(
            hits, options.ClassFilter, player, out int failedGeoreferenceCount);
        if (points.Count == 0)
        {
            return Fail(
                $"None of the {inputHitCount} '{options.ClassFilter}' hits could be " +
                "converted to WGS84.",
                out error);
        }

        ProjectToLocalMetricPlane(points);
        int[] labels = RunDbscan(
            points,
            options.DbscanEpsilonMeters,
            options.DbscanMinimumPoints,
            out int clusterCount,
            out int noiseHitCount);

        var clusters = new List<List<ClusterPoint>>(clusterCount);
        for (int clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
            clusters.Add(new List<ClusterPoint>());
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            if (labels[pointIndex] >= 0)
                clusters[labels[pointIndex]].Add(points[pointIndex]);
        }

        var features = new JArray();
        int omittedCount = 0;
        for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
        {
            if (!TryBuildFeature(
                    clusters[clusterIndex],
                    clusterIndex,
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
            ["clustering_method"] = "dbscan_local_enu",
            ["polygon_method"] = "horizontal_convex_hull",
            ["dbscan_epsilon_m"] = options.DbscanEpsilonMeters,
            ["dbscan_minimum_points"] = options.DbscanMinimumPoints,
            ["input_hit_count"] = inputHitCount,
            ["georeferenced_hit_count"] = points.Count,
            ["failed_georeference_hit_count"] = failedGeoreferenceCount,
            ["cluster_count"] = clusterCount,
            ["noise_hit_count"] = noiseHitCount,
            ["exported_polygon_count"] = features.Count,
            ["omitted_cluster_count"] = omittedCount
        };
        var rootObject = new JObject
        {
            ["type"] = "FeatureCollection",
            ["generated_at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["metadata"] = metadata,
            ["features"] = features
        };

        if (!TryValidatePolygonFeatures(features, out error))
            return false;

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (string.IsNullOrWhiteSpace(directory))
                return Fail("Polygon output directory could not be resolved.", out error);
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, rootObject.ToString(Formatting.Indented));
        }
        catch (Exception exception)
        {
            return Fail($"Could not write polygon GeoJSON: {exception.Message}", out error);
        }

        summary = new ExportSummary(
            inputHitCount,
            points.Count,
            clusterCount,
            noiseHitCount,
            features.Count,
            omittedCount);
        return true;
    }

    private static int CountClassHits(IReadOnlyList<SurfaceHit> hits, string classFilter)
    {
        int count = 0;
        for (int i = 0; i < hits.Count; i++)
        {
            if (string.Equals(hits[i].ClassName, classFilter, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private static List<ClusterPoint> BuildClusterPoints(
        IReadOnlyList<SurfaceHit> hits,
        string classFilter,
        SrtDroneRaycastPlayer player,
        out int failedGeoreferenceCount)
    {
        failedGeoreferenceCount = 0;
        var points = new List<ClusterPoint>();
        for (int i = 0; i < hits.Count; i++)
        {
            SurfaceHit hit = hits[i];
            if (!string.Equals(hit.ClassName, classFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!player.TryConvertWorldToWgs84(
                    hit.WorldPoint,
                    out double longitude,
                    out double latitude,
                    out double altitude))
            {
                failedGeoreferenceCount++;
                continue;
            }
            points.Add(new ClusterPoint(hit, longitude, latitude));
        }
        return points;
    }

    private static void ProjectToLocalMetricPlane(List<ClusterPoint> points)
    {
        double longitudeSum = 0.0;
        double latitudeSum = 0.0;
        for (int i = 0; i < points.Count; i++)
        {
            longitudeSum += points[i].Geo.Longitude;
            latitudeSum += points[i].Geo.Latitude;
        }

        double referenceLongitude = longitudeSum / points.Count;
        double referenceLatitude = latitudeSum / points.Count;
        double longitudeScale = Math.Cos(DegreesToRadians(referenceLatitude));
        for (int i = 0; i < points.Count; i++)
        {
            ClusterPoint point = points[i];
            point.EastMeters = EarthRadiusMeters *
                DegreesToRadians(point.Geo.Longitude - referenceLongitude) * longitudeScale;
            point.NorthMeters = EarthRadiusMeters *
                DegreesToRadians(point.Geo.Latitude - referenceLatitude);
        }
    }

    private static int[] RunDbscan(
        List<ClusterPoint> points,
        double epsilonMeters,
        int minimumPoints,
        out int clusterCount,
        out int noiseHitCount)
    {
        var labels = new int[points.Count];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = Unvisited;

        Dictionary<GridKey, List<int>> spatialGrid = BuildSpatialGrid(points, epsilonMeters);
        var queuedGeneration = new int[points.Count];
        int generation = 0;
        clusterCount = 0;

        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            if (labels[pointIndex] != Unvisited)
                continue;

            List<int> neighbours = FindNeighbours(
                pointIndex, points, spatialGrid, epsilonMeters);
            if (neighbours.Count < minimumPoints)
            {
                labels[pointIndex] = Noise;
                continue;
            }

            int clusterLabel = clusterCount++;
            labels[pointIndex] = clusterLabel;
            generation++;
            var expansionQueue = new Queue<int>();
            EnqueueUnique(neighbours, expansionQueue, queuedGeneration, generation);

            while (expansionQueue.Count > 0)
            {
                int neighbourIndex = expansionQueue.Dequeue();
                if (labels[neighbourIndex] == Noise)
                    labels[neighbourIndex] = clusterLabel;
                if (labels[neighbourIndex] != Unvisited)
                    continue;

                labels[neighbourIndex] = clusterLabel;
                List<int> neighbourNeighbours = FindNeighbours(
                    neighbourIndex, points, spatialGrid, epsilonMeters);
                if (neighbourNeighbours.Count >= minimumPoints)
                {
                    EnqueueUnique(
                        neighbourNeighbours,
                        expansionQueue,
                        queuedGeneration,
                        generation);
                }
            }
        }

        noiseHitCount = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] == Noise)
                noiseHitCount++;
        }
        return labels;
    }

    private static Dictionary<GridKey, List<int>> BuildSpatialGrid(
        List<ClusterPoint> points, double cellSize)
    {
        var grid = new Dictionary<GridKey, List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            GridKey key = GridKey.FromPoint(points[i], cellSize);
            if (!grid.TryGetValue(key, out List<int> indices))
            {
                indices = new List<int>();
                grid.Add(key, indices);
            }
            indices.Add(i);
        }
        return grid;
    }

    private static List<int> FindNeighbours(
        int pointIndex,
        List<ClusterPoint> points,
        Dictionary<GridKey, List<int>> grid,
        double epsilonMeters)
    {
        ClusterPoint point = points[pointIndex];
        GridKey centre = GridKey.FromPoint(point, epsilonMeters);
        double squaredEpsilon = epsilonMeters * epsilonMeters;
        var neighbours = new List<int>();
        for (int xOffset = -1; xOffset <= 1; xOffset++)
        {
            for (int yOffset = -1; yOffset <= 1; yOffset++)
            {
                var key = new GridKey(centre.X + xOffset, centre.Y + yOffset);
                if (!grid.TryGetValue(key, out List<int> candidates))
                    continue;
                for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    int candidate = candidates[candidateIndex];
                    double eastDelta = points[candidate].EastMeters - point.EastMeters;
                    double northDelta = points[candidate].NorthMeters - point.NorthMeters;
                    if (eastDelta * eastDelta + northDelta * northDelta <= squaredEpsilon)
                        neighbours.Add(candidate);
                }
            }
        }
        return neighbours;
    }

    private static void EnqueueUnique(
        List<int> indices,
        Queue<int> queue,
        int[] queuedGeneration,
        int generation)
    {
        for (int i = 0; i < indices.Count; i++)
        {
            int index = indices[i];
            if (queuedGeneration[index] == generation)
                continue;
            queuedGeneration[index] = generation;
            queue.Enqueue(index);
        }
    }

    private static bool TryBuildFeature(
        List<ClusterPoint> cluster,
        int clusterIndex,
        Options options,
        out JObject feature)
    {
        feature = null;
        var geoPoints = new List<GeoPoint>(cluster.Count);
        var uniqueGeoPoints = new HashSet<GeoPoint>();
        var detectionIds = new HashSet<string>(StringComparer.Ordinal);
        var viewIndices = new HashSet<int>();
        for (int i = 0; i < cluster.Count; i++)
        {
            ClusterPoint point = cluster[i];
            if (uniqueGeoPoints.Add(point.Geo))
                geoPoints.Add(point.Geo);
            if (!string.IsNullOrWhiteSpace(point.Hit.LocalDetectionId))
                detectionIds.Add(point.Hit.LocalDetectionId);
            viewIndices.Add(point.Hit.ViewIndex);
        }

        List<GeoPoint> hull = BuildConvexHull(geoPoints);
        if (hull.Count < 3)
            return false;

        var ring = new JArray();
        for (int i = 0; i < hull.Count; i++)
            ring.Add(new JArray(hull[i].Longitude, hull[i].Latitude));
        ring.Add(new JArray(hull[0].Longitude, hull[0].Latitude));
        var polygonCoordinates = new JArray();
        polygonCoordinates.Add(ring);

        var sortedDetectionIds = new List<string>(detectionIds);
        sortedDetectionIds.Sort(StringComparer.Ordinal);
        var sortedViews = new List<int>(viewIndices);
        sortedViews.Sort();
        feature = new JObject
        {
            ["type"] = "Feature",
            ["id"] = $"{SanitizeId(options.ClassFilter)}_{clusterIndex:D3}",
            ["properties"] = new JObject
            {
                ["class"] = options.ClassFilter,
                ["source"] = "mask_raycast_multiview",
                ["provisional"] = true,
                ["clustering_method"] = "dbscan_local_enu",
                ["polygon_method"] = "horizontal_convex_hull",
                ["detection_ids"] = new JArray(sortedDetectionIds),
                ["view_indices"] = new JArray(sortedViews),
                ["cluster_hit_count"] = cluster.Count,
                ["hull_vertex_count"] = hull.Count
            },
            ["geometry"] = new JObject
            {
                ["type"] = "Polygon",
                ["coordinates"] = polygonCoordinates
            }
        };
        return true;
    }

    private static bool TryValidatePolygonFeatures(JArray features, out string error)
    {
        error = string.Empty;
        for (int featureIndex = 0; featureIndex < features.Count; featureIndex++)
        {
            JObject feature = features[featureIndex] as JObject;
            JObject geometry = feature?["geometry"] as JObject;
            JArray coordinates = geometry?["coordinates"] as JArray;
            if (geometry == null ||
                !string.Equals((string)geometry["type"], "Polygon", StringComparison.Ordinal) ||
                coordinates == null || coordinates.Count == 0)
            {
                return Fail(
                    $"Generated feature {featureIndex} does not contain valid Polygon coordinates.",
                    out error);
            }

            for (int ringIndex = 0; ringIndex < coordinates.Count; ringIndex++)
            {
                JArray ring = coordinates[ringIndex] as JArray;
                if (ring == null || ring.Count < 4)
                {
                    return Fail(
                        $"Generated feature {featureIndex}, ring {ringIndex} has fewer than " +
                        "four positions.",
                        out error);
                }

                for (int positionIndex = 0; positionIndex < ring.Count; positionIndex++)
                {
                    JArray position = ring[positionIndex] as JArray;
                    if (position == null || position.Count < 2 ||
                        !IsNumeric(position[0]) || !IsNumeric(position[1]))
                    {
                        return Fail(
                            $"Generated feature {featureIndex}, ring {ringIndex}, position " +
                            $"{positionIndex} is not a numeric longitude/latitude pair.",
                            out error);
                    }
                }

                if (!JToken.DeepEquals(ring[0], ring[ring.Count - 1]))
                {
                    return Fail(
                        $"Generated feature {featureIndex}, ring {ringIndex} is not closed.",
                        out error);
                }
            }
        }
        return true;
    }

    private static bool IsNumeric(JToken token)
    {
        return token != null &&
               (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);
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

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
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

    private sealed class ClusterPoint
    {
        public readonly SurfaceHit Hit;
        public readonly GeoPoint Geo;
        public double EastMeters;
        public double NorthMeters;

        public ClusterPoint(SurfaceHit hit, double longitude, double latitude)
        {
            Hit = hit;
            Geo = new GeoPoint(longitude, latitude);
        }
    }

    private readonly struct GridKey : IEquatable<GridKey>
    {
        public readonly int X;
        public readonly int Y;

        public GridKey(int x, int y)
        {
            X = x;
            Y = y;
        }

        public static GridKey FromPoint(ClusterPoint point, double cellSize)
        {
            return new GridKey(
                (int)Math.Floor(point.EastMeters / cellSize),
                (int)Math.Floor(point.NorthMeters / cellSize));
        }

        public bool Equals(GridKey other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is GridKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Y;
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
}
