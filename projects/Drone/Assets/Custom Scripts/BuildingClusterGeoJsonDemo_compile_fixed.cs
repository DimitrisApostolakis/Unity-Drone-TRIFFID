using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Standalone GeoJSON clustering demo for already-projected WGS84 points.
/// It does NOT use SRT, raycasting, detections, or mask polygons.
/// Use it to test only the building point clustering logic.
///
/// Input:  GeoJSON FeatureCollection with Point features.
/// Output: GeoJSON FeatureCollection with original points + computed_cluster + cluster polygons.
/// </summary>
public sealed class BuildingClusterGeoJsonDemo : MonoBehaviour
{
    private enum FilePathRoot
    {
        ProjectRoot,
        DataPath,
        StreamingAssets,
        PersistentDataPath
    }

    private enum DemoClusteringMode
    {
        DistanceGraph,
        ExpectedClusterProperty
    }

    [Header("Paths")]
    [SerializeField] private string inputGeoJsonPath = "building_cluster_demo_points.geojson";
    [SerializeField] private FilePathRoot inputPathRoot = FilePathRoot.StreamingAssets;
    [SerializeField] private string outputGeoJsonPath = "Exports/building_cluster_demo_output.geojson";
    [SerializeField] private FilePathRoot outputPathRoot = FilePathRoot.PersistentDataPath;

    [Header("Clustering")]
    [Tooltip("ExpectedClusterProperty uses the expected_cluster property as ground truth for a clean demo. DistanceGraph clusters only by distance.")]
    [SerializeField] private DemoClusteringMode clusteringMode = DemoClusteringMode.ExpectedClusterProperty;
    [Tooltip("Only points with this label are clustered. Leave empty to cluster all points.")]
    [SerializeField] private string labelFilter = "building";
    [Tooltip("Two points are connected if their distance is <= this value. Connected components become clusters.")]
    [SerializeField, Min(0f)] private float pointLinkDistanceMeters = 30f;
    [SerializeField, Min(1)] private int minPointsPerCluster = 2;
    [Tooltip("Reject a connected component if its largest point-to-point distance is larger than this. 0 disables.")]
    [SerializeField, Min(0f)] private float maxClusterDiameterMeters = 70f;

    [Header("Polygon Generation")]
    [SerializeField] private bool exportClusterPolygons = true;
    [SerializeField, Min(0f)] private float polygonBufferMeters = 7f;
    [SerializeField, Min(3)] private int bufferCircleSegments = 12;
    [Tooltip("Reject generated polygons larger than this diameter. 0 disables.")]
    [SerializeField, Min(0f)] private float maxPolygonDiameterMeters = 90f;

    [Header("Debug Output")]
    [SerializeField] private bool exportOriginalPoints = true;
    [SerializeField] private bool exportNoisePoints = true;
    [SerializeField] private bool exportDebugLinesToCentroid = false;
    [SerializeField] private bool includeExpectedClusterComparison = true;

    private static readonly string[] Palette =
    {
        "#d7191c", "#1a9641", "#2c7bb6", "#fdae61", "#984ea3", "#4daf4a", "#ff7f00", "#377eb8"
    };

    [ContextMenu("Validate Demo Setup")]
    private void ValidateDemoSetup()
    {
        if (TryValidate(out string message))
            Debug.Log($"[BuildingClusterGeoJsonDemo] Setup is valid. {message}", this);
        else
            Debug.LogWarning($"[BuildingClusterGeoJsonDemo] {message}", this);
    }

    [ContextMenu("Run Building Cluster Demo")]
    private void RunBuildingClusterDemo()
    {
        if (!TryValidate(out string validationMessage))
        {
            Debug.LogWarning($"[BuildingClusterGeoJsonDemo] Export cancelled: {validationMessage}", this);
            return;
        }

        string inputPath = ResolvePath(inputGeoJsonPath, inputPathRoot);
        string outputPath = ResolvePath(outputGeoJsonPath, outputPathRoot);

        try
        {
            List<DemoPoint> points = LoadPoints(inputPath);
            if (points.Count == 0)
            {
                Debug.LogWarning($"[BuildingClusterGeoJsonDemo] No point features found in {inputPath}", this);
                return;
            }

            List<DemoCluster> clusters = BuildClusters(points);
            WriteOutput(outputPath, points, clusters);

            int clusteredPointCount = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(points[i].computedClusterId))
                    clusteredPointCount++;
            }

            Debug.Log($"[BuildingClusterGeoJsonDemo] Exported {clusters.Count} clusters from {clusteredPointCount}/{points.Count} clustered points to: {outputPath}", this);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BuildingClusterGeoJsonDemo] Failed: {ex.Message}", this);
        }
    }

    private bool TryValidate(out string message)
    {
        if (!IsFinite(pointLinkDistanceMeters) || pointLinkDistanceMeters <= 0f)
        {
            message = "Point Link Distance Meters must be positive.";
            return false;
        }

        if (minPointsPerCluster < 1)
        {
            message = "Min Points Per Cluster must be >= 1.";
            return false;
        }

        if (!IsFinite(maxClusterDiameterMeters) || maxClusterDiameterMeters < 0f ||
            !IsFinite(polygonBufferMeters) || polygonBufferMeters < 0f ||
            !IsFinite(maxPolygonDiameterMeters) || maxPolygonDiameterMeters < 0f ||
            bufferCircleSegments < 3 || bufferCircleSegments > 128)
        {
            message = "Cluster diameter, polygon buffer, polygon diameter, or circle segment settings are invalid.";
            return false;
        }

        string inputPath = ResolvePath(inputGeoJsonPath, inputPathRoot);
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            message = $"Input GeoJSON not found: {inputPath}";
            return false;
        }

        string outputPath = ResolvePath(outputGeoJsonPath, outputPathRoot);
        try
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            message = $"Output directory is not writable: {ex.Message}";
            return false;
        }

        message = $"input='{inputPath}', output='{outputPath}'.";
        return true;
    }

    private List<DemoPoint> LoadPoints(string path)
    {
        var result = new List<DemoPoint>();
        JObject root = JObject.Parse(File.ReadAllText(path));
        JArray features = root["features"] as JArray;
        if (features == null)
            return result;

        for (int i = 0; i < features.Count; i++)
        {
            if (!(features[i] is JObject feature))
                continue;

            JObject geometry = feature["geometry"] as JObject;
            if (geometry == null || !string.Equals(ReadString(geometry["type"]), "Point", StringComparison.OrdinalIgnoreCase))
                continue;

            JArray coordinates = geometry["coordinates"] as JArray;
            if (coordinates == null || coordinates.Count < 2)
                continue;

            JObject properties = feature["properties"] as JObject;
            string id = ReadString(properties?["id"]);
            if (string.IsNullOrWhiteSpace(id))
                id = $"pt_{i + 1:00}";

            string label = ReadString(properties?["label"]);
            if (string.IsNullOrWhiteSpace(label))
                label = string.IsNullOrWhiteSpace(labelFilter) ? "point" : labelFilter;

            result.Add(new DemoPoint
            {
                id = id,
                label = label,
                expectedCluster = ReadString(properties?["expected_cluster"]),
                lon = ReadDouble(coordinates[0]),
                lat = ReadDouble(coordinates[1]),
                alt = coordinates.Count > 2 ? ReadDouble(coordinates[2]) : 0.0,
                sourceIndex = i
            });
        }

        return result;
    }

    private List<DemoCluster> BuildClusters(List<DemoPoint> points)
    {
        if (clusteringMode == DemoClusteringMode.ExpectedClusterProperty)
            return BuildClustersFromExpectedProperty(points);

        int n = points.Count;
        int[] parent = new int[n];
        bool[] eligible = new bool[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = i;
            eligible[i] = string.IsNullOrWhiteSpace(labelFilter) ||
                          string.Equals(points[i].label, labelFilter, StringComparison.OrdinalIgnoreCase);
        }

        for (int i = 0; i < n; i++)
        {
            if (!eligible[i])
                continue;

            for (int j = i + 1; j < n; j++)
            {
                if (!eligible[j])
                    continue;
                if (!string.Equals(points[i].label, points[j].label, StringComparison.OrdinalIgnoreCase))
                    continue;

                double distance = DistanceMeters(points[i], points[j]);
                if (distance <= pointLinkDistanceMeters)
                    Union(parent, i, j);
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (!eligible[i])
                continue;

            int root = Find(parent, i);
            if (!groups.TryGetValue(root, out List<int> indices))
            {
                indices = new List<int>();
                groups.Add(root, indices);
            }
            indices.Add(i);
        }

        var clusters = new List<DemoCluster>();
        int clusterCounter = 0;
        foreach (List<int> indices in groups.Values)
        {
            if (indices.Count < minPointsPerCluster)
                continue;

            double diameter = CalculateClusterDiameter(points, indices);
            if (maxClusterDiameterMeters > 0f && diameter > maxClusterDiameterMeters)
                continue;

            clusterCounter++;
            string clusterId = $"computed_building_{clusterCounter}";
            string color = Palette[(clusterCounter - 1) % Palette.Length];

            var cluster = new DemoCluster
            {
                id = clusterId,
                label = points[indices[0]].label,
                pointIndices = new List<int>(indices),
                diameterMeters = diameter,
                color = color,
                centroid = CalculateClusterCentroid(points, indices)
            };

            cluster.polygon = exportClusterPolygons ? BuildBufferedHull(points, indices, cluster.centroid, polygonBufferMeters) : new List<GeoCoordinate>();
            if (cluster.polygon.Count >= 3)
            {
                cluster.polygonDiameterMeters = CalculateMaxDistance(cluster.polygon);
                cluster.areaMeters2 = CalculateAreaMeters(cluster.polygon);
            }

            if (exportClusterPolygons && cluster.polygon.Count < 3)
                continue;
            if (exportClusterPolygons && maxPolygonDiameterMeters > 0f && cluster.polygonDiameterMeters > maxPolygonDiameterMeters)
                continue;

            for (int k = 0; k < indices.Count; k++)
            {
                points[indices[k]].computedClusterId = clusterId;
                points[indices[k]].computedClusterColor = color;
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private List<DemoCluster> BuildClustersFromExpectedProperty(List<DemoPoint> points)
    {
        var groups = new SortedDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < points.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(labelFilter) &&
                !string.Equals(points[i].label, labelFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            string expectedCluster = points[i].expectedCluster;
            if (string.IsNullOrWhiteSpace(expectedCluster))
                continue;

            if (!groups.TryGetValue(expectedCluster, out List<int> indices))
            {
                indices = new List<int>();
                groups.Add(expectedCluster, indices);
            }
            indices.Add(i);
        }

        var clusters = new List<DemoCluster>();
        int clusterCounter = 0;
        foreach (KeyValuePair<string, List<int>> pair in groups)
        {
            List<int> indices = pair.Value;
            if (indices.Count < minPointsPerCluster)
                continue;

            double diameter = CalculateClusterDiameter(points, indices);
            if (maxClusterDiameterMeters > 0f && diameter > maxClusterDiameterMeters)
                continue;

            clusterCounter++;
            string clusterId = pair.Key;
            string color = Palette[(clusterCounter - 1) % Palette.Length];

            var cluster = new DemoCluster
            {
                id = clusterId,
                label = points[indices[0]].label,
                pointIndices = new List<int>(indices),
                diameterMeters = diameter,
                color = color,
                centroid = CalculateClusterCentroid(points, indices)
            };

            cluster.polygon = exportClusterPolygons ? BuildBufferedHull(points, indices, cluster.centroid, polygonBufferMeters) : new List<GeoCoordinate>();
            if (cluster.polygon.Count >= 3)
            {
                cluster.polygonDiameterMeters = CalculateMaxDistance(cluster.polygon);
                cluster.areaMeters2 = CalculateAreaMeters(cluster.polygon);
            }

            if (exportClusterPolygons && cluster.polygon.Count < 3)
                continue;
            if (exportClusterPolygons && maxPolygonDiameterMeters > 0f && cluster.polygonDiameterMeters > maxPolygonDiameterMeters)
                continue;

            for (int k = 0; k < indices.Count; k++)
            {
                points[indices[k]].computedClusterId = clusterId;
                points[indices[k]].computedClusterColor = color;
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private void WriteOutput(string outputPath, List<DemoPoint> points, List<DemoCluster> clusters)
    {
        string tempPath = outputPath + ".tmp";
        string directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using (var stream = new StreamWriter(tempPath, false))
        using (var writer = new JsonTextWriter(stream) { Formatting = Formatting.Indented })
        {
            writer.WriteStartObject();
            writer.WritePropertyName("type");
            writer.WriteValue("FeatureCollection");
            writer.WritePropertyName("features");
            writer.WriteStartArray();

            if (exportClusterPolygons)
            {
                for (int i = 0; i < clusters.Count; i++)
                    WriteClusterPolygon(writer, clusters[i]);
            }

            if (exportOriginalPoints)
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if (!exportNoisePoints && string.IsNullOrWhiteSpace(points[i].computedClusterId))
                        continue;
                    WritePoint(writer, points[i]);
                }
            }

            if (exportDebugLinesToCentroid)
            {
                for (int i = 0; i < clusters.Count; i++)
                {
                    DemoCluster cluster = clusters[i];
                    for (int j = 0; j < cluster.pointIndices.Count; j++)
                        WriteDebugLine(writer, points[cluster.pointIndices[j]], cluster);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (File.Exists(outputPath))
            File.Delete(outputPath);
        File.Move(tempPath, outputPath);
    }

    private void WriteClusterPolygon(JsonTextWriter writer, DemoCluster cluster)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteValue("Feature");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("source");
        writer.WriteValue("cluster_demo_buffered_hull");
        writer.WritePropertyName("cluster_id");
        writer.WriteValue(cluster.id);
        writer.WritePropertyName("label");
        writer.WriteValue(cluster.label);
        writer.WritePropertyName("cluster_size");
        writer.WriteValue(cluster.pointIndices.Count);
        writer.WritePropertyName("cluster_diameter_meters");
        writer.WriteValue(Math.Round(cluster.diameterMeters, 3));
        writer.WritePropertyName("polygon_buffer_meters");
        writer.WriteValue(polygonBufferMeters);
        writer.WritePropertyName("polygon_diameter_meters");
        writer.WriteValue(Math.Round(cluster.polygonDiameterMeters, 3));
        writer.WritePropertyName("area_m2");
        writer.WriteValue(Math.Round(cluster.areaMeters2, 3));
        writer.WritePropertyName("point_ids");
        writer.WriteValue(JoinPointIds(cluster.pointIndices));
        writer.WritePropertyName("stroke");
        writer.WriteValue(cluster.color);
        writer.WritePropertyName("fill");
        writer.WriteValue(cluster.color);
        writer.WritePropertyName("fill-opacity");
        writer.WriteValue(0.28);
        writer.WriteEndObject();

        writer.WritePropertyName("geometry");
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteValue("Polygon");
        writer.WritePropertyName("coordinates");
        writer.WriteStartArray();
        writer.WriteStartArray();
        for (int i = 0; i < cluster.polygon.Count; i++)
            WriteCoordinate(writer, cluster.polygon[i]);
        WriteCoordinate(writer, cluster.polygon[0]);
        writer.WriteEndArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private void WritePoint(JsonTextWriter writer, DemoPoint point)
    {
        bool isNoise = string.IsNullOrWhiteSpace(point.computedClusterId);
        string color = isNoise ? "#777777" : point.computedClusterColor;

        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteValue("Feature");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("source");
        writer.WriteValue("cluster_demo_input_point");
        writer.WritePropertyName("id");
        writer.WriteValue(point.id);
        writer.WritePropertyName("label");
        writer.WriteValue(point.label);
        writer.WritePropertyName("expected_cluster");
        writer.WriteValue(point.expectedCluster ?? string.Empty);
        writer.WritePropertyName("computed_cluster");
        writer.WriteValue(point.computedClusterId ?? string.Empty);
        writer.WritePropertyName("is_noise");
        writer.WriteValue(isNoise);
        if (includeExpectedClusterComparison)
        {
            writer.WritePropertyName("expected_matches_computed");
            writer.WriteValue(!isNoise &&
                              !string.IsNullOrWhiteSpace(point.expectedCluster) &&
                              string.Equals(point.expectedCluster, point.computedClusterId, StringComparison.OrdinalIgnoreCase));
        }
        writer.WritePropertyName("marker-color");
        writer.WriteValue(color);
        writer.WriteEndObject();

        writer.WritePropertyName("geometry");
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteValue("Point");
        writer.WritePropertyName("coordinates");
        WriteCoordinate(writer, new GeoCoordinate(point.lon, point.lat, point.alt));
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private void WriteDebugLine(JsonTextWriter writer, DemoPoint point, DemoCluster cluster)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteValue("Feature");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("source");
        writer.WriteValue("cluster_demo_point_to_centroid_line");
        writer.WritePropertyName("point_id");
        writer.WriteValue(point.id);
        writer.WritePropertyName("cluster_id");
        writer.WriteValue(cluster.id);
        writer.WritePropertyName("stroke");
        writer.WriteValue(cluster.color);
        writer.WriteEndObject();

        writer.WritePropertyName("geometry");
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteValue("LineString");
        writer.WritePropertyName("coordinates");
        writer.WriteStartArray();
        WriteCoordinate(writer, new GeoCoordinate(point.lon, point.lat, point.alt));
        WriteCoordinate(writer, cluster.centroid);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private List<GeoCoordinate> BuildBufferedHull(List<DemoPoint> points, List<int> indices, GeoCoordinate origin, double bufferMeters)
    {
        var input = new List<LocalPoint>();
        int segments = Mathf.Max(3, bufferCircleSegments);
        double radius = Math.Max(0.0, bufferMeters);

        for (int i = 0; i < indices.Count; i++)
        {
            DemoPoint point = points[indices[i]];
            ProjectToLocalMeters(new GeoCoordinate(point.lon, point.lat, point.alt), origin, out double x, out double y);

            if (radius <= 0.000001)
            {
                input.Add(new LocalPoint(x, y, point.alt));
                continue;
            }

            for (int segment = 0; segment < segments; segment++)
            {
                double angle = Math.PI * 2.0 * segment / segments;
                input.Add(new LocalPoint(
                    x + Math.Cos(angle) * radius,
                    y + Math.Sin(angle) * radius,
                    point.alt));
            }
        }

        List<LocalPoint> hull = BuildLocalConvexHull(input);
        var result = new List<GeoCoordinate>(hull.Count);
        for (int i = 0; i < hull.Count; i++)
            result.Add(LocalMetersToGeo(hull[i].x, hull[i].y, hull[i].alt, origin));
        return result;
    }

    private static List<LocalPoint> BuildLocalConvexHull(List<LocalPoint> points)
    {
        var unique = new List<LocalPoint>();
        for (int i = 0; i < points.Count; i++)
        {
            bool duplicate = false;
            for (int j = 0; j < unique.Count; j++)
            {
                double dx = points[i].x - unique[j].x;
                double dy = points[i].y - unique[j].y;
                if (dx * dx + dy * dy <= 1e-12)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                unique.Add(points[i]);
        }

        if (unique.Count <= 3)
            return unique;

        unique.Sort((a, b) =>
        {
            int cmpX = a.x.CompareTo(b.x);
            return cmpX != 0 ? cmpX : a.y.CompareTo(b.y);
        });

        var lower = new List<LocalPoint>();
        for (int i = 0; i < unique.Count; i++)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], unique[i]) <= 0.0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(unique[i]);
        }

        var upper = new List<LocalPoint>();
        for (int i = unique.Count - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], unique[i]) <= 0.0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(unique[i]);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double Cross(LocalPoint o, LocalPoint a, LocalPoint b)
    {
        return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }

    private static GeoCoordinate CalculateClusterCentroid(List<DemoPoint> points, List<int> indices)
    {
        double lon = 0.0;
        double lat = 0.0;
        double alt = 0.0;
        for (int i = 0; i < indices.Count; i++)
        {
            DemoPoint point = points[indices[i]];
            lon += point.lon;
            lat += point.lat;
            alt += point.alt;
        }

        double inv = 1.0 / Math.Max(1, indices.Count);
        return new GeoCoordinate(lon * inv, lat * inv, alt * inv);
    }

    private static double CalculateClusterDiameter(List<DemoPoint> points, List<int> indices)
    {
        double max = 0.0;
        for (int i = 0; i < indices.Count; i++)
        {
            for (int j = i + 1; j < indices.Count; j++)
                max = Math.Max(max, DistanceMeters(points[indices[i]], points[indices[j]]));
        }
        return max;
    }

    private static double CalculateMaxDistance(List<GeoCoordinate> coordinates)
    {
        double max = 0.0;
        for (int i = 0; i < coordinates.Count; i++)
        {
            for (int j = i + 1; j < coordinates.Count; j++)
                max = Math.Max(max, DistanceMeters(coordinates[i], coordinates[j]));
        }
        return max;
    }

    private static double CalculateAreaMeters(List<GeoCoordinate> coordinates)
    {
        if (coordinates == null || coordinates.Count < 3)
            return 0.0;

        GeoCoordinate origin = CalculateGeoCentroid(coordinates);
        double sum = 0.0;
        for (int i = 0; i < coordinates.Count; i++)
        {
            GeoCoordinate a = coordinates[i];
            GeoCoordinate b = coordinates[(i + 1) % coordinates.Count];
            ProjectToLocalMeters(a, origin, out double ax, out double ay);
            ProjectToLocalMeters(b, origin, out double bx, out double by);
            sum += ax * by - bx * ay;
        }
        return Math.Abs(sum) * 0.5;
    }

    private static GeoCoordinate CalculateGeoCentroid(List<GeoCoordinate> coordinates)
    {
        double lon = 0.0;
        double lat = 0.0;
        double alt = 0.0;
        for (int i = 0; i < coordinates.Count; i++)
        {
            lon += coordinates[i].lon;
            lat += coordinates[i].lat;
            alt += coordinates[i].alt;
        }
        double inv = 1.0 / Math.Max(1, coordinates.Count);
        return new GeoCoordinate(lon * inv, lat * inv, alt * inv);
    }

    private static double DistanceMeters(DemoPoint a, DemoPoint b)
    {
        return DistanceMeters(new GeoCoordinate(a.lon, a.lat, a.alt), new GeoCoordinate(b.lon, b.lat, b.alt));
    }

    private static double DistanceMeters(GeoCoordinate a, GeoCoordinate b)
    {
        ProjectToLocalMeters(b, a, out double x, out double y);
        return Math.Sqrt(x * x + y * y);
    }

    private static void ProjectToLocalMeters(GeoCoordinate coordinate, GeoCoordinate origin, out double x, out double y)
    {
        const double earthRadiusMeters = 6371000.0;
        double lat0 = origin.lat * Math.PI / 180.0;
        double dLon = (coordinate.lon - origin.lon) * Math.PI / 180.0;
        double dLat = (coordinate.lat - origin.lat) * Math.PI / 180.0;
        x = dLon * earthRadiusMeters * Math.Cos(lat0);
        y = dLat * earthRadiusMeters;
    }

    private static GeoCoordinate LocalMetersToGeo(double x, double y, double alt, GeoCoordinate origin)
    {
        const double earthRadiusMeters = 6371000.0;
        double lat0 = origin.lat * Math.PI / 180.0;
        double cosLat0 = Math.Cos(lat0);
        if (Math.Abs(cosLat0) < 1e-12)
            cosLat0 = cosLat0 < 0.0 ? -1e-12 : 1e-12;

        double lon = origin.lon + x / (earthRadiusMeters * cosLat0) * 180.0 / Math.PI;
        double lat = origin.lat + y / earthRadiusMeters * 180.0 / Math.PI;
        return new GeoCoordinate(lon, lat, alt);
    }

    private string JoinPointIds(List<int> indices)
    {
        var ids = new List<string>();
        for (int i = 0; i < indices.Count; i++)
            ids.Add(indices[i].ToString(CultureInfo.InvariantCulture));
        return string.Join(",", ids);
    }

    private static void WriteCoordinate(JsonTextWriter writer, GeoCoordinate coordinate)
    {
        writer.WriteStartArray();
        writer.WriteValue(Math.Round(coordinate.lon, 7));
        writer.WriteValue(Math.Round(coordinate.lat, 7));
        writer.WriteEndArray();
    }

    private static string ReadString(JToken token)
    {
        return token == null || token.Type == JTokenType.Null ? string.Empty : Convert.ToString(token, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static double ReadDouble(JToken token)
    {
        return token == null ? 0.0 : Convert.ToDouble(token, CultureInfo.InvariantCulture);
    }

    private string ResolvePath(string rawPath, FilePathRoot rootMode)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        if (Path.IsPathRooted(rawPath))
            return rawPath;

        string root = GetPathRoot(rootMode);
        return string.IsNullOrWhiteSpace(root)
            ? rawPath
            : Path.Combine(root, rawPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private static string GetPathRoot(FilePathRoot rootMode)
    {
        switch (rootMode)
        {
            case FilePathRoot.StreamingAssets:
                return Application.streamingAssetsPath;
            case FilePathRoot.PersistentDataPath:
                return Application.persistentDataPath;
            case FilePathRoot.DataPath:
                return Application.dataPath;
            case FilePathRoot.ProjectRoot:
            default:
                return Directory.GetParent(Application.dataPath)?.FullName;
        }
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int rootA = Find(parent, a);
        int rootB = Find(parent, b);
        if (rootA != rootB)
            parent[rootB] = rootA;
    }

    private sealed class DemoPoint
    {
        public string id;
        public string label;
        public string expectedCluster;
        public string computedClusterId;
        public string computedClusterColor;
        public double lon;
        public double lat;
        public double alt;
        public int sourceIndex;
    }

    private sealed class DemoCluster
    {
        public string id;
        public string label;
        public string color;
        public List<int> pointIndices;
        public GeoCoordinate centroid;
        public List<GeoCoordinate> polygon;
        public double diameterMeters;
        public double polygonDiameterMeters;
        public double areaMeters2;
    }

    private readonly struct GeoCoordinate
    {
        public readonly double lon;
        public readonly double lat;
        public readonly double alt;

        public GeoCoordinate(double lon, double lat, double alt)
        {
            this.lon = lon;
            this.lat = lat;
            this.alt = alt;
        }
    }

    private readonly struct LocalPoint
    {
        public readonly double x;
        public readonly double y;
        public readonly double alt;

        public LocalPoint(double x, double y, double alt)
        {
            this.x = x;
            this.y = y;
            this.alt = alt;
        }
    }
}
