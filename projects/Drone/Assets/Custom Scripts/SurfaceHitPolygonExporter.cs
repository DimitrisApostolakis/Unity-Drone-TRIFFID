using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public enum SurfaceHitClusterAssociationMode
{
    DominantClusterPerDetection = 0,
    AggregateClassMask = 1,
    MultiViewConsensus = 2
}

/// <summary>
/// Uses reliable eroded-mask hits to find object instances, then assigns raycasts from the
/// original mask boundary to those instances and traces one metric occupancy contour per cluster.
/// Raw SurfaceHits are never modified or discarded by this exporter.
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
        public float BoundaryAssignmentDistanceMeters = 4f;
        public float GridCellSizeMeters = 0.5f;
        public float HitRadiusMeters = 0.75f;
        public float SimplificationToleranceMeters = 0.5f;
        public SurfaceHitClusterAssociationMode ClusterAssociationMode =
            SurfaceHitClusterAssociationMode.DominantClusterPerDetection;
        public float ConsensusGridCellSizeMeters = 0.5f;
        public float ConsensusOverlapToleranceMeters = 1.25f;
        public int ConsensusMinimumSupportingViews = 2;
        public float ConsensusSingleViewExpansionDistanceMeters = 1.5f;
        public bool ConsensusSemanticCarvingEnabled = true;
        public float ConsensusSemanticVetoDistanceMeters = 1f;
        public int ConsensusSemanticVetoMinimumViews = 2;
        public float ConsensusFragmentMergeDistanceMeters = 1f;
        public int ConsensusFragmentMinimumSharedViews = 1;
        public bool ConsensusBlockMergeAcrossSemanticVeto = true;
        public bool ExcludeSemanticConflicts = true;
        public string SemanticConflictClassFilters = "green_trees,tree,vegetation";
        public float SemanticConflictDistanceMeters = 1.5f;
        public float SemanticConflictRatioThreshold = 0.65f;
        public int SemanticConflictMinimumHits = 5;
    }

    public readonly struct ExportSummary
    {
        public readonly int InputHitCount;
        public readonly int GeoreferencedHitCount;
        public readonly int ClusterCount;
        public readonly int NoiseHitCount;
        public readonly int BoundaryHitCount;
        public readonly int AssignedBoundaryHitCount;
        public readonly int SuppressedClusterCount;
        public readonly int SemanticConflictSourceHitCount;
        public readonly int RejectedSemanticConflictCount;
        public readonly int ConsensusAvailableViewCount;
        public readonly int ConsensusConfirmedCellCount;
        public readonly int ConsensusExpandedCoreHitCount;
        public readonly int ConsensusSemanticVetoCellCount;
        public readonly int ConsensusCarvedConfirmedCellCount;
        public readonly int ConsensusCarvedCoreHitCount;
        public readonly int ConsensusCarvedBoundaryHitCount;
        public readonly int ConsensusProvisionalClusterCount;
        public readonly int ConsensusFragmentMergeCount;
        public readonly int ExportedPolygonCount;
        public readonly int OmittedClusterCount;

        public ExportSummary(
            int inputHitCount,
            int georeferencedHitCount,
            int clusterCount,
            int noiseHitCount,
            int boundaryHitCount,
            int assignedBoundaryHitCount,
            int suppressedClusterCount,
            int semanticConflictSourceHitCount,
            int rejectedSemanticConflictCount,
            int consensusAvailableViewCount,
            int consensusConfirmedCellCount,
            int consensusExpandedCoreHitCount,
            int consensusSemanticVetoCellCount,
            int consensusCarvedConfirmedCellCount,
            int consensusCarvedCoreHitCount,
            int consensusCarvedBoundaryHitCount,
            int consensusProvisionalClusterCount,
            int consensusFragmentMergeCount,
            int exportedPolygonCount,
            int omittedClusterCount)
        {
            InputHitCount = inputHitCount;
            GeoreferencedHitCount = georeferencedHitCount;
            ClusterCount = clusterCount;
            NoiseHitCount = noiseHitCount;
            BoundaryHitCount = boundaryHitCount;
            AssignedBoundaryHitCount = assignedBoundaryHitCount;
            SuppressedClusterCount = suppressedClusterCount;
            SemanticConflictSourceHitCount = semanticConflictSourceHitCount;
            RejectedSemanticConflictCount = rejectedSemanticConflictCount;
            ConsensusAvailableViewCount = consensusAvailableViewCount;
            ConsensusConfirmedCellCount = consensusConfirmedCellCount;
            ConsensusExpandedCoreHitCount = consensusExpandedCoreHitCount;
            ConsensusSemanticVetoCellCount = consensusSemanticVetoCellCount;
            ConsensusCarvedConfirmedCellCount = consensusCarvedConfirmedCellCount;
            ConsensusCarvedCoreHitCount = consensusCarvedCoreHitCount;
            ConsensusCarvedBoundaryHitCount = consensusCarvedBoundaryHitCount;
            ConsensusProvisionalClusterCount = consensusProvisionalClusterCount;
            ConsensusFragmentMergeCount = consensusFragmentMergeCount;
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
        if (!TryValidateInputs(hits, player, outputPath, options, out error))
            return false;

        int inputHitCount = CountClassHits(hits, options.ClassFilter);
        if (inputHitCount == 0)
            return Fail($"No surface hits have class '{options.ClassFilter}'.", out error);

        BuildClusterPoints(
            hits,
            options.ClassFilter,
            player,
            out List<ClusterPoint> corePoints,
            out List<ClusterPoint> boundaryPoints,
            out int failedGeoreferenceCount);
        if (corePoints.Count == 0)
        {
            return Fail(
                $"No eroded-mask core hits with class '{options.ClassFilter}' could be " +
                "converted to WGS84.",
                out error);
        }

        MetricReference metricReference = ProjectToLocalMetricPlane(corePoints, boundaryPoints);
        HashSet<string> semanticConflictClasses = ParseClassFilters(
            options.SemanticConflictClassFilters);
        semanticConflictClasses.Remove(options.ClassFilter);
        List<ClusterPoint> semanticConflictPoints = BuildSemanticConflictPoints(
            hits,
            semanticConflictClasses,
            player,
            metricReference,
            out int failedSemanticConflictGeoreferenceCount);
        Dictionary<GridKey, List<int>> semanticConflictGrid =
            semanticConflictPoints.Count > 0
                ? BuildSpatialGrid(
                    semanticConflictPoints,
                    options.SemanticConflictDistanceMeters,
                    null)
                : new Dictionary<GridKey, List<int>>();
        List<List<ClusterPoint>> coreClusters;
        bool[] retainedCorePoints;
        int suppressedClusterCount;
        List<List<ClusterPoint>> boundaryClusters;
        int assignedBoundaryHitCount;
        string clusteringMethod;
        int[] labels;
        int rawClusterCount;
        int noiseHitCount;
        int consensusAvailableViewCount = 0;
        int consensusConfirmedCellCount = 0;
        int consensusExpandedCoreHitCount = 0;
        int consensusSemanticVetoCellCount = 0;
        int consensusCarvedConfirmedCellCount = 0;
        int consensusCarvedCoreHitCount = 0;
        int consensusCarvedBoundaryHitCount = 0;
        int consensusProvisionalClusterCount = 0;
        int consensusFragmentMergeCount = 0;
        HashSet<GridKey> consensusSemanticVetoCells = null;
        List<ClusterConsensusStats> consensusStats = null;
        if (options.ClusterAssociationMode ==
            SurfaceHitClusterAssociationMode.MultiViewConsensus)
        {
            BuildMultiViewConsensusClusters(
                corePoints,
                semanticConflictPoints,
                options,
                out coreClusters,
                out labels,
                out retainedCorePoints,
                out consensusStats,
                out consensusAvailableViewCount,
                out consensusConfirmedCellCount,
                out consensusExpandedCoreHitCount,
                out consensusSemanticVetoCells,
                out consensusSemanticVetoCellCount,
                out consensusCarvedConfirmedCellCount,
                out consensusCarvedCoreHitCount,
                out consensusProvisionalClusterCount,
                out consensusFragmentMergeCount,
                out noiseHitCount);
            rawClusterCount = coreClusters.Count;
            if (rawClusterCount == 0)
            {
                return Fail(
                    $"Multi-view consensus found no cells supported by at least " +
                    $"{options.ConsensusMinimumSupportingViews} distinct views. " +
                    $"Available building views: {consensusAvailableViewCount}. Reduce the " +
                    "minimum supporting views or increase overlap tolerance.",
                    out error);
            }
            suppressedClusterCount = 0;
            boundaryClusters = AssignBoundaryPointsToNearestCluster(
                boundaryPoints,
                corePoints,
                labels,
                retainedCorePoints,
                coreClusters,
                options.BoundaryAssignmentDistanceMeters,
                consensusSemanticVetoCells,
                options.ConsensusGridCellSizeMeters,
                options.ConsensusSemanticVetoDistanceMeters,
                out assignedBoundaryHitCount,
                out consensusCarvedBoundaryHitCount);
            clusteringMethod =
                "multi_view_consensus_semantic_carving_connected_components_fragment_merge";
        }
        else
        {
            labels = RunDbscan(
                corePoints,
                options.DbscanEpsilonMeters,
                options.DbscanMinimumPoints,
                out rawClusterCount,
                out noiseHitCount);
            if (rawClusterCount == 0)
            {
                return Fail(
                    "DBSCAN did not find any core cluster. Reduce DBSCAN minimum points or " +
                    "increase epsilon.",
                    out error);
            }
            if (options.ClusterAssociationMode ==
                SurfaceHitClusterAssociationMode.AggregateClassMask)
            {
                BuildAllCoreClusters(
                    corePoints,
                    labels,
                    rawClusterCount,
                    out coreClusters,
                    out retainedCorePoints);
                suppressedClusterCount = 0;
                boundaryClusters = AssignBoundaryPointsToNearestCluster(
                    boundaryPoints,
                    corePoints,
                    labels,
                    retainedCorePoints,
                    coreClusters,
                    options.BoundaryAssignmentDistanceMeters,
                    out assignedBoundaryHitCount);
                clusteringMethod = "dbscan_all_class_hits_nearest_boundary_cluster";
            }
            else if (options.ClusterAssociationMode ==
                     SurfaceHitClusterAssociationMode.DominantClusterPerDetection)
            {
                Dictionary<string, int> dominantClusterByDetection =
                    FindDominantClustersByDetection(corePoints, labels);
                BuildDominantCoreClusters(
                    corePoints,
                    labels,
                    rawClusterCount,
                    dominantClusterByDetection,
                    out coreClusters,
                    out retainedCorePoints,
                    out suppressedClusterCount);
                boundaryClusters = AssignBoundaryPoints(
                    boundaryPoints,
                    corePoints,
                    labels,
                    retainedCorePoints,
                    coreClusters,
                    dominantClusterByDetection,
                    options.BoundaryAssignmentDistanceMeters,
                    out assignedBoundaryHitCount);
                clusteringMethod = "dbscan_core_hits_dominant_detection_cluster";
            }
            else
            {
                return Fail(
                    $"Unsupported cluster association mode: {options.ClusterAssociationMode}.",
                    out error);
            }
        }

        var features = new JArray();
        var rejectedSemanticConflicts = new JArray();
        int omittedCount = 0;
        int activeClusterCount = 0;
        for (int rawClusterIndex = 0; rawClusterIndex < coreClusters.Count; rawClusterIndex++)
        {
            if (coreClusters[rawClusterIndex].Count == 0)
                continue;
            activeClusterCount++;

            SemanticConflictAssessment conflict = AssessSemanticConflict(
                coreClusters[rawClusterIndex],
                semanticConflictPoints,
                semanticConflictGrid,
                options);
            if (conflict.ShouldReject)
            {
                rejectedSemanticConflicts.Add(BuildSemanticConflictDiagnostic(
                    coreClusters[rawClusterIndex],
                    boundaryClusters[rawClusterIndex],
                    rawClusterIndex,
                    options.ClassFilter,
                    conflict));
                continue;
            }

            if (!TryBuildFeature(
                    coreClusters[rawClusterIndex],
                    boundaryClusters[rawClusterIndex],
                    rawClusterIndex,
                    features.Count,
                    metricReference,
                    options,
                    clusteringMethod,
                    consensusStats != null ? consensusStats[rawClusterIndex] : null,
                    conflict,
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
            ["cluster_association_mode"] = options.ClusterAssociationMode.ToString(),
            ["clustering_method"] = clusteringMethod,
            ["polygon_method"] = "original_mask_boundary_metric_occupancy_contour",
            ["dbscan_epsilon_m"] = options.DbscanEpsilonMeters,
            ["dbscan_minimum_points"] = options.DbscanMinimumPoints,
            ["consensus_grid_cell_size_m"] = options.ConsensusGridCellSizeMeters,
            ["consensus_overlap_tolerance_m"] = options.ConsensusOverlapToleranceMeters,
            ["consensus_minimum_supporting_views"] =
                options.ConsensusMinimumSupportingViews,
            ["consensus_single_view_expansion_distance_m"] =
                options.ConsensusSingleViewExpansionDistanceMeters,
            ["consensus_available_view_count"] = consensusAvailableViewCount,
            ["consensus_confirmed_cell_count"] = consensusConfirmedCellCount,
            ["consensus_expanded_core_hit_count"] = consensusExpandedCoreHitCount,
            ["consensus_semantic_carving_enabled"] =
                options.ConsensusSemanticCarvingEnabled,
            ["consensus_semantic_veto_distance_m"] =
                options.ConsensusSemanticVetoDistanceMeters,
            ["consensus_semantic_veto_minimum_views"] =
                options.ConsensusSemanticVetoMinimumViews,
            ["consensus_semantic_veto_cell_count"] = consensusSemanticVetoCellCount,
            ["consensus_carved_confirmed_cell_count"] =
                consensusCarvedConfirmedCellCount,
            ["consensus_carved_core_hit_count"] = consensusCarvedCoreHitCount,
            ["consensus_carved_boundary_hit_count"] = consensusCarvedBoundaryHitCount,
            ["consensus_fragment_merge_distance_m"] =
                options.ConsensusFragmentMergeDistanceMeters,
            ["consensus_fragment_minimum_shared_views"] =
                options.ConsensusFragmentMinimumSharedViews,
            ["consensus_block_merge_across_semantic_veto"] =
                options.ConsensusBlockMergeAcrossSemanticVeto,
            ["consensus_provisional_cluster_count"] = consensusProvisionalClusterCount,
            ["consensus_fragment_merge_count"] = consensusFragmentMergeCount,
            ["consensus_unassigned_core_hit_count"] =
                options.ClusterAssociationMode ==
                SurfaceHitClusterAssociationMode.MultiViewConsensus
                    ? noiseHitCount
                    : 0,
            ["boundary_assignment_distance_m"] = options.BoundaryAssignmentDistanceMeters,
            ["grid_cell_size_m"] = options.GridCellSizeMeters,
            ["hit_radius_m"] = options.HitRadiusMeters,
            ["simplification_tolerance_m"] = options.SimplificationToleranceMeters,
            ["semantic_conflict_filter_enabled"] = options.ExcludeSemanticConflicts,
            ["semantic_conflict_classes"] = SortedStringArray(semanticConflictClasses),
            ["semantic_conflict_distance_m"] = options.SemanticConflictDistanceMeters,
            ["semantic_conflict_ratio_threshold"] = options.SemanticConflictRatioThreshold,
            ["semantic_conflict_minimum_hits"] = options.SemanticConflictMinimumHits,
            ["semantic_conflict_source_hit_count"] = semanticConflictPoints.Count,
            ["failed_semantic_conflict_georeference_hit_count"] =
                failedSemanticConflictGeoreferenceCount,
            ["rejected_semantic_conflict_count"] = rejectedSemanticConflicts.Count,
            ["rejected_semantic_conflicts"] = rejectedSemanticConflicts,
            ["input_hit_count"] = inputHitCount,
            ["core_hit_count"] = corePoints.Count,
            ["boundary_hit_count"] = boundaryPoints.Count,
            ["assigned_boundary_hit_count"] = assignedBoundaryHitCount,
            ["failed_georeference_hit_count"] = failedGeoreferenceCount,
            ["raw_dbscan_cluster_count"] =
                options.ClusterAssociationMode ==
                SurfaceHitClusterAssociationMode.MultiViewConsensus
                    ? 0
                    : rawClusterCount,
            ["raw_cluster_count"] = rawClusterCount,
            ["active_cluster_count"] = activeClusterCount,
            ["suppressed_secondary_cluster_count"] = suppressedClusterCount,
            ["noise_core_hit_count"] = noiseHitCount,
            ["unassigned_core_hit_count"] = noiseHitCount,
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
        if (!TryWriteGeoJson(outputPath, rootObject, out error))
            return false;

        summary = new ExportSummary(
            inputHitCount,
            corePoints.Count + boundaryPoints.Count,
            activeClusterCount,
            noiseHitCount,
            boundaryPoints.Count,
            assignedBoundaryHitCount,
            suppressedClusterCount,
            semanticConflictPoints.Count,
            rejectedSemanticConflicts.Count,
            consensusAvailableViewCount,
            consensusConfirmedCellCount,
            consensusExpandedCoreHitCount,
            consensusSemanticVetoCellCount,
            consensusCarvedConfirmedCellCount,
            consensusCarvedCoreHitCount,
            consensusCarvedBoundaryHitCount,
            consensusProvisionalClusterCount,
            consensusFragmentMergeCount,
            features.Count,
            omittedCount);
        return true;
    }

    private static bool TryValidateInputs(
        IReadOnlyList<SurfaceHit> hits,
        SrtDroneRaycastPlayer player,
        string outputPath,
        Options options,
        out string error)
    {
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
        if (options.BoundaryAssignmentDistanceMeters <= 0f)
            return Fail("Boundary assignment distance must be greater than zero metres.", out error);
        if (options.GridCellSizeMeters <= 0f)
            return Fail("Polygon grid cell size must be greater than zero metres.", out error);
        if (options.HitRadiusMeters <= 0f)
            return Fail("Polygon hit radius must be greater than zero metres.", out error);
        if (options.SimplificationToleranceMeters < 0f)
            return Fail("Polygon simplification tolerance cannot be negative.", out error);
        if (options.ConsensusGridCellSizeMeters <= 0f)
            return Fail("Consensus grid cell size must be greater than zero metres.", out error);
        if (options.ConsensusOverlapToleranceMeters < 0f)
            return Fail("Consensus overlap tolerance cannot be negative.", out error);
        if (options.ConsensusMinimumSupportingViews < 2)
            return Fail("Consensus minimum supporting views must be at least two.", out error);
        if (options.ConsensusSingleViewExpansionDistanceMeters < 0f)
        {
            return Fail(
                "Consensus single-view expansion distance cannot be negative.",
                out error);
        }
        if (options.ConsensusSemanticVetoDistanceMeters < 0f)
            return Fail("Consensus semantic veto distance cannot be negative.", out error);
        if (options.ConsensusSemanticVetoMinimumViews < 1)
            return Fail("Consensus semantic veto minimum views must be at least one.", out error);
        if (options.ConsensusFragmentMergeDistanceMeters < 0f)
            return Fail("Consensus fragment merge distance cannot be negative.", out error);
        if (options.ConsensusFragmentMinimumSharedViews < 1)
        {
            return Fail(
                "Consensus fragment minimum shared views must be at least one.",
                out error);
        }
        if (options.SemanticConflictDistanceMeters <= 0f)
            return Fail("Semantic conflict distance must be greater than zero metres.", out error);
        if (options.SemanticConflictRatioThreshold < 0f ||
            options.SemanticConflictRatioThreshold > 1f)
        {
            return Fail("Semantic conflict ratio threshold must be between zero and one.", out error);
        }
        if (options.SemanticConflictMinimumHits < 1)
            return Fail("Semantic conflict minimum hits must be at least one.", out error);
        if (string.IsNullOrWhiteSpace(outputPath))
            return Fail("Polygon output path is empty.", out error);
        return true;
    }

    private static HashSet<string> ParseClassFilters(string value)
    {
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return classes;

        string[] tokens = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            string className = tokens[i].Trim();
            if (!string.IsNullOrWhiteSpace(className))
                classes.Add(className);
        }
        return classes;
    }

    private static List<ClusterPoint> BuildSemanticConflictPoints(
        IReadOnlyList<SurfaceHit> hits,
        HashSet<string> classFilters,
        SrtDroneRaycastPlayer player,
        MetricReference metricReference,
        out int failedGeoreferenceCount)
    {
        var points = new List<ClusterPoint>();
        failedGeoreferenceCount = 0;
        if (classFilters.Count == 0)
            return points;

        for (int i = 0; i < hits.Count; i++)
        {
            SurfaceHit hit = hits[i];
            if (hit.SampleKind != MaskSampleKind.Core || !classFilters.Contains(hit.ClassName))
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

            var point = new ClusterPoint(hit, longitude, latitude)
            {
                EastMeters = metricReference.LongitudeToEast(longitude),
                NorthMeters = metricReference.LatitudeToNorth(latitude)
            };
            points.Add(point);
        }
        return points;
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

    private static void BuildClusterPoints(
        IReadOnlyList<SurfaceHit> hits,
        string classFilter,
        SrtDroneRaycastPlayer player,
        out List<ClusterPoint> corePoints,
        out List<ClusterPoint> boundaryPoints,
        out int failedGeoreferenceCount)
    {
        corePoints = new List<ClusterPoint>();
        boundaryPoints = new List<ClusterPoint>();
        failedGeoreferenceCount = 0;
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

            var point = new ClusterPoint(hit, longitude, latitude);
            if (hit.SampleKind == MaskSampleKind.Boundary)
                boundaryPoints.Add(point);
            else
                corePoints.Add(point);
        }
    }

    private static MetricReference ProjectToLocalMetricPlane(
        List<ClusterPoint> corePoints, List<ClusterPoint> boundaryPoints)
    {
        double longitudeSum = 0.0;
        double latitudeSum = 0.0;
        for (int i = 0; i < corePoints.Count; i++)
        {
            longitudeSum += corePoints[i].Geo.Longitude;
            latitudeSum += corePoints[i].Geo.Latitude;
        }

        var reference = new MetricReference(
            longitudeSum / corePoints.Count,
            latitudeSum / corePoints.Count);
        ProjectPoints(corePoints, reference);
        ProjectPoints(boundaryPoints, reference);
        return reference;
    }

    private static void ProjectPoints(List<ClusterPoint> points, MetricReference reference)
    {
        for (int i = 0; i < points.Count; i++)
        {
            ClusterPoint point = points[i];
            point.EastMeters = reference.LongitudeToEast(point.Geo.Longitude);
            point.NorthMeters = reference.LatitudeToNorth(point.Geo.Latitude);
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

        Dictionary<GridKey, List<int>> spatialGrid = BuildSpatialGrid(points, epsilonMeters, null);
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

    private static Dictionary<string, int> FindDominantClustersByDetection(
        List<ClusterPoint> corePoints, int[] labels)
    {
        var counts = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        for (int i = 0; i < corePoints.Count; i++)
        {
            int label = labels[i];
            string detectionId = corePoints[i].Hit.LocalDetectionId ?? string.Empty;
            if (label < 0 || string.IsNullOrWhiteSpace(detectionId))
                continue;
            if (!counts.TryGetValue(detectionId, out Dictionary<int, int> byCluster))
            {
                byCluster = new Dictionary<int, int>();
                counts.Add(detectionId, byCluster);
            }
            byCluster.TryGetValue(label, out int count);
            byCluster[label] = count + 1;
        }

        var dominant = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Dictionary<int, int>> detection in counts)
        {
            int bestCluster = -1;
            int bestCount = -1;
            foreach (KeyValuePair<int, int> candidate in detection.Value)
            {
                if (candidate.Value > bestCount ||
                    (candidate.Value == bestCount && candidate.Key < bestCluster))
                {
                    bestCluster = candidate.Key;
                    bestCount = candidate.Value;
                }
            }
            dominant.Add(detection.Key, bestCluster);
        }
        return dominant;
    }

    private static void BuildDominantCoreClusters(
        List<ClusterPoint> corePoints,
        int[] labels,
        int clusterCount,
        Dictionary<string, int> dominantClusterByDetection,
        out List<List<ClusterPoint>> clusters,
        out bool[] retainedPoints,
        out int suppressedClusterCount)
    {
        clusters = new List<List<ClusterPoint>>(clusterCount);
        for (int clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
            clusters.Add(new List<ClusterPoint>());
        retainedPoints = new bool[corePoints.Count];
        for (int i = 0; i < corePoints.Count; i++)
        {
            int label = labels[i];
            if (label < 0)
                continue;
            string detectionId = corePoints[i].Hit.LocalDetectionId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(detectionId) &&
                dominantClusterByDetection.TryGetValue(detectionId, out int dominantCluster) &&
                dominantCluster != label)
            {
                continue;
            }
            retainedPoints[i] = true;
            clusters[label].Add(corePoints[i]);
        }

        suppressedClusterCount = 0;
        for (int i = 0; i < clusters.Count; i++)
        {
            if (clusters[i].Count == 0)
                suppressedClusterCount++;
        }
    }

    private static void BuildAllCoreClusters(
        List<ClusterPoint> corePoints,
        int[] labels,
        int clusterCount,
        out List<List<ClusterPoint>> clusters,
        out bool[] retainedPoints)
    {
        clusters = new List<List<ClusterPoint>>(clusterCount);
        for (int clusterIndex = 0; clusterIndex < clusterCount; clusterIndex++)
            clusters.Add(new List<ClusterPoint>());
        retainedPoints = new bool[corePoints.Count];
        for (int i = 0; i < corePoints.Count; i++)
        {
            int label = labels[i];
            if (label < 0)
                continue;
            retainedPoints[i] = true;
            clusters[label].Add(corePoints[i]);
        }
    }

    private static void BuildMultiViewConsensusClusters(
        List<ClusterPoint> corePoints,
        List<ClusterPoint> semanticConflictPoints,
        Options options,
        out List<List<ClusterPoint>> clusters,
        out int[] labels,
        out bool[] retainedPoints,
        out List<ClusterConsensusStats> clusterStats,
        out int availableViewCount,
        out int confirmedCellCount,
        out int expandedCoreHitCount,
        out HashSet<GridKey> semanticVetoCells,
        out int semanticVetoCellCount,
        out int carvedConfirmedCellCount,
        out int carvedCoreHitCount,
        out int provisionalClusterCount,
        out int fragmentMergeCount,
        out int unassignedCoreHitCount)
    {
        double cellSize = options.ConsensusGridCellSizeMeters;
        semanticVetoCells = options.ConsensusSemanticCarvingEnabled
            ? BuildSemanticVetoCells(semanticConflictPoints, options, cellSize)
            : new HashSet<GridKey>();
        semanticVetoCellCount = semanticVetoCells.Count;
        var pointIndicesByCell = new Dictionary<GridKey, List<int>>();
        var occupiedCellsByView = new Dictionary<int, HashSet<GridKey>>();
        for (int pointIndex = 0; pointIndex < corePoints.Count; pointIndex++)
        {
            ClusterPoint point = corePoints[pointIndex];
            GridKey cell = GridKey.FromPoint(point, cellSize);
            if (!pointIndicesByCell.TryGetValue(cell, out List<int> pointIndices))
            {
                pointIndices = new List<int>();
                pointIndicesByCell.Add(cell, pointIndices);
            }
            pointIndices.Add(pointIndex);

            int viewIndex = point.Hit.ViewIndex;
            if (!occupiedCellsByView.TryGetValue(viewIndex, out HashSet<GridKey> viewCells))
            {
                viewCells = new HashSet<GridKey>();
                occupiedCellsByView.Add(viewIndex, viewCells);
            }
            viewCells.Add(cell);
        }
        availableViewCount = occupiedCellsByView.Count;

        int overlapRadiusCells = (int)Math.Ceiling(
            options.ConsensusOverlapToleranceMeters / cellSize);
        double overlapSquared = options.ConsensusOverlapToleranceMeters *
                                options.ConsensusOverlapToleranceMeters;
        var supportViewsByCell = new Dictionary<GridKey, HashSet<int>>();
        carvedConfirmedCellCount = 0;
        foreach (GridKey candidateCell in pointIndicesByCell.Keys)
        {
            var supportingViews = new HashSet<int>();
            foreach (KeyValuePair<int, HashSet<GridKey>> view in occupiedCellsByView)
            {
                bool supportsCell = false;
                for (int xOffset = -overlapRadiusCells;
                     xOffset <= overlapRadiusCells && !supportsCell;
                     xOffset++)
                {
                    for (int yOffset = -overlapRadiusCells;
                         yOffset <= overlapRadiusCells;
                         yOffset++)
                    {
                        double eastOffset = xOffset * cellSize;
                        double northOffset = yOffset * cellSize;
                        if (eastOffset * eastOffset + northOffset * northOffset > overlapSquared)
                            continue;
                        if (!view.Value.Contains(new GridKey(
                                candidateCell.X + xOffset,
                                candidateCell.Y + yOffset)))
                        {
                            continue;
                        }
                        supportsCell = true;
                        break;
                    }
                }
                if (supportsCell)
                    supportingViews.Add(view.Key);
            }
            if (supportingViews.Count < options.ConsensusMinimumSupportingViews)
                continue;
            if (IsCellNearAny(
                    candidateCell,
                    semanticVetoCells,
                    cellSize,
                    options.ConsensusSemanticVetoDistanceMeters))
            {
                carvedConfirmedCellCount++;
                continue;
            }
            supportViewsByCell.Add(candidateCell, supportingViews);
        }
        confirmedCellCount = supportViewsByCell.Count;

        var orderedConfirmedCells = new List<GridKey>(supportViewsByCell.Keys);
        orderedConfirmedCells.Sort(CompareGridKeys);
        var clusterByConfirmedCell = new Dictionary<GridKey, int>();
        clusters = new List<List<ClusterPoint>>();
        clusterStats = new List<ClusterConsensusStats>();
        double connectionDistance = cellSize * Math.Sqrt(2.0);
        double connectionSquared = connectionDistance * connectionDistance;
        int connectionRadiusCells = Math.Max(
            1,
            (int)Math.Ceiling(connectionDistance / cellSize));
        for (int seedIndex = 0; seedIndex < orderedConfirmedCells.Count; seedIndex++)
        {
            GridKey seed = orderedConfirmedCells[seedIndex];
            if (clusterByConfirmedCell.ContainsKey(seed))
                continue;

            int clusterIndex = clusters.Count;
            clusters.Add(new List<ClusterPoint>());
            var stats = new ClusterConsensusStats();
            clusterStats.Add(stats);
            var queue = new Queue<GridKey>();
            clusterByConfirmedCell.Add(seed, clusterIndex);
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                GridKey cell = queue.Dequeue();
                stats.ConfirmedCellCount++;
                HashSet<int> supportingViews = supportViewsByCell[cell];
                stats.MaximumViewSupport = Math.Max(
                    stats.MaximumViewSupport, supportingViews.Count);
                stats.SupportingViews.UnionWith(supportingViews);

                for (int xOffset = -connectionRadiusCells;
                     xOffset <= connectionRadiusCells;
                     xOffset++)
                {
                    for (int yOffset = -connectionRadiusCells;
                         yOffset <= connectionRadiusCells;
                         yOffset++)
                    {
                        if (xOffset == 0 && yOffset == 0)
                            continue;
                        double eastOffset = xOffset * cellSize;
                        double northOffset = yOffset * cellSize;
                        if (eastOffset * eastOffset + northOffset * northOffset >
                            connectionSquared)
                        {
                            continue;
                        }
                        var neighbour = new GridKey(
                            cell.X + xOffset,
                            cell.Y + yOffset);
                        if (!supportViewsByCell.ContainsKey(neighbour) ||
                            clusterByConfirmedCell.ContainsKey(neighbour))
                        {
                            continue;
                        }
                        clusterByConfirmedCell.Add(neighbour, clusterIndex);
                        queue.Enqueue(neighbour);
                    }
                }
            }
        }

        provisionalClusterCount = clusters.Count;
        fragmentMergeCount = MergeConsensusFragments(
            orderedConfirmedCells,
            supportViewsByCell,
            clusterByConfirmedCell,
            semanticVetoCells,
            cellSize,
            options,
            ref clusters,
            ref clusterStats);

        labels = new int[corePoints.Count];
        retainedPoints = new bool[corePoints.Count];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = Noise;

        expandedCoreHitCount = 0;
        carvedCoreHitCount = 0;
        double expansionDistance = options.ConsensusSingleViewExpansionDistanceMeters;
        double expansionSquared = expansionDistance * expansionDistance;
        int expansionRadiusCells = (int)Math.Ceiling(expansionDistance / cellSize);
        for (int pointIndex = 0; pointIndex < corePoints.Count; pointIndex++)
        {
            ClusterPoint point = corePoints[pointIndex];
            GridKey pointCell = GridKey.FromPoint(point, cellSize);
            if (IsCellNearAny(
                    pointCell,
                    semanticVetoCells,
                    cellSize,
                    options.ConsensusSemanticVetoDistanceMeters))
            {
                carvedCoreHitCount++;
                continue;
            }
            bool isConfirmedCell = clusterByConfirmedCell.TryGetValue(
                pointCell, out int targetCluster);
            if (!isConfirmedCell)
            {
                targetCluster = -1;
                if (expansionDistance > 0.0)
                {
                    targetCluster = FindNearestConsensusCluster(
                        point,
                        pointCell,
                        clusterByConfirmedCell,
                        cellSize,
                        expansionRadiusCells,
                        expansionSquared);
                }
            }
            if (targetCluster < 0)
                continue;

            labels[pointIndex] = targetCluster;
            retainedPoints[pointIndex] = true;
            clusters[targetCluster].Add(point);
            if (!isConfirmedCell)
            {
                clusterStats[targetCluster].ExpandedCoreHitCount++;
                expandedCoreHitCount++;
            }
        }

        unassignedCoreHitCount = 0;
        for (int i = 0; i < retainedPoints.Length; i++)
        {
            if (!retainedPoints[i])
                unassignedCoreHitCount++;
        }
    }

    private static HashSet<GridKey> BuildSemanticVetoCells(
        List<ClusterPoint> semanticConflictPoints,
        Options options,
        double cellSize)
    {
        var occupiedCellsByView = new Dictionary<int, HashSet<GridKey>>();
        var candidateCells = new HashSet<GridKey>();
        for (int i = 0; i < semanticConflictPoints.Count; i++)
        {
            ClusterPoint point = semanticConflictPoints[i];
            GridKey cell = GridKey.FromPoint(point, cellSize);
            candidateCells.Add(cell);
            int viewIndex = point.Hit.ViewIndex;
            if (!occupiedCellsByView.TryGetValue(viewIndex, out HashSet<GridKey> viewCells))
            {
                viewCells = new HashSet<GridKey>();
                occupiedCellsByView.Add(viewIndex, viewCells);
            }
            viewCells.Add(cell);
        }

        var vetoCells = new HashSet<GridKey>();
        foreach (GridKey candidateCell in candidateCells)
        {
            int supportingViews = 0;
            foreach (HashSet<GridKey> viewCells in occupiedCellsByView.Values)
            {
                if (!IsCellNearAny(
                        candidateCell,
                        viewCells,
                        cellSize,
                        options.ConsensusSemanticVetoDistanceMeters))
                {
                    continue;
                }
                supportingViews++;
                if (supportingViews >= options.ConsensusSemanticVetoMinimumViews)
                {
                    vetoCells.Add(candidateCell);
                    break;
                }
            }
        }
        return vetoCells;
    }

    private static int MergeConsensusFragments(
        List<GridKey> orderedConfirmedCells,
        Dictionary<GridKey, HashSet<int>> supportViewsByCell,
        Dictionary<GridKey, int> clusterByConfirmedCell,
        HashSet<GridKey> semanticVetoCells,
        double cellSize,
        Options options,
        ref List<List<ClusterPoint>> clusters,
        ref List<ClusterConsensusStats> clusterStats)
    {
        int provisionalClusterCount = clusters.Count;
        double mergeDistance = options.ConsensusFragmentMergeDistanceMeters;
        if (provisionalClusterCount < 2 || mergeDistance <= 0.0)
            return 0;

        var unionFind = new UnionFind(provisionalClusterCount);
        double mergeSquared = mergeDistance * mergeDistance;
        int radiusCells = (int)Math.Ceiling(mergeDistance / cellSize);
        for (int cellIndex = 0; cellIndex < orderedConfirmedCells.Count; cellIndex++)
        {
            GridKey cell = orderedConfirmedCells[cellIndex];
            int sourceCluster = clusterByConfirmedCell[cell];
            for (int xOffset = -radiusCells; xOffset <= radiusCells; xOffset++)
            {
                for (int yOffset = -radiusCells; yOffset <= radiusCells; yOffset++)
                {
                    if (xOffset == 0 && yOffset == 0)
                        continue;
                    double eastOffset = xOffset * cellSize;
                    double northOffset = yOffset * cellSize;
                    if (eastOffset * eastOffset + northOffset * northOffset > mergeSquared)
                        continue;

                    var neighbour = new GridKey(cell.X + xOffset, cell.Y + yOffset);
                    if (CompareGridKeys(cell, neighbour) >= 0 ||
                        !clusterByConfirmedCell.TryGetValue(
                            neighbour, out int targetCluster) ||
                        sourceCluster == targetCluster)
                    {
                        continue;
                    }
                    if (CountSharedViews(
                            clusterStats[sourceCluster].SupportingViews,
                            clusterStats[targetCluster].SupportingViews) <
                        options.ConsensusFragmentMinimumSharedViews)
                    {
                        continue;
                    }
                    if (options.ConsensusBlockMergeAcrossSemanticVeto &&
                        IsGridLineBlocked(cell, neighbour, semanticVetoCells))
                    {
                        continue;
                    }
                    unionFind.Union(sourceCluster, targetCluster);
                }
            }
        }

        var rootToMergedCluster = new Dictionary<int, int>();
        var mergedClusterByCell = new Dictionary<GridKey, int>();
        var mergedClusters = new List<List<ClusterPoint>>();
        var mergedStats = new List<ClusterConsensusStats>();
        for (int i = 0; i < orderedConfirmedCells.Count; i++)
        {
            GridKey cell = orderedConfirmedCells[i];
            int root = unionFind.Find(clusterByConfirmedCell[cell]);
            if (!rootToMergedCluster.TryGetValue(root, out int mergedCluster))
            {
                mergedCluster = mergedClusters.Count;
                rootToMergedCluster.Add(root, mergedCluster);
                mergedClusters.Add(new List<ClusterPoint>());
                mergedStats.Add(new ClusterConsensusStats());
            }
            mergedClusterByCell.Add(cell, mergedCluster);
            ClusterConsensusStats stats = mergedStats[mergedCluster];
            HashSet<int> supportingViews = supportViewsByCell[cell];
            stats.ConfirmedCellCount++;
            stats.MaximumViewSupport = Math.Max(
                stats.MaximumViewSupport, supportingViews.Count);
            stats.SupportingViews.UnionWith(supportingViews);
        }

        clusterByConfirmedCell.Clear();
        foreach (KeyValuePair<GridKey, int> pair in mergedClusterByCell)
            clusterByConfirmedCell.Add(pair.Key, pair.Value);
        clusters = mergedClusters;
        clusterStats = mergedStats;
        return provisionalClusterCount - mergedClusters.Count;
    }

    private static int CountSharedViews(HashSet<int> left, HashSet<int> right)
    {
        HashSet<int> smaller = left.Count <= right.Count ? left : right;
        HashSet<int> larger = ReferenceEquals(smaller, left) ? right : left;
        int count = 0;
        foreach (int viewIndex in smaller)
        {
            if (larger.Contains(viewIndex))
                count++;
        }
        return count;
    }

    private static bool IsCellNearAny(
        GridKey cell,
        HashSet<GridKey> candidates,
        double cellSize,
        double maximumDistanceMeters)
    {
        if (candidates == null || candidates.Count == 0)
            return false;
        int radiusCells = (int)Math.Ceiling(maximumDistanceMeters / cellSize);
        double maximumSquared = maximumDistanceMeters * maximumDistanceMeters;
        for (int xOffset = -radiusCells; xOffset <= radiusCells; xOffset++)
        {
            for (int yOffset = -radiusCells; yOffset <= radiusCells; yOffset++)
            {
                double eastOffset = xOffset * cellSize;
                double northOffset = yOffset * cellSize;
                if (eastOffset * eastOffset + northOffset * northOffset > maximumSquared)
                    continue;
                if (candidates.Contains(new GridKey(
                        cell.X + xOffset, cell.Y + yOffset)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsGridLineBlocked(
        GridKey start,
        GridKey end,
        HashSet<GridKey> blockedCells)
    {
        if (blockedCells == null || blockedCells.Count == 0)
            return false;
        int x = start.X;
        int y = start.Y;
        int deltaX = Math.Abs(end.X - start.X);
        int stepX = start.X < end.X ? 1 : -1;
        int deltaY = -Math.Abs(end.Y - start.Y);
        int stepY = start.Y < end.Y ? 1 : -1;
        int error = deltaX + deltaY;
        while (true)
        {
            if (blockedCells.Contains(new GridKey(x, y)))
                return true;
            if (x == end.X && y == end.Y)
                return false;
            int doubledError = 2 * error;
            if (doubledError >= deltaY)
            {
                error += deltaY;
                x += stepX;
            }
            if (doubledError <= deltaX)
            {
                error += deltaX;
                y += stepY;
            }
        }
    }

    private static int FindNearestConsensusCluster(
        ClusterPoint point,
        GridKey pointCell,
        Dictionary<GridKey, int> clusterByConfirmedCell,
        double cellSize,
        int searchRadiusCells,
        double maximumSquaredDistance)
    {
        int nearestCluster = -1;
        double nearestSquaredDistance = double.PositiveInfinity;
        for (int xOffset = -searchRadiusCells; xOffset <= searchRadiusCells; xOffset++)
        {
            for (int yOffset = -searchRadiusCells; yOffset <= searchRadiusCells; yOffset++)
            {
                var cell = new GridKey(pointCell.X + xOffset, pointCell.Y + yOffset);
                if (!clusterByConfirmedCell.TryGetValue(cell, out int clusterIndex))
                    continue;
                double cellEast = (cell.X + 0.5) * cellSize;
                double cellNorth = (cell.Y + 0.5) * cellSize;
                double squaredDistance = SquaredDistance(
                    point.EastMeters,
                    point.NorthMeters,
                    cellEast,
                    cellNorth);
                if (squaredDistance > maximumSquaredDistance ||
                    squaredDistance > nearestSquaredDistance)
                {
                    continue;
                }
                if (squaredDistance == nearestSquaredDistance &&
                    nearestCluster >= 0 && clusterIndex >= nearestCluster)
                {
                    continue;
                }
                nearestSquaredDistance = squaredDistance;
                nearestCluster = clusterIndex;
            }
        }
        return nearestCluster;
    }

    private static int CompareGridKeys(GridKey left, GridKey right)
    {
        int xComparison = left.X.CompareTo(right.X);
        return xComparison != 0 ? xComparison : left.Y.CompareTo(right.Y);
    }

    private static List<List<ClusterPoint>> AssignBoundaryPoints(
        List<ClusterPoint> boundaryPoints,
        List<ClusterPoint> corePoints,
        int[] labels,
        bool[] retainedCorePoints,
        List<List<ClusterPoint>> coreClusters,
        Dictionary<string, int> dominantClusterByDetection,
        double maximumDistanceMeters,
        out int assignedCount)
    {
        var assigned = new List<List<ClusterPoint>>(coreClusters.Count);
        for (int i = 0; i < coreClusters.Count; i++)
            assigned.Add(new List<ClusterPoint>());
        assignedCount = 0;

        Dictionary<GridKey, List<int>> grid = BuildSpatialGrid(
            corePoints, maximumDistanceMeters, retainedCorePoints);
        double maximumSquaredDistance = maximumDistanceMeters * maximumDistanceMeters;
        for (int boundaryIndex = 0; boundaryIndex < boundaryPoints.Count; boundaryIndex++)
        {
            ClusterPoint boundary = boundaryPoints[boundaryIndex];
            string detectionId = boundary.Hit.LocalDetectionId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(detectionId) ||
                !dominantClusterByDetection.TryGetValue(detectionId, out int targetCluster) ||
                targetCluster < 0 || targetCluster >= coreClusters.Count ||
                coreClusters[targetCluster].Count == 0)
            {
                continue;
            }

            GridKey centre = GridKey.FromPoint(boundary, maximumDistanceMeters);
            double nearestSquaredDistance = double.PositiveInfinity;
            for (int xOffset = -1; xOffset <= 1; xOffset++)
            {
                for (int yOffset = -1; yOffset <= 1; yOffset++)
                {
                    var key = new GridKey(centre.X + xOffset, centre.Y + yOffset);
                    if (!grid.TryGetValue(key, out List<int> candidates))
                        continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        int coreIndex = candidates[i];
                        if (labels[coreIndex] != targetCluster)
                            continue;
                        double squaredDistance = SquaredDistance(
                            boundary.EastMeters,
                            boundary.NorthMeters,
                            corePoints[coreIndex].EastMeters,
                            corePoints[coreIndex].NorthMeters);
                        if (squaredDistance < nearestSquaredDistance)
                            nearestSquaredDistance = squaredDistance;
                    }
                }
            }
            if (nearestSquaredDistance > maximumSquaredDistance)
                continue;
            assigned[targetCluster].Add(boundary);
            assignedCount++;
        }
        return assigned;
    }

    private static List<List<ClusterPoint>> AssignBoundaryPointsToNearestCluster(
        List<ClusterPoint> boundaryPoints,
        List<ClusterPoint> corePoints,
        int[] labels,
        bool[] retainedCorePoints,
        List<List<ClusterPoint>> coreClusters,
        double maximumDistanceMeters,
        out int assignedCount)
    {
        return AssignBoundaryPointsToNearestCluster(
            boundaryPoints,
            corePoints,
            labels,
            retainedCorePoints,
            coreClusters,
            maximumDistanceMeters,
            null,
            1.0,
            0.0,
            out assignedCount,
            out _);
    }

    private static List<List<ClusterPoint>> AssignBoundaryPointsToNearestCluster(
        List<ClusterPoint> boundaryPoints,
        List<ClusterPoint> corePoints,
        int[] labels,
        bool[] retainedCorePoints,
        List<List<ClusterPoint>> coreClusters,
        double maximumDistanceMeters,
        HashSet<GridKey> excludedCells,
        double excludedCellSizeMeters,
        double excludedDistanceMeters,
        out int assignedCount,
        out int excludedCount)
    {
        var assigned = new List<List<ClusterPoint>>(coreClusters.Count);
        for (int i = 0; i < coreClusters.Count; i++)
            assigned.Add(new List<ClusterPoint>());
        assignedCount = 0;
        excludedCount = 0;

        Dictionary<GridKey, List<int>> grid = BuildSpatialGrid(
            corePoints, maximumDistanceMeters, retainedCorePoints);
        double maximumSquaredDistance = maximumDistanceMeters * maximumDistanceMeters;
        for (int boundaryIndex = 0; boundaryIndex < boundaryPoints.Count; boundaryIndex++)
        {
            ClusterPoint boundary = boundaryPoints[boundaryIndex];
            if (IsCellNearAny(
                    GridKey.FromPoint(boundary, excludedCellSizeMeters),
                    excludedCells,
                    excludedCellSizeMeters,
                    excludedDistanceMeters))
            {
                excludedCount++;
                continue;
            }
            GridKey centre = GridKey.FromPoint(boundary, maximumDistanceMeters);
            int nearestCluster = -1;
            double nearestSquaredDistance = double.PositiveInfinity;
            for (int xOffset = -1; xOffset <= 1; xOffset++)
            {
                for (int yOffset = -1; yOffset <= 1; yOffset++)
                {
                    var key = new GridKey(centre.X + xOffset, centre.Y + yOffset);
                    if (!grid.TryGetValue(key, out List<int> candidates))
                        continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        int coreIndex = candidates[i];
                        int cluster = labels[coreIndex];
                        if (cluster < 0 || cluster >= coreClusters.Count)
                            continue;
                        double squaredDistance = SquaredDistance(
                            boundary.EastMeters,
                            boundary.NorthMeters,
                            corePoints[coreIndex].EastMeters,
                            corePoints[coreIndex].NorthMeters);
                        if (squaredDistance >= nearestSquaredDistance)
                            continue;
                        nearestSquaredDistance = squaredDistance;
                        nearestCluster = cluster;
                    }
                }
            }
            if (nearestCluster < 0 || nearestSquaredDistance > maximumSquaredDistance)
                continue;
            assigned[nearestCluster].Add(boundary);
            assignedCount++;
        }
        return assigned;
    }

    private static Dictionary<GridKey, List<int>> BuildSpatialGrid(
        List<ClusterPoint> points, double cellSize, bool[] includedPoints)
    {
        var grid = new Dictionary<GridKey, List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            if (includedPoints != null && !includedPoints[i])
                continue;
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
                    if (SquaredDistance(
                            point.EastMeters,
                            point.NorthMeters,
                            points[candidate].EastMeters,
                            points[candidate].NorthMeters) <= squaredEpsilon)
                    {
                        neighbours.Add(candidate);
                    }
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

    private static SemanticConflictAssessment AssessSemanticConflict(
        List<ClusterPoint> coreCluster,
        List<ClusterPoint> semanticConflictPoints,
        Dictionary<GridKey, List<int>> semanticConflictGrid,
        Options options)
    {
        if (coreCluster.Count == 0 || semanticConflictPoints.Count == 0)
        {
            return new SemanticConflictAssessment(
                0,
                coreCluster.Count,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<int>(),
                false);
        }

        double maximumDistance = options.SemanticConflictDistanceMeters;
        double maximumSquaredDistance = maximumDistance * maximumDistance;
        int conflictingCoreHitCount = 0;
        var conflictDetectionIds = new HashSet<string>(StringComparer.Ordinal);
        var conflictViewIndices = new HashSet<int>();
        for (int coreIndex = 0; coreIndex < coreCluster.Count; coreIndex++)
        {
            ClusterPoint corePoint = coreCluster[coreIndex];
            GridKey centre = GridKey.FromPoint(corePoint, maximumDistance);
            bool hasConflict = false;
            for (int xOffset = -1; xOffset <= 1; xOffset++)
            {
                for (int yOffset = -1; yOffset <= 1; yOffset++)
                {
                    var key = new GridKey(centre.X + xOffset, centre.Y + yOffset);
                    if (!semanticConflictGrid.TryGetValue(key, out List<int> candidates))
                        continue;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        ClusterPoint candidate = semanticConflictPoints[candidates[i]];
                        if (SquaredDistance(
                                corePoint.EastMeters,
                                corePoint.NorthMeters,
                                candidate.EastMeters,
                                candidate.NorthMeters) > maximumSquaredDistance)
                        {
                            continue;
                        }

                        hasConflict = true;
                        if (!string.IsNullOrWhiteSpace(candidate.Hit.LocalDetectionId))
                            conflictDetectionIds.Add(candidate.Hit.LocalDetectionId);
                        conflictViewIndices.Add(candidate.Hit.ViewIndex);
                    }
                }
            }
            if (hasConflict)
                conflictingCoreHitCount++;
        }

        double ratio = coreCluster.Count > 0
            ? conflictingCoreHitCount / (double)coreCluster.Count
            : 0.0;
        bool shouldReject = options.ExcludeSemanticConflicts &&
            conflictingCoreHitCount >= options.SemanticConflictMinimumHits &&
            ratio >= options.SemanticConflictRatioThreshold;
        return new SemanticConflictAssessment(
            conflictingCoreHitCount,
            coreCluster.Count,
            conflictDetectionIds,
            conflictViewIndices,
            shouldReject);
    }

    private static JObject BuildSemanticConflictDiagnostic(
        List<ClusterPoint> coreCluster,
        List<ClusterPoint> boundaryCluster,
        int rawClusterIndex,
        string classFilter,
        SemanticConflictAssessment conflict)
    {
        var buildingDetectionIds = new HashSet<string>(StringComparer.Ordinal);
        var buildingViewIndices = new HashSet<int>();
        CollectProvenance(coreCluster, buildingDetectionIds, buildingViewIndices);
        CollectProvenance(boundaryCluster, buildingDetectionIds, buildingViewIndices);

        return new JObject
        {
            ["candidate_id"] =
                $"{SanitizeId(classFilter)}_cluster_{rawClusterIndex:D3}",
            ["raw_dbscan_cluster_index"] = rawClusterIndex,
            ["building_detection_ids"] = SortedStringArray(buildingDetectionIds),
            ["building_view_indices"] = SortedIntegerArray(buildingViewIndices),
            ["core_hit_count"] = coreCluster.Count,
            ["conflicting_core_hit_count"] = conflict.ConflictingCoreHitCount,
            ["semantic_conflict_ratio"] = conflict.Ratio,
            ["conflict_detection_ids"] = SortedStringArray(conflict.ConflictDetectionIds),
            ["conflict_view_indices"] = SortedIntegerArray(conflict.ConflictViewIndices)
        };
    }

    private static bool TryBuildFeature(
        List<ClusterPoint> coreCluster,
        List<ClusterPoint> boundaryCluster,
        int rawClusterIndex,
        int featureIndex,
        MetricReference metricReference,
        Options options,
        string clusteringMethod,
        ClusterConsensusStats consensus,
        SemanticConflictAssessment conflict,
        out JObject feature)
    {
        feature = null;
        bool usedOccupancyContour = TryBuildOccupancyContour(
            coreCluster,
            boundaryCluster,
            metricReference,
            options,
            out List<GeoPoint> polygon,
            out int occupiedCellCount);
        if (!usedOccupancyContour)
        {
            polygon = BuildFallbackConvexHull(coreCluster, boundaryCluster);
            occupiedCellCount = 0;
        }
        if (polygon.Count < 3)
            return false;

        var detectionIds = new HashSet<string>(StringComparer.Ordinal);
        var viewIndices = new HashSet<int>();
        CollectProvenance(coreCluster, detectionIds, viewIndices);
        CollectProvenance(boundaryCluster, detectionIds, viewIndices);
        var sortedDetectionIds = new List<string>(detectionIds);
        sortedDetectionIds.Sort(StringComparer.Ordinal);
        var sortedViews = new List<int>(viewIndices);
        sortedViews.Sort();

        var ring = new JArray();
        for (int i = 0; i < polygon.Count; i++)
            ring.Add(new JArray(polygon[i].Longitude, polygon[i].Latitude));
        ring.Add(new JArray(polygon[0].Longitude, polygon[0].Latitude));
        var polygonCoordinates = new JArray();
        polygonCoordinates.Add(ring);
        feature = new JObject
        {
            ["type"] = "Feature",
            ["id"] = options.ClusterAssociationMode !=
                     SurfaceHitClusterAssociationMode.DominantClusterPerDetection
                ? $"{SanitizeId(options.ClassFilter)}_cluster_{rawClusterIndex:D3}"
                : $"{SanitizeId(options.ClassFilter)}_{featureIndex:D3}",
            ["properties"] = new JObject
            {
                ["class"] = options.ClassFilter,
                ["source"] = "mask_raycast_multiview",
                ["provisional"] = true,
                ["cluster_association_mode"] = options.ClusterAssociationMode.ToString(),
                ["clustering_method"] = clusteringMethod,
                ["polygon_method"] = usedOccupancyContour
                    ? "original_mask_boundary_metric_occupancy_contour"
                    : "convex_hull_fallback",
                ["raw_cluster_index"] = rawClusterIndex,
                ["raw_dbscan_cluster_index"] = options.ClusterAssociationMode ==
                                                SurfaceHitClusterAssociationMode.MultiViewConsensus
                    ? -1
                    : rawClusterIndex,
                ["consensus_status"] = consensus != null
                    ? "confirmed"
                    : "not_applicable",
                ["consensus_supporting_views"] = consensus != null
                    ? SortedIntegerArray(consensus.SupportingViews)
                    : new JArray(),
                ["consensus_maximum_view_support"] = consensus?.MaximumViewSupport ?? 0,
                ["consensus_confirmed_cell_count"] = consensus?.ConfirmedCellCount ?? 0,
                ["consensus_expanded_core_hit_count"] = consensus?.ExpandedCoreHitCount ?? 0,
                ["detection_ids"] = new JArray(sortedDetectionIds),
                ["view_indices"] = new JArray(sortedViews),
                ["core_hit_count"] = coreCluster.Count,
                ["assigned_boundary_hit_count"] = boundaryCluster.Count,
                ["occupied_grid_cell_count"] = occupiedCellCount,
                ["polygon_vertex_count"] = polygon.Count,
                ["semantic_conflicting_core_hit_count"] = conflict.ConflictingCoreHitCount,
                ["semantic_conflict_ratio"] = conflict.Ratio,
                ["semantic_conflict_detection_ids"] =
                    SortedStringArray(conflict.ConflictDetectionIds),
                ["semantic_conflict_view_indices"] =
                    SortedIntegerArray(conflict.ConflictViewIndices)
            },
            ["geometry"] = new JObject
            {
                ["type"] = "Polygon",
                ["coordinates"] = polygonCoordinates
            }
        };
        return true;
    }

    private static JArray SortedStringArray(HashSet<string> values)
    {
        var sorted = new List<string>(values);
        sorted.Sort(StringComparer.Ordinal);
        return new JArray(sorted);
    }

    private static JArray SortedIntegerArray(HashSet<int> values)
    {
        var sorted = new List<int>(values);
        sorted.Sort();
        return new JArray(sorted);
    }

    private static void CollectProvenance(
        List<ClusterPoint> points,
        HashSet<string> detectionIds,
        HashSet<int> viewIndices)
    {
        for (int i = 0; i < points.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(points[i].Hit.LocalDetectionId))
                detectionIds.Add(points[i].Hit.LocalDetectionId);
            viewIndices.Add(points[i].Hit.ViewIndex);
        }
    }

    private static bool TryBuildOccupancyContour(
        List<ClusterPoint> coreCluster,
        List<ClusterPoint> boundaryCluster,
        MetricReference metricReference,
        Options options,
        out List<GeoPoint> polygon,
        out int occupiedCellCount)
    {
        polygon = new List<GeoPoint>();
        occupiedCellCount = 0;
        var occupied = new HashSet<GridKey>();
        AddPointDiscsToGrid(coreCluster, options, occupied);
        AddPointDiscsToGrid(boundaryCluster, options, occupied);
        if (occupied.Count == 0)
            return false;

        HashSet<GridKey> largestComponent = FindLargestGridComponent(occupied);
        occupiedCellCount = largestComponent.Count;
        if (largestComponent.Count == 0)
            return false;
        List<GridVertex> gridRing = TraceLargestOuterGridRing(largestComponent);
        if (gridRing.Count < 3)
            return false;
        gridRing = RemoveCollinearGridVertices(gridRing);
        if (gridRing.Count < 3)
            return false;

        var metricRing = new List<MetricPoint>(gridRing.Count);
        for (int i = 0; i < gridRing.Count; i++)
        {
            metricRing.Add(new MetricPoint(
                gridRing[i].X * options.GridCellSizeMeters,
                gridRing[i].Y * options.GridCellSizeMeters));
        }
        List<MetricPoint> unsimplifiedMetricRing = metricRing;
        metricRing = SimplifyClosedRing(metricRing, options.SimplificationToleranceMeters);
        if (metricRing.Count < 3 || !IsSimplePolygon(metricRing))
            metricRing = unsimplifiedMetricRing;
        if (metricRing.Count < 3 || !IsSimplePolygon(metricRing))
            return false;
        for (int i = 0; i < metricRing.Count; i++)
            polygon.Add(metricReference.ToGeoPoint(metricRing[i].East, metricRing[i].North));
        return polygon.Count >= 3;
    }

    private static void AddPointDiscsToGrid(
        List<ClusterPoint> points, Options options, HashSet<GridKey> occupied)
    {
        double cellSize = options.GridCellSizeMeters;
        double radius = options.HitRadiusMeters;
        int cellRadius = Math.Max(1, (int)Math.Ceiling(radius / cellSize));
        double inclusionRadius = radius + cellSize * Math.Sqrt(0.5);
        double squaredInclusionRadius = inclusionRadius * inclusionRadius;
        for (int i = 0; i < points.Count; i++)
        {
            int centreX = (int)Math.Floor(points[i].EastMeters / cellSize);
            int centreY = (int)Math.Floor(points[i].NorthMeters / cellSize);
            for (int xOffset = -cellRadius; xOffset <= cellRadius; xOffset++)
            {
                for (int yOffset = -cellRadius; yOffset <= cellRadius; yOffset++)
                {
                    int cellX = centreX + xOffset;
                    int cellY = centreY + yOffset;
                    double cellCentreEast = (cellX + 0.5) * cellSize;
                    double cellCentreNorth = (cellY + 0.5) * cellSize;
                    if (SquaredDistance(
                            points[i].EastMeters,
                            points[i].NorthMeters,
                            cellCentreEast,
                            cellCentreNorth) <= squaredInclusionRadius)
                    {
                        occupied.Add(new GridKey(cellX, cellY));
                    }
                }
            }
        }
    }

    private static HashSet<GridKey> FindLargestGridComponent(HashSet<GridKey> occupied)
    {
        var remaining = new HashSet<GridKey>(occupied);
        var largest = new HashSet<GridKey>();
        while (remaining.Count > 0)
        {
            GridKey seed = default;
            foreach (GridKey candidate in remaining)
            {
                seed = candidate;
                break;
            }

            var component = new HashSet<GridKey>();
            var queue = new Queue<GridKey>();
            remaining.Remove(seed);
            component.Add(seed);
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                GridKey current = queue.Dequeue();
                AddGridNeighbour(current.X - 1, current.Y, remaining, component, queue);
                AddGridNeighbour(current.X + 1, current.Y, remaining, component, queue);
                AddGridNeighbour(current.X, current.Y - 1, remaining, component, queue);
                AddGridNeighbour(current.X, current.Y + 1, remaining, component, queue);
            }
            if (component.Count > largest.Count)
                largest = component;
        }
        return largest;
    }

    private static void AddGridNeighbour(
        int x,
        int y,
        HashSet<GridKey> remaining,
        HashSet<GridKey> component,
        Queue<GridKey> queue)
    {
        var neighbour = new GridKey(x, y);
        if (!remaining.Remove(neighbour))
            return;
        component.Add(neighbour);
        queue.Enqueue(neighbour);
    }

    private static List<GridVertex> TraceLargestOuterGridRing(HashSet<GridKey> occupied)
    {
        var edges = new List<DirectedGridEdge>();
        foreach (GridKey cell in occupied)
        {
            if (!occupied.Contains(new GridKey(cell.X, cell.Y - 1)))
                edges.Add(new DirectedGridEdge(cell.X, cell.Y, cell.X + 1, cell.Y));
            if (!occupied.Contains(new GridKey(cell.X + 1, cell.Y)))
                edges.Add(new DirectedGridEdge(cell.X + 1, cell.Y, cell.X + 1, cell.Y + 1));
            if (!occupied.Contains(new GridKey(cell.X, cell.Y + 1)))
                edges.Add(new DirectedGridEdge(cell.X + 1, cell.Y + 1, cell.X, cell.Y + 1));
            if (!occupied.Contains(new GridKey(cell.X - 1, cell.Y)))
                edges.Add(new DirectedGridEdge(cell.X, cell.Y + 1, cell.X, cell.Y));
        }

        var outgoing = new Dictionary<GridVertex, List<int>>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (!outgoing.TryGetValue(edges[i].Start, out List<int> edgeIndices))
            {
                edgeIndices = new List<int>();
                outgoing.Add(edges[i].Start, edgeIndices);
            }
            edgeIndices.Add(i);
        }

        var used = new bool[edges.Count];
        List<GridVertex> largestRing = null;
        double largestArea = double.NegativeInfinity;
        for (int startEdgeIndex = 0; startEdgeIndex < edges.Count; startEdgeIndex++)
        {
            if (used[startEdgeIndex])
                continue;
            List<GridVertex> ring = TraceGridRing(startEdgeIndex, edges, outgoing, used);
            if (ring.Count < 3)
                continue;
            double signedArea = SignedArea(ring);
            if (signedArea > largestArea)
            {
                largestArea = signedArea;
                largestRing = ring;
            }
        }
        return largestRing ?? new List<GridVertex>();
    }

    private static List<GridVertex> TraceGridRing(
        int startEdgeIndex,
        List<DirectedGridEdge> edges,
        Dictionary<GridVertex, List<int>> outgoing,
        bool[] used)
    {
        var ring = new List<GridVertex>();
        DirectedGridEdge startEdge = edges[startEdgeIndex];
        GridVertex start = startEdge.Start;
        int currentEdgeIndex = startEdgeIndex;
        ring.Add(start);
        for (int guard = 0; guard <= edges.Count; guard++)
        {
            if (used[currentEdgeIndex])
                return new List<GridVertex>();
            DirectedGridEdge current = edges[currentEdgeIndex];
            used[currentEdgeIndex] = true;
            if (current.End.Equals(start))
                return ring;
            ring.Add(current.End);
            if (!outgoing.TryGetValue(current.End, out List<int> candidates))
                return new List<GridVertex>();
            int nextEdgeIndex = SelectNextGridEdge(current.Direction, candidates, edges, used);
            if (nextEdgeIndex < 0)
                return new List<GridVertex>();
            currentEdgeIndex = nextEdgeIndex;
        }
        return new List<GridVertex>();
    }

    private static int SelectNextGridEdge(
        int incomingDirection,
        List<int> candidates,
        List<DirectedGridEdge> edges,
        bool[] used)
    {
        int bestIndex = -1;
        int bestRank = int.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            int edgeIndex = candidates[i];
            if (used[edgeIndex])
                continue;
            int turn = (edges[edgeIndex].Direction - incomingDirection + 4) % 4;
            int rank;
            switch (turn)
            {
                case 3: rank = 0; break;
                case 0: rank = 1; break;
                case 1: rank = 2; break;
                default: rank = 3; break;
            }
            if (rank < bestRank)
            {
                bestRank = rank;
                bestIndex = edgeIndex;
            }
        }
        return bestIndex;
    }

    private static double SignedArea(List<GridVertex> ring)
    {
        double twiceArea = 0.0;
        for (int i = 0; i < ring.Count; i++)
        {
            GridVertex current = ring[i];
            GridVertex next = ring[(i + 1) % ring.Count];
            twiceArea += (double)current.X * next.Y - (double)next.X * current.Y;
        }
        return twiceArea * 0.5;
    }

    private static List<GridVertex> RemoveCollinearGridVertices(List<GridVertex> ring)
    {
        if (ring.Count < 4)
            return ring;
        var simplified = new List<GridVertex>();
        for (int i = 0; i < ring.Count; i++)
        {
            GridVertex previous = ring[(i - 1 + ring.Count) % ring.Count];
            GridVertex current = ring[i];
            GridVertex next = ring[(i + 1) % ring.Count];
            long cross = (long)(current.X - previous.X) * (next.Y - current.Y) -
                         (long)(current.Y - previous.Y) * (next.X - current.X);
            if (cross != 0)
                simplified.Add(current);
        }
        return simplified.Count >= 3 ? simplified : ring;
    }

    private static List<MetricPoint> SimplifyClosedRing(
        List<MetricPoint> ring, double tolerance)
    {
        if (ring.Count <= 3 || tolerance <= 0.0)
            return ring;
        int first = 0;
        int second = FindFarthestPoint(ring, first);
        first = FindFarthestPoint(ring, second);
        second = FindFarthestPoint(ring, first);
        if (first == second)
            return ring;

        List<MetricPoint> firstArc = BuildRingArc(ring, first, second);
        List<MetricPoint> secondArc = BuildRingArc(ring, second, first);
        firstArc = SimplifyOpenLine(firstArc, tolerance);
        secondArc = SimplifyOpenLine(secondArc, tolerance);
        var simplified = new List<MetricPoint>(firstArc);
        for (int i = 1; i < secondArc.Count - 1; i++)
            simplified.Add(secondArc[i]);
        return simplified.Count >= 3 ? simplified : ring;
    }

    private static int FindFarthestPoint(List<MetricPoint> points, int originIndex)
    {
        int farthestIndex = originIndex;
        double farthestDistance = -1.0;
        for (int i = 0; i < points.Count; i++)
        {
            double distance = SquaredDistance(
                points[originIndex].East,
                points[originIndex].North,
                points[i].East,
                points[i].North);
            if (distance > farthestDistance)
            {
                farthestDistance = distance;
                farthestIndex = i;
            }
        }
        return farthestIndex;
    }

    private static List<MetricPoint> BuildRingArc(
        List<MetricPoint> ring, int startIndex, int endIndex)
    {
        var arc = new List<MetricPoint>();
        int index = startIndex;
        arc.Add(ring[index]);
        while (index != endIndex)
        {
            index = (index + 1) % ring.Count;
            arc.Add(ring[index]);
        }
        return arc;
    }

    private static List<MetricPoint> SimplifyOpenLine(
        List<MetricPoint> points, double tolerance)
    {
        if (points.Count <= 2)
            return points;
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[points.Count - 1] = true;
        MarkRamerDouglasPeucker(points, 0, points.Count - 1, tolerance * tolerance, keep);
        var simplified = new List<MetricPoint>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
                simplified.Add(points[i]);
        }
        return simplified;
    }

    private static void MarkRamerDouglasPeucker(
        List<MetricPoint> points,
        int startIndex,
        int endIndex,
        double squaredTolerance,
        bool[] keep)
    {
        if (endIndex <= startIndex + 1)
            return;
        double maximumSquaredDistance = -1.0;
        int farthestIndex = -1;
        for (int i = startIndex + 1; i < endIndex; i++)
        {
            double squaredDistance = SquaredDistanceToSegment(
                points[i], points[startIndex], points[endIndex]);
            if (squaredDistance > maximumSquaredDistance)
            {
                maximumSquaredDistance = squaredDistance;
                farthestIndex = i;
            }
        }
        if (maximumSquaredDistance <= squaredTolerance || farthestIndex < 0)
            return;
        keep[farthestIndex] = true;
        MarkRamerDouglasPeucker(
            points, startIndex, farthestIndex, squaredTolerance, keep);
        MarkRamerDouglasPeucker(
            points, farthestIndex, endIndex, squaredTolerance, keep);
    }

    private static double SquaredDistanceToSegment(
        MetricPoint point, MetricPoint start, MetricPoint end)
    {
        double deltaEast = end.East - start.East;
        double deltaNorth = end.North - start.North;
        double squaredLength = deltaEast * deltaEast + deltaNorth * deltaNorth;
        if (squaredLength <= double.Epsilon)
        {
            return SquaredDistance(
                point.East, point.North, start.East, start.North);
        }
        double projection = ((point.East - start.East) * deltaEast +
                             (point.North - start.North) * deltaNorth) / squaredLength;
        projection = Math.Max(0.0, Math.Min(1.0, projection));
        return SquaredDistance(
            point.East,
            point.North,
            start.East + projection * deltaEast,
            start.North + projection * deltaNorth);
    }

    private static bool IsSimplePolygon(List<MetricPoint> ring)
    {
        if (ring.Count < 3)
            return false;
        for (int firstEdge = 0; firstEdge < ring.Count; firstEdge++)
        {
            int firstEnd = (firstEdge + 1) % ring.Count;
            for (int secondEdge = firstEdge + 1; secondEdge < ring.Count; secondEdge++)
            {
                int secondEnd = (secondEdge + 1) % ring.Count;
                if (firstEdge == secondEdge || firstEnd == secondEdge ||
                    secondEnd == firstEdge)
                {
                    continue;
                }
                if (SegmentsIntersect(
                        ring[firstEdge], ring[firstEnd], ring[secondEdge], ring[secondEnd]))
                    return false;
            }
        }
        return true;
    }

    private static bool SegmentsIntersect(
        MetricPoint firstStart,
        MetricPoint firstEnd,
        MetricPoint secondStart,
        MetricPoint secondEnd)
    {
        double firstSideStart = MetricCross(firstStart, firstEnd, secondStart);
        double firstSideEnd = MetricCross(firstStart, firstEnd, secondEnd);
        double secondSideStart = MetricCross(secondStart, secondEnd, firstStart);
        double secondSideEnd = MetricCross(secondStart, secondEnd, firstEnd);
        const double tolerance = 1e-9;
        if (Math.Abs(firstSideStart) <= tolerance &&
            IsPointOnSegment(secondStart, firstStart, firstEnd, tolerance))
            return true;
        if (Math.Abs(firstSideEnd) <= tolerance &&
            IsPointOnSegment(secondEnd, firstStart, firstEnd, tolerance))
            return true;
        if (Math.Abs(secondSideStart) <= tolerance &&
            IsPointOnSegment(firstStart, secondStart, secondEnd, tolerance))
            return true;
        if (Math.Abs(secondSideEnd) <= tolerance &&
            IsPointOnSegment(firstEnd, secondStart, secondEnd, tolerance))
            return true;
        return (firstSideStart > 0.0) != (firstSideEnd > 0.0) &&
               (secondSideStart > 0.0) != (secondSideEnd > 0.0);
    }

    private static double MetricCross(
        MetricPoint origin, MetricPoint first, MetricPoint second)
    {
        return (first.East - origin.East) * (second.North - origin.North) -
               (first.North - origin.North) * (second.East - origin.East);
    }

    private static bool IsPointOnSegment(
        MetricPoint point,
        MetricPoint start,
        MetricPoint end,
        double tolerance)
    {
        return point.East >= Math.Min(start.East, end.East) - tolerance &&
               point.East <= Math.Max(start.East, end.East) + tolerance &&
               point.North >= Math.Min(start.North, end.North) - tolerance &&
               point.North <= Math.Max(start.North, end.North) + tolerance;
    }

    private static List<GeoPoint> BuildFallbackConvexHull(
        List<ClusterPoint> coreCluster, List<ClusterPoint> boundaryCluster)
    {
        var points = new List<GeoPoint>(coreCluster.Count + boundaryCluster.Count);
        var unique = new HashSet<GeoPoint>();
        AddUniqueGeoPoints(coreCluster, unique, points);
        AddUniqueGeoPoints(boundaryCluster, unique, points);
        return BuildConvexHull(points);
    }

    private static void AddUniqueGeoPoints(
        List<ClusterPoint> source, HashSet<GeoPoint> unique, List<GeoPoint> output)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (unique.Add(source[i].Geo))
                output.Add(source[i].Geo);
        }
    }

    private static List<GeoPoint> BuildConvexHull(List<GeoPoint> points)
    {
        if (points.Count < 3)
            return points;
        points.Sort(GeoPoint.Compare);
        var lower = new List<GeoPoint>();
        for (int i = 0; i < points.Count; i++)
        {
            while (lower.Count >= 2 && GeoCross(
                       lower[lower.Count - 2], lower[lower.Count - 1], points[i]) <= 0.0)
            {
                lower.RemoveAt(lower.Count - 1);
            }
            lower.Add(points[i]);
        }

        var upper = new List<GeoPoint>();
        for (int i = points.Count - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 && GeoCross(
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

    private static double GeoCross(GeoPoint origin, GeoPoint left, GeoPoint right)
    {
        return (left.Longitude - origin.Longitude) * (right.Latitude - origin.Latitude) -
               (left.Latitude - origin.Latitude) * (right.Longitude - origin.Longitude);
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

    private static bool TryWriteGeoJson(string outputPath, JObject rootObject, out string error)
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (string.IsNullOrWhiteSpace(directory))
                return Fail("Polygon output directory could not be resolved.", out error);
            Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, rootObject.ToString(Formatting.Indented));
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            return Fail($"Could not write polygon GeoJSON: {exception.Message}", out error);
        }
    }

    private static bool IsNumeric(JToken token)
    {
        return token != null &&
               (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);
    }

    private static double SquaredDistance(
        double firstEast,
        double firstNorth,
        double secondEast,
        double secondNorth)
    {
        double eastDelta = firstEast - secondEast;
        double northDelta = firstNorth - secondNorth;
        return eastDelta * eastDelta + northDelta * northDelta;
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }

    private static double RadiansToDegrees(double radians)
    {
        return radians * 180.0 / Math.PI;
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

    private sealed class ClusterConsensusStats
    {
        public readonly HashSet<int> SupportingViews = new HashSet<int>();
        public int ConfirmedCellCount;
        public int MaximumViewSupport;
        public int ExpandedCoreHitCount;
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
            int root = value;
            while (parent[root] != root)
                root = parent[root];
            while (parent[value] != value)
            {
                int next = parent[value];
                parent[value] = root;
                value = next;
            }
            return root;
        }

        public void Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot == rightRoot)
                return;
            if (rank[leftRoot] < rank[rightRoot])
            {
                parent[leftRoot] = rightRoot;
                return;
            }
            parent[rightRoot] = leftRoot;
            if (rank[leftRoot] == rank[rightRoot])
                rank[leftRoot]++;
        }
    }

    private readonly struct SemanticConflictAssessment
    {
        public readonly int ConflictingCoreHitCount;
        public readonly int CoreHitCount;
        public readonly HashSet<string> ConflictDetectionIds;
        public readonly HashSet<int> ConflictViewIndices;
        public readonly bool ShouldReject;

        public double Ratio => CoreHitCount > 0
            ? ConflictingCoreHitCount / (double)CoreHitCount
            : 0.0;

        public SemanticConflictAssessment(
            int conflictingCoreHitCount,
            int coreHitCount,
            HashSet<string> conflictDetectionIds,
            HashSet<int> conflictViewIndices,
            bool shouldReject)
        {
            ConflictingCoreHitCount = conflictingCoreHitCount;
            CoreHitCount = coreHitCount;
            ConflictDetectionIds = conflictDetectionIds;
            ConflictViewIndices = conflictViewIndices;
            ShouldReject = shouldReject;
        }
    }

    private readonly struct MetricReference
    {
        private readonly double longitude;
        private readonly double latitude;
        private readonly double longitudeScale;

        public MetricReference(double longitude, double latitude)
        {
            this.longitude = longitude;
            this.latitude = latitude;
            longitudeScale = Math.Cos(DegreesToRadians(latitude));
        }

        public double LongitudeToEast(double value)
        {
            return EarthRadiusMeters * DegreesToRadians(value - longitude) * longitudeScale;
        }

        public double LatitudeToNorth(double value)
        {
            return EarthRadiusMeters * DegreesToRadians(value - latitude);
        }

        public GeoPoint ToGeoPoint(double east, double north)
        {
            double resultLatitude = latitude + RadiansToDegrees(north / EarthRadiusMeters);
            double resultLongitude = longitude +
                                     RadiansToDegrees(east / (EarthRadiusMeters * longitudeScale));
            return new GeoPoint(resultLongitude, resultLatitude);
        }
    }

    private readonly struct MetricPoint
    {
        public readonly double East;
        public readonly double North;

        public MetricPoint(double east, double north)
        {
            East = east;
            North = north;
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

    private readonly struct GridVertex : IEquatable<GridVertex>
    {
        public readonly int X;
        public readonly int Y;

        public GridVertex(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(GridVertex other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is GridVertex other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Y;
            }
        }
    }

    private readonly struct DirectedGridEdge
    {
        public readonly GridVertex Start;
        public readonly GridVertex End;
        public readonly int Direction;

        public DirectedGridEdge(int startX, int startY, int endX, int endY)
        {
            Start = new GridVertex(startX, startY);
            End = new GridVertex(endX, endY);
            if (endX > startX)
                Direction = 0;
            else if (endY > startY)
                Direction = 1;
            else if (endX < startX)
                Direction = 2;
            else
                Direction = 3;
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
