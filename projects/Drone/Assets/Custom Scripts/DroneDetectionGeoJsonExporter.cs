using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public sealed class DroneDetectionGeoJsonExporter : MonoBehaviour
{
    [Header("Configuration")]
    [SerializeField] private DroneGeoJsonExportConfiguration initialConfiguration;

    [Header("Projection Engine")]
    [SerializeField] private SrtDroneRaycastPlayer player;

    [Header("Paths")]
    [SerializeField] private string detectionJsonPath = "campus_video_detections.json";
    [SerializeField] private FilePathRoot inputPathRoot = FilePathRoot.StreamingAssets;
    [SerializeField] private FilePathRoot outputPathRoot = FilePathRoot.PersistentDataPath;
    [SerializeField] private string geoJsonOutputPath = "Exports/detection_polygons.geojson";

    [Header("Feature Types")]
    [SerializeField] private bool exportPoints = false;
    [SerializeField] private bool exportPolygons = true;
    [SerializeField] private bool oneFeaturePerTrackId = true;
    [SerializeField] private bool preferHighestConfidencePerTrack = true;
    [SerializeField, Range(0f, 1f)] private float confidenceThreshold = 0.95f;
    [SerializeField, Min(1)] private int maxFeatures = 20;
    [SerializeField, Min(1)] private int sampleEveryNFrames = 1;
    [SerializeField, Min(1)] private int sampleEveryNDetections = 1;
    [SerializeField] private string[] areaLabels = { "building" };
    [SerializeField] private string[] pointLabels = Array.Empty<string>();

    [Header("Polygon Simplification")]
    [SerializeField, Min(3)] private int maxPolygonVertices = 8;
    [SerializeField, Min(0f)] private float simplificationEpsilonPixels = 25f;
    [SerializeField, Min(3)] private int minValidPolygonVertices = 3;
    [SerializeField, Range(0f, 1f)] private float minPolygonHitRatio = 0.85f;
    [SerializeField] private bool includeAltitude = true;
    [SerializeField] private bool fallbackToPointWhenPolygonFails = false;

    [Header("Polygon Debug Points")]
    [Tooltip("When a polygon is exported, also export the representative detection point from the same detection. Useful for debugging polygon offset/alignment.")]
    [SerializeField] private bool exportRepresentativePointForPolygons = true;
    [Tooltip("If enabled, representative debug points count toward Max Features. If disabled, they are written in addition to Max Features.")]
    [SerializeField] private bool representativePointsCountTowardMaxFeatures = false;

    [Header("Polygon Cleanup")]
    [Tooltip("Converts projected polygon vertices to a 2D convex hull in lon/lat space. This removes self-crossing rings and makes demo polygons more defined.")]
    [SerializeField] private bool useConvexHullForPolygons = true;
    [Tooltip("Reject masks that touch the image border, because they usually represent partial buildings.")]
    [SerializeField] private bool rejectBorderClippedMasks = true;
    [SerializeField, Min(0f)] private float borderMarginPixels = 50f;
    [Tooltip("Reject tiny area-label masks before projection. Area is measured in original video pixels.")]
    [SerializeField, Min(0f)] private float minMaskAreaPixels = 50000f;

    [Header("Polygon Merge")]
    [Tooltip("Merge nearby projected polygons from the same labels into one cleaner polygon. Useful when several tracks describe the same real building.")]
    [SerializeField] private bool mergeNearbyPolygons = true;
    [SerializeField] private string[] mergePolygonLabels = { "building" };
    [SerializeField, Min(0f)] private float mergeDistanceMeters = 8f;
    [Tooltip("Require polygon bounds to overlap/touch or be within this boundary gap before merging. This prevents nearby but separate buildings from being merged only because their centroids are close.")]
    [SerializeField] private bool requireBoundaryDistanceForMerge = true;
    [SerializeField, Min(0f)] private float mergeBoundaryDistanceMeters = 3f;
    [Tooltip("Reject a merge if the merged convex hull area becomes too large compared to the sum of the original polygon areas. Helps prevent merging separate buildings with empty space between them.")]
    [SerializeField, Min(1f)] private float maxMergedHullAreaGrowthRatio = 2.0f;
    [Tooltip("Reject a merged cluster if its largest vertex-to-vertex span exceeds this many meters. Set 0 to disable.")]
    [SerializeField, Min(0f)] private float maxMergedClusterDiameterMeters = 25f;
    [SerializeField, Min(2)] private int minPolygonsPerMergeCluster = 2;
    [SerializeField] private bool keepOriginalPolygonsWhenMerged = false;
    [SerializeField] private bool useConvexHullForMergedPolygons = true;

    [Header("Representative Point Clustering")]
    [Tooltip("Build same-label clusters from projected polygon representative points before strict polygon merging.")]
    [SerializeField] private bool clusterByRepresentativePoints = true;
    [SerializeField] private string[] pointClusterLabels = { "building" };
    [SerializeField, Min(0f)] private float pointClusterDistanceMeters = 10f;
    [SerializeField, Min(2)] private int minPointsPerCluster = 2;
    [SerializeField, Min(0f)] private float maxPointClusterDiameterMeters = 25f;
    [SerializeField] private bool buildMergedPolygonFromPointClusters = true;
    [SerializeField, Min(0f)] private float maxVertexDistanceFromPointClusterCenterMeters = 20f;
    [SerializeField] private bool exportUnclusteredRawPolygons = false;
    [SerializeField] private bool exportPointClusterRepresentativePoints = true;
    [SerializeField] private bool exportDebugPointToClusterLines = false;
    [SerializeField] private bool buildPolygonFromPointClusterFallback = true;
    [SerializeField] private bool preferPointClusterHullOverRawPolygonHull = true;
    [SerializeField, Min(0f)] private float pointClusterPolygonBufferMeters = 6f;
    [SerializeField, Min(0f)] private float maxPointClusterPolygonDiameterMeters = 40f;
    [SerializeField, Min(3)] private int pointClusterHullCircleSegments = 8;
    [SerializeField] private bool includeRawPolygonVerticesInPointClusterHull = false;
    [SerializeField, Min(0f)] private float maxRawVertexDistanceFromPointClusterCenterMeters = 20f;

    [Header("Building Aggregation")]
    [Tooltip("Second-pass merge that combines nearby representative-point clusters into one final building-level polygon.")]
    [SerializeField] private bool mergePointClustersIntoBuildings = true;
    [SerializeField, Min(0f)] private float pointClusterMergeDistanceMeters = 12f;
    [SerializeField, Min(0f)] private float maxBuildingAggregateDiameterMeters = 50f;
    [SerializeField, Min(0f)] private float buildingAggregatePolygonBufferMeters = 7f;
    [SerializeField] private bool exportIntermediatePointClusterPolygons = false;
    [SerializeField] private bool exportBuildingAggregatePolygons = true;

    [Header("Clean Output")]
    [Tooltip("In clean/demo exports, hide representative debug points that did not become part of a final building aggregate.")]
    [SerializeField] private bool exportOnlyAggregatedRepresentativePoints = true;

    private int malformedDetectionCount;
    private int duplicateDetectionCount;
    private int candidatePoolDropCount;
    private int eligibleDetectionCounter;
    private int representativePointSkipCount;

    [ContextMenu("Save Current Inspector As GeoJSON Export Configuration")]
    private void SaveCurrentInspectorAsGeoJsonExportConfiguration()
    {
        if (initialConfiguration == null)
        {
            Debug.LogWarning("[DroneDetectionGeoJsonExporter] Assign a GeoJSON Export Configuration asset before saving.", this);
            return;
        }

#if UNITY_EDITOR
        if (Application.isPlaying)
        {
            Debug.LogWarning("[DroneDetectionGeoJsonExporter] Save the GeoJSON export configuration in Edit Mode.", this);
            return;
        }

        if (Path.IsPathRooted(detectionJsonPath) || Path.IsPathRooted(geoJsonOutputPath))
        {
            Debug.LogWarning("[DroneDetectionGeoJsonExporter] Configuration was not saved because paths must be relative and portable.", this);
            return;
        }

        UnityEditor.Undo.RecordObject(initialConfiguration, "Save GeoJSON Export Configuration");
        CopyInspectorToConfiguration(initialConfiguration);
        UnityEditor.EditorUtility.SetDirty(initialConfiguration);
        UnityEditor.AssetDatabase.SaveAssets();
        Debug.Log($"[DroneDetectionGeoJsonExporter] Saved exporter settings to '{initialConfiguration.name}'.", initialConfiguration);
#else
        Debug.LogWarning("[DroneDetectionGeoJsonExporter] Configuration assets can only be saved in the Unity Editor.", this);
#endif
    }

    [ContextMenu("Apply GeoJSON Export Configuration")]
    private void ApplyGeoJsonExportConfiguration()
    {
        if (initialConfiguration == null)
        {
            Debug.LogWarning("[DroneDetectionGeoJsonExporter] Assign a GeoJSON Export Configuration asset before applying.", this);
            return;
        }

#if UNITY_EDITOR
        if (Application.isPlaying)
        {
            Debug.LogWarning("[DroneDetectionGeoJsonExporter] Apply the GeoJSON export configuration in Edit Mode.", this);
            return;
        }

        if (Path.IsPathRooted(initialConfiguration.detectionJsonPath) ||
            Path.IsPathRooted(initialConfiguration.geoJsonOutputPath))
        {
            Debug.LogWarning("[DroneDetectionGeoJsonExporter] Configuration was not applied because it contains an absolute path.", initialConfiguration);
            return;
        }

        UnityEditor.Undo.RecordObject(this, "Apply GeoJSON Export Configuration");
        CopyConfigurationToInspector(initialConfiguration);
        UnityEditor.EditorUtility.SetDirty(this);
        if (gameObject.scene.IsValid())
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        Debug.Log($"[DroneDetectionGeoJsonExporter] Applied exporter settings from '{initialConfiguration.name}'.", this);
#else
        Debug.LogWarning("[DroneDetectionGeoJsonExporter] Configuration assets can only be applied in the Unity Editor.", this);
#endif
    }

    private void CopyInspectorToConfiguration(DroneGeoJsonExportConfiguration configuration)
    {
        configuration.detectionJsonPath = detectionJsonPath;
        configuration.inputPathRoot = inputPathRoot;
        configuration.outputPathRoot = outputPathRoot;
        configuration.geoJsonOutputPath = geoJsonOutputPath;
        configuration.exportPoints = exportPoints;
        configuration.exportPolygons = exportPolygons;
        configuration.oneFeaturePerTrackId = oneFeaturePerTrackId;
        configuration.preferHighestConfidencePerTrack = preferHighestConfidencePerTrack;
        configuration.confidenceThreshold = confidenceThreshold;
        configuration.maxFeatures = maxFeatures;
        configuration.sampleEveryNFrames = sampleEveryNFrames;
        configuration.sampleEveryNDetections = sampleEveryNDetections;
        configuration.areaLabels = CloneLabels(areaLabels);
        configuration.pointLabels = CloneLabels(pointLabels);
        configuration.maxPolygonVertices = maxPolygonVertices;
        configuration.simplificationEpsilonPixels = simplificationEpsilonPixels;
        configuration.minValidPolygonVertices = minValidPolygonVertices;
        configuration.minPolygonHitRatio = minPolygonHitRatio;
        configuration.includeAltitude = includeAltitude;
        configuration.fallbackToPointWhenPolygonFails = fallbackToPointWhenPolygonFails;
        configuration.exportRepresentativePointForPolygons = exportRepresentativePointForPolygons;
        configuration.representativePointsCountTowardMaxFeatures = representativePointsCountTowardMaxFeatures;
        configuration.useConvexHullForPolygons = useConvexHullForPolygons;
        configuration.rejectBorderClippedMasks = rejectBorderClippedMasks;
        configuration.borderMarginPixels = borderMarginPixels;
        configuration.minMaskAreaPixels = minMaskAreaPixels;
        configuration.mergeNearbyPolygons = mergeNearbyPolygons;
        configuration.mergePolygonLabels = CloneLabels(mergePolygonLabels);
        configuration.mergeDistanceMeters = mergeDistanceMeters;
        configuration.requireBoundaryDistanceForMerge = requireBoundaryDistanceForMerge;
        configuration.mergeBoundaryDistanceMeters = mergeBoundaryDistanceMeters;
        configuration.maxMergedHullAreaGrowthRatio = maxMergedHullAreaGrowthRatio;
        configuration.maxMergedClusterDiameterMeters = maxMergedClusterDiameterMeters;
        configuration.minPolygonsPerMergeCluster = minPolygonsPerMergeCluster;
        configuration.keepOriginalPolygonsWhenMerged = keepOriginalPolygonsWhenMerged;
        configuration.useConvexHullForMergedPolygons = useConvexHullForMergedPolygons;
        configuration.clusterByRepresentativePoints = clusterByRepresentativePoints;
        configuration.pointClusterLabels = CloneLabels(pointClusterLabels);
        configuration.pointClusterDistanceMeters = pointClusterDistanceMeters;
        configuration.minPointsPerCluster = minPointsPerCluster;
        configuration.maxPointClusterDiameterMeters = maxPointClusterDiameterMeters;
        configuration.buildMergedPolygonFromPointClusters = buildMergedPolygonFromPointClusters;
        configuration.maxVertexDistanceFromPointClusterCenterMeters = maxVertexDistanceFromPointClusterCenterMeters;
        configuration.exportUnclusteredRawPolygons = exportUnclusteredRawPolygons;
        configuration.exportPointClusterRepresentativePoints = exportPointClusterRepresentativePoints;
        configuration.exportDebugPointToClusterLines = exportDebugPointToClusterLines;
        configuration.buildPolygonFromPointClusterFallback = buildPolygonFromPointClusterFallback;
        configuration.preferPointClusterHullOverRawPolygonHull = preferPointClusterHullOverRawPolygonHull;
        configuration.pointClusterPolygonBufferMeters = pointClusterPolygonBufferMeters;
        configuration.maxPointClusterPolygonDiameterMeters = maxPointClusterPolygonDiameterMeters;
        configuration.pointClusterHullCircleSegments = pointClusterHullCircleSegments;
        configuration.includeRawPolygonVerticesInPointClusterHull = includeRawPolygonVerticesInPointClusterHull;
        configuration.maxRawVertexDistanceFromPointClusterCenterMeters = maxRawVertexDistanceFromPointClusterCenterMeters;
        TrySetOptionalConfigurationField(configuration, "mergePointClustersIntoBuildings", mergePointClustersIntoBuildings);
        TrySetOptionalConfigurationField(configuration, "pointClusterMergeDistanceMeters", pointClusterMergeDistanceMeters);
        TrySetOptionalConfigurationField(configuration, "maxBuildingAggregateDiameterMeters", maxBuildingAggregateDiameterMeters);
        TrySetOptionalConfigurationField(configuration, "buildingAggregatePolygonBufferMeters", buildingAggregatePolygonBufferMeters);
        TrySetOptionalConfigurationField(configuration, "exportIntermediatePointClusterPolygons", exportIntermediatePointClusterPolygons);
        TrySetOptionalConfigurationField(configuration, "exportBuildingAggregatePolygons", exportBuildingAggregatePolygons);
        TrySetOptionalConfigurationField(configuration, "exportOnlyAggregatedRepresentativePoints", exportOnlyAggregatedRepresentativePoints);
    }

    private void CopyConfigurationToInspector(DroneGeoJsonExportConfiguration configuration)
    {
        detectionJsonPath = configuration.detectionJsonPath;
        inputPathRoot = configuration.inputPathRoot;
        outputPathRoot = configuration.outputPathRoot;
        geoJsonOutputPath = configuration.geoJsonOutputPath;
        exportPoints = configuration.exportPoints;
        exportPolygons = configuration.exportPolygons;
        oneFeaturePerTrackId = configuration.oneFeaturePerTrackId;
        preferHighestConfidencePerTrack = configuration.preferHighestConfidencePerTrack;
        confidenceThreshold = configuration.confidenceThreshold;
        maxFeatures = configuration.maxFeatures;
        sampleEveryNFrames = configuration.sampleEveryNFrames;
        sampleEveryNDetections = configuration.sampleEveryNDetections;
        areaLabels = CloneLabels(configuration.areaLabels);
        pointLabels = CloneLabels(configuration.pointLabels);
        maxPolygonVertices = configuration.maxPolygonVertices;
        simplificationEpsilonPixels = configuration.simplificationEpsilonPixels;
        minValidPolygonVertices = configuration.minValidPolygonVertices;
        minPolygonHitRatio = configuration.minPolygonHitRatio;
        includeAltitude = configuration.includeAltitude;
        fallbackToPointWhenPolygonFails = configuration.fallbackToPointWhenPolygonFails;
        exportRepresentativePointForPolygons = configuration.exportRepresentativePointForPolygons;
        representativePointsCountTowardMaxFeatures = configuration.representativePointsCountTowardMaxFeatures;
        useConvexHullForPolygons = configuration.useConvexHullForPolygons;
        rejectBorderClippedMasks = configuration.rejectBorderClippedMasks;
        borderMarginPixels = configuration.borderMarginPixels;
        minMaskAreaPixels = configuration.minMaskAreaPixels;
        mergeNearbyPolygons = configuration.mergeNearbyPolygons;
        mergePolygonLabels = CloneLabels(configuration.mergePolygonLabels);
        mergeDistanceMeters = configuration.mergeDistanceMeters;
        requireBoundaryDistanceForMerge = configuration.requireBoundaryDistanceForMerge;
        mergeBoundaryDistanceMeters = configuration.mergeBoundaryDistanceMeters;
        maxMergedHullAreaGrowthRatio = configuration.maxMergedHullAreaGrowthRatio;
        maxMergedClusterDiameterMeters = configuration.maxMergedClusterDiameterMeters;
        minPolygonsPerMergeCluster = configuration.minPolygonsPerMergeCluster;
        keepOriginalPolygonsWhenMerged = configuration.keepOriginalPolygonsWhenMerged;
        useConvexHullForMergedPolygons = configuration.useConvexHullForMergedPolygons;
        clusterByRepresentativePoints = configuration.clusterByRepresentativePoints;
        pointClusterLabels = CloneLabels(configuration.pointClusterLabels);
        pointClusterDistanceMeters = configuration.pointClusterDistanceMeters;
        minPointsPerCluster = configuration.minPointsPerCluster;
        maxPointClusterDiameterMeters = configuration.maxPointClusterDiameterMeters;
        buildMergedPolygonFromPointClusters = configuration.buildMergedPolygonFromPointClusters;
        maxVertexDistanceFromPointClusterCenterMeters = configuration.maxVertexDistanceFromPointClusterCenterMeters;
        exportUnclusteredRawPolygons = configuration.exportUnclusteredRawPolygons;
        exportPointClusterRepresentativePoints = configuration.exportPointClusterRepresentativePoints;
        exportDebugPointToClusterLines = configuration.exportDebugPointToClusterLines;
        buildPolygonFromPointClusterFallback = configuration.buildPolygonFromPointClusterFallback;
        preferPointClusterHullOverRawPolygonHull = configuration.preferPointClusterHullOverRawPolygonHull;
        pointClusterPolygonBufferMeters = configuration.pointClusterPolygonBufferMeters;
        maxPointClusterPolygonDiameterMeters = configuration.maxPointClusterPolygonDiameterMeters;
        pointClusterHullCircleSegments = configuration.pointClusterHullCircleSegments;
        includeRawPolygonVerticesInPointClusterHull = configuration.includeRawPolygonVerticesInPointClusterHull;
        maxRawVertexDistanceFromPointClusterCenterMeters = configuration.maxRawVertexDistanceFromPointClusterCenterMeters;
        mergePointClustersIntoBuildings = TryGetOptionalConfigurationField(configuration, "mergePointClustersIntoBuildings", mergePointClustersIntoBuildings);
        pointClusterMergeDistanceMeters = TryGetOptionalConfigurationField(configuration, "pointClusterMergeDistanceMeters", pointClusterMergeDistanceMeters);
        maxBuildingAggregateDiameterMeters = TryGetOptionalConfigurationField(configuration, "maxBuildingAggregateDiameterMeters", maxBuildingAggregateDiameterMeters);
        buildingAggregatePolygonBufferMeters = TryGetOptionalConfigurationField(configuration, "buildingAggregatePolygonBufferMeters", buildingAggregatePolygonBufferMeters);
        exportIntermediatePointClusterPolygons = TryGetOptionalConfigurationField(configuration, "exportIntermediatePointClusterPolygons", exportIntermediatePointClusterPolygons);
        exportBuildingAggregatePolygons = TryGetOptionalConfigurationField(configuration, "exportBuildingAggregatePolygons", exportBuildingAggregatePolygons);
        exportOnlyAggregatedRepresentativePoints = TryGetOptionalConfigurationField(configuration, "exportOnlyAggregatedRepresentativePoints", exportOnlyAggregatedRepresentativePoints);
    }

    private static void TrySetOptionalConfigurationField(DroneGeoJsonExportConfiguration configuration, string fieldName, object value)
    {
        if (configuration == null || string.IsNullOrWhiteSpace(fieldName))
            return;

        var field = configuration.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        if (field == null)
            return;

        try
        {
            field.SetValue(configuration, value);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[DroneDetectionGeoJsonExporter] Could not save optional configuration field '{fieldName}': {ex.Message}");
        }
    }

    private static T TryGetOptionalConfigurationField<T>(DroneGeoJsonExportConfiguration configuration, string fieldName, T fallback)
    {
        if (configuration == null || string.IsNullOrWhiteSpace(fieldName))
            return fallback;

        var field = configuration.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        if (field == null)
            return fallback;

        try
        {
            object value = field.GetValue(configuration);
            if (value is T typed)
                return typed;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[DroneDetectionGeoJsonExporter] Could not read optional configuration field '{fieldName}': {ex.Message}");
        }

        return fallback;
    }

    private static string[] CloneLabels(string[] labels)
    {
        return labels == null ? Array.Empty<string>() : (string[])labels.Clone();
    }

    [ContextMenu("Validate Detection GeoJSON Export Setup")]
    private void ValidateDetectionGeoJsonExportSetup()
    {
        if (TryValidateSetup(out string message))
            Debug.Log($"[DroneDetectionGeoJsonExporter] Setup is valid. {message}", this);
        else
            Debug.LogWarning($"[DroneDetectionGeoJsonExporter] {message}", this);
    }

    [ContextMenu("Export Detection GeoJSON")]
    private void ExportDetectionGeoJson()
    {
        if (!TryValidateSetup(out string validationMessage))
        {
            Debug.LogWarning($"[DroneDetectionGeoJsonExporter] Export cancelled: {validationMessage}", this);
            return;
        }

        malformedDetectionCount = 0;
        duplicateDetectionCount = 0;
        candidatePoolDropCount = 0;
        eligibleDetectionCounter = 0;
        representativePointSkipCount = 0;

        string inputPath = ResolvePath(detectionJsonPath, inputPathRoot);
        if (!TryLoadCandidates(inputPath, out DetectionMetadata metadata, out List<DetectionCandidate> candidates, out string parseError))
        {
            Debug.LogWarning($"[DroneDetectionGeoJsonExporter] Export cancelled: {parseError}", this);
            return;
        }

        string outputPath = ResolvePath(geoJsonOutputPath, outputPathRoot);
        if (!TryWriteGeoJson(outputPath, metadata, candidates, out int featureCount, out int projectionFailureCount, out string writeError))
        {
            Debug.LogWarning($"[DroneDetectionGeoJsonExporter] Failed to export GeoJSON: {writeError}", this);
            return;
        }

        if (malformedDetectionCount > 0 || duplicateDetectionCount > 0 || candidatePoolDropCount > 0 ||
            projectionFailureCount > 0 || representativePointSkipCount > 0)
        {
            Debug.LogWarning(
                $"[DroneDetectionGeoJsonExporter] Export completed with skipped data: malformed={malformedDetectionCount}, " +
                $"same-frame duplicates={duplicateDetectionCount}, candidate-pool drops={candidatePoolDropCount}, " +
                $"projection failures={projectionFailureCount}, representative point skips={representativePointSkipCount}.", this);
        }

        Debug.Log($"[DroneDetectionGeoJsonExporter] Exported {featureCount} compact GeoJSON features to: {outputPath}", this);
    }

    private bool TryValidateSetup(out string message)
    {
        if (player == null)
        {
            message = "Player reference is missing.";
            return false;
        }

        if (!player.TryPrepareForPixelProjection(out string projectionReason))
        {
            message = $"Player cannot project pixels: {projectionReason}";
            return false;
        }

        if (maxFeatures <= 0 || sampleEveryNFrames <= 0 || sampleEveryNDetections <= 0)
        {
            message = "Max Features and sampling intervals must be greater than zero.";
            return false;
        }

        if (maxPolygonVertices < 3 || minValidPolygonVertices < 3 || minValidPolygonVertices > maxPolygonVertices)
        {
            message = "Polygon vertex limits are invalid; require 3 <= Min Valid Polygon Vertices <= Max Polygon Vertices.";
            return false;
        }

        if (!exportPoints && !exportPolygons)
        {
            message = "Enable Point or Polygon export.";
            return false;
        }

        if (!HasAnyConfiguredLabel())
        {
            message = "Configure at least one Area Label or Point Label.";
            return false;
        }

        if (!IsFinite(confidenceThreshold) || confidenceThreshold < 0f || confidenceThreshold > 1f ||
            !IsFinite(minPolygonHitRatio) || minPolygonHitRatio < 0f || minPolygonHitRatio > 1f ||
            !IsFinite(simplificationEpsilonPixels) || simplificationEpsilonPixels < 0f ||
            !IsFinite(borderMarginPixels) || borderMarginPixels < 0f ||
            !IsFinite(minMaskAreaPixels) || minMaskAreaPixels < 0f)
        {
            message = "Confidence, hit-ratio, simplification, border-margin, or mask-area settings are invalid.";
            return false;
        }

        if (mergeNearbyPolygons)
        {
            if (!HasAnyNonEmpty(mergePolygonLabels))
            {
                message = "Polygon merging is enabled, but no Merge Polygon Labels are configured.";
                return false;
            }

            if (!IsFinite(mergeDistanceMeters) || mergeDistanceMeters <= 0f ||
                !IsFinite(mergeBoundaryDistanceMeters) || mergeBoundaryDistanceMeters < 0f ||
                !IsFinite(maxMergedHullAreaGrowthRatio) || maxMergedHullAreaGrowthRatio < 1f ||
                !IsFinite(maxMergedClusterDiameterMeters) || maxMergedClusterDiameterMeters < 0f ||
                minPolygonsPerMergeCluster < 2)
            {
                message = "Polygon merge distances, hull-growth ratio, cluster diameter, or minimum cluster size are invalid.";
                return false;
            }

            if (!useConvexHullForMergedPolygons)
            {
                message = "Use Convex Hull For Merged Polygons must be enabled so merged output is a valid ordered GeoJSON ring.";
                return false;
            }
        }

        if (clusterByRepresentativePoints)
        {
            if (!exportPolygons || !exportRepresentativePointForPolygons)
            {
                message = "Representative-point clustering requires Polygon export and Polygon Debug Points.";
                return false;
            }

            if (!HasAnyNonEmpty(pointClusterLabels))
            {
                message = "Representative-point clustering is enabled, but no Point Cluster Labels are configured.";
                return false;
            }

            if (!IsFinite(pointClusterDistanceMeters) || pointClusterDistanceMeters <= 0f ||
                minPointsPerCluster < 2 ||
                !IsFinite(maxPointClusterDiameterMeters) || maxPointClusterDiameterMeters <= 0f ||
                !IsFinite(maxVertexDistanceFromPointClusterCenterMeters) ||
                maxVertexDistanceFromPointClusterCenterMeters < 0f ||
                (buildMergedPolygonFromPointClusters &&
                 (!IsFinite(maxMergedHullAreaGrowthRatio) || maxMergedHullAreaGrowthRatio < 1f)))
            {
                message = "Point-cluster distance, minimum point count, diameter, or vertex-distance settings are invalid.";
                return false;
            }

            bool bufferedHullEnabled = buildMergedPolygonFromPointClusters &&
                                       (preferPointClusterHullOverRawPolygonHull ||
                                        buildPolygonFromPointClusterFallback);
            if (bufferedHullEnabled &&
                (!IsFinite(pointClusterPolygonBufferMeters) || pointClusterPolygonBufferMeters <= 0f ||
                 !IsFinite(maxPointClusterPolygonDiameterMeters) || maxPointClusterPolygonDiameterMeters <= 0f ||
                 pointClusterHullCircleSegments < 3 || pointClusterHullCircleSegments > 128 ||
                 !IsFinite(maxRawVertexDistanceFromPointClusterCenterMeters) ||
                 maxRawVertexDistanceFromPointClusterCenterMeters < 0f ||
                 pointClusterPolygonBufferMeters * 2f > maxPointClusterPolygonDiameterMeters ||
                 (includeRawPolygonVerticesInPointClusterHull &&
                  maxRawVertexDistanceFromPointClusterCenterMeters <= 0f)))
            {
                message = "Buffered point-cluster hull settings are invalid; require a positive buffer, sufficient polygon diameter, 3-128 circle segments, and a valid raw-vertex distance.";
                return false;
            }
        }

        if (clusterByRepresentativePoints && mergePointClustersIntoBuildings)
        {
            if (!IsFinite(pointClusterMergeDistanceMeters) || pointClusterMergeDistanceMeters <= 0f ||
                !IsFinite(maxBuildingAggregateDiameterMeters) || maxBuildingAggregateDiameterMeters <= 0f ||
                !IsFinite(buildingAggregatePolygonBufferMeters) || buildingAggregatePolygonBufferMeters <= 0f ||
                buildingAggregatePolygonBufferMeters * 2f > maxBuildingAggregateDiameterMeters)
            {
                message = "Building aggregate settings are invalid; require positive merge distance, positive aggregate diameter, and a positive buffer smaller than the aggregate diameter.";
                return false;
            }
        }

        string inputPath;
        string outputPath;
        try
        {
            inputPath = ResolvePath(detectionJsonPath, inputPathRoot);
            outputPath = ResolvePath(geoJsonOutputPath, outputPathRoot);
        }
        catch (Exception ex)
        {
            message = $"A configured path is invalid: {ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            message = $"Detection JSON was not found: {inputPath}";
            return false;
        }

        if (!TryReadHeader(inputPath, out DetectionMetadata metadata, out string headerError))
        {
            message = $"Detection JSON header is invalid: {headerError}";
            return false;
        }

        if (!TryCheckOutputWritable(outputPath, out string outputError))
        {
            message = $"Output path is not writable: {outputError}";
            return false;
        }

        message = $"Video='{metadata.video}', dimensions={metadata.frameWidth}x{metadata.frameHeight}, output='{outputPath}'.";
        return true;
    }

    private bool TryReadHeader(string path, out DetectionMetadata metadata, out string error)
    {
        metadata = default;
        error = string.Empty;

        try
        {
            using (var stream = new StreamReader(path))
            using (var reader = CreateJsonReader(stream))
            {
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                {
                    error = "Expected a top-level JSON object. The file may be truncated or use an unsupported schema.";
                    return false;
                }

                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndObject)
                        break;
                    if (reader.TokenType != JsonToken.PropertyName)
                        continue;

                    string propertyName = Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
                    if (!reader.Read())
                        break;

                    if (propertyName == "video")
                        metadata.video = Convert.ToString(reader.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    else if (propertyName == "frame_width")
                        metadata.frameWidth = ReadIntValue(reader.Value);
                    else if (propertyName == "frame_height")
                        metadata.frameHeight = ReadIntValue(reader.Value);
                    else if (propertyName == "frames")
                    {
                        if (reader.TokenType != JsonToken.StartArray)
                        {
                            error = "The 'frames' property is not an array.";
                            return false;
                        }

                        if (metadata.frameWidth <= 1 || metadata.frameHeight <= 1)
                        {
                            error = "frame_width and frame_height must appear before frames and be greater than one.";
                            return false;
                        }

                        return true;
                    }
                    else
                    {
                        SkipContainerValue(reader);
                    }
                }
            }

            error = "Required top-level 'frames' array was not found.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryLoadCandidates(
        string path,
        out DetectionMetadata metadata,
        out List<DetectionCandidate> candidates,
        out string error)
    {
        metadata = default;
        candidates = new List<DetectionCandidate>();
        error = string.Empty;
        var tracks = new Dictionary<string, DetectionCandidate>(StringComparer.Ordinal);
        int frameOrdinal = 0;
        int trackPoolLimit = Mathf.Max(1, maxFeatures) * 4;
        bool foundFrames = false;
        bool stopReading = false;

        try
        {
            using (var stream = new StreamReader(path))
            using (var reader = CreateJsonReader(stream))
            {
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                {
                    error = "Expected a top-level JSON object; the file appears truncated or malformed.";
                    return false;
                }

                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndObject)
                        break;
                    if (reader.TokenType != JsonToken.PropertyName)
                        continue;

                    string propertyName = Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
                    if (!reader.Read())
                        break;

                    if (propertyName == "video")
                        metadata.video = Convert.ToString(reader.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    else if (propertyName == "frame_width")
                        metadata.frameWidth = ReadIntValue(reader.Value);
                    else if (propertyName == "frame_height")
                        metadata.frameHeight = ReadIntValue(reader.Value);
                    else if (propertyName == "frames")
                    {
                        foundFrames = true;
                        if (reader.TokenType != JsonToken.StartArray)
                            throw new JsonReaderException("The 'frames' property is not an array.");
                        if (metadata.frameWidth <= 1 || metadata.frameHeight <= 1)
                            throw new JsonReaderException("Valid frame_width and frame_height are required before frames.");

                        while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                        {
                            if (reader.TokenType != JsonToken.StartObject)
                            {
                                SkipContainerValue(reader);
                                continue;
                            }

                            bool sampleFrame = (frameOrdinal % Mathf.Max(1, sampleEveryNFrames)) == 0;
                            frameOrdinal++;
                            if (!sampleFrame)
                            {
                                reader.Skip();
                                continue;
                            }

                            JObject frameObject = JObject.Load(reader);
                            ProcessFrameObject(frameObject, metadata, tracks, candidates, trackPoolLimit);

                            if (!oneFeaturePerTrackId && candidates.Count >= maxFeatures)
                            {
                                stopReading = true;
                                break;
                            }
                        }

                        if (stopReading)
                            break;
                    }
                    else
                    {
                        SkipContainerValue(reader);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Failed while streaming detection JSON: {ex.Message}";
            return false;
        }

        if (!foundFrames)
        {
            error = "Required top-level 'frames' array was not found.";
            return false;
        }

        if (oneFeaturePerTrackId)
        {
            candidates.AddRange(tracks.Values);
            candidates.Sort(CompareCandidatesBestFirst);
            if (candidates.Count > maxFeatures)
                candidates.RemoveRange(maxFeatures, candidates.Count - maxFeatures);
        }

        return true;
    }

    private void ProcessFrameObject(
        JObject frameObject,
        DetectionMetadata metadata,
        Dictionary<string, DetectionCandidate> tracks,
        List<DetectionCandidate> candidates,
        int trackPoolLimit)
    {
        int frameIndex = ReadIntToken(frameObject["frame_index"], -1);
        float timeSeconds = ReadFloatToken(frameObject["timestamp_s"], float.NaN);
        JArray detections = frameObject["detections"] as JArray;
        if (frameIndex < 0 || !IsFinite(timeSeconds) || detections == null)
        {
            malformedDetectionCount++;
            return;
        }

        var frameBest = new Dictionary<string, DetectionCandidate>(StringComparer.Ordinal);
        for (int i = 0; i < detections.Count; i++)
        {
            if (!(detections[i] is JObject detectionObject))
            {
                malformedDetectionCount++;
                continue;
            }

            if (!TryParseCandidate(
                    detectionObject,
                    frameIndex,
                    timeSeconds,
                    metadata,
                    out DetectionCandidate candidate,
                    out bool filteredOut))
            {
                if (!filteredOut)
                    malformedDetectionCount++;
                continue;
            }

            string key = MakeTrackKey(candidate.label, candidate.detectionId);
            if (frameBest.TryGetValue(key, out DetectionCandidate existing))
            {
                duplicateDetectionCount++;
                if (IsBetterCandidate(candidate, existing))
                    frameBest[key] = candidate;
            }
            else
            {
                frameBest.Add(key, candidate);
            }
        }

        foreach (DetectionCandidate candidate in frameBest.Values)
        {
            int sampleIndex = eligibleDetectionCounter++;
            if ((sampleIndex % Mathf.Max(1, sampleEveryNDetections)) != 0)
                continue;

            if (!oneFeaturePerTrackId)
            {
                if (candidates.Count < maxFeatures)
                    candidates.Add(candidate);
                continue;
            }

            string key = MakeTrackKey(candidate.label, candidate.detectionId);
            if (tracks.TryGetValue(key, out DetectionCandidate existing))
            {
                if (preferHighestConfidencePerTrack && IsBetterCandidate(candidate, existing))
                    tracks[key] = candidate;
            }
            else if (tracks.Count < trackPoolLimit)
            {
                tracks.Add(key, candidate);
            }
            else
            {
                candidatePoolDropCount++;
            }
        }
    }

    private bool TryParseCandidate(
        JObject detectionObject,
        int frameIndex,
        float timeSeconds,
        DetectionMetadata metadata,
        out DetectionCandidate candidate,
        out bool filteredOut)
    {
        candidate = null;
        filteredOut = false;
        string detectionId = ReadStringToken(detectionObject["id"]);
        string label = ReadStringToken(detectionObject["label"]);
        int classId = ReadIntToken(detectionObject["class_id"], -1);
        float confidence = ReadFloatToken(detectionObject["confidence"], float.NaN);

        bool isAreaLabel = ContainsLabel(areaLabels, label);
        bool isPointLabel = ContainsLabel(pointLabels, label);
        if (string.IsNullOrWhiteSpace(detectionId) || string.IsNullOrWhiteSpace(label) || classId < 0 ||
            !IsFinite(confidence))
            return false;

        if (confidence < confidenceThreshold || (!isAreaLabel && !isPointLabel))
        {
            filteredOut = true;
            return false;
        }

        string inputGeometryType = ReadStringToken(detectionObject["geometry_type"]).Trim();
        float[] bbox = ReadNumberArray(detectionObject["bbox"], 4);
        List<Vector2> geometryPoints = ReadGeometryPoints(detectionObject["points"]);
        JToken maskPolygonToken = detectionObject["mask_polygon"];
        bool hasMaskPolygonProperty = maskPolygonToken != null && maskPolygonToken.Type != JTokenType.Null;
        List<Vector2> originalPolygon;
        string inputGeometrySource = string.Empty;

        // Legacy geometry remains authoritative; new-schema points are the polygon fallback.
        if (hasMaskPolygonProperty)
        {
            originalPolygon = ReadPolygon(maskPolygonToken);
            if (originalPolygon.Count > 0)
                inputGeometrySource = "mask_polygon";
        }
        else if (string.Equals(inputGeometryType, "Polygon", StringComparison.OrdinalIgnoreCase))
        {
            originalPolygon = new List<Vector2>(geometryPoints);
            if (originalPolygon.Count > 0)
                inputGeometrySource = "points";
        }
        else
        {
            originalPolygon = new List<Vector2>();
        }

        float[] centroidValues = ReadNumberArray(detectionObject["centroid"], 2);
        bool hasCentroid = centroidValues != null;
        Vector2 centroid = hasCentroid ? new Vector2(centroidValues[0], centroidValues[1]) : Vector2.zero;
        // Representative-point priority: explicit centroid, bbox center, then geometry points.
        if (hasCentroid && string.IsNullOrWhiteSpace(inputGeometrySource))
        {
            inputGeometrySource = "centroid";
        }
        else if (!hasCentroid && CalculateBBoxCenter(bbox, out centroid))
        {
            hasCentroid = true;
            if (string.IsNullOrWhiteSpace(inputGeometrySource))
                inputGeometrySource = "bbox_centroid";
        }
        else if (!hasCentroid &&
                 string.Equals(inputGeometryType, "Point", StringComparison.OrdinalIgnoreCase) &&
                 ReadPointFromGeometryPoints(geometryPoints, out centroid))
        {
            hasCentroid = true;
            inputGeometrySource = "points";
        }
        else if (!hasCentroid && CalculatePointAverage(geometryPoints, out centroid))
        {
            hasCentroid = true;
            if (string.IsNullOrWhiteSpace(inputGeometrySource))
                inputGeometrySource = "points";
        }
        else if (!hasCentroid && CalculatePointAverage(originalPolygon, out centroid))
        {
            hasCentroid = true;
            if (string.IsNullOrWhiteSpace(inputGeometrySource))
                inputGeometrySource = "mask_polygon";
        }

        if (string.IsNullOrWhiteSpace(inputGeometryType))
        {
            if (originalPolygon.Count >= 3)
                inputGeometryType = "Polygon";
            else if (hasCentroid)
                inputGeometryType = "Point";
        }

        int originalVertexCount = originalPolygon.Count;
        double polygonArea = CalculatePolygonArea(originalPolygon);
        bool borderClipped = IsBorderClipped(bbox, originalPolygon, metadata, Mathf.Max(0f, borderMarginPixels));

        if (isAreaLabel)
        {
            bool hasReliablePolygon = originalPolygon.Count >= 3;
            if (hasReliablePolygon && minMaskAreaPixels > 0f && polygonArea < minMaskAreaPixels)
            {
                filteredOut = true;
                return false;
            }

            if (rejectBorderClippedMasks && borderClipped)
            {
                filteredOut = true;
                return false;
            }
        }

        List<Vector2> simplifiedPolygon = SimplifyAndLimitPolygon(originalPolygon);

        if (!hasCentroid && bbox == null && simplifiedPolygon.Count == 0)
            return false;

        double candidateScore = CalculateCandidateScore(confidence, polygonArea, originalVertexCount, borderClipped);

        candidate = new DetectionCandidate
        {
            frameIndex = frameIndex,
            timeSeconds = timeSeconds,
            detectionId = detectionId,
            label = label,
            classId = classId,
            confidence = confidence,
            bbox = bbox,
            centroid = centroid,
            hasCentroid = hasCentroid,
            polygon = simplifiedPolygon,
            originalMaskVertexCount = originalVertexCount,
            polygonArea = polygonArea,
            borderClipped = borderClipped,
            candidateScore = candidateScore,
            isAreaLabel = isAreaLabel,
            inputGeometryType = inputGeometryType,
            inputGeometrySource = inputGeometrySource
        };
        return true;
    }

    private bool TryWriteGeoJson(
        string outputPath,
        DetectionMetadata metadata,
        List<DetectionCandidate> candidates,
        out int featureCount,
        out int projectionFailureCount,
        out string error)
    {
        featureCount = 0;
        projectionFailureCount = 0;
        error = string.Empty;
        string tempPath = outputPath + ".tmp";

        try
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            // Project raw geometry and representative points before any grouping or suppression.
            List<FeatureRecord> records = ProjectFeatureRecords(metadata, candidates, ref projectionFailureCount);
            // Build same-label graph components from representative points.
            if (clusterByRepresentativePoints)
                records = ClusterByRepresentativePoints(records);
            // Merge nearby point components into final building-level aggregates.
            if (clusterByRepresentativePoints && mergePointClustersIntoBuildings)
                records = MergePointClustersIntoBuildingAggregates(records);
            // Preserve the independent strict raw-polygon merge as a fallback path.
            if (mergeNearbyPolygons)
                records = MergeNearbyPolygonRecords(records);
            // Apply clean-demo visibility policy only after all aggregate candidates exist.
            if (clusterByRepresentativePoints)
                records = ApplyPointClusterOutputPolicy(records);

            using (var stream = new StreamWriter(tempPath, false))
            using (var writer = new JsonTextWriter(stream) { Formatting = Formatting.None })
            {
                writer.WriteStartObject();
                writer.WritePropertyName("type");
                writer.WriteValue("FeatureCollection");
                writer.WritePropertyName("features");
                writer.WriteStartArray();

                int countedFeatureCount = 0;
                for (int i = 0; i < records.Count; i++)
                {
                    FeatureRecord record = records[i];
                    bool countsTowardMax = !record.feature.isLineString &&
                                           (representativePointsCountTowardMaxFeatures ||
                                            !IsRepresentativePointRecord(record));
                    if (countsTowardMax && countedFeatureCount >= maxFeatures)
                        continue;

                    WriteFeature(writer, metadata.video, record.candidate, record.feature);
                    featureCount++;
                    if (countsTowardMax)
                        countedFeatureCount++;
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }

            if (File.Exists(outputPath))
                File.Delete(outputPath);
            File.Move(tempPath, outputPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Preserve the original export error.
            }
            return false;
        }
    }

    private List<FeatureRecord> ProjectFeatureRecords(
        DetectionMetadata metadata,
        List<DetectionCandidate> candidates,
        ref int projectionFailureCount)
    {
        var records = new List<FeatureRecord>();
        int countedFeatureCount = 0;
        for (int i = 0; i < candidates.Count && countedFeatureCount < maxFeatures; i++)
        {
            DetectionCandidate candidate = candidates[i];
            if (!TryProjectFeature(candidate, metadata, out ProjectedFeature feature))
            {
                projectionFailureCount++;
                if (clusterByRepresentativePoints && candidate.isAreaLabel &&
                    exportRepresentativePointForPolygons &&
                    TryProjectPoint(
                        candidate,
                        metadata,
                        "polygon_representative_point",
                        "raw_polygon_projection_failed",
                        out ProjectedFeature representativePoint))
                {
                    representativePoint.linkedDetectionId = candidate.detectionId;
                    records.Add(new FeatureRecord(candidate, representativePoint));
                    countedFeatureCount++;
                }
                else if (clusterByRepresentativePoints && candidate.isAreaLabel &&
                         exportRepresentativePointForPolygons)
                {
                    representativePointSkipCount++;
                }
                continue;
            }

            records.Add(new FeatureRecord(candidate, feature));
            if (feature.isPolygon && string.Equals(feature.source, "mask_polygon_projection", StringComparison.Ordinal))
                feature.rawPolygonId = MakeRawPolygonId(candidate);
            countedFeatureCount++;

            if (feature.isPolygon && exportRepresentativePointForPolygons)
            {
                bool canWriteRepresentativePoint = !representativePointsCountTowardMaxFeatures || countedFeatureCount < maxFeatures;
                if (canWriteRepresentativePoint &&
                    TryProjectPoint(candidate, metadata, "polygon_representative_point", string.Empty, out ProjectedFeature representativePoint))
                {
                    representativePoint.linkedPolygon = true;
                    representativePoint.linkedDetectionId = candidate.detectionId;
                    representativePoint.linkedRawPolygonId = feature.rawPolygonId;
                    records.Add(new FeatureRecord(candidate, representativePoint));
                    if (representativePointsCountTowardMaxFeatures)
                        countedFeatureCount++;
                }
                else
                {
                    representativePointSkipCount++;
                }
            }
        }

        return records;
    }

    private List<FeatureRecord> ClusterByRepresentativePoints(List<FeatureRecord> records)
    {
        if (records == null || records.Count == 0 || !HasAnyNonEmpty(pointClusterLabels))
            return records;

        // Previous versions used a greedy/complete-link clustering rule:
        // a point had to be close to every point already in the cluster.
        // That is safe, but it splits one real building into many small clusters when
        // detections form a chain across a long facade. For representative points we
        // want DBSCAN-like connected components instead:
        // if A is close to B and B is close to C, A/B/C describe one candidate building
        // as long as the final component stays under the aggregate diameter cap.
        var eligiblePointIndices = new List<int>();
        for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
        {
            FeatureRecord record = records[recordIndex];
            if (IsRepresentativePointRecord(record) &&
                ContainsLabel(pointClusterLabels, record.candidate.label))
                eligiblePointIndices.Add(recordIndex);
        }

        if (eligiblePointIndices.Count == 0)
            return records;

        int[] parent = new int[eligiblePointIndices.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        for (int i = 0; i < eligiblePointIndices.Count; i++)
        {
            FeatureRecord a = records[eligiblePointIndices[i]];
            GeoCoordinate pointA = a.feature.coordinates[0];

            for (int j = i + 1; j < eligiblePointIndices.Count; j++)
            {
                FeatureRecord b = records[eligiblePointIndices[j]];
                if (!string.Equals(a.candidate.label, b.candidate.label, StringComparison.OrdinalIgnoreCase))
                    continue;

                GeoCoordinate pointB = b.feature.coordinates[0];
                double distance = DistanceMeters(pointA, pointB);
                if (distance <= pointClusterDistanceMeters)
                    Union(parent, i, j);
            }
        }

        var componentMap = new Dictionary<int, List<int>>();
        for (int i = 0; i < eligiblePointIndices.Count; i++)
        {
            int root = Find(parent, i);
            if (!componentMap.TryGetValue(root, out List<int> component))
            {
                component = new List<int>();
                componentMap.Add(root, component);
            }

            component.Add(eligiblePointIndices[i]);
        }

        var suppressedRawPolygonIds = new HashSet<string>(StringComparer.Ordinal);
        var additions = new List<FeatureRecord>();
        int clusterCounter = 0;

        foreach (List<int> recordIndices in componentMap.Values)
        {
            if (recordIndices.Count < Mathf.Max(2, minPointsPerCluster))
                continue;

            double clusterDiameter = CalculatePointRecordDiameter(records, recordIndices);

            // When the second-pass building aggregate is enabled, allow a point cluster
            // to span a full building. maxPointClusterDiameterMeters remains useful for
            // intermediate cluster polygons, but it should not stop a valid long building
            // from getting a point_cluster_id and becoming one aggregate.
            double allowedClusterDiameter = maxPointClusterDiameterMeters;
            if (mergePointClustersIntoBuildings && maxBuildingAggregateDiameterMeters > allowedClusterDiameter)
                allowedClusterDiameter = maxBuildingAggregateDiameterMeters;

            if (allowedClusterDiameter > 0f && clusterDiameter > allowedClusterDiameter)
                continue;

            GeoCoordinate clusterCenter = CalculatePointClusterCenter(records, recordIndices);
            clusterCounter++;
            string label = records[recordIndices[0]].candidate.label;
            string clusterId = $"point_cluster_{MakeIdPart(label)}_{clusterCounter}";
            HashSet<string> memberRawPolygonIds = GetPointClusterRawPolygonIds(records, recordIndices);

            for (int i = 0; i < recordIndices.Count; i++)
            {
                FeatureRecord pointRecord = records[recordIndices[i]];
                pointRecord.feature.pointClusterId = clusterId;
                pointRecord.feature.clusterLinkType = "representative_point_graph_cluster";
                pointRecord.feature.nearestClusterDistanceMeters =
                    DistanceMeters(pointRecord.feature.coordinates[0], clusterCenter);

                if (exportDebugPointToClusterLines)
                    additions.Add(BuildPointClusterDebugLine(pointRecord, clusterCenter, clusterId));
            }

            for (int i = 0; i < records.Count; i++)
            {
                FeatureRecord polygonRecord = records[i];
                if (IsRawPolygonRecord(polygonRecord) &&
                    memberRawPolygonIds.Contains(polygonRecord.feature.rawPolygonId))
                    polygonRecord.feature.pointClusterId = clusterId;
            }

            if (!buildMergedPolygonFromPointClusters)
                continue;

            var cluster = new PointCluster(label);
            cluster.recordIndices.AddRange(recordIndices);

            FeatureRecord merged = BuildPointClusterMergedRecord(
                records,
                cluster,
                memberRawPolygonIds,
                clusterCenter,
                clusterDiameter,
                clusterId);

            if (merged == null)
                continue;

            additions.Add(merged);
            foreach (string rawPolygonId in memberRawPolygonIds)
                suppressedRawPolygonIds.Add(rawPolygonId);
        }

        if (additions.Count == 0 && suppressedRawPolygonIds.Count == 0)
            return records;

        var result = new List<FeatureRecord>(records.Count + additions.Count);
        result.AddRange(additions);
        for (int i = 0; i < records.Count; i++)
        {
            FeatureRecord record = records[i];
            if (IsRawPolygonRecord(record) && suppressedRawPolygonIds.Contains(record.feature.rawPolygonId))
                continue;
            result.Add(record);
        }

        return result;
    }

    private FeatureRecord BuildPointClusterMergedRecord(
        List<FeatureRecord> records,
        PointCluster cluster,
        HashSet<string> memberRawPolygonIds,
        GeoCoordinate clusterCenter,
        double clusterDiameter,
        string clusterId)
    {
        if (preferPointClusterHullOverRawPolygonHull)
        {
            return BuildBufferedPointClusterMergedRecord(
                records,
                cluster,
                memberRawPolygonIds,
                clusterCenter,
                clusterDiameter,
                clusterId);
        }

        FeatureRecord rawPolygonHull = BuildRawPointClusterMergedRecord(
            records,
            cluster,
            memberRawPolygonIds,
            clusterCenter,
            clusterDiameter,
            clusterId);
        if (rawPolygonHull != null || !buildPolygonFromPointClusterFallback)
            return rawPolygonHull;

        return BuildBufferedPointClusterMergedRecord(
            records,
            cluster,
            memberRawPolygonIds,
            clusterCenter,
            clusterDiameter,
            clusterId);
    }

    private FeatureRecord BuildRawPointClusterMergedRecord(
        List<FeatureRecord> records,
        PointCluster cluster,
        HashSet<string> memberRawPolygonIds,
        GeoCoordinate clusterCenter,
        double clusterDiameter,
        string clusterId)
    {
        var vertices = new List<GeoCoordinate>();
        var detectionIds = new List<string>();
        var frameIndices = new List<int>();
        double sourceAreaSum = 0.0;
        double maskAreaSum = 0.0;
        double confidenceSum = 0.0;
        float maxConfidence = 0f;
        int originalVertexCount = 0;
        int validVertexCount = 0;
        float hitRatioSum = 0f;
        int sourcePolygonCount = 0;

        for (int i = 0; i < records.Count; i++)
        {
            FeatureRecord record = records[i];
            if (!IsRawPolygonRecord(record) || !memberRawPolygonIds.Contains(record.feature.rawPolygonId))
                continue;

            sourcePolygonCount++;
            AddUniqueString(detectionIds, record.candidate.detectionId);
            AddUniqueInt(frameIndices, record.candidate.frameIndex);
            confidenceSum += record.candidate.confidence;
            maxConfidence = Mathf.Max(maxConfidence, record.candidate.confidence);
            maskAreaSum += record.candidate.polygonArea;
            originalVertexCount += record.candidate.originalMaskVertexCount;
            validVertexCount += record.feature.validVertexCount;
            hitRatioSum += record.feature.hitRatio;
            sourceAreaSum += Math.Max(0.0, CalculateAreaMeters(record.feature.coordinates));

            for (int vertexIndex = 0; vertexIndex < record.feature.coordinates.Count; vertexIndex++)
            {
                GeoCoordinate vertex = record.feature.coordinates[vertexIndex];
                if (maxVertexDistanceFromPointClusterCenterMeters > 0f &&
                    DistanceMeters(vertex, clusterCenter) > maxVertexDistanceFromPointClusterCenterMeters)
                    continue;
                if (!ContainsCoordinate(vertices, vertex))
                    vertices.Add(vertex);
            }
        }

        if (sourcePolygonCount < 1 || vertices.Count < 3)
            return null;

        List<GeoCoordinate> hull = BuildConvexHull(vertices);
        if (hull.Count < 3)
            return null;

        double hullArea = CalculateAreaMeters(hull);
        double hullDiameter = CalculateMaxDistanceMeters(hull);
        double areaGrowthRatio = sourceAreaSum > 0.000001 ? hullArea / sourceAreaSum : 1.0;
        if (!IsFinite(hullArea) || hullArea <= 0.000001 ||
            hullDiameter > maxPointClusterDiameterMeters ||
            areaGrowthRatio > maxMergedHullAreaGrowthRatio)
            return null;

        FeatureRecord firstPoint = records[cluster.recordIndices[0]];
        var candidate = new DetectionCandidate
        {
            frameIndex = firstPoint.candidate.frameIndex,
            timeSeconds = firstPoint.candidate.timeSeconds,
            detectionId = clusterId,
            label = firstPoint.candidate.label,
            classId = firstPoint.candidate.classId,
            confidence = maxConfidence,
            bbox = null,
            centroid = Vector2.zero,
            hasCentroid = false,
            polygon = null,
            originalMaskVertexCount = originalVertexCount,
            polygonArea = maskAreaSum,
            borderClipped = false,
            candidateScore = maxConfidence,
            isAreaLabel = true
        };

        var feature = new ProjectedFeature
        {
            isPolygon = true,
            source = "point_cluster_merged_polygon",
            coordinates = hull,
            validVertexCount = validVertexCount,
            hitRatio = hitRatioSum / Math.Max(1, sourcePolygonCount),
            polygonFailedReason = string.Empty,
            pointClusterId = clusterId,
            pointClusterCount = cluster.recordIndices.Count,
            pointClusterDetectionIds = JoinStrings(detectionIds),
            pointClusterFrameIndices = JoinInts(frameIndices),
            averageConfidence = (float)(confidenceSum / Math.Max(1, sourcePolygonCount)),
            maxConfidence = maxConfidence,
            pointClusterDiameterMeters = clusterDiameter,
            mergedAreaMeters = hullArea,
            mergedAreaGrowthRatio = areaGrowthRatio,
            mergedClusterDiameterMeters = hullDiameter,
            generatedFrom = "representative_point_cluster"
        };
        return new FeatureRecord(candidate, feature);
    }

    private FeatureRecord BuildBufferedPointClusterMergedRecord(
        List<FeatureRecord> records,
        PointCluster cluster,
        HashSet<string> memberRawPolygonIds,
        GeoCoordinate clusterCenter,
        double clusterDiameter,
        string clusterId)
    {
        var localHullInput = new List<LocalHullPoint>();
        var detectionIds = new List<string>();
        var frameIndices = new List<int>();
        double confidenceSum = 0.0;
        float maxConfidence = 0f;
        double maskAreaSum = 0.0;
        int originalVertexCount = 0;
        int circleSegments = Mathf.Max(3, pointClusterHullCircleSegments);
        double radius = pointClusterPolygonBufferMeters;

        for (int i = 0; i < cluster.recordIndices.Count; i++)
        {
            FeatureRecord pointRecord = records[cluster.recordIndices[i]];
            GeoCoordinate point = pointRecord.feature.coordinates[0];
            AddUniqueString(detectionIds, pointRecord.candidate.detectionId);
            AddUniqueInt(frameIndices, pointRecord.candidate.frameIndex);
            confidenceSum += pointRecord.candidate.confidence;
            maxConfidence = Mathf.Max(maxConfidence, pointRecord.candidate.confidence);

            ProjectToLocalMeters(point, clusterCenter, out double pointX, out double pointY);
            for (int segment = 0; segment < circleSegments; segment++)
            {
                double angle = Math.PI * 2.0 * segment / circleSegments;
                localHullInput.Add(new LocalHullPoint(
                    pointX + Math.Cos(angle) * radius,
                    pointY + Math.Sin(angle) * radius,
                    clusterCenter.alt));
            }
        }

        for (int i = 0; i < records.Count; i++)
        {
            FeatureRecord rawRecord = records[i];
            if (!IsRawPolygonRecord(rawRecord) ||
                !memberRawPolygonIds.Contains(rawRecord.feature.rawPolygonId))
                continue;

            maskAreaSum += rawRecord.candidate.polygonArea;
            originalVertexCount += rawRecord.candidate.originalMaskVertexCount;
            if (!includeRawPolygonVerticesInPointClusterHull)
                continue;

            for (int vertexIndex = 0; vertexIndex < rawRecord.feature.coordinates.Count; vertexIndex++)
            {
                GeoCoordinate vertex = rawRecord.feature.coordinates[vertexIndex];
                if (DistanceMeters(vertex, clusterCenter) > maxRawVertexDistanceFromPointClusterCenterMeters)
                    continue;
                ProjectToLocalMeters(vertex, clusterCenter, out double vertexX, out double vertexY);
                localHullInput.Add(new LocalHullPoint(vertexX, vertexY, vertex.alt));
            }
        }

        List<LocalHullPoint> localHull = BuildLocalConvexHull(localHullInput);
        if (localHull.Count < 3)
            return null;

        double polygonDiameter = CalculateLocalMaxDistance(localHull);
        double polygonArea = CalculateLocalPolygonArea(localHull);
        if (!IsFinite(polygonDiameter) ||
            polygonDiameter > maxPointClusterPolygonDiameterMeters ||
            !IsFinite(polygonArea) || polygonArea <= 0.000001)
            return null;

        var coordinates = new List<GeoCoordinate>(localHull.Count);
        for (int i = 0; i < localHull.Count; i++)
        {
            LocalHullPoint point = localHull[i];
            coordinates.Add(LocalMetersToGeoCoordinate(point.x, point.y, point.alt, clusterCenter));
        }

        FeatureRecord firstPoint = records[cluster.recordIndices[0]];
        var candidate = new DetectionCandidate
        {
            frameIndex = firstPoint.candidate.frameIndex,
            timeSeconds = firstPoint.candidate.timeSeconds,
            detectionId = clusterId,
            label = firstPoint.candidate.label,
            classId = firstPoint.candidate.classId,
            confidence = maxConfidence,
            bbox = null,
            centroid = Vector2.zero,
            hasCentroid = false,
            polygon = null,
            originalMaskVertexCount = originalVertexCount,
            polygonArea = maskAreaSum,
            borderClipped = false,
            candidateScore = maxConfidence,
            isAreaLabel = true
        };

        var feature = new ProjectedFeature
        {
            isPolygon = true,
            source = "point_cluster_buffered_hull",
            coordinates = coordinates,
            validVertexCount = coordinates.Count,
            hitRatio = 1f,
            polygonFailedReason = string.Empty,
            pointClusterId = clusterId,
            pointClusterCount = cluster.recordIndices.Count,
            pointClusterDetectionIds = JoinStrings(detectionIds),
            pointClusterFrameIndices = JoinInts(frameIndices),
            averageConfidence = (float)(confidenceSum / Math.Max(1, cluster.recordIndices.Count)),
            maxConfidence = maxConfidence,
            pointClusterDiameterMeters = clusterDiameter,
            pointClusterPolygonBufferMeters = pointClusterPolygonBufferMeters,
            mergedAreaMeters = polygonArea,
            mergedClusterDiameterMeters = polygonDiameter,
            generatedFrom = "representative_points_buffered_hull"
        };
        return new FeatureRecord(candidate, feature);
    }

    private List<FeatureRecord> MergePointClustersIntoBuildingAggregates(List<FeatureRecord> records)
    {
        if (records == null || records.Count == 0 || !exportBuildingAggregatePolygons)
            return records;

        List<PointClusterSummary> summaries = BuildPointClusterSummaries(records);
        if (summaries.Count == 0)
            return records;

        int[] parent = new int[summaries.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        for (int i = 0; i < summaries.Count; i++)
        {
            for (int j = i + 1; j < summaries.Count; j++)
            {
                if (!string.Equals(summaries[i].label, summaries[j].label, StringComparison.OrdinalIgnoreCase))
                    continue;

                double distance = CalculateMinPointDistance(records, summaries[i].pointRecordIndices, summaries[j].pointRecordIndices);
                double bufferedHullGap = CalculateBufferedPointClusterHullGapMeters(
                    records,
                    summaries[i].pointRecordIndices,
                    summaries[j].pointRecordIndices,
                    Math.Max(0.0, buildingAggregatePolygonBufferMeters));

                // Important: two point clusters can belong to the same building even when the nearest
                // representative points are just outside pointClusterMergeDistanceMeters. If their
                // buffered hulls overlap/touch, they should still become one building aggregate.
                // This avoids splitting one real building into multiple aggregate polygons.
                double allowedOverlapGapMeters = Math.Max(0.25, Math.Max(0.0, buildingAggregatePolygonBufferMeters) * 0.15);
                bool closeByPoints = distance <= pointClusterMergeDistanceMeters;
                bool closeByBufferedHulls = bufferedHullGap <= allowedOverlapGapMeters;
                if (!closeByPoints && !closeByBufferedHulls)
                    continue;

                double combinedDiameter = CalculateCombinedPointDiameter(records, summaries[i].pointRecordIndices, summaries[j].pointRecordIndices);
                if (maxBuildingAggregateDiameterMeters > 0f && combinedDiameter > maxBuildingAggregateDiameterMeters)
                    continue;

                Union(parent, i, j);
            }
        }

        var aggregateMap = new Dictionary<int, List<int>>();
        for (int i = 0; i < summaries.Count; i++)
        {
            int root = Find(parent, i);
            if (!aggregateMap.TryGetValue(root, out List<int> values))
            {
                values = new List<int>();
                aggregateMap.Add(root, values);
            }
            values.Add(i);
        }

        var additions = new List<FeatureRecord>();
        int aggregateCounter = 0;
        foreach (List<int> summaryIndices in aggregateMap.Values)
        {
            var pointRecordIndices = new List<int>();
            var pointClusterIds = new List<string>();
            string label = string.Empty;

            for (int i = 0; i < summaryIndices.Count; i++)
            {
                PointClusterSummary summary = summaries[summaryIndices[i]];
                label = summary.label;
                AddUniqueString(pointClusterIds, summary.pointClusterId);
                for (int j = 0; j < summary.pointRecordIndices.Count; j++)
                {
                    int index = summary.pointRecordIndices[j];
                    if (!pointRecordIndices.Contains(index))
                        pointRecordIndices.Add(index);
                }
            }

            if (pointRecordIndices.Count < Mathf.Max(2, minPointsPerCluster))
                continue;

            double aggregatePointDiameter = CalculatePointRecordDiameter(records, pointRecordIndices);
            if (maxBuildingAggregateDiameterMeters > 0f && aggregatePointDiameter > maxBuildingAggregateDiameterMeters)
                continue;

            aggregateCounter++;
            string aggregateId = $"building_aggregate_{MakeIdPart(label)}_{aggregateCounter}";
            FeatureRecord aggregateRecord = BuildBuildingAggregateRecord(
                records,
                pointRecordIndices,
                pointClusterIds,
                aggregateId,
                aggregatePointDiameter);
            if (aggregateRecord == null)
                continue;

            additions.Add(aggregateRecord);

            for (int i = 0; i < pointRecordIndices.Count; i++)
            {
                FeatureRecord pointRecord = records[pointRecordIndices[i]];
                pointRecord.feature.buildingAggregateId = aggregateId;
                pointRecord.feature.clusterLinkType = "representative_point_building_aggregate";
            }
        }

        if (additions.Count == 0)
            return records;

        var result = new List<FeatureRecord>(records.Count + additions.Count);
        result.AddRange(additions);
        result.AddRange(records);
        return result;
    }

    private List<PointClusterSummary> BuildPointClusterSummaries(List<FeatureRecord> records)
    {
        var map = new Dictionary<string, PointClusterSummary>(StringComparer.Ordinal);
        for (int i = 0; i < records.Count; i++)
        {
            FeatureRecord record = records[i];
            if (!IsRepresentativePointRecord(record) || string.IsNullOrWhiteSpace(record.feature.pointClusterId))
                continue;

            string id = record.feature.pointClusterId;
            if (!map.TryGetValue(id, out PointClusterSummary summary))
            {
                summary = new PointClusterSummary(id, record.candidate.label);
                map.Add(id, summary);
            }
            summary.pointRecordIndices.Add(i);
        }

        var summaries = new List<PointClusterSummary>(map.Values);
        for (int i = 0; i < summaries.Count; i++)
        {
            summaries[i].center = CalculatePointClusterCenter(records, summaries[i].pointRecordIndices);
            summaries[i].diameter = CalculatePointRecordDiameter(records, summaries[i].pointRecordIndices);
        }
        return summaries;
    }

    private FeatureRecord BuildBuildingAggregateRecord(
        List<FeatureRecord> records,
        List<int> pointRecordIndices,
        List<string> pointClusterIds,
        string aggregateId,
        double aggregatePointDiameter)
    {
        if (pointRecordIndices == null || pointRecordIndices.Count < 2)
            return null;

        GeoCoordinate center = CalculatePointClusterCenter(records, pointRecordIndices);
        var localHullInput = new List<LocalHullPoint>();
        var detectionIds = new List<string>();
        var frameIndices = new List<int>();
        double confidenceSum = 0.0;
        float maxConfidence = 0f;
        int circleSegments = Mathf.Max(3, pointClusterHullCircleSegments);
        double radius = buildingAggregatePolygonBufferMeters;
        double avgAlt = 0.0;

        for (int i = 0; i < pointRecordIndices.Count; i++)
        {
            FeatureRecord pointRecord = records[pointRecordIndices[i]];
            GeoCoordinate point = pointRecord.feature.coordinates[0];
            AddUniqueString(detectionIds, pointRecord.candidate.detectionId);
            AddUniqueInt(frameIndices, pointRecord.candidate.frameIndex);
            confidenceSum += pointRecord.candidate.confidence;
            maxConfidence = Mathf.Max(maxConfidence, pointRecord.candidate.confidence);
            avgAlt += point.alt;

            ProjectToLocalMeters(point, center, out double pointX, out double pointY);
            for (int segment = 0; segment < circleSegments; segment++)
            {
                double angle = Math.PI * 2.0 * segment / circleSegments;
                localHullInput.Add(new LocalHullPoint(
                    pointX + Math.Cos(angle) * radius,
                    pointY + Math.Sin(angle) * radius,
                    point.alt));
            }
        }

        if (localHullInput.Count < 3)
            return null;

        avgAlt /= Math.Max(1, pointRecordIndices.Count);
        List<LocalHullPoint> localHull = BuildLocalConvexHull(localHullInput);
        if (localHull.Count < 3)
            return null;

        double polygonArea = CalculateLocalPolygonArea(localHull);
        double polygonDiameter = CalculateLocalMaxDistance(localHull);
        double allowedPolygonDiameter = maxBuildingAggregateDiameterMeters > 0f
            ? maxBuildingAggregateDiameterMeters + buildingAggregatePolygonBufferMeters * 2.0
            : double.PositiveInfinity;
        if (!IsFinite(polygonArea) || polygonArea <= 0.000001 ||
            !IsFinite(polygonDiameter) || polygonDiameter > allowedPolygonDiameter)
            return null;

        var coordinates = new List<GeoCoordinate>(localHull.Count);
        for (int i = 0; i < localHull.Count; i++)
        {
            LocalHullPoint point = localHull[i];
            coordinates.Add(LocalMetersToGeoCoordinate(point.x, point.y, avgAlt, center));
        }

        FeatureRecord firstPoint = records[pointRecordIndices[0]];
        float averageConfidence = (float)(confidenceSum / Math.Max(1, pointRecordIndices.Count));
        var candidate = new DetectionCandidate
        {
            frameIndex = firstPoint.candidate.frameIndex,
            timeSeconds = firstPoint.candidate.timeSeconds,
            detectionId = aggregateId,
            label = firstPoint.candidate.label,
            classId = firstPoint.candidate.classId,
            confidence = maxConfidence,
            bbox = null,
            centroid = Vector2.zero,
            hasCentroid = false,
            polygon = null,
            originalMaskVertexCount = 0,
            polygonArea = 0.0,
            borderClipped = false,
            candidateScore = maxConfidence,
            isAreaLabel = true
        };

        var feature = new ProjectedFeature
        {
            isPolygon = true,
            source = "building_aggregate_polygon",
            coordinates = coordinates,
            validVertexCount = coordinates.Count,
            hitRatio = 1f,
            polygonFailedReason = string.Empty,
            buildingAggregateId = aggregateId,
            buildingAggregateCount = pointRecordIndices.Count,
            buildingAggregateDetectionIds = JoinStrings(detectionIds),
            buildingAggregatePointClusterIds = JoinStrings(pointClusterIds),
            buildingAggregateFrameIndices = JoinInts(frameIndices),
            buildingAggregateDiameterMeters = aggregatePointDiameter,
            buildingAggregatePolygonBufferMeters = buildingAggregatePolygonBufferMeters,
            averageConfidence = averageConfidence,
            maxConfidence = maxConfidence,
            mergedAreaMeters = polygonArea,
            mergedClusterDiameterMeters = polygonDiameter,
            generatedFrom = "representative_points_building_aggregate"
        };
        return new FeatureRecord(candidate, feature);
    }

    private static double CalculateMinPointDistance(
        List<FeatureRecord> records,
        List<int> aIndices,
        List<int> bIndices)
    {
        double minDistance = double.PositiveInfinity;
        for (int i = 0; i < aIndices.Count; i++)
        {
            GeoCoordinate a = records[aIndices[i]].feature.coordinates[0];
            for (int j = 0; j < bIndices.Count; j++)
            {
                GeoCoordinate b = records[bIndices[j]].feature.coordinates[0];
                minDistance = Math.Min(minDistance, DistanceMeters(a, b));
            }
        }
        return minDistance;
    }

    private static double CalculateCombinedPointDiameter(
        List<FeatureRecord> records,
        List<int> aIndices,
        List<int> bIndices)
    {
        var combined = new List<int>(aIndices.Count + bIndices.Count);
        combined.AddRange(aIndices);
        combined.AddRange(bIndices);
        return CalculatePointRecordDiameter(records, combined);
    }

    private static double CalculateBufferedPointClusterHullGapMeters(
        List<FeatureRecord> records,
        List<int> aIndices,
        List<int> bIndices,
        double bufferMeters)
    {
        if (records == null || aIndices == null || bIndices == null ||
            aIndices.Count == 0 || bIndices.Count == 0)
            return double.PositiveInfinity;

        GeoCoordinate origin = CalculatePointRecordCenter(records, aIndices, bIndices);
        List<LocalHullPoint> hullA = BuildBufferedPointClusterLocalHull(records, aIndices, origin, bufferMeters);
        List<LocalHullPoint> hullB = BuildBufferedPointClusterLocalHull(records, bIndices, origin, bufferMeters);
        if (hullA.Count < 3 || hullB.Count < 3)
            return double.PositiveInfinity;

        if (LocalPolygonsOverlapOrTouch(hullA, hullB))
            return 0.0;

        double minDistance = double.PositiveInfinity;
        for (int i = 0; i < hullA.Count; i++)
        {
            LocalHullPoint a0 = hullA[i];
            LocalHullPoint a1 = hullA[(i + 1) % hullA.Count];
            for (int j = 0; j < hullB.Count; j++)
            {
                LocalHullPoint b0 = hullB[j];
                LocalHullPoint b1 = hullB[(j + 1) % hullB.Count];
                minDistance = Math.Min(minDistance, DistancePointToLocalSegment(a0, b0, b1));
                minDistance = Math.Min(minDistance, DistancePointToLocalSegment(b0, a0, a1));
            }
        }

        return minDistance;
    }

    private static GeoCoordinate CalculatePointRecordCenter(
        List<FeatureRecord> records,
        List<int> aIndices,
        List<int> bIndices)
    {
        double lon = 0.0;
        double lat = 0.0;
        double alt = 0.0;
        int count = 0;

        if (aIndices != null)
        {
            for (int i = 0; i < aIndices.Count; i++)
            {
                GeoCoordinate point = records[aIndices[i]].feature.coordinates[0];
                lon += point.lon;
                lat += point.lat;
                alt += point.alt;
                count++;
            }
        }

        if (bIndices != null)
        {
            for (int i = 0; i < bIndices.Count; i++)
            {
                GeoCoordinate point = records[bIndices[i]].feature.coordinates[0];
                lon += point.lon;
                lat += point.lat;
                alt += point.alt;
                count++;
            }
        }

        if (count == 0)
            return new GeoCoordinate(0.0, 0.0, 0.0);

        double inverse = 1.0 / count;
        return new GeoCoordinate(lon * inverse, lat * inverse, alt * inverse);
    }

    private static List<LocalHullPoint> BuildBufferedPointClusterLocalHull(
        List<FeatureRecord> records,
        List<int> pointRecordIndices,
        GeoCoordinate origin,
        double bufferMeters)
    {
        var hullInput = new List<LocalHullPoint>();
        int segments = 8;
        double radius = Math.Max(0.0, bufferMeters);

        for (int i = 0; i < pointRecordIndices.Count; i++)
        {
            GeoCoordinate point = records[pointRecordIndices[i]].feature.coordinates[0];
            ProjectToLocalMeters(point, origin, out double x, out double y);
            if (radius <= 0.000001)
            {
                hullInput.Add(new LocalHullPoint(x, y, point.alt));
                continue;
            }

            for (int segment = 0; segment < segments; segment++)
            {
                double angle = Math.PI * 2.0 * segment / segments;
                hullInput.Add(new LocalHullPoint(
                    x + Math.Cos(angle) * radius,
                    y + Math.Sin(angle) * radius,
                    point.alt));
            }
        }

        return BuildLocalConvexHull(hullInput);
    }

    private static bool LocalPolygonsOverlapOrTouch(List<LocalHullPoint> a, List<LocalHullPoint> b)
    {
        for (int i = 0; i < a.Count; i++)
        {
            LocalHullPoint a0 = a[i];
            LocalHullPoint a1 = a[(i + 1) % a.Count];
            for (int j = 0; j < b.Count; j++)
            {
                LocalHullPoint b0 = b[j];
                LocalHullPoint b1 = b[(j + 1) % b.Count];
                if (LocalSegmentsIntersect(a0, a1, b0, b1))
                    return true;
            }
        }

        return IsLocalPointInsidePolygon(a[0], b) || IsLocalPointInsidePolygon(b[0], a);
    }

    private static bool LocalSegmentsIntersect(LocalHullPoint a, LocalHullPoint b, LocalHullPoint c, LocalHullPoint d)
    {
        double o1 = CrossLocal(a, b, c);
        double o2 = CrossLocal(a, b, d);
        double o3 = CrossLocal(c, d, a);
        double o4 = CrossLocal(c, d, b);
        const double epsilon = 1e-9;

        if (((o1 > epsilon && o2 < -epsilon) || (o1 < -epsilon && o2 > epsilon)) &&
            ((o3 > epsilon && o4 < -epsilon) || (o3 < -epsilon && o4 > epsilon)))
            return true;

        if (Math.Abs(o1) <= epsilon && IsLocalPointOnSegment(c, a, b))
            return true;
        if (Math.Abs(o2) <= epsilon && IsLocalPointOnSegment(d, a, b))
            return true;
        if (Math.Abs(o3) <= epsilon && IsLocalPointOnSegment(a, c, d))
            return true;
        if (Math.Abs(o4) <= epsilon && IsLocalPointOnSegment(b, c, d))
            return true;

        return false;
    }

    private static bool IsLocalPointOnSegment(LocalHullPoint p, LocalHullPoint a, LocalHullPoint b)
    {
        const double epsilon = 1e-9;
        return p.x >= Math.Min(a.x, b.x) - epsilon &&
               p.x <= Math.Max(a.x, b.x) + epsilon &&
               p.y >= Math.Min(a.y, b.y) - epsilon &&
               p.y <= Math.Max(a.y, b.y) + epsilon;
    }

    private static bool IsLocalPointInsidePolygon(LocalHullPoint point, List<LocalHullPoint> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            LocalHullPoint pi = polygon[i];
            LocalHullPoint pj = polygon[j];
            bool intersects = ((pi.y > point.y) != (pj.y > point.y)) &&
                              (point.x < (pj.x - pi.x) * (point.y - pi.y) /
                               Math.Max(1e-12, pj.y - pi.y) + pi.x);
            if (intersects)
                inside = !inside;
        }
        return inside;
    }

    private static double DistancePointToLocalSegment(LocalHullPoint point, LocalHullPoint start, LocalHullPoint end)
    {
        double dx = end.x - start.x;
        double dy = end.y - start.y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 1e-12)
            return Math.Sqrt((point.x - start.x) * (point.x - start.x) + (point.y - start.y) * (point.y - start.y));

        double t = ((point.x - start.x) * dx + (point.y - start.y) * dy) / lengthSquared;
        t = Math.Max(0.0, Math.Min(1.0, t));
        double projectedX = start.x + t * dx;
        double projectedY = start.y + t * dy;
        double px = point.x - projectedX;
        double py = point.y - projectedY;
        return Math.Sqrt(px * px + py * py);
    }

    private static double CalculatePointRecordDiameter(List<FeatureRecord> records, List<int> recordIndices)
    {
        if (records == null || recordIndices == null || recordIndices.Count < 2)
            return 0.0;

        double diameter = 0.0;
        for (int i = 0; i < recordIndices.Count; i++)
        {
            GeoCoordinate a = records[recordIndices[i]].feature.coordinates[0];
            for (int j = i + 1; j < recordIndices.Count; j++)
            {
                GeoCoordinate b = records[recordIndices[j]].feature.coordinates[0];
                diameter = Math.Max(diameter, DistanceMeters(a, b));
            }
        }
        return diameter;
    }

    private static FeatureRecord BuildPointClusterDebugLine(
        FeatureRecord pointRecord,
        GeoCoordinate clusterCenter,
        string clusterId)
    {
        var feature = new ProjectedFeature
        {
            isLineString = true,
            source = "debug_point_to_cluster_line",
            coordinates = new List<GeoCoordinate>
            {
                pointRecord.feature.coordinates[0],
                clusterCenter
            },
            validVertexCount = 2,
            hitRatio = 1f,
            pointClusterId = clusterId,
            linkedDetectionId = pointRecord.candidate.detectionId,
            linkedRawPolygonId = pointRecord.feature.linkedRawPolygonId,
            generatedFrom = "representative_point_cluster"
        };
        return new FeatureRecord(pointRecord.candidate, feature);
    }

    private List<FeatureRecord> ApplyPointClusterOutputPolicy(List<FeatureRecord> records)
    {
        var result = new List<FeatureRecord>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            FeatureRecord record = records[i];
            if (!exportUnclusteredRawPolygons && IsRawPolygonRecord(record))
                continue;
            if (!exportIntermediatePointClusterPolygons && IsPointClusterPolygonRecord(record))
                continue;
            if (!exportPointClusterRepresentativePoints && IsRepresentativePointRecord(record) &&
                !string.IsNullOrWhiteSpace(record.feature.pointClusterId))
                continue;
            if (exportOnlyAggregatedRepresentativePoints && IsRepresentativePointRecord(record) &&
                string.IsNullOrWhiteSpace(record.feature.buildingAggregateId))
                continue;
            result.Add(record);
        }
        return result;
    }

    private static bool IsRawPolygonRecord(FeatureRecord record)
    {
        return record != null && record.feature != null && record.feature.isPolygon &&
               string.Equals(record.feature.source, "mask_polygon_projection", StringComparison.Ordinal);
    }

    private static bool IsPointClusterPolygonRecord(FeatureRecord record)
    {
        return record != null && record.feature != null && record.feature.isPolygon &&
               (string.Equals(record.feature.source, "point_cluster_buffered_hull", StringComparison.Ordinal) ||
                string.Equals(record.feature.source, "point_cluster_merged_polygon", StringComparison.Ordinal));
    }

    private static bool IsRepresentativePointRecord(FeatureRecord record)
    {
        return record != null && record.feature != null && !record.feature.isPolygon &&
               !record.feature.isLineString &&
               record.feature.coordinates != null && record.feature.coordinates.Count == 1 &&
               string.Equals(record.feature.source, "polygon_representative_point", StringComparison.Ordinal);
    }

    private static GeoCoordinate CalculatePointClusterCenter(List<FeatureRecord> records, List<int> recordIndices)
    {
        double lon = 0.0;
        double lat = 0.0;
        double alt = 0.0;
        for (int i = 0; i < recordIndices.Count; i++)
        {
            GeoCoordinate point = records[recordIndices[i]].feature.coordinates[0];
            lon += point.lon;
            lat += point.lat;
            alt += point.alt;
        }
        double inverse = 1.0 / Math.Max(1, recordIndices.Count);
        return new GeoCoordinate(lon * inverse, lat * inverse, alt * inverse);
    }

    private static HashSet<string> GetPointClusterRawPolygonIds(List<FeatureRecord> records, List<int> recordIndices)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < recordIndices.Count; i++)
        {
            string rawPolygonId = records[recordIndices[i]].feature.linkedRawPolygonId;
            if (!string.IsNullOrWhiteSpace(rawPolygonId))
                ids.Add(rawPolygonId);
        }
        return ids;
    }

    private List<FeatureRecord> MergeNearbyPolygonRecords(List<FeatureRecord> records)
    {
        if (records == null || records.Count == 0 || mergeDistanceMeters <= 0f || !HasAnyNonEmpty(mergePolygonLabels))
            return records;

        int count = records.Count;
        int[] parent = new int[count];
        bool[] eligible = new bool[count];
        GeoCoordinate[] centroids = new GeoCoordinate[count];
        for (int i = 0; i < count; i++)
        {
            parent[i] = i;
            FeatureRecord record = records[i];
            eligible[i] = record.feature != null && record.feature.isPolygon &&
                          string.Equals(record.feature.source, "mask_polygon_projection", StringComparison.Ordinal) &&
                          record.feature.coordinates != null && record.feature.coordinates.Count >= 3 &&
                          ContainsLabel(mergePolygonLabels, record.candidate.label);
            if (eligible[i])
                centroids[i] = CalculateCentroid(record.feature.coordinates);
        }

        for (int i = 0; i < count; i++)
        {
            if (!eligible[i])
                continue;
            for (int j = i + 1; j < count; j++)
            {
                if (!eligible[j])
                    continue;
                if (!string.Equals(records[i].candidate.label, records[j].candidate.label, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (CanMergePolygonPair(records[i], records[j], centroids[i], centroids[j]))
                    Union(parent, i, j);
            }
        }

        var clusterMap = new Dictionary<int, List<int>>();
        for (int i = 0; i < count; i++)
        {
            if (!eligible[i])
                continue;
            int root = Find(parent, i);
            if (!clusterMap.TryGetValue(root, out List<int> indices))
            {
                indices = new List<int>();
                clusterMap.Add(root, indices);
            }
            indices.Add(i);
        }

        bool[] suppressOriginal = new bool[count];
        var mergedRecords = new List<FeatureRecord>();
        int clusterCounter = 0;
        foreach (List<int> indices in clusterMap.Values)
        {
            if (indices.Count < Mathf.Max(2, minPolygonsPerMergeCluster))
                continue;

            // Union-find produces connected components. Require complete-link validity as well so
            // A-B and B-C proximity cannot merge A with C when that pair fails the configured gates.
            if (!IsMergeClusterPairwiseValid(records, indices))
                continue;

            clusterCounter++;
            string clusterId = $"merged_{records[indices[0]].candidate.label}_{clusterCounter}";
            FeatureRecord merged = BuildMergedFeatureRecord(records, indices, clusterId);
            if (merged == null)
                continue;

            mergedRecords.Add(merged);
            MarkPointRecordsForCluster(records, indices, clusterId);

            if (!keepOriginalPolygonsWhenMerged)
            {
                for (int i = 0; i < indices.Count; i++)
                    suppressOriginal[indices[i]] = true;
            }
        }

        if (mergedRecords.Count == 0)
            return records;

        var finalRecords = new List<FeatureRecord>(records.Count + mergedRecords.Count);
        finalRecords.AddRange(mergedRecords);
        for (int i = 0; i < records.Count; i++)
        {
            if (suppressOriginal[i])
                continue;
            finalRecords.Add(records[i]);
        }
        return finalRecords;
    }

    private FeatureRecord BuildMergedFeatureRecord(List<FeatureRecord> records, List<int> indices, string clusterId)
    {
        var allCoordinates = new List<GeoCoordinate>();
        var detectionIds = new List<string>();
        var frameIndices = new List<int>();
        double confidenceSum = 0.0;
        float maxConfidence = 0f;
        int validVertexCount = 0;
        float hitRatioSum = 0f;
        double maskAreaSum = 0.0;
        double originalAreaMetersSum = 0.0;
        int originalVertexCount = 0;

        for (int i = 0; i < indices.Count; i++)
        {
            FeatureRecord record = records[indices[i]];
            DetectionCandidate candidate = record.candidate;
            ProjectedFeature feature = record.feature;

            AddUniqueString(detectionIds, candidate.detectionId);
            AddUniqueInt(frameIndices, candidate.frameIndex);
            confidenceSum += candidate.confidence;
            maxConfidence = Mathf.Max(maxConfidence, candidate.confidence);
            validVertexCount += feature.validVertexCount;
            hitRatioSum += feature.hitRatio;
            maskAreaSum += candidate.polygonArea;
            originalAreaMetersSum += Math.Max(0.0, CalculateAreaMeters(feature.coordinates));
            originalVertexCount += candidate.originalMaskVertexCount;

            for (int c = 0; c < feature.coordinates.Count; c++)
            {
                if (!ContainsCoordinate(allCoordinates, feature.coordinates[c]))
                    allCoordinates.Add(feature.coordinates[c]);
            }
        }

        if (allCoordinates.Count < 3)
            return null;

        List<GeoCoordinate> mergedCoordinates = useConvexHullForMergedPolygons
            ? BuildConvexHull(allCoordinates)
            : allCoordinates;

        if (mergedCoordinates.Count < 3)
            return null;

        double mergedAreaMeters = CalculateAreaMeters(mergedCoordinates);
        double mergedAreaGrowthRatio = originalAreaMetersSum > 0.000001
            ? mergedAreaMeters / originalAreaMetersSum
            : 1.0;
        double mergedClusterDiameterMeters = CalculateMaxDistanceMeters(mergedCoordinates);

        if (mergedAreaGrowthRatio > maxMergedHullAreaGrowthRatio)
            return null;

        if (maxMergedClusterDiameterMeters > 0f && mergedClusterDiameterMeters > maxMergedClusterDiameterMeters)
            return null;

        DetectionCandidate first = records[indices[0]].candidate;
        float averageConfidence = (float)(confidenceSum / Math.Max(1, indices.Count));
        CalculateClusterMergeMetrics(
            records,
            indices,
            out double observedMergeDistanceMeters,
            out double observedBoundaryDistanceMeters);
        var mergedCandidate = new DetectionCandidate
        {
            frameIndex = first.frameIndex,
            timeSeconds = first.timeSeconds,
            detectionId = clusterId,
            label = first.label,
            classId = first.classId,
            confidence = maxConfidence,
            bbox = null,
            centroid = Vector2.zero,
            hasCentroid = false,
            polygon = null,
            originalMaskVertexCount = originalVertexCount,
            polygonArea = maskAreaSum,
            borderClipped = false,
            candidateScore = maxConfidence,
            isAreaLabel = true
        };

        var mergedFeature = new ProjectedFeature
        {
            isPolygon = true,
            source = "merged_nearby_polygons",
            coordinates = mergedCoordinates,
            validVertexCount = validVertexCount,
            hitRatio = hitRatioSum / Math.Max(1, indices.Count),
            polygonFailedReason = string.Empty,
            mergedClusterId = clusterId,
            mergedCount = indices.Count,
            mergedDetectionIds = JoinStrings(detectionIds),
            mergedFrameIndices = JoinInts(frameIndices),
            averageConfidence = averageConfidence,
            maxConfidence = maxConfidence,
            mergeDistanceMeters = observedMergeDistanceMeters,
            mergeBoundaryDistanceMeters = requireBoundaryDistanceForMerge ? observedBoundaryDistanceMeters : -1.0,
            mergedAreaMeters = mergedAreaMeters,
            mergedAreaGrowthRatio = mergedAreaGrowthRatio,
            mergedClusterDiameterMeters = mergedClusterDiameterMeters
        };

        return new FeatureRecord(mergedCandidate, mergedFeature);
    }

    private bool IsMergeClusterPairwiseValid(List<FeatureRecord> records, List<int> indices)
    {
        for (int i = 0; i < indices.Count; i++)
        {
            FeatureRecord a = records[indices[i]];
            GeoCoordinate centroidA = CalculateCentroid(a.feature.coordinates);
            for (int j = i + 1; j < indices.Count; j++)
            {
                FeatureRecord b = records[indices[j]];
                if (!string.Equals(a.candidate.label, b.candidate.label, StringComparison.OrdinalIgnoreCase))
                    return false;

                GeoCoordinate centroidB = CalculateCentroid(b.feature.coordinates);
                if (!CanMergePolygonPair(a, b, centroidA, centroidB))
                    return false;
            }
        }

        return true;
    }

    private static void CalculateClusterMergeMetrics(
        List<FeatureRecord> records,
        List<int> indices,
        out double maxCentroidDistanceMeters,
        out double maxBoundaryDistanceMeters)
    {
        maxCentroidDistanceMeters = 0.0;
        maxBoundaryDistanceMeters = 0.0;
        for (int i = 0; i < indices.Count; i++)
        {
            List<GeoCoordinate> coordinatesA = records[indices[i]].feature.coordinates;
            GeoCoordinate centroidA = CalculateCentroid(coordinatesA);
            for (int j = i + 1; j < indices.Count; j++)
            {
                List<GeoCoordinate> coordinatesB = records[indices[j]].feature.coordinates;
                GeoCoordinate centroidB = CalculateCentroid(coordinatesB);
                maxCentroidDistanceMeters = Math.Max(
                    maxCentroidDistanceMeters,
                    DistanceMeters(centroidA, centroidB));
                maxBoundaryDistanceMeters = Math.Max(
                    maxBoundaryDistanceMeters,
                    CalculateBoundsGapMeters(coordinatesA, coordinatesB));
            }
        }
    }

    private bool CanMergePolygonPair(
        FeatureRecord a,
        FeatureRecord b,
        GeoCoordinate centroidA,
        GeoCoordinate centroidB)
    {
        if (a == null || b == null || a.feature == null || b.feature == null ||
            a.feature.coordinates == null || b.feature.coordinates == null ||
            a.feature.coordinates.Count < 3 || b.feature.coordinates.Count < 3)
            return false;

        double centroidDistance = DistanceMeters(centroidA, centroidB);
        if (centroidDistance > mergeDistanceMeters)
            return false;

        if (requireBoundaryDistanceForMerge)
        {
            double boundaryGap = CalculateBoundsGapMeters(a.feature.coordinates, b.feature.coordinates);
            if (boundaryGap > mergeBoundaryDistanceMeters)
                return false;
        }

        double areaA = CalculateAreaMeters(a.feature.coordinates);
        double areaB = CalculateAreaMeters(b.feature.coordinates);
        double originalAreaSum = areaA + areaB;

        var combined = new List<GeoCoordinate>(a.feature.coordinates.Count + b.feature.coordinates.Count);
        for (int i = 0; i < a.feature.coordinates.Count; i++)
        {
            if (!ContainsCoordinate(combined, a.feature.coordinates[i]))
                combined.Add(a.feature.coordinates[i]);
        }
        for (int i = 0; i < b.feature.coordinates.Count; i++)
        {
            if (!ContainsCoordinate(combined, b.feature.coordinates[i]))
                combined.Add(b.feature.coordinates[i]);
        }

        if (combined.Count < 3)
            return false;

        List<GeoCoordinate> mergedHull = BuildConvexHull(combined);
        if (mergedHull.Count < 3)
            return false;

        double mergedArea = CalculateAreaMeters(mergedHull);
        double areaGrowthRatio = originalAreaSum > 0.000001 ? mergedArea / originalAreaSum : 1.0;
        if (areaGrowthRatio > maxMergedHullAreaGrowthRatio)
            return false;

        double diameter = CalculateMaxDistanceMeters(mergedHull);
        if (maxMergedClusterDiameterMeters > 0f && diameter > maxMergedClusterDiameterMeters)
            return false;

        return true;
    }

    private static double CalculateBoundsGapMeters(List<GeoCoordinate> a, List<GeoCoordinate> b)
    {
        if (a == null || b == null || a.Count == 0 || b.Count == 0)
            return double.PositiveInfinity;

        GeoCoordinate origin = CalculateCentroidForLocalMeters(a, b);
        LocalBounds2D boundsA = CalculateLocalBoundsMeters(a, origin);
        LocalBounds2D boundsB = CalculateLocalBoundsMeters(b, origin);

        double gapX = 0.0;
        if (boundsA.maxX < boundsB.minX)
            gapX = boundsB.minX - boundsA.maxX;
        else if (boundsB.maxX < boundsA.minX)
            gapX = boundsA.minX - boundsB.maxX;

        double gapY = 0.0;
        if (boundsA.maxY < boundsB.minY)
            gapY = boundsB.minY - boundsA.maxY;
        else if (boundsB.maxY < boundsA.minY)
            gapY = boundsA.minY - boundsB.maxY;

        return Math.Sqrt(gapX * gapX + gapY * gapY);
    }

    private static LocalBounds2D CalculateLocalBoundsMeters(List<GeoCoordinate> coordinates, GeoCoordinate origin)
    {
        var bounds = new LocalBounds2D
        {
            minX = double.PositiveInfinity,
            minY = double.PositiveInfinity,
            maxX = double.NegativeInfinity,
            maxY = double.NegativeInfinity
        };

        for (int i = 0; i < coordinates.Count; i++)
        {
            ProjectToLocalMeters(coordinates[i], origin, out double x, out double y);
            bounds.minX = Math.Min(bounds.minX, x);
            bounds.minY = Math.Min(bounds.minY, y);
            bounds.maxX = Math.Max(bounds.maxX, x);
            bounds.maxY = Math.Max(bounds.maxY, y);
        }

        return bounds;
    }

    private static double CalculateAreaMeters(List<GeoCoordinate> coordinates)
    {
        if (coordinates == null || coordinates.Count < 3)
            return 0.0;

        GeoCoordinate origin = CalculateCentroid(coordinates);
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

    private static double CalculateMaxDistanceMeters(List<GeoCoordinate> coordinates)
    {
        if (coordinates == null || coordinates.Count < 2)
            return 0.0;

        double maxDistance = 0.0;
        for (int i = 0; i < coordinates.Count; i++)
        {
            for (int j = i + 1; j < coordinates.Count; j++)
                maxDistance = Math.Max(maxDistance, DistanceMeters(coordinates[i], coordinates[j]));
        }

        return maxDistance;
    }

    private static GeoCoordinate CalculateCentroidForLocalMeters(List<GeoCoordinate> a, List<GeoCoordinate> b)
    {
        double lon = 0.0;
        double lat = 0.0;
        double alt = 0.0;
        int count = 0;

        if (a != null)
        {
            for (int i = 0; i < a.Count; i++)
            {
                lon += a[i].lon;
                lat += a[i].lat;
                alt += a[i].alt;
                count++;
            }
        }

        if (b != null)
        {
            for (int i = 0; i < b.Count; i++)
            {
                lon += b[i].lon;
                lat += b[i].lat;
                alt += b[i].alt;
                count++;
            }
        }

        if (count == 0)
            return new GeoCoordinate(0.0, 0.0, 0.0);

        double inv = 1.0 / count;
        return new GeoCoordinate(lon * inv, lat * inv, alt * inv);
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

    private static GeoCoordinate LocalMetersToGeoCoordinate(
        double x,
        double y,
        double altitude,
        GeoCoordinate origin)
    {
        const double earthRadiusMeters = 6371000.0;
        double lat0 = origin.lat * Math.PI / 180.0;
        double cosLat0 = Math.Cos(lat0);
        if (Math.Abs(cosLat0) < 0.000000000001)
            cosLat0 = cosLat0 < 0.0 ? -0.000000000001 : 0.000000000001;

        double lon = origin.lon + x / (earthRadiusMeters * cosLat0) * 180.0 / Math.PI;
        double lat = origin.lat + y / earthRadiusMeters * 180.0 / Math.PI;
        return new GeoCoordinate(lon, lat, altitude);
    }

    private static List<LocalHullPoint> BuildLocalConvexHull(List<LocalHullPoint> points)
    {
        var unique = new List<LocalHullPoint>();
        for (int i = 0; i < points.Count; i++)
        {
            bool duplicate = false;
            for (int j = 0; j < unique.Count; j++)
            {
                double dx = points[i].x - unique[j].x;
                double dy = points[i].y - unique[j].y;
                if (dx * dx + dy * dy <= 0.000000000001)
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
            int xComparison = a.x.CompareTo(b.x);
            return xComparison != 0 ? xComparison : a.y.CompareTo(b.y);
        });

        var lower = new List<LocalHullPoint>();
        for (int i = 0; i < unique.Count; i++)
        {
            while (lower.Count >= 2 &&
                   CrossLocal(lower[lower.Count - 2], lower[lower.Count - 1], unique[i]) <= 0.0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(unique[i]);
        }

        var upper = new List<LocalHullPoint>();
        for (int i = unique.Count - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 &&
                   CrossLocal(upper[upper.Count - 2], upper[upper.Count - 1], unique[i]) <= 0.0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(unique[i]);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static double CrossLocal(LocalHullPoint origin, LocalHullPoint a, LocalHullPoint b)
    {
        return (a.x - origin.x) * (b.y - origin.y) -
               (a.y - origin.y) * (b.x - origin.x);
    }

    private static double CalculateLocalPolygonArea(List<LocalHullPoint> points)
    {
        if (points == null || points.Count < 3)
            return 0.0;

        double sum = 0.0;
        for (int i = 0; i < points.Count; i++)
        {
            LocalHullPoint a = points[i];
            LocalHullPoint b = points[(i + 1) % points.Count];
            sum += a.x * b.y - b.x * a.y;
        }
        return Math.Abs(sum) * 0.5;
    }

    private static double CalculateLocalMaxDistance(List<LocalHullPoint> points)
    {
        double maxDistanceSquared = 0.0;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                double dx = points[i].x - points[j].x;
                double dy = points[i].y - points[j].y;
                maxDistanceSquared = Math.Max(maxDistanceSquared, dx * dx + dy * dy);
            }
        }
        return Math.Sqrt(maxDistanceSquared);
    }

    private static void MarkPointRecordsForCluster(List<FeatureRecord> records, List<int> polygonIndices, string clusterId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < polygonIndices.Count; i++)
            ids.Add(MakeTrackKey(records[polygonIndices[i]].candidate.label, records[polygonIndices[i]].candidate.detectionId));

        for (int i = 0; i < records.Count; i++)
        {
            FeatureRecord record = records[i];
            if (record.feature == null || record.feature.isPolygon)
                continue;
            string key = MakeTrackKey(record.candidate.label, record.candidate.detectionId);
            if (ids.Contains(key))
                record.feature.mergedClusterId = clusterId;
        }
    }

    private static GeoCoordinate CalculateCentroid(List<GeoCoordinate> coordinates)
    {
        if (coordinates == null || coordinates.Count == 0)
            return new GeoCoordinate(0.0, 0.0, 0.0);

        double lon = 0.0;
        double lat = 0.0;
        double alt = 0.0;
        for (int i = 0; i < coordinates.Count; i++)
        {
            lon += coordinates[i].lon;
            lat += coordinates[i].lat;
            alt += coordinates[i].alt;
        }
        double inv = 1.0 / coordinates.Count;
        return new GeoCoordinate(lon * inv, lat * inv, alt * inv);
    }

    private static double DistanceMeters(GeoCoordinate a, GeoCoordinate b)
    {
        const double earthRadiusMeters = 6371000.0;
        double lat1 = a.lat * Math.PI / 180.0;
        double lat2 = b.lat * Math.PI / 180.0;
        double dLat = (b.lat - a.lat) * Math.PI / 180.0;
        double dLon = (b.lon - a.lon) * Math.PI / 180.0;
        double sinLat = Math.Sin(dLat * 0.5);
        double sinLon = Math.Sin(dLon * 0.5);
        double h = sinLat * sinLat + Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon;
        double c = 2.0 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(Math.Max(0.0, 1.0 - h)));
        return earthRadiusMeters * c;
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }
        return index;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int rootA = Find(parent, a);
        int rootB = Find(parent, b);
        if (rootA != rootB)
            parent[rootB] = rootA;
    }

    private static void AddUniqueString(List<string> values, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
                return;
        }
        values.Add(value);
    }

    private static void AddUniqueInt(List<int> values, int value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == value)
                return;
        }
        values.Add(value);
    }

    private static string JoinStrings(List<string> values)
    {
        if (values == null || values.Count == 0)
            return string.Empty;
        return string.Join(",", values.ToArray());
    }

    private static string JoinInts(List<int> values)
    {
        if (values == null || values.Count == 0)
            return string.Empty;
        string[] parts = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
            parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
        return string.Join(",", parts);
    }

    private bool TryProjectFeature(
        DetectionCandidate candidate,
        DetectionMetadata metadata,
        out ProjectedFeature feature)
    {
        feature = null;
        if (candidate.isAreaLabel && exportPolygons)
        {
            if (TryProjectPolygon(candidate, metadata, out feature, out string polygonFailureReason))
                return true;

            if (exportPoints && fallbackToPointWhenPolygonFails)
                return TryProjectPoint(candidate, metadata, "fallback_point", polygonFailureReason, out feature);

            return false;
        }

        if (exportPoints)
            return TryProjectPoint(candidate, metadata, "representative_point", string.Empty, out feature);

        return false;
    }

    private bool TryProjectPolygon(
        DetectionCandidate candidate,
        DetectionMetadata metadata,
        out ProjectedFeature feature,
        out string failureReason)
    {
        feature = null;
        failureReason = string.Empty;
        if (candidate.polygon == null || candidate.polygon.Count < 3)
        {
            failureReason = "missing_or_too_small_mask_polygon";
            return false;
        }

        var normalizedPixels = new List<Vector2>(candidate.polygon.Count);
        for (int i = 0; i < candidate.polygon.Count; i++)
            normalizedPixels.Add(NormalizePixel(candidate.polygon[i], metadata));

        List<SrtDroneRaycastPlayer.PixelGeoResult> results =
            player.MapPixelsToGeoAtTime(normalizedPixels, true, candidate.timeSeconds, true, 1);

        int validCount = 0;
        var coordinates = new List<GeoCoordinate>();
        for (int i = 0; i < results.Count; i++)
        {
            SrtDroneRaycastPlayer.PixelGeoResult result = results[i];
            if (!result.hit || !IsFinite(result.lon) || !IsFinite(result.lat) || !IsFinite(result.alt))
                continue;

            validCount++;
            var coordinate = new GeoCoordinate(result.lon, result.lat, result.alt);
            if (!ContainsCoordinate(coordinates, coordinate))
                coordinates.Add(coordinate);
        }

        float hitRatio = candidate.polygon.Count > 0 ? (float)validCount / candidate.polygon.Count : 0f;
        if (useConvexHullForPolygons && coordinates.Count >= 3)
            coordinates = BuildConvexHull(coordinates);

        int requiredVertices = Mathf.Max(3, minValidPolygonVertices);
        if (coordinates.Count < requiredVertices)
        {
            failureReason = "too_few_unique_valid_vertices";
            return false;
        }
        if (hitRatio < minPolygonHitRatio)
        {
            failureReason = "hit_ratio_below_threshold";
            return false;
        }

        feature = new ProjectedFeature
        {
            isPolygon = true,
            source = "mask_polygon_projection",
            coordinates = coordinates,
            validVertexCount = validCount,
            hitRatio = hitRatio,
            polygonFailedReason = string.Empty
        };
        return true;
    }

    private bool TryProjectPoint(
        DetectionCandidate candidate,
        DetectionMetadata metadata,
        string source,
        string polygonFailureReason,
        out ProjectedFeature feature)
    {
        feature = null;
        if (!TryGetRepresentativePoint(candidate, out Vector2 pixel))
            return false;

        var pixels = new List<Vector2> { NormalizePixel(pixel, metadata) };
        List<SrtDroneRaycastPlayer.PixelGeoResult> results =
            player.MapPixelsToGeoAtTime(pixels, true, candidate.timeSeconds, false, 1);
        if (results.Count == 0 || !results[0].hit || !IsFinite(results[0].lon) ||
            !IsFinite(results[0].lat) || !IsFinite(results[0].alt))
            return false;

        feature = new ProjectedFeature
        {
            isPolygon = false,
            source = source,
            coordinates = new List<GeoCoordinate>
            {
                new GeoCoordinate(results[0].lon, results[0].lat, results[0].alt)
            },
            validVertexCount = 1,
            hitRatio = 1f,
            polygonFailedReason = polygonFailureReason
        };
        return true;
    }

    private static bool TryGetRepresentativePoint(DetectionCandidate candidate, out Vector2 pixel)
    {
        if (candidate.hasCentroid)
        {
            pixel = candidate.centroid;
            return true;
        }

        if (candidate.bbox != null && candidate.bbox.Length >= 4)
        {
            pixel = new Vector2(
                (candidate.bbox[0] + candidate.bbox[2]) * 0.5f,
                (candidate.bbox[1] + candidate.bbox[3]) * 0.5f);
            return true;
        }

        if (candidate.polygon != null && candidate.polygon.Count > 0)
        {
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < candidate.polygon.Count; i++)
                sum += candidate.polygon[i];
            pixel = sum / candidate.polygon.Count;
            return true;
        }

        pixel = Vector2.zero;
        return false;
    }

    private void WriteFeature(
        JsonTextWriter writer,
        string video,
        DetectionCandidate candidate,
        ProjectedFeature feature)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteValue("Feature");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        WriteProperty(writer, "source", feature.source);
        if (!string.IsNullOrWhiteSpace(candidate.inputGeometryType))
            WriteProperty(writer, "input_geometry_type", candidate.inputGeometryType);
        if (!string.IsNullOrWhiteSpace(candidate.inputGeometrySource))
            WriteProperty(writer, "input_geometry_source", candidate.inputGeometrySource);
        if (!string.IsNullOrWhiteSpace(feature.rawPolygonId))
            WriteProperty(writer, "raw_polygon_id", feature.rawPolygonId);
        if (feature.linkedPolygon)
            WriteProperty(writer, "linked_polygon", true);
        if (!string.IsNullOrWhiteSpace(feature.linkedDetectionId))
            WriteProperty(writer, "linked_detection_id", feature.linkedDetectionId);
        if (!string.IsNullOrWhiteSpace(feature.linkedRawPolygonId))
            WriteProperty(writer, "linked_raw_polygon_id", feature.linkedRawPolygonId);
        if (!string.IsNullOrWhiteSpace(feature.mergedClusterId))
            WriteProperty(writer, "merged_cluster_id", feature.mergedClusterId);
        if (!string.IsNullOrWhiteSpace(feature.pointClusterId))
            WriteProperty(writer, "point_cluster_id", feature.pointClusterId);
        if (feature.nearestClusterDistanceMeters >= 0.0)
            WriteProperty(writer, "nearest_cluster_distance_meters", feature.nearestClusterDistanceMeters);
        if (!string.IsNullOrWhiteSpace(feature.generatedFrom))
            WriteProperty(writer, "generated_from", feature.generatedFrom);
        if (!string.IsNullOrWhiteSpace(feature.clusterLinkType))
            WriteProperty(writer, "cluster_link_type", feature.clusterLinkType);
        if (feature.pointClusterCount > 0)
        {
            WriteProperty(writer, "point_cluster_count", feature.pointClusterCount);
            WriteProperty(writer, "point_cluster_detection_ids", feature.pointClusterDetectionIds ?? string.Empty);
            WriteProperty(writer, "point_cluster_frame_indices", feature.pointClusterFrameIndices ?? string.Empty);
            WriteProperty(writer, "average_confidence", feature.averageConfidence);
            WriteProperty(writer, "max_confidence", feature.maxConfidence);
            WriteProperty(writer, "point_cluster_diameter_meters", feature.pointClusterDiameterMeters);
            if (feature.pointClusterPolygonBufferMeters >= 0.0)
                WriteProperty(writer, "point_cluster_polygon_buffer_meters", feature.pointClusterPolygonBufferMeters);
        }
        if (!string.IsNullOrWhiteSpace(feature.buildingAggregateId))
        {
            WriteProperty(writer, "building_aggregate_id", feature.buildingAggregateId);
            if (feature.buildingAggregateCount > 0)
            {
                WriteProperty(writer, "aggregate_point_count", feature.buildingAggregateCount);
                WriteProperty(writer, "aggregate_detection_ids", feature.buildingAggregateDetectionIds ?? string.Empty);
                WriteProperty(writer, "aggregate_point_cluster_ids", feature.buildingAggregatePointClusterIds ?? string.Empty);
                WriteProperty(writer, "aggregate_frame_indices", feature.buildingAggregateFrameIndices ?? string.Empty);
                WriteProperty(writer, "aggregate_diameter_meters", feature.buildingAggregateDiameterMeters);
                WriteProperty(writer, "building_aggregate_polygon_buffer_meters", feature.buildingAggregatePolygonBufferMeters);
            }
        }
        if (feature.mergedCount > 0)
        {
            WriteProperty(writer, "merged_count", feature.mergedCount);
            WriteProperty(writer, "merged_detection_count", feature.mergedCount);
            WriteProperty(writer, "merged_detection_ids", feature.mergedDetectionIds ?? string.Empty);
            WriteProperty(writer, "merged_frame_indices", feature.mergedFrameIndices ?? string.Empty);
            WriteProperty(writer, "average_confidence", feature.averageConfidence);
            WriteProperty(writer, "max_confidence", feature.maxConfidence);
            WriteProperty(writer, "merge_distance_meters", feature.mergeDistanceMeters);
            if (feature.mergeBoundaryDistanceMeters >= 0.0)
                WriteProperty(writer, "merge_boundary_distance_meters", feature.mergeBoundaryDistanceMeters);
            WriteProperty(writer, "merged_area_m2", feature.mergedAreaMeters);
            WriteProperty(writer, "merged_hull_area_m2", feature.mergedAreaMeters);
            WriteProperty(writer, "merged_area_growth_ratio", feature.mergedAreaGrowthRatio);
            WriteProperty(writer, "merged_hull_area_growth_ratio", feature.mergedAreaGrowthRatio);
            WriteProperty(writer, "merged_cluster_diameter_meters", feature.mergedClusterDiameterMeters);
        }
        WriteProperty(writer, "video", video ?? string.Empty);
        WriteProperty(writer, "frame_index", candidate.frameIndex);
        WriteProperty(writer, "time_s", candidate.timeSeconds);
        WriteProperty(writer, "class_id", candidate.classId);
        WriteProperty(writer, "label", candidate.label);
        WriteProperty(writer, "detection_id", candidate.detectionId);
        WriteProperty(writer, "confidence", candidate.confidence);
        WriteProperty(writer, "mask_area_pixels", candidate.polygonArea);
        WriteProperty(writer, "border_clipped", candidate.borderClipped);
        WriteProperty(writer, "candidate_score", candidate.candidateScore);
        WriteNumberArrayProperty(writer, "bbox", candidate.bbox);
        WriteCentroidProperty(writer, candidate);
        WriteProperty(writer, "original_mask_vertex_count", candidate.originalMaskVertexCount);
        WriteProperty(writer, "exported_vertex_count", feature.coordinates.Count);
        WriteProperty(writer, "valid_vertex_count", feature.validVertexCount);
        WriteProperty(writer, "hit_ratio", feature.hitRatio);
        if (!string.IsNullOrWhiteSpace(feature.polygonFailedReason))
            WriteProperty(writer, "polygon_failed_reason", feature.polygonFailedReason);
        writer.WriteEndObject();

        writer.WritePropertyName("geometry");
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteValue(feature.isPolygon ? "Polygon" : feature.isLineString ? "LineString" : "Point");
        writer.WritePropertyName("coordinates");
        if (feature.isPolygon)
        {
            writer.WriteStartArray();
            writer.WriteStartArray();
            for (int i = 0; i < feature.coordinates.Count; i++)
                WriteCoordinate(writer, feature.coordinates[i]);
            WriteCoordinate(writer, feature.coordinates[0]);
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        else if (feature.isLineString)
        {
            writer.WriteStartArray();
            for (int i = 0; i < feature.coordinates.Count; i++)
                WriteCoordinate(writer, feature.coordinates[i]);
            writer.WriteEndArray();
        }
        else
        {
            WriteCoordinate(writer, feature.coordinates[0]);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private void WriteCoordinate(JsonTextWriter writer, GeoCoordinate coordinate)
    {
        writer.WriteStartArray();
        writer.WriteValue(coordinate.lon);
        writer.WriteValue(coordinate.lat);
        if (includeAltitude)
            writer.WriteValue(coordinate.alt);
        writer.WriteEndArray();
    }

    private static void WriteProperty(JsonTextWriter writer, string name, object value)
    {
        writer.WritePropertyName(name);
        writer.WriteValue(value);
    }

    private static void WriteNumberArrayProperty(JsonTextWriter writer, string name, float[] values)
    {
        writer.WritePropertyName(name);
        if (values == null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartArray();
        for (int i = 0; i < values.Length; i++)
            writer.WriteValue(values[i]);
        writer.WriteEndArray();
    }

    private static void WriteCentroidProperty(JsonTextWriter writer, DetectionCandidate candidate)
    {
        writer.WritePropertyName("centroid");
        if (!candidate.hasCentroid)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartArray();
        writer.WriteValue(candidate.centroid.x);
        writer.WriteValue(candidate.centroid.y);
        writer.WriteEndArray();
    }

    private List<Vector2> SimplifyAndLimitPolygon(List<Vector2> polygon)
    {
        List<Vector2> clean = RemoveConsecutiveDuplicates(polygon);
        if (clean.Count < 3)
            return clean;

        List<Vector2> simplified = simplificationEpsilonPixels > 0f
            ? SimplifyRamerDouglasPeucker(clean, simplificationEpsilonPixels)
            : clean;

        int limit = Mathf.Max(3, maxPolygonVertices);
        if (simplified.Count <= limit)
            return simplified;

        var limited = new List<Vector2>(limit);
        float step = (float)simplified.Count / limit;
        for (int i = 0; i < limit; i++)
            limited.Add(simplified[Mathf.Min(simplified.Count - 1, Mathf.FloorToInt(i * step))]);
        return RemoveConsecutiveDuplicates(limited);
    }

    private static List<Vector2> SimplifyRamerDouglasPeucker(List<Vector2> points, float epsilon)
    {
        if (points.Count < 3)
            return new List<Vector2>(points);

        bool[] keep = new bool[points.Count];
        keep[0] = true;
        keep[points.Count - 1] = true;
        var ranges = new Stack<IndexRange>();
        ranges.Push(new IndexRange(0, points.Count - 1));

        while (ranges.Count > 0)
        {
            IndexRange range = ranges.Pop();
            float maximumDistance = 0f;
            int maximumIndex = -1;
            for (int i = range.start + 1; i < range.end; i++)
            {
                float distance = DistanceToSegment(points[i], points[range.start], points[range.end]);
                if (distance > maximumDistance)
                {
                    maximumDistance = distance;
                    maximumIndex = i;
                }
            }

            if (maximumIndex >= 0 && maximumDistance > epsilon)
            {
                keep[maximumIndex] = true;
                ranges.Push(new IndexRange(range.start, maximumIndex));
                ranges.Push(new IndexRange(maximumIndex, range.end));
            }
        }

        var result = new List<Vector2>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }
        return result;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0.000001f)
            return Vector2.Distance(point, start);
        float t = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
        return Vector2.Distance(point, start + segment * t);
    }

    private static List<Vector2> RemoveConsecutiveDuplicates(List<Vector2> points)
    {
        var result = new List<Vector2>();
        for (int i = 0; i < points.Count; i++)
        {
            if (result.Count == 0 || (result[result.Count - 1] - points[i]).sqrMagnitude > 0.000001f)
                result.Add(points[i]);
        }
        if (result.Count > 1 && (result[0] - result[result.Count - 1]).sqrMagnitude <= 0.000001f)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static List<Vector2> ReadPolygon(JToken token)
    {
        var result = new List<Vector2>();
        if (!(token is JArray polygon))
            return result;

        for (int i = 0; i < polygon.Count; i++)
        {
            if (!(polygon[i] is JArray point) || point.Count < 2)
                continue;
            float x = ReadFloatToken(point[0], float.NaN);
            float y = ReadFloatToken(point[1], float.NaN);
            if (IsFinite(x) && IsFinite(y))
                result.Add(new Vector2(x, y));
        }
        return result;
    }

    private static List<Vector2> ReadGeometryPoints(JToken token)
    {
        return ReadPolygon(token);
    }

    private static bool ReadPointFromGeometryPoints(List<Vector2> points, out Vector2 point)
    {
        if (points != null && points.Count > 0)
        {
            point = points[0];
            return true;
        }

        point = Vector2.zero;
        return false;
    }

    private static bool CalculatePointAverage(List<Vector2> points, out Vector2 average)
    {
        if (points == null || points.Count == 0)
        {
            average = Vector2.zero;
            return false;
        }

        double x = 0.0;
        double y = 0.0;
        for (int i = 0; i < points.Count; i++)
        {
            x += points[i].x;
            y += points[i].y;
        }

        double inverse = 1.0 / points.Count;
        average = new Vector2((float)(x * inverse), (float)(y * inverse));
        return IsFinite(average.x) && IsFinite(average.y);
    }

    private static bool CalculateBBoxCenter(float[] bbox, out Vector2 center)
    {
        if (bbox == null || bbox.Length < 4)
        {
            center = Vector2.zero;
            return false;
        }

        center = new Vector2(
            (bbox[0] + bbox[2]) * 0.5f,
            (bbox[1] + bbox[3]) * 0.5f);
        return IsFinite(center.x) && IsFinite(center.y);
    }

    private static bool IsBorderClipped(float[] bbox, List<Vector2> polygon, DetectionMetadata metadata, float marginPixels)
    {
        if (marginPixels <= 0f || metadata.frameWidth <= 1 || metadata.frameHeight <= 1)
            return false;

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        if (bbox != null && bbox.Length >= 4)
        {
            minX = Mathf.Min(minX, bbox[0]);
            minY = Mathf.Min(minY, bbox[1]);
            maxX = Mathf.Max(maxX, bbox[2]);
            maxY = Mathf.Max(maxY, bbox[3]);
        }

        if (polygon != null)
        {
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 point = polygon[i];
                minX = Mathf.Min(minX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxX = Mathf.Max(maxX, point.x);
                maxY = Mathf.Max(maxY, point.y);
            }
        }

        if (float.IsPositiveInfinity(minX) || float.IsPositiveInfinity(minY) ||
            float.IsNegativeInfinity(maxX) || float.IsNegativeInfinity(maxY))
            return false;

        return minX <= marginPixels ||
               minY <= marginPixels ||
               maxX >= metadata.frameWidth - 1f - marginPixels ||
               maxY >= metadata.frameHeight - 1f - marginPixels;
    }

    private static double CalculateCandidateScore(float confidence, double polygonArea, int vertexCount, bool borderClipped)
    {
        double areaBonus = Math.Log10(Math.Max(1.0, polygonArea)) * 0.05;
        double vertexBonus = Math.Min(0.05, Math.Max(0, vertexCount) * 0.0005);
        double borderPenalty = borderClipped ? 1.0 : 0.0;
        return confidence + areaBonus + vertexBonus - borderPenalty;
    }

    private static List<GeoCoordinate> BuildConvexHull(List<GeoCoordinate> points)
    {
        List<GeoCoordinate> unique = new List<GeoCoordinate>();
        for (int i = 0; i < points.Count; i++)
        {
            if (!ContainsCoordinate(unique, points[i]))
                unique.Add(points[i]);
        }

        if (unique.Count <= 3)
            return unique;

        unique.Sort((a, b) =>
        {
            int lonCompare = a.lon.CompareTo(b.lon);
            return lonCompare != 0 ? lonCompare : a.lat.CompareTo(b.lat);
        });

        List<GeoCoordinate> lower = new List<GeoCoordinate>();
        for (int i = 0; i < unique.Count; i++)
        {
            while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], unique[i]) <= 0.0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(unique[i]);
        }

        List<GeoCoordinate> upper = new List<GeoCoordinate>();
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

    private static double Cross(GeoCoordinate origin, GeoCoordinate a, GeoCoordinate b)
    {
        return (a.lon - origin.lon) * (b.lat - origin.lat) -
               (a.lat - origin.lat) * (b.lon - origin.lon);
    }

    private static float[] ReadNumberArray(JToken token, int expectedCount)
    {
        if (!(token is JArray array) || array.Count < expectedCount)
            return null;
        var values = new float[expectedCount];
        for (int i = 0; i < expectedCount; i++)
        {
            values[i] = ReadFloatToken(array[i], float.NaN);
            if (!IsFinite(values[i]))
                return null;
        }
        return values;
    }

    private static double CalculatePolygonArea(List<Vector2> polygon)
    {
        if (polygon == null || polygon.Count < 3)
            return 0.0;
        double sum = 0.0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % polygon.Count];
            sum += (double)a.x * b.y - (double)b.x * a.y;
        }
        return Math.Abs(sum) * 0.5;
    }

    private static int CompareCandidatesBestFirst(DetectionCandidate a, DetectionCandidate b)
    {
        if (IsBetterCandidate(a, b))
            return -1;
        if (IsBetterCandidate(b, a))
            return 1;
        return a.frameIndex.CompareTo(b.frameIndex);
    }

    private static bool IsBetterCandidate(DetectionCandidate candidate, DetectionCandidate existing)
    {
        int scoreComparison = candidate.candidateScore.CompareTo(existing.candidateScore);
        if (scoreComparison != 0)
            return scoreComparison > 0;

        int confidenceComparison = candidate.confidence.CompareTo(existing.confidence);
        if (confidenceComparison != 0)
            return confidenceComparison > 0;

        int areaComparison = candidate.polygonArea.CompareTo(existing.polygonArea);
        if (areaComparison != 0)
            return areaComparison > 0;

        return candidate.originalMaskVertexCount > existing.originalMaskVertexCount;
    }

    private static string MakeTrackKey(string label, string detectionId)
    {
        return label.ToLowerInvariant() + "\u001f" + detectionId;
    }

    private static string MakeRawPolygonId(DetectionCandidate candidate)
    {
        return $"raw_polygon_{MakeIdPart(candidate.label)}_{MakeIdPart(candidate.detectionId)}_{candidate.frameIndex}";
    }

    private static string MakeIdPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        char[] buffer = new char[value.Length];
        int count = 0;
        bool previousWasSeparator = false;
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (char.IsLetterOrDigit(character))
            {
                buffer[count++] = char.ToLowerInvariant(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && count > 0)
            {
                buffer[count++] = '_';
                previousWasSeparator = true;
            }
        }

        if (count > 0 && buffer[count - 1] == '_')
            count--;
        return count > 0 ? new string(buffer, 0, count) : "unknown";
    }

    private bool HasAnyConfiguredLabel()
    {
        return HasAnyNonEmpty(areaLabels) || HasAnyNonEmpty(pointLabels);
    }

    private static bool HasAnyNonEmpty(string[] labels)
    {
        if (labels == null)
            return false;
        for (int i = 0; i < labels.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(labels[i]))
                return true;
        }
        return false;
    }

    private static bool ContainsLabel(string[] labels, string label)
    {
        if (labels == null || string.IsNullOrWhiteSpace(label))
            return false;
        for (int i = 0; i < labels.Length; i++)
        {
            if (string.Equals(labels[i]?.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static Vector2 NormalizePixel(Vector2 pixel, DetectionMetadata metadata)
    {
        return new Vector2(
            Mathf.Clamp01(pixel.x / Mathf.Max(1f, metadata.frameWidth - 1f)),
            Mathf.Clamp01(pixel.y / Mathf.Max(1f, metadata.frameHeight - 1f)));
    }

    private static bool ContainsCoordinate(List<GeoCoordinate> coordinates, GeoCoordinate candidate)
    {
        const double epsilon = 1e-10;
        for (int i = 0; i < coordinates.Count; i++)
        {
            if (Math.Abs(coordinates[i].lon - candidate.lon) <= epsilon &&
                Math.Abs(coordinates[i].lat - candidate.lat) <= epsilon)
                return true;
        }
        return false;
    }

    private static JsonTextReader CreateJsonReader(TextReader stream)
    {
        return new JsonTextReader(stream)
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double
        };
    }

    private static void SkipContainerValue(JsonTextReader reader)
    {
        if (reader.TokenType == JsonToken.StartArray || reader.TokenType == JsonToken.StartObject)
            reader.Skip();
    }

    private bool TryCheckOutputWritable(string outputPath, out string error)
    {
        error = string.Empty;
        string testPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                error = "Output path is empty.";
                return false;
            }
            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            testPath = outputPath + ".write-test-" + Guid.NewGuid().ToString("N");
            using (File.Create(testPath))
            {
            }
            File.Delete(testPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                if (!string.IsNullOrWhiteSpace(testPath) && File.Exists(testPath))
                    File.Delete(testPath);
            }
            catch
            {
                // Preserve the original validation error.
            }
            return false;
        }
    }

    private static string ResolvePath(string rawPath, FilePathRoot rootMode)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;
        if (Path.IsPathRooted(rawPath))
            return rawPath;
        return Path.Combine(GetPathRoot(rootMode), rawPath.Replace('/', Path.DirectorySeparatorChar));
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
                return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }
    }

    private static int ReadIntValue(object value)
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static int ReadIntToken(JToken token, int fallback)
    {
        if (token == null)
            return fallback;
        try
        {
            return token.Value<int>();
        }
        catch
        {
            return fallback;
        }
    }

    private static float ReadFloatToken(JToken token, float fallback)
    {
        if (token == null)
            return fallback;
        try
        {
            return token.Value<float>();
        }
        catch
        {
            return fallback;
        }
    }

    private static string ReadStringToken(JToken token)
    {
        if (!(token is JValue value) || token.Type == JTokenType.Null)
            return string.Empty;
        return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    [Serializable]
    public enum FilePathRoot
    {
        ProjectRoot,
        StreamingAssets,
        PersistentDataPath,
        DataPath
    }

    private struct DetectionMetadata
    {
        public string video;
        public int frameWidth;
        public int frameHeight;
    }

    private sealed class DetectionCandidate
    {
        public int frameIndex;
        public float timeSeconds;
        public string detectionId;
        public string label;
        public int classId;
        public float confidence;
        public float[] bbox;
        public Vector2 centroid;
        public bool hasCentroid;
        public List<Vector2> polygon;
        public int originalMaskVertexCount;
        public double polygonArea;
        public bool borderClipped;
        public double candidateScore;
        public bool isAreaLabel;
        public string inputGeometryType;
        public string inputGeometrySource;
    }

    private sealed class ProjectedFeature
    {
        public bool isPolygon;
        public bool isLineString;
        public string source;
        public List<GeoCoordinate> coordinates;
        public int validVertexCount;
        public float hitRatio;
        public string polygonFailedReason;
        public bool linkedPolygon;
        public string linkedDetectionId;
        public string linkedRawPolygonId;
        public string rawPolygonId;
        public string mergedClusterId;
        public int mergedCount;
        public string mergedDetectionIds;
        public string mergedFrameIndices;
        public float averageConfidence;
        public float maxConfidence;
        public double mergeDistanceMeters;
        public double mergeBoundaryDistanceMeters = -1.0;
        public double mergedAreaMeters;
        public double mergedAreaGrowthRatio;
        public double mergedClusterDiameterMeters;
        public string pointClusterId;
        public int pointClusterCount;
        public string pointClusterDetectionIds;
        public string pointClusterFrameIndices;
        public double pointClusterDiameterMeters;
        public double pointClusterPolygonBufferMeters = -1.0;
        public double nearestClusterDistanceMeters = -1.0;
        public string generatedFrom;
        public string clusterLinkType;
        public string buildingAggregateId;
        public int buildingAggregateCount;
        public string buildingAggregateDetectionIds;
        public string buildingAggregatePointClusterIds;
        public string buildingAggregateFrameIndices;
        public double buildingAggregateDiameterMeters;
        public double buildingAggregatePolygonBufferMeters = -1.0;
    }

    private sealed class FeatureRecord
    {
        public readonly DetectionCandidate candidate;
        public readonly ProjectedFeature feature;

        public FeatureRecord(DetectionCandidate candidate, ProjectedFeature feature)
        {
            this.candidate = candidate;
            this.feature = feature;
        }
    }

    private sealed class PointClusterSummary
    {
        public readonly string pointClusterId;
        public readonly string label;
        public readonly List<int> pointRecordIndices = new List<int>();
        public GeoCoordinate center;
        public double diameter;

        public PointClusterSummary(string pointClusterId, string label)
        {
            this.pointClusterId = pointClusterId;
            this.label = label;
        }
    }

    private sealed class PointCluster
    {
        public readonly string label;
        public readonly List<int> recordIndices = new List<int>();

        public PointCluster(string label)
        {
            this.label = label;
        }
    }

    private struct LocalBounds2D
    {
        public double minX;
        public double minY;
        public double maxX;
        public double maxY;
    }

    private readonly struct LocalHullPoint
    {
        public readonly double x;
        public readonly double y;
        public readonly double alt;

        public LocalHullPoint(double x, double y, double alt)
        {
            this.x = x;
            this.y = y;
            this.alt = alt;
        }
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

    private readonly struct IndexRange
    {
        public readonly int start;
        public readonly int end;

        public IndexRange(int start, int end)
        {
            this.start = start;
            this.end = end;
        }
    }
}
