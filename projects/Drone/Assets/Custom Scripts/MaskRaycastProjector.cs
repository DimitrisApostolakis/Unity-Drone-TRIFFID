using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Describes one local semantic component whose binary mask belongs to one original video frame.
/// FrameIndex is zero-based, matching the frame_index convention used by the Python preprocessor.
/// </summary>
[Serializable]
public sealed class MaskProjectionRequest
{
    [Tooltip("PNG/JPG mask path. Relative paths use the projector's Mask Path Root.")]
    public string maskPath = string.Empty;

    [Min(0)] public int viewIndex;
    [Min(0)] public int frameIndex;
    public string localDetectionId = string.Empty;
    public string className = "building";
    [Range(0f, 1f)] public float confidence = 1f;
}

[Serializable]
public sealed class MaskViewFrameMapping
{
    [Min(0)] public int viewIndex;
    [Min(0)] public int frameIndex;

    public MaskViewFrameMapping(int viewIndex, int frameIndex)
    {
        this.viewIndex = viewIndex;
        this.frameIndex = frameIndex;
    }
}

/// <summary>
/// Per-mask measurements retained after the most recent projection batch and written to CSV.
/// These are diagnostic measurements only; they do not filter the raw SurfaceHit collection.
/// </summary>
public sealed class MaskProjectionStats
{
    public string MaskPath { get; }
    public int ViewIndex { get; }
    public int FrameIndex { get; }
    public string LocalDetectionId { get; }
    public string ClassName { get; }
    public int MaskWidth { get; }
    public int MaskHeight { get; }
    public int ForegroundPixels { get; }
    public int ErodedForegroundPixels { get; }
    public int CoreSamples { get; }
    public int CoreHits { get; }
    public int BoundarySamples { get; }
    public int BoundaryHits { get; }
    public int Samples => CoreSamples + BoundarySamples;
    public int Hits => CoreHits + BoundaryHits;
    public int Misses => Samples - Hits;
    public float HitRate => Samples > 0 ? (float)Hits / Samples : 0f;
    public int UniqueColliders { get; }
    public int UniqueTriangles { get; }
    public float MinimumHitDistance { get; }
    public float MedianHitDistance { get; }
    public float MeanHitDistance { get; }
    public float MaximumHitDistance { get; }

    public MaskProjectionStats(
        string maskPath,
        MaskProjectionRequest request,
        int maskWidth,
        int maskHeight,
        int foregroundPixels,
        int erodedForegroundPixels,
        int coreSamples,
        int coreHits,
        int boundarySamples,
        int boundaryHits,
        int uniqueColliders,
        int uniqueTriangles,
        float minimumHitDistance,
        float medianHitDistance,
        float meanHitDistance,
        float maximumHitDistance)
    {
        MaskPath = maskPath;
        ViewIndex = request.viewIndex;
        FrameIndex = request.frameIndex;
        LocalDetectionId = request.localDetectionId;
        ClassName = request.className;
        MaskWidth = maskWidth;
        MaskHeight = maskHeight;
        ForegroundPixels = foregroundPixels;
        ErodedForegroundPixels = erodedForegroundPixels;
        CoreSamples = coreSamples;
        CoreHits = coreHits;
        BoundarySamples = boundarySamples;
        BoundaryHits = boundaryHits;
        UniqueColliders = uniqueColliders;
        UniqueTriangles = uniqueTriangles;
        MinimumHitDistance = minimumHitDistance;
        MedianHitDistance = medianHitDistance;
        MeanHitDistance = meanHitDistance;
        MaximumHitDistance = maximumHitDistance;
    }
}

/// <summary>
/// Loads binary semantic masks, samples their eroded boundary and interior, configures the
/// authoritative SRT camera for each original frame, and retains the complete mesh raycast hits.
/// </summary>
public sealed class MaskRaycastProjector : MonoBehaviour
{
    private static readonly Regex BatchMaskFileNameRegex = new Regex(
        @"^(?<class>.+)_view(?<view>\d+)_component(?<component>\d+)\.(?:png|jpe?g)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AggregateMaskFileNameRegex = new Regex(
        @"^view_(?<view>\d+)_frame_(?<frame>\d+)_mask\.(?:png|jpe?g)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Header("Projection Engine")]
    [SerializeField] private SrtDroneRaycastPlayer player;

    [Header("Mask Inputs")]
    [SerializeField] private SrtDroneRaycastPlayer.FilePathRoot maskPathRoot =
        SrtDroneRaycastPlayer.FilePathRoot.ProjectRoot;
    [SerializeField] private List<MaskProjectionRequest> masks = new List<MaskProjectionRequest>();

    [Header("Batch Discovery And Report")]
    [Tooltip("Primary folder containing component or aggregate PNG/JPG masks. Leave empty " +
             "to use the folder of the first configured mask.")]
    [SerializeField] private string batchMaskDirectory = string.Empty;
    [Tooltip("Optional extra mask folders, for example a separate green_trees output folder.")]
    [SerializeField] private List<string> additionalBatchMaskDirectories = new List<string>();
    [Tooltip("Also discover view_XX_frame_XXXXXX_mask files as aggregate class masks.")]
    [SerializeField] private bool discoverAggregateClassMasks = true;
    [Tooltip("Class assigned to aggregate masks whose filenames do not contain a class name.")]
    [SerializeField] private string aggregateMaskClassName = "building";
    [SerializeField] private List<MaskViewFrameMapping> viewFrameMappings =
        new List<MaskViewFrameMapping>
        {
            new MaskViewFrameMapping(0, 0),
            new MaskViewFrameMapping(1, 1440),
            new MaskViewFrameMapping(2, 2250),
            new MaskViewFrameMapping(3, 3300)
        };
    [Tooltip("Write one timestamped CSV beside the masks after every successful projection.")]
    [SerializeField] private bool exportCsvAfterProjection = true;
    [Tooltip("Optional report folder. Leave empty to write beside the discovered masks.")]
    [SerializeField] private string reportOutputDirectory = string.Empty;

    [Header("Provisional Polygon Export")]
    [SerializeField] private string polygonClassFilter = "building";
    [Tooltip("Dominant mode expects one instance per mask. Aggregate mode retains every DBSCAN " +
             "cluster. Multi-view consensus requires overlapping class evidence from distinct views.")]
    [SerializeField] private SurfaceHitClusterAssociationMode polygonClusterAssociationMode =
        SurfaceHitClusterAssociationMode.DominantClusterPerDetection;

    [Header("Multi-View Consensus Clustering")]
    [Tooltip("Metric cell size used to compare projected masks from different views.")]
    [Min(0.05f)] [SerializeField] private float consensusGridCellSizeMeters = 0.5f;
    [Tooltip("Maximum horizontal offset for two views to support the same consensus cell.")]
    [Min(0f)] [SerializeField] private float consensusOverlapToleranceMeters = 1.25f;
    [Tooltip("Distinct views required before a cell can seed a building polygon.")]
    [Min(2)] [SerializeField] private int consensusMinimumSupportingViews = 2;
    [Tooltip("Attach nearby single-view core hits to a confirmed multi-view building core.")]
    [Min(0f)] [SerializeField] private float consensusSingleViewExpansionDistanceMeters = 1.5f;

    [Header("DBSCAN Cluster Modes")]
    [Tooltip("Maximum horizontal distance in metres between neighbouring DBSCAN hits.")]
    [Min(0.01f)] [SerializeField] private float dbscanEpsilonMeters = 2f;
    [Tooltip("Minimum number of neighbouring hits needed to form a dense DBSCAN region.")]
    [Min(3)] [SerializeField] private int dbscanMinimumPoints = 5;
    [Tooltip("Maximum distance in metres for assigning an original-mask boundary hit to a core cluster.")]
    [Min(0.01f)] [SerializeField] private float boundaryAssignmentDistanceMeters = 4f;
    [Tooltip("Metric grid cell size used to trace the polygon boundary.")]
    [Min(0.05f)] [SerializeField] private float polygonGridCellSizeMeters = 0.5f;
    [Tooltip("Radius around each accepted hit used to fill the polygon occupancy grid.")]
    [Min(0.05f)] [SerializeField] private float polygonHitRadiusMeters = 0.75f;
    [Tooltip("Tolerance in metres used to simplify the extracted grid contour.")]
    [Min(0f)] [SerializeField] private float polygonSimplificationMeters = 0.5f;

    [Header("Semantic Conflict Rejection")]
    [Tooltip("Exclude candidate polygons whose core hits overlap strongly with another semantic class.")]
    [SerializeField] private bool excludeSemanticConflicts = true;
    [Tooltip("Comma-separated classes that contradict the polygon class, for example green_trees,tree,vegetation.")]
    [SerializeField] private string semanticConflictClassFilters =
        "green_trees,tree,vegetation";
    [Tooltip("Maximum horizontal distance in metres for a building core hit to match contradictory evidence.")]
    [Min(0.01f)] [SerializeField] private float semanticConflictDistanceMeters = 1.5f;
    [Tooltip("Reject when at least this fraction of building core hits have nearby contradictory hits.")]
    [Range(0f, 1f)] [SerializeField] private float semanticConflictRatioThreshold = 0.65f;
    [Tooltip("Minimum number of conflicting building core hits required before rejection.")]
    [Min(1)] [SerializeField] private int semanticConflictMinimumHits = 5;

    [Header("Source Video Dimensions")]
    [Tooltip("Use zero to infer the width from each mask. Set this when masks were resized.")]
    [Min(0)] [SerializeField] private int sourceFrameWidth;
    [Tooltip("Use zero to infer the height from each mask. Set this when masks were resized.")]
    [Min(0)] [SerializeField] private int sourceFrameHeight;

    [Header("Sparse Sampling")]
    [Range(0f, 1f)] [SerializeField] private float foregroundThreshold = 0.5f;
    [Range(0, 16)] [SerializeField] private int erosionRadiusPixels = 3;
    [Min(1)] [SerializeField] private int boundaryStridePixels = 8;
    [Min(1)] [SerializeField] private int interiorStridePixels = 16;
    [Tooltip("Sampling stride for the original, non-eroded mask contour used by polygon export.")]
    [Min(1)] [SerializeField] private int polygonBoundaryStridePixels = 4;

    [Header("Debug Visualization")]
    [SerializeField] private bool drawHitGizmos = true;
    [Min(1)] [SerializeField] private int maxGizmoHits = 2000;
    [Min(0.001f)] [SerializeField] private float hitGizmoRadius = 0.08f;
    [SerializeField] private Color hitGizmoColor = new Color(0f, 1f, 0.75f, 0.85f);
    [Tooltip("Use -1 to draw hits from every view.")]
    [SerializeField] private int gizmoViewFilter = -1;
    [Tooltip("Exact local detection ID to draw. Leave empty to draw every detection.")]
    [SerializeField] private string gizmoDetectionFilter = string.Empty;
    [SerializeField] private bool colorGizmosByView = true;

    [NonSerialized] private List<SurfaceHit> surfaceHits = new List<SurfaceHit>();
    [NonSerialized] private List<MaskProjectionStats> projectionStats =
        new List<MaskProjectionStats>();
    [NonSerialized] private string lastProjectionReportPath = string.Empty;
    [NonSerialized] private string lastPolygonOutputPath = string.Empty;
    private bool projectionInProgress;

    public IReadOnlyList<SurfaceHit> SurfaceHits => surfaceHits;
    public IReadOnlyList<MaskProjectionStats> ProjectionStats => projectionStats;
    public string LastProjectionReportPath => lastProjectionReportPath;
    public string LastPolygonOutputPath => lastPolygonOutputPath;

    [ContextMenu("Discover Masks From Batch Directory")]
    public void DiscoverMasksFromBatchDirectory()
    {
        if (!TryDiscoverMasksFromBatchDirectory(out string error))
            Debug.LogError($"[MaskRaycastProjector] Mask discovery aborted: {error}", this);
    }

    public bool TryDiscoverMasksFromBatchDirectory(out string error)
    {
        error = string.Empty;
        List<string> resolvedDirectories;
        try
        {
            resolvedDirectories = ResolveBatchMaskDirectories();
        }
        catch (Exception exception)
        {
            return Fail($"Invalid batch mask directory: {exception.Message}", out error);
        }

        if (resolvedDirectories.Count == 0)
            return Fail("No batch mask directory is configured.", out error);
        for (int i = 0; i < resolvedDirectories.Count; i++)
        {
            if (!Directory.Exists(resolvedDirectories[i]))
            {
                return Fail(
                    $"Batch mask directory does not exist: '{resolvedDirectories[i]}'.",
                    out error);
            }
        }

        EnsureDefaultViewFrameMappings();
        if (discoverAggregateClassMasks && string.IsNullOrWhiteSpace(aggregateMaskClassName))
            return Fail("Aggregate mask class name is empty.", out error);
        var framesByView = new Dictionary<int, int>();
        for (int i = 0; i < viewFrameMappings.Count; i++)
        {
            MaskViewFrameMapping mapping = viewFrameMappings[i];
            if (mapping == null)
                return Fail($"View/frame mapping {i} is null.", out error);
            if (mapping.viewIndex < 0 || mapping.frameIndex < 0)
                return Fail($"View/frame mapping {i} contains a negative value.", out error);
            if (framesByView.ContainsKey(mapping.viewIndex))
                return Fail($"View {mapping.viewIndex} has more than one frame mapping.", out error);
            framesByView.Add(mapping.viewIndex, mapping.frameIndex);
        }

        var discovered = new List<DiscoveredMask>();
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ignoredImageCount = 0;
        int unmappedViewCount = 0;
        int aggregateMaskCount = 0;
        int aggregateFrameMismatchCount = 0;
        for (int directoryIndex = 0; directoryIndex < resolvedDirectories.Count; directoryIndex++)
        {
            string[] files = Directory.GetFiles(
                resolvedDirectories[directoryIndex], "*", SearchOption.TopDirectoryOnly);
            for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
            {
                string fileName = Path.GetFileName(files[fileIndex]);
                if (!IsSupportedMaskImage(fileName))
                    continue;

                string className;
                int viewIndex;
                int componentIndex;
                int aggregateFrameIndex = -1;
                bool isAggregateMask = false;
                Match componentMatch = BatchMaskFileNameRegex.Match(fileName);
                if (componentMatch.Success &&
                    int.TryParse(componentMatch.Groups["view"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out viewIndex) &&
                    int.TryParse(componentMatch.Groups["component"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out componentIndex))
                {
                    className = componentMatch.Groups["class"].Value;
                }
                else
                {
                    Match aggregateMatch = AggregateMaskFileNameRegex.Match(fileName);
                    if (!discoverAggregateClassMasks || !aggregateMatch.Success ||
                        !int.TryParse(aggregateMatch.Groups["view"].Value, NumberStyles.None,
                            CultureInfo.InvariantCulture, out viewIndex) ||
                        !int.TryParse(aggregateMatch.Groups["frame"].Value, NumberStyles.None,
                            CultureInfo.InvariantCulture, out aggregateFrameIndex))
                    {
                        ignoredImageCount++;
                        continue;
                    }
                    className = aggregateMaskClassName.Trim();
                    componentIndex = -1;
                    isAggregateMask = true;
                }
                if (!framesByView.TryGetValue(viewIndex, out int frameIndex))
                {
                    unmappedViewCount++;
                    continue;
                }
                if (isAggregateMask && aggregateFrameIndex != frameIndex)
                {
                    aggregateFrameMismatchCount++;
                    continue;
                }

                string fullPath = Path.GetFullPath(files[fileIndex]);
                if (!discoveredPaths.Add(fullPath))
                    continue;
                if (isAggregateMask)
                    aggregateMaskCount++;
                discovered.Add(new DiscoveredMask(
                    fullPath,
                    className,
                    viewIndex,
                    frameIndex,
                    componentIndex,
                    isAggregateMask));
            }
        }

        discovered.Sort(DiscoveredMask.Compare);
        var requests = new List<MaskProjectionRequest>(discovered.Count);
        for (int i = 0; i < discovered.Count; i++)
        {
            DiscoveredMask item = discovered[i];
            requests.Add(new MaskProjectionRequest
            {
                maskPath = item.path.Replace('\\', '/'),
                viewIndex = item.viewIndex,
                frameIndex = item.frameIndex,
                localDetectionId = item.isAggregateMask
                    ? $"{item.className}_view{item.viewIndex:D2}_aggregate"
                    : Path.GetFileNameWithoutExtension(item.path),
                className = item.className,
                confidence = 1f
            });
        }

        if (requests.Count == 0)
        {
            return Fail(
                "No component or aggregate masks matching the configured views were found " +
                "in the selected directories.",
                out error);
        }

#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "Discover projection masks");
#endif
        batchMaskDirectory = resolvedDirectories[0].Replace('\\', '/');
        masks = requests;
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
        Debug.Log(
            $"[MaskRaycastProjector] Discovered {masks.Count} mask(s) from " +
            $"{resolvedDirectories.Count} folder(s), including {aggregateMaskCount} aggregate " +
            $"mask(s). Ignored {ignoredImageCount} unmatched image(s), " +
            $"{unmappedViewCount} mask(s) from unmapped view(s), and " +
            $"{aggregateFrameMismatchCount} aggregate mask(s) whose filename frame did not " +
            "match the configured view mapping.",
            this);
        return true;
    }

    [ContextMenu("Project Configured Masks To Surface")]
    public void ProjectConfiguredMasks()
    {
        if (!TryProjectConfiguredMasks(out string error))
            Debug.LogError($"[MaskRaycastProjector] Projection aborted: {error}", this);
    }

    public bool TryProjectConfiguredMasks(out string error)
    {
        error = string.Empty;
        if (projectionInProgress)
            return Fail("A projection is already running.", out error);
        if (!TryValidateConfiguration(out List<ResolvedMaskRequest> resolvedRequests, out error))
            return false;

        projectionInProgress = true;
        bool hasSavedProjectionState = false;
        SrtDroneRaycastPlayer.ProjectionState savedProjectionState = default;
#if UNITY_EDITOR
        SceneDirtinessState sceneDirtinessState = default;
#endif
        try
        {
            savedProjectionState = player.CaptureProjectionState();
            hasSavedProjectionState = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                sceneDirtinessState = SceneDirtinessState.Capture(player, savedProjectionState.Camera);
#endif

            if (!player.TryPrepareForGeoreferencing(out string preparationError))
                return Fail($"Projection preparation failed: {preparationError}", out error);

            var projectedHits = new List<SurfaceHit>();
            var projectedStats = new List<MaskProjectionStats>();
            var colliderPaths = new Dictionary<int, string>();
            for (int i = 0; i < resolvedRequests.Count; i++)
            {
                if (!TryProjectMask(
                        resolvedRequests[i], projectedHits, colliderPaths,
                        out MaskProjectionStats maskStats, out error))
                    return false;
                projectedStats.Add(maskStats);
            }

            surfaceHits = projectedHits;
            projectionStats = projectedStats;
            Debug.Log(
                $"[MaskRaycastProjector] Retained {surfaceHits.Count} surface hit(s) " +
                $"from {resolvedRequests.Count} mask(s).",
                this);
            if (exportCsvAfterProjection &&
                !TryExportLastProjectionReport(out string reportError))
            {
                Debug.LogWarning(
                    $"[MaskRaycastProjector] Projection succeeded, but the CSV report could " +
                    $"not be written: {reportError}",
                    this);
            }
            return true;
        }
        catch (Exception exception)
        {
            return Fail($"Unexpected projection error: {exception.Message}", out error);
        }
        finally
        {
            if (hasSavedProjectionState && player != null)
            {
                player.RestoreProjectionState(savedProjectionState);
                Physics.SyncTransforms();
            }
#if UNITY_EDITOR
            if (!Application.isPlaying)
                sceneDirtinessState.Restore();
#endif
            projectionInProgress = false;
        }
    }

    public List<SurfaceHit> GetHitsForDetection(string localDetectionId)
    {
        var matches = new List<SurfaceHit>();
        if (string.IsNullOrWhiteSpace(localDetectionId))
            return matches;

        for (int i = 0; i < surfaceHits.Count; i++)
        {
            if (string.Equals(
                    surfaceHits[i].LocalDetectionId, localDetectionId, StringComparison.Ordinal))
                matches.Add(surfaceHits[i]);
        }
        return matches;
    }

    public void ClearSurfaceHits()
    {
        surfaceHits.Clear();
        projectionStats.Clear();
        lastProjectionReportPath = string.Empty;
        lastPolygonOutputPath = string.Empty;
    }

    [ContextMenu("Export Last Projection Report CSV")]
    public void ExportLastProjectionReport()
    {
        if (!TryExportLastProjectionReport(out string error))
            Debug.LogError($"[MaskRaycastProjector] CSV export aborted: {error}", this);
    }

    public bool TryExportLastProjectionReport(out string error)
    {
        error = string.Empty;
        if (projectionStats == null || projectionStats.Count == 0)
            return Fail("There are no projection statistics to export.", out error);

        string outputDirectory;
        try
        {
            outputDirectory = ResolveReportOutputDirectory();
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception)
        {
            return Fail($"Invalid report output directory: {exception.Message}", out error);
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string outputPath = Path.Combine(
            outputDirectory, $"mask_projection_report_{timestamp}.csv");
        var csv = new StringBuilder();
        csv.AppendLine(
            "mask_path,view_index,frame_index,local_detection_id,class_name," +
            "mask_width,mask_height,foreground_pixels,eroded_foreground_pixels," +
            "core_samples,core_hits,boundary_samples,boundary_hits," +
            "samples,hits,misses,hit_rate,unique_colliders,unique_triangles," +
            "min_hit_distance,median_hit_distance,mean_hit_distance,max_hit_distance");
        for (int i = 0; i < projectionStats.Count; i++)
        {
            MaskProjectionStats stats = projectionStats[i];
            csv.Append(CsvEscape(stats.MaskPath)).Append(',')
                .Append(stats.ViewIndex).Append(',')
                .Append(stats.FrameIndex).Append(',')
                .Append(CsvEscape(stats.LocalDetectionId)).Append(',')
                .Append(CsvEscape(stats.ClassName)).Append(',')
                .Append(stats.MaskWidth).Append(',')
                .Append(stats.MaskHeight).Append(',')
                .Append(stats.ForegroundPixels).Append(',')
                .Append(stats.ErodedForegroundPixels).Append(',')
                .Append(stats.CoreSamples).Append(',')
                .Append(stats.CoreHits).Append(',')
                .Append(stats.BoundarySamples).Append(',')
                .Append(stats.BoundaryHits).Append(',')
                .Append(stats.Samples).Append(',')
                .Append(stats.Hits).Append(',')
                .Append(stats.Misses).Append(',')
                .Append(stats.HitRate.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.UniqueColliders).Append(',')
                .Append(stats.UniqueTriangles).Append(',')
                .Append(FiniteFloatOrEmpty(stats.MinimumHitDistance)).Append(',')
                .Append(FiniteFloatOrEmpty(stats.MedianHitDistance)).Append(',')
                .Append(FiniteFloatOrEmpty(stats.MeanHitDistance)).Append(',')
                .Append(FiniteFloatOrEmpty(stats.MaximumHitDistance)).AppendLine();
        }

        try
        {
            File.WriteAllText(outputPath, csv.ToString(), new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            return Fail($"Could not write '{outputPath}': {exception.Message}", out error);
        }

        lastProjectionReportPath = outputPath;
        Debug.Log(
            $"[MaskRaycastProjector] Wrote {projectionStats.Count} mask report row(s) to " +
            $"'{lastProjectionReportPath}'.",
            this);
        return true;
    }

    [ContextMenu("Export One Polygon Per Class Cluster")]
    public void ExportOnePolygonPerAssociatedBuilding()
    {
        if (surfaceHits == null || surfaceHits.Count == 0)
        {
            Debug.LogError(
                "[MaskRaycastProjector] Polygon export aborted: project the configured masks first.",
                this);
            return;
        }

        string outputDirectory;
        try
        {
            outputDirectory = ResolveReportOutputDirectory();
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[MaskRaycastProjector] Polygon export aborted: {exception.Message}",
                this);
            return;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        string safeClassName = SanitizeFileNameToken(polygonClassFilter);
        string outputPath = Path.Combine(
            outputDirectory, $"clustered_{safeClassName}_polygons_{timestamp}.geojson");
        var options = new SurfaceHitPolygonExporter.Options
        {
            ClassFilter = polygonClassFilter,
            DbscanEpsilonMeters = dbscanEpsilonMeters,
            DbscanMinimumPoints = dbscanMinimumPoints,
            BoundaryAssignmentDistanceMeters = boundaryAssignmentDistanceMeters,
            GridCellSizeMeters = polygonGridCellSizeMeters,
            HitRadiusMeters = polygonHitRadiusMeters,
            SimplificationToleranceMeters = polygonSimplificationMeters,
            ClusterAssociationMode = polygonClusterAssociationMode,
            ConsensusGridCellSizeMeters = consensusGridCellSizeMeters,
            ConsensusOverlapToleranceMeters = consensusOverlapToleranceMeters,
            ConsensusMinimumSupportingViews = consensusMinimumSupportingViews,
            ConsensusSingleViewExpansionDistanceMeters =
                consensusSingleViewExpansionDistanceMeters,
            ExcludeSemanticConflicts = excludeSemanticConflicts,
            SemanticConflictClassFilters = semanticConflictClassFilters,
            SemanticConflictDistanceMeters = semanticConflictDistanceMeters,
            SemanticConflictRatioThreshold = semanticConflictRatioThreshold,
            SemanticConflictMinimumHits = semanticConflictMinimumHits
        };
        if (!SurfaceHitPolygonExporter.TryExport(
                surfaceHits,
                player,
                outputPath,
                options,
                out SurfaceHitPolygonExporter.ExportSummary summary,
                out string error))
        {
            Debug.LogError($"[MaskRaycastProjector] Polygon export aborted: {error}", this);
            return;
        }

        lastPolygonOutputPath = outputPath;
        string clusterDescription;
        string omittedDescription;
        switch (polygonClusterAssociationMode)
        {
            case SurfaceHitClusterAssociationMode.AggregateClassMask:
                clusterDescription = "aggregate DBSCAN";
                omittedDescription = "DBSCAN noise";
                break;
            case SurfaceHitClusterAssociationMode.MultiViewConsensus:
                clusterDescription = "multi-view consensus";
                omittedDescription = "unconfirmed";
                break;
            default:
                clusterDescription = "dominant DBSCAN";
                omittedDescription = "DBSCAN noise";
                break;
        }
        string consensusDescription = polygonClusterAssociationMode ==
                                      SurfaceHitClusterAssociationMode.MultiViewConsensus
            ? $" Consensus confirmed {summary.ConsensusConfirmedCellCount} cell(s) across " +
              $"{summary.ConsensusAvailableViewCount} available view(s) and attached " +
              $"{summary.ConsensusExpandedCoreHitCount} nearby single-view core hit(s)."
            : string.Empty;
        Debug.Log(
            $"[MaskRaycastProjector] Exported {summary.ExportedPolygonCount} provisional " +
            $"polygon(s) from {summary.ClusterCount} {clusterDescription} " +
            $"'{polygonClassFilter}' " +
            $"cluster(s) to '{lastPolygonOutputPath}'. Assigned " +
            $"{summary.AssignedBoundaryHitCount}/{summary.BoundaryHitCount} original-mask " +
            $"boundary hit(s), suppressed {summary.SuppressedClusterCount} secondary " +
            $"cluster(s), rejected {summary.RejectedSemanticConflictCount} semantic " +
            $"conflict(s) using {summary.SemanticConflictSourceHitCount} contradictory " +
            $"core hit(s), and omitted {summary.NoiseHitCount} {omittedDescription} core " +
            $"hit(s) plus " +
            $"{summary.OmittedClusterCount} invalid contour(s).{consensusDescription}",
            this);
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawHitGizmos || surfaceHits == null || surfaceHits.Count == 0)
            return;

        int eligibleCount = 0;
        for (int i = 0; i < surfaceHits.Count; i++)
        {
            if (ShouldDrawHit(surfaceHits[i]))
                eligibleCount++;
        }
        if (eligibleCount == 0)
            return;

        int count = Math.Min(eligibleCount, Math.Max(1, maxGizmoHits));
        int step = Math.Max(1, Mathf.CeilToInt((float)eligibleCount / count));
        int eligibleIndex = 0;
        int drawn = 0;
        for (int i = 0; i < surfaceHits.Count && drawn < count; i++)
        {
            SurfaceHit hit = surfaceHits[i];
            if (!ShouldDrawHit(hit))
                continue;
            if (eligibleIndex++ % step != 0)
                continue;

            Gizmos.color = colorGizmosByView
                ? GetViewGizmoColor(hit.ViewIndex)
                : hitGizmoColor;
            Gizmos.DrawSphere(surfaceHits[i].WorldPoint, Math.Max(0.001f, hitGizmoRadius));
            drawn++;
        }
    }

    private bool ShouldDrawHit(SurfaceHit hit)
    {
        if (gizmoViewFilter >= 0 && hit.ViewIndex != gizmoViewFilter)
            return false;
        return string.IsNullOrWhiteSpace(gizmoDetectionFilter) ||
               string.Equals(
                   hit.LocalDetectionId, gizmoDetectionFilter.Trim(),
                   StringComparison.Ordinal);
    }

    private Color GetViewGizmoColor(int viewIndex)
    {
        Color color = Color.HSVToRGB(Mathf.Repeat(viewIndex * 0.217f, 1f), 0.8f, 1f);
        color.a = hitGizmoColor.a;
        return color;
    }

    private bool TryValidateConfiguration(
        out List<ResolvedMaskRequest> resolvedRequests, out string error)
    {
        resolvedRequests = new List<ResolvedMaskRequest>();
        error = string.Empty;

        if (player == null)
            return Fail("SrtDroneRaycastPlayer reference is missing.", out error);
        if (masks == null || masks.Count == 0)
            return Fail("At least one mask request is required.", out error);
        if (sourceFrameWidth < 0 || sourceFrameHeight < 0 ||
            (sourceFrameWidth == 1 || sourceFrameHeight == 1))
            return Fail("Source frame dimensions must be zero (infer) or greater than one.", out error);
        if ((sourceFrameWidth == 0) != (sourceFrameHeight == 0))
            return Fail("Set both source frame dimensions, or leave both at zero.", out error);
        if (boundaryStridePixels < 1 || interiorStridePixels < 1)
            return Fail("Boundary and interior strides must be at least one pixel.", out error);
        if (polygonBoundaryStridePixels < 1)
            return Fail("Polygon boundary stride must be at least one pixel.", out error);
        if (erosionRadiusPixels < 0)
            return Fail("Erosion radius cannot be negative.", out error);
        if (float.IsNaN(foregroundThreshold) || float.IsInfinity(foregroundThreshold) ||
            foregroundThreshold <= 0f || foregroundThreshold > 1f)
            return Fail("Foreground threshold must be greater than zero and at most one.", out error);

        var detectionIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < masks.Count; i++)
        {
            MaskProjectionRequest request = masks[i];
            if (request == null)
                return Fail($"Mask request {i} is null.", out error);
            if (request.viewIndex < 0 || request.frameIndex < 0)
                return Fail($"Mask request {i} has a negative view or frame index.", out error);
            if (string.IsNullOrWhiteSpace(request.localDetectionId))
                return Fail($"Mask request {i} has no local detection ID.", out error);
            if (!detectionIds.Add(request.localDetectionId))
                return Fail($"Local detection ID '{request.localDetectionId}' is duplicated.", out error);
            if (string.IsNullOrWhiteSpace(request.className))
                return Fail($"Mask request {i} has no class name.", out error);
            if (float.IsNaN(request.confidence) || float.IsInfinity(request.confidence))
                return Fail($"Mask request {i} has non-finite confidence.", out error);

            string resolvedPath;
            try
            {
                resolvedPath = ResolvePath(request.maskPath, maskPathRoot);
            }
            catch (Exception exception)
            {
                return Fail($"Mask request {i} has an invalid path: {exception.Message}", out error);
            }
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                return Fail($"Mask request {i} file does not exist: '{resolvedPath}'.", out error);

            resolvedRequests.Add(new ResolvedMaskRequest(request, resolvedPath));
        }
        return true;
    }

    private bool TryProjectMask(
        ResolvedMaskRequest resolvedRequest,
        List<SurfaceHit> output,
        Dictionary<int, string> colliderPaths,
        out MaskProjectionStats stats,
        out string error)
    {
        stats = null;
        error = string.Empty;
        MaskProjectionRequest request = resolvedRequest.request;
        Texture2D maskTexture = null;
        try
        {
            maskTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            byte[] bytes = File.ReadAllBytes(resolvedRequest.path);
            if (!ImageConversion.LoadImage(maskTexture, bytes, false))
                return Fail($"Could not decode mask image '{resolvedRequest.path}'.", out error);

            int projectionWidth = sourceFrameWidth > 1 ? sourceFrameWidth : maskTexture.width;
            int projectionHeight = sourceFrameHeight > 1 ? sourceFrameHeight : maskTexture.height;
            if (!HaveMatchingAspectRatios(
                    maskTexture.width, maskTexture.height, projectionWidth, projectionHeight))
            {
                return Fail(
                    $"Mask '{resolvedRequest.path}' is {maskTexture.width}x{maskTexture.height}, " +
                    $"but the source frame is {projectionWidth}x{projectionHeight}; aspect ratios differ.",
                    out error);
            }

            if (!player.TryConfigureSourceFrameDimensions(
                    projectionWidth, projectionHeight, out string dimensionError))
                return Fail($"Cannot configure source frame dimensions: {dimensionError}", out error);
            if (!player.TryConfigureForFrameCnt(request.frameIndex + 1, out string frameError))
                return Fail(
                    $"Cannot configure original frame_index {request.frameIndex}: {frameError}", out error);

            Camera camera = player.DroneViewCamera;
            if (camera == null)
                return Fail("Drone View Camera is missing after frame configuration.", out error);

            Color32[] pixels = maskTexture.GetPixels32();
            bool[] foreground = BuildForegroundMask(pixels, foregroundThreshold);
            int foregroundCount = CountTrue(foreground);
            if (foregroundCount == 0)
                return Fail(
                    $"Mask '{resolvedRequest.path}' contains no foreground pixels at threshold " +
                    $"{foregroundThreshold:0.###}.",
                    out error);
            bool[] eroded = ErodeMask(
                foreground, maskTexture.width, maskTexture.height, erosionRadiusPixels);
            int erodedForegroundCount = CountTrue(eroded);
            if (erosionRadiusPixels > 0 && erodedForegroundCount == 0)
            {
                Debug.LogWarning(
                    $"[MaskRaycastProjector] {request.localDetectionId}: erosion removed the entire " +
                    "component; sampling the original mask instead.",
                    this);
                eroded = foreground;
                erodedForegroundCount = foregroundCount;
            }
            List<int> coreSampleIndices = CollectSampleIndices(
                eroded,
                maskTexture.width,
                maskTexture.height,
                boundaryStridePixels,
                interiorStridePixels);
            List<int> polygonBoundaryIndices = CollectBoundarySampleIndices(
                foreground,
                maskTexture.width,
                maskTexture.height,
                polygonBoundaryStridePixels);

            var uniqueColliders = new HashSet<int>();
            var uniqueTriangles = new HashSet<long>();
            var hitDistances = new List<float>();
            int coreHitCount = ProjectSampleSet(
                coreSampleIndices,
                maskTexture.width,
                maskTexture.height,
                camera,
                request,
                MaskSampleKind.Core,
                output,
                colliderPaths,
                uniqueColliders,
                uniqueTriangles,
                hitDistances);
            int boundaryHitCount = ProjectSampleSet(
                polygonBoundaryIndices,
                maskTexture.width,
                maskTexture.height,
                camera,
                request,
                MaskSampleKind.Boundary,
                output,
                colliderPaths,
                uniqueColliders,
                uniqueTriangles,
                hitDistances);
            CalculateDistanceSummary(
                hitDistances,
                out float minimumHitDistance,
                out float medianHitDistance,
                out float meanHitDistance,
                out float maximumHitDistance);
            stats = new MaskProjectionStats(
                resolvedRequest.path,
                request,
                maskTexture.width,
                maskTexture.height,
                foregroundCount,
                erodedForegroundCount,
                coreSampleIndices.Count,
                coreHitCount,
                polygonBoundaryIndices.Count,
                boundaryHitCount,
                uniqueColliders.Count,
                uniqueTriangles.Count,
                minimumHitDistance,
                medianHitDistance,
                meanHitDistance,
                maximumHitDistance);
            Debug.Log(
                $"[MaskRaycastProjector] {request.localDetectionId}: " +
                $"{coreHitCount}/{coreSampleIndices.Count} core hit(s), " +
                $"{boundaryHitCount}/{polygonBoundaryIndices.Count} original-boundary hit(s).",
                this);
            return true;
        }
        catch (Exception exception)
        {
            return Fail($"Failed to project mask '{resolvedRequest.path}': {exception.Message}", out error);
        }
        finally
        {
            if (maskTexture != null)
            {
                if (Application.isPlaying)
                    Destroy(maskTexture);
                else
                    DestroyImmediate(maskTexture);
            }
        }
    }

    private int ProjectSampleSet(
        List<int> sampleIndices,
        int maskWidth,
        int maskHeight,
        Camera camera,
        MaskProjectionRequest request,
        MaskSampleKind sampleKind,
        List<SurfaceHit> output,
        Dictionary<int, string> colliderPaths,
        HashSet<int> uniqueColliders,
        HashSet<long> uniqueTriangles,
        List<float> hitDistances)
    {
        int hitCount = 0;
        for (int i = 0; i < sampleIndices.Count; i++)
        {
            int index = sampleIndices[i];
            int textureX = index % maskWidth;
            int textureY = index / maskWidth;
            // GetPixels32 and viewport coordinates both start at the bottom-left, so the
            // sampled texture Y maps directly to viewport Y without an image-space flip.
            float viewportX = (textureX + 0.5f) / maskWidth;
            float viewportY = (textureY + 0.5f) / maskHeight;
            Ray ray = camera.ViewportPointToRay(new Vector3(viewportX, viewportY, 0f));
            if (!player.TryRaycastMap(ray, out RaycastHit raycastHit))
                continue;

            int colliderInstanceId = raycastHit.collider != null
                ? raycastHit.collider.GetInstanceID()
                : 0;
            if (!colliderPaths.TryGetValue(colliderInstanceId, out string colliderPath))
            {
                colliderPath = SurfaceHit.BuildColliderPath(raycastHit.collider);
                colliderPaths.Add(colliderInstanceId, colliderPath);
            }

            uniqueColliders.Add(colliderInstanceId);
            if (raycastHit.triangleIndex >= 0)
            {
                long triangleKey = ((long)colliderInstanceId << 32) ^
                                   (uint)raycastHit.triangleIndex;
                uniqueTriangles.Add(triangleKey);
            }
            hitDistances.Add(raycastHit.distance);
            output.Add(new SurfaceHit(
                raycastHit,
                ray,
                request.viewIndex,
                request.frameIndex,
                request.localDetectionId,
                request.className,
                Mathf.Clamp01(request.confidence),
                sampleKind,
                colliderPath));
            hitCount++;
        }
        return hitCount;
    }

    private static bool[] BuildForegroundMask(Color32[] pixels, float normalizedThreshold)
    {
        if (pixels == null)
            throw new ArgumentNullException(nameof(pixels));
        if (float.IsNaN(normalizedThreshold) || float.IsInfinity(normalizedThreshold) ||
            normalizedThreshold < 0f || normalizedThreshold > 1f)
            throw new ArgumentOutOfRangeException(
                nameof(normalizedThreshold), "Threshold must be between zero and one.");

        int threshold = Mathf.RoundToInt(Mathf.Clamp01(normalizedThreshold) * 255f);
        var foreground = new bool[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 pixel = pixels[i];
            int strongestChannel = Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
            foreground[i] = pixel.a >= threshold && strongestChannel >= threshold;
        }
        return foreground;
    }

    private static int CountTrue(bool[] values)
    {
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
                count++;
        }
        return count;
    }

    private static bool[] ErodeMask(bool[] foreground, int width, int height, int radius)
    {
        ValidateMaskDimensions(foreground, width, height);
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius cannot be negative.");
        if (radius <= 0)
            return (bool[])foreground.Clone();

        var eroded = new bool[foreground.Length];
        if (width <= radius * 2 || height <= radius * 2)
            return eroded;

        int integralWidth = width + 1;
        var integral = new int[integralWidth * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            int rowSum = 0;
            for (int x = 0; x < width; x++)
            {
                if (foreground[y * width + x])
                    rowSum++;
                integral[(y + 1) * integralWidth + x + 1] =
                    integral[y * integralWidth + x + 1] + rowSum;
            }
        }

        int windowSize = radius * 2 + 1;
        int requiredCount = windowSize * windowSize;
        for (int y = radius; y < height - radius; y++)
        {
            int bottom = y - radius;
            int topExclusive = y + radius + 1;
            for (int x = radius; x < width - radius; x++)
            {
                int left = x - radius;
                int rightExclusive = x + radius + 1;
                int count =
                    integral[topExclusive * integralWidth + rightExclusive] -
                    integral[bottom * integralWidth + rightExclusive] -
                    integral[topExclusive * integralWidth + left] +
                    integral[bottom * integralWidth + left];
                eroded[y * width + x] = count == requiredCount;
            }
        }
        return eroded;
    }

    private static List<int> CollectBoundarySampleIndices(
        bool[] mask, int width, int height, int stride)
    {
        ValidateMaskDimensions(mask, width, height);
        if (stride < 1)
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride must be at least one.");

        int cellColumns = (width + stride - 1) / stride;
        int cellRows = (height + stride - 1) / stride;
        var cellSamples = new int[cellColumns * cellRows];
        for (int i = 0; i < cellSamples.Length; i++)
            cellSamples[i] = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (!mask[index] || !IsBoundaryPixel(mask, width, height, x, y))
                    continue;
                int cell = (y / stride) * cellColumns + x / stride;
                if (cellSamples[cell] < 0)
                    cellSamples[cell] = index;
            }
        }

        var samples = new List<int>();
        for (int i = 0; i < cellSamples.Length; i++)
        {
            if (cellSamples[i] >= 0)
                samples.Add(cellSamples[i]);
        }
        return samples;
    }

    private static List<int> CollectSampleIndices(
        bool[] mask, int width, int height, int boundaryStride, int interiorStride)
    {
        ValidateMaskDimensions(mask, width, height);
        if (boundaryStride < 1)
            throw new ArgumentOutOfRangeException(
                nameof(boundaryStride), "Boundary stride must be at least one.");
        if (interiorStride < 1)
            throw new ArgumentOutOfRangeException(
                nameof(interiorStride), "Interior stride must be at least one.");

        var boundary = new bool[mask.Length];
        int cellColumns = (width + boundaryStride - 1) / boundaryStride;
        int cellRows = (height + boundaryStride - 1) / boundaryStride;
        var boundaryCellSamples = new int[cellColumns * cellRows];
        for (int i = 0; i < boundaryCellSamples.Length; i++)
            boundaryCellSamples[i] = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (!mask[index] || !IsBoundaryPixel(mask, width, height, x, y))
                    continue;

                boundary[index] = true;
                int cell = (y / boundaryStride) * cellColumns + x / boundaryStride;
                if (boundaryCellSamples[cell] < 0)
                    boundaryCellSamples[cell] = index;
            }
        }

        var samples = new List<int>();
        for (int i = 0; i < boundaryCellSamples.Length; i++)
        {
            if (boundaryCellSamples[i] >= 0)
                samples.Add(boundaryCellSamples[i]);
        }

        int interiorOffset = interiorStride / 2;
        for (int y = interiorOffset; y < height; y += interiorStride)
        {
            for (int x = interiorOffset; x < width; x += interiorStride)
            {
                int index = y * width + x;
                if (mask[index] && !boundary[index])
                    samples.Add(index);
            }
        }
        return samples;
    }

    private static bool IsBoundaryPixel(bool[] mask, int width, int height, int x, int y)
    {
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetY == 0)
                    continue;
                int neighborX = x + offsetX;
                int neighborY = y + offsetY;
                if (neighborX < 0 || neighborX >= width ||
                    neighborY < 0 || neighborY >= height ||
                    !mask[neighborY * width + neighborX])
                    return true;
            }
        }
        return false;
    }

    private static void ValidateMaskDimensions(bool[] mask, int width, int height)
    {
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));
        if (width < 1)
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height < 1)
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        if ((long)width * height != mask.Length)
            throw new ArgumentException(
                "Mask length must equal width multiplied by height.", nameof(mask));
    }

    private static bool HaveMatchingAspectRatios(
        int leftWidth, int leftHeight, int rightWidth, int rightHeight)
    {
        double left = (double)leftWidth / leftHeight;
        double right = (double)rightWidth / rightHeight;
        return Math.Abs(left - right) <= 0.001;
    }

    private void EnsureDefaultViewFrameMappings()
    {
        if (viewFrameMappings != null && viewFrameMappings.Count > 0)
            return;

        viewFrameMappings = new List<MaskViewFrameMapping>
        {
            new MaskViewFrameMapping(0, 0),
            new MaskViewFrameMapping(1, 1440),
            new MaskViewFrameMapping(2, 2250),
            new MaskViewFrameMapping(3, 3300)
        };
    }

    private string ResolveBatchMaskDirectory()
    {
        if (!string.IsNullOrWhiteSpace(batchMaskDirectory))
            return ResolvePath(batchMaskDirectory, maskPathRoot);

        if (masks != null && masks.Count > 0 && masks[0] != null &&
            !string.IsNullOrWhiteSpace(masks[0].maskPath))
        {
            string firstMaskPath = ResolvePath(masks[0].maskPath, maskPathRoot);
            return Path.GetDirectoryName(firstMaskPath);
        }

        return string.Empty;
    }

    private List<string> ResolveBatchMaskDirectories()
    {
        var directories = new List<string>();
        var uniqueDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string primaryDirectory = ResolveBatchMaskDirectory();
        if (!string.IsNullOrWhiteSpace(primaryDirectory))
        {
            primaryDirectory = Path.GetFullPath(primaryDirectory);
            if (uniqueDirectories.Add(primaryDirectory))
                directories.Add(primaryDirectory);
        }

        if (additionalBatchMaskDirectories == null)
            return directories;
        for (int i = 0; i < additionalBatchMaskDirectories.Count; i++)
        {
            string rawDirectory = additionalBatchMaskDirectories[i];
            if (string.IsNullOrWhiteSpace(rawDirectory))
                continue;
            string resolvedDirectory = ResolvePath(rawDirectory, maskPathRoot);
            if (uniqueDirectories.Add(resolvedDirectory))
                directories.Add(resolvedDirectory);
        }
        return directories;
    }

    private string ResolveReportOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(reportOutputDirectory))
            return ResolvePath(reportOutputDirectory, maskPathRoot);

        string batchDirectory = ResolveBatchMaskDirectory();
        if (!string.IsNullOrWhiteSpace(batchDirectory))
            return batchDirectory;

        if (projectionStats != null && projectionStats.Count > 0)
            return Path.GetDirectoryName(projectionStats[0].MaskPath);

        return string.Empty;
    }

    private static void CalculateDistanceSummary(
        List<float> distances,
        out float minimum,
        out float median,
        out float mean,
        out float maximum)
    {
        if (distances == null || distances.Count == 0)
        {
            minimum = median = mean = maximum = float.NaN;
            return;
        }

        distances.Sort();
        minimum = distances[0];
        maximum = distances[distances.Count - 1];
        double sum = 0.0;
        for (int i = 0; i < distances.Count; i++)
            sum += distances[i];
        mean = (float)(sum / distances.Count);
        int middle = distances.Count / 2;
        median = distances.Count % 2 == 1
            ? distances[middle]
            : (distances[middle - 1] + distances[middle]) * 0.5f;
    }

    private static string CsvEscape(string value)
    {
        string safe = value ?? string.Empty;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }

    private static string FiniteFloatOrEmpty(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? string.Empty
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string SanitizeFileNameToken(string value)
    {
        string safe = string.IsNullOrWhiteSpace(value) ? "objects" : value.Trim();
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        for (int i = 0; i < invalidCharacters.Length; i++)
            safe = safe.Replace(invalidCharacters[i], '_');
        return safe.Replace(' ', '_').ToLowerInvariant();
    }

    private static string ResolvePath(
        string rawPath, SrtDroneRaycastPlayer.FilePathRoot rootMode)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;
        if (Path.IsPathRooted(rawPath))
            return Path.GetFullPath(rawPath);
        return Path.GetFullPath(Path.Combine(
            SrtDroneRaycastPlayer.GetPathRoot(rootMode),
            rawPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool IsSupportedMaskImage(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private readonly struct ResolvedMaskRequest
    {
        public readonly MaskProjectionRequest request;
        public readonly string path;

        public ResolvedMaskRequest(MaskProjectionRequest request, string path)
        {
            this.request = request;
            this.path = path;
        }
    }

    private readonly struct DiscoveredMask
    {
        public readonly string path;
        public readonly string className;
        public readonly int viewIndex;
        public readonly int frameIndex;
        public readonly int componentIndex;
        public readonly bool isAggregateMask;

        public DiscoveredMask(
            string path,
            string className,
            int viewIndex,
            int frameIndex,
            int componentIndex,
            bool isAggregateMask)
        {
            this.path = path;
            this.className = className;
            this.viewIndex = viewIndex;
            this.frameIndex = frameIndex;
            this.componentIndex = componentIndex;
            this.isAggregateMask = isAggregateMask;
        }

        public static int Compare(DiscoveredMask left, DiscoveredMask right)
        {
            int viewComparison = left.viewIndex.CompareTo(right.viewIndex);
            if (viewComparison != 0)
                return viewComparison;
            int classComparison = string.Compare(
                left.className, right.className, StringComparison.Ordinal);
            if (classComparison != 0)
                return classComparison;
            int componentComparison = left.componentIndex.CompareTo(right.componentIndex);
            if (componentComparison != 0)
                return componentComparison;
            return string.Compare(left.path, right.path, StringComparison.Ordinal);
        }
    }

#if UNITY_EDITOR
    private readonly struct SceneDirtinessState
    {
        private readonly UnityEngine.SceneManagement.Scene playerScene;
        private readonly UnityEngine.SceneManagement.Scene cameraScene;
        private readonly bool playerSceneWasDirty;
        private readonly bool cameraSceneWasDirty;
        private readonly bool hasPlayerScene;
        private readonly bool hasSeparateCameraScene;

        private SceneDirtinessState(
            UnityEngine.SceneManagement.Scene playerScene,
            UnityEngine.SceneManagement.Scene cameraScene,
            bool playerSceneWasDirty,
            bool cameraSceneWasDirty,
            bool hasPlayerScene,
            bool hasSeparateCameraScene)
        {
            this.playerScene = playerScene;
            this.cameraScene = cameraScene;
            this.playerSceneWasDirty = playerSceneWasDirty;
            this.cameraSceneWasDirty = cameraSceneWasDirty;
            this.hasPlayerScene = hasPlayerScene;
            this.hasSeparateCameraScene = hasSeparateCameraScene;
        }

        public static SceneDirtinessState Capture(SrtDroneRaycastPlayer player, Camera camera)
        {
            UnityEngine.SceneManagement.Scene playerScene = player.gameObject.scene;
            bool hasPlayerScene = playerScene.IsValid();
            UnityEngine.SceneManagement.Scene cameraScene = camera != null
                ? camera.gameObject.scene
                : default;
            bool hasSeparateCameraScene = cameraScene.IsValid() &&
                                          (!hasPlayerScene || cameraScene.handle != playerScene.handle);
            return new SceneDirtinessState(
                playerScene,
                cameraScene,
                hasPlayerScene && playerScene.isDirty,
                hasSeparateCameraScene && cameraScene.isDirty,
                hasPlayerScene,
                hasSeparateCameraScene);
        }

        public void Restore()
        {
            if (hasPlayerScene && !playerSceneWasDirty && playerScene.isDirty)
                ClearSceneDirtiness(playerScene);
            if (hasSeparateCameraScene && !cameraSceneWasDirty && cameraScene.isDirty)
                ClearSceneDirtiness(cameraScene);
        }

        private static void ClearSceneDirtiness(UnityEngine.SceneManagement.Scene scene)
        {
            try
            {
                System.Reflection.MethodInfo method =
                    typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod(
                        "ClearSceneDirtiness",
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                if (method != null)
                    method.Invoke(null, new object[] { scene });
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[MaskRaycastProjector] Could not restore scene dirtiness state: {exception.Message}");
            }
        }
    }
#endif
}
