using System;
using UnityEngine;

[CreateAssetMenu(
    fileName = "DroneGeoJsonExportConfiguration",
    menuName = "TRIFFID/Drone GeoJSON Export Configuration")]
public sealed class DroneGeoJsonExportConfiguration : ScriptableObject
{
    [Header("Paths")]
    public string detectionJsonPath = "campus_video_detections.json";
    public DroneDetectionGeoJsonExporter.FilePathRoot inputPathRoot = DroneDetectionGeoJsonExporter.FilePathRoot.StreamingAssets;
    public DroneDetectionGeoJsonExporter.FilePathRoot outputPathRoot = DroneDetectionGeoJsonExporter.FilePathRoot.ProjectRoot;
    public string geoJsonOutputPath = "Exports/detection_polygons.geojson";

    [Header("Feature Types")]
    public bool exportPoints;
    public bool exportPolygons = true;
    public bool oneFeaturePerTrackId = true;
    public bool preferHighestConfidencePerTrack = true;
    [Range(0f, 1f)] public float confidenceThreshold = 0.95f;
    [Min(1)] public int maxFeatures = 20;
    [Min(1)] public int sampleEveryNFrames = 1;
    [Min(1)] public int sampleEveryNDetections = 1;
    public string[] areaLabels = { "building" };
    public string[] pointLabels = Array.Empty<string>();

    [Header("Polygon Simplification")]
    [Min(3)] public int maxPolygonVertices = 8;
    [Min(0f)] public float simplificationEpsilonPixels = 25f;
    [Min(3)] public int minValidPolygonVertices = 3;
    [Range(0f, 1f)] public float minPolygonHitRatio = 0.85f;
    public bool includeAltitude = true;
    public bool fallbackToPointWhenPolygonFails;

    [Header("Polygon Debug Points")]
    public bool exportRepresentativePointForPolygons = true;
    public bool representativePointsCountTowardMaxFeatures;

    [Header("Polygon Cleanup")]
    public bool useConvexHullForPolygons = true;
    public bool rejectBorderClippedMasks = true;
    [Min(0f)] public float borderMarginPixels = 50f;
    [Min(0f)] public float minMaskAreaPixels = 50000f;

    [Header("Strict Polygon Merge")]
    public bool mergeNearbyPolygons = true;
    public string[] mergePolygonLabels = { "building" };
    [Min(0f)] public float mergeDistanceMeters = 8f;
    public bool requireBoundaryDistanceForMerge = true;
    [Min(0f)] public float mergeBoundaryDistanceMeters = 3f;
    [Min(1f)] public float maxMergedHullAreaGrowthRatio = 2f;
    [Min(0f)] public float maxMergedClusterDiameterMeters = 25f;
    [Min(2)] public int minPolygonsPerMergeCluster = 2;
    public bool keepOriginalPolygonsWhenMerged;
    public bool useConvexHullForMergedPolygons = true;

    [Header("Representative Point Clustering")]
    public bool clusterByRepresentativePoints = true;
    public string[] pointClusterLabels = { "building" };
    [Min(0f)] public float pointClusterDistanceMeters = 10f;
    [Min(2)] public int minPointsPerCluster = 2;
    [Min(0f)] public float maxPointClusterDiameterMeters = 25f;
    public bool buildMergedPolygonFromPointClusters = true;
    [Min(0f)] public float maxVertexDistanceFromPointClusterCenterMeters = 20f;
    public bool exportUnclusteredRawPolygons;
    public bool exportPointClusterRepresentativePoints = true;
    public bool exportDebugPointToClusterLines;
    public bool buildPolygonFromPointClusterFallback = true;
    public bool preferPointClusterHullOverRawPolygonHull = true;
    [Min(0f)] public float pointClusterPolygonBufferMeters = 6f;
    [Min(0f)] public float maxPointClusterPolygonDiameterMeters = 40f;
    [Min(3)] public int pointClusterHullCircleSegments = 8;
    public bool includeRawPolygonVerticesInPointClusterHull;
    [Min(0f)] public float maxRawVertexDistanceFromPointClusterCenterMeters = 20f;
}
