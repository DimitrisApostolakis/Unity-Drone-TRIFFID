using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Triffid.Uav;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>Projects segmentation masks from a real drone photo using its exact SRT capture pose.</summary>
public sealed class PolygonProjector : UavProjector
{
    [Header("Photo Capture")]
    [Tooltip("The original image submitted to segment-top-view. Do not crop or resize it.")]
    [SerializeField] private string photoPath = "";
    [Tooltip("DJI SRT containing the telemetry at this photograph's capture.")]
    [SerializeField] private string photoSrtPath = "";
    [Min(1)] [FormerlySerializedAs("referenceFrameCount")]
    [Tooltip("Exact DJI FrameCnt in Photo SRT, starting at 1. Not a video frame index.")]
    [SerializeField] private int captureFrameCount = 1;

    [Header("Polygon Projection")]
    [Min(1f)] [Tooltip("Add samples along long contour edges so the polygon follows the mesh surface.")]
    [SerializeField] private float maximumSampleSpacingPixels = 32f;
    [Min(4)] [SerializeField] private int maximumVerticesPerObservation = 8192;

    protected override string TaskName => "polygon_segmentation";
    protected override string Geometry => "Polygon";
    protected override string DefaultOutput => "Exports/uav_polygons.geojson";

    protected override void Prepare(UavObservationDocument doc)
    {
        UavObservationDocument.Require(doc.Sources.Count == 1, "Polygon projection expects one top-view photo.");
        UavObservationSource source = doc.Sources[0];
        UavObservationDocument.Require((string)source.Metadata["source_type"] == "top_view_image", "Expected top-view image source.");
        UavObservationDocument.Require(maximumSampleSpacingPixels >= 1f &&
            !float.IsNaN(maximumSampleSpacingPixels) && !float.IsInfinity(maximumSampleSpacingPixels) &&
            maximumVerticesPerObservation >= 4, "Invalid contour sampling settings.");
        string image = Input(photoPath);
        string exportedName = ((string)source.Metadata["image_file"] ?? "").Replace('\\', '/');
        exportedName = exportedName.Substring(exportedName.LastIndexOf('/') + 1);
        UavObservationDocument.Require(string.Equals(Path.GetFileName(image), exportedName, StringComparison.OrdinalIgnoreCase),
            "Photo filename differs from the segmentation source. Use the original segmented photo.");
        var texture = new Texture2D(2, 2);
        try
        {
            UavObservationDocument.Require(texture.LoadImage(File.ReadAllBytes(image)), "Photo is not a supported PNG/JPG.");
            UavObservationDocument.Require(texture.width == source.Width && texture.height == source.Height,
                "Photo dimensions differ from segmentation JSON; resized/cropped masks cannot be projected.");
        }
        finally
        {
            if (Application.isPlaying) Destroy(texture); else DestroyImmediate(texture);
        }
        if (!player.TryPrepareProjection(false, out string error)) throw new InvalidDataException(error);
        using (var session = new UavProjectionSession(player, new UavProjectionDebug())) Configure(source, session.Camera);
    }
    private void Configure(UavObservationSource source, Camera camera)
    {
        if (!player.TryConfigurePhotoCamera(camera, Input(photoSrtPath), captureFrameCount,
                source.Width, source.Height, out string error))
            throw new InvalidDataException(error);
    }
    protected override void AddProvenance(JObject document)
    {
        document["projection"]["photo_file"] = Input(photoPath);
        document["projection"]["photo_srt"] = Input(photoSrtPath);
        document["projection"]["capture_frame_count"] = captureFrameCount;
    }
    protected override IEnumerator Project(UavObservationDocument doc, UavProjectionSession session)
    {
        UavObservationSource source = doc.Sources[0];
        Configure(source, session.Camera);
        int budget = 0;
        foreach (JObject observation in source.Observations)
        {
            Status = $"Projecting {source.Id}: {observation["class_name"]}; {session.Features.Count} exported so far.";
            UavBinaryMask mask = UavBinaryMask.Decode(observation, source.Width, source.Height);
            List<UavMaskContours.Polygon> polygons = UavMaskContours.Trace(mask, maximumVerticesPerObservation);
            var coordinates = new JArray();
            var surfaceHits = new JArray();
            var debugRings = new List<List<Vector3>>();
            bool failed = false;
            int totalVertices = 0;
            // Each exterior and each interior ring must hit in full. Never join across ray misses.
            foreach (UavMaskContours.Polygon polygon in polygons)
            {
                var polygonCoordinates = new JArray();
                var allRings = new List<List<Vector2>> { polygon.Exterior };
                allRings.AddRange(polygon.Holes);
                for (int ringIndex = 0; ringIndex < allRings.Count; ringIndex++)
                {
                    List<Vector2> pixels = Sample(allRings[ringIndex]);
                    totalVertices = checked(totalVertices + pixels.Count);
                    UavObservationDocument.Require(totalVertices <= maximumVerticesPerObservation,
                        "Contour sampling exceeds Maximum Vertices Per Observation.");
                    var ring = new JArray();
                    var worldRing = new List<Vector3>();
                    foreach (Vector2 pixel in pixels)
                    {
                        bool hit = session.Project(pixel, source, (string)observation["class_name"],
                            out JArray coordinate, out JObject surface, out Vector3 world);
                        if (hit) { ring.Add(coordinate); surfaceHits.Add(surface); worldRing.Add(world); }
                        else failed = true;
                        if (++budget >= raysPerUpdate) { budget = 0; yield return null; }
                        if (failed) break;
                    }
                    if (failed) break;
                    // Reject collapsed or self-intersecting geographic rings instead of emitting invalid GeoJSON.
                    if (!UavGeoJsonRing.TryClose(ring, ringIndex == 0)) { failed = true; break; }
                    polygonCoordinates.Add(ring);
                    debugRings.Add(worldRing);
                }
                if (failed) break;
                coordinates.Add(polygonCoordinates);
            }
            if (failed)
                session.Skip(source, observation, "A contour sample missed the mesh, or its geographic ring was invalid; entire observation omitted.");
            else
            {
                JObject properties = session.Properties(source, observation);
                properties["capture_frame_count"] = captureFrameCount;
                properties["surface_hits"] = surfaceHits;
                session.Features.Add(new JObject { ["type"] = "Feature", ["properties"] = properties,
                    ["geometry"] = new JObject
                    {
                        ["type"] = coordinates.Count == 1 ? "Polygon" : "MultiPolygon",
                        ["coordinates"] = coordinates.Count == 1 ? coordinates[0].DeepClone() : coordinates
                    } });
                foreach (List<Vector3> ring in debugRings) DebugView.Ring(ring);
            }
            yield return null;
        }
    }
    private List<Vector2> Sample(List<Vector2> ring)
    {
        var result = new List<Vector2>();
        for (int i = 0; i < ring.Count; i++)
        {
            Vector2 a = ring[i], b = ring[(i + 1) % ring.Count];
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / maximumSampleSpacingPixels));
            UavObservationDocument.Require((long)result.Count + steps <= maximumVerticesPerObservation,
                "Contour sampling exceeds Maximum Vertices Per Observation.");
            for (int step = 0; step < steps; step++) result.Add(Vector2.Lerp(a, b, (float)step / steps));
        }
        return result;
    }
}
