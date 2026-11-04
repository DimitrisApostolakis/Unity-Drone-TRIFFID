using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using Triffid.Uav;
using UnityEngine;

/// <summary>Point observations from the selected drone views, projected onto the map collider.</summary>
public sealed class PointProjector : UavProjector
{
    [Header("Duplicate Matching Evidence")]
    [SerializeField] private bool collectMergeEvidence=true;
    [Range(4,128)] [SerializeField] private int foregroundSamplesPerObservation=25;
    private JArray metricFrame;
    protected override string TaskName => "point_detections";
    protected override string Geometry => "Point";
    protected override string DefaultOutput => "Exports/uav_points.geojson";

    protected override void Prepare(UavObservationDocument doc)
    {
        if (!player.TryPrepareProjection(true, out string error)) throw new InvalidDataException(error);
        using (var session = new UavProjectionSession(player, new UavProjectionDebug()))
        {
            if(collectMergeEvidence && (!session.HasMetricFrame || foregroundSamplesPerObservation<4 || foregroundSamplesPerObservation>128))
                throw new InvalidDataException("Merger sampling needs a valid metric map frame and 4–128 samples.");
            foreach (UavObservationSource source in doc.Sources) Configure(source, session.Camera);
        }
    }
    protected override void AddProvenance(JObject document)
    {
        document["projection"]["trajectory_srt"] = player.ProjectionSrtPath;
        if(collectMergeEvidence)
        {
            document["projection"]["evidence_space"]="local_enu_metres_v1";
            document["projection"]["world_to_enu_metres_row_major"]=metricFrame;
            document["projection"]["requested_samples_per_observation"]=foregroundSamplesPerObservation;
        }
    }
    private void Configure(UavObservationSource source, Camera camera)
    {
        UavObservationDocument.Require((string)source.Metadata["source_type"] == "video_frame", "Expected video-frame sources.");
        JObject telemetry = source.Metadata["telemetry"] as JObject;
        UavObservationDocument.Require(telemetry != null, $"Missing telemetry for {source.Id}.");
        // This is the SRT record's 0-based FrameCnt, NOT the selected MP4 frame index.
        int frame = checked(UavObservationDocument.Integer(telemetry["frame_index"]) + 1);
        double time = UavObservationDocument.Number(telemetry["timestamp_s"]);
        if (!player.TryConfigureProjectionCamera(camera, frame, time, source.Width, source.Height, out string error))
            throw new InvalidDataException($"{source.Id}: {error}");
    }
    protected override IEnumerator Project(UavObservationDocument doc, UavProjectionSession session)
    {
        int budget = 0;
        metricFrame=session.MetricFrame;
        foreach (UavObservationSource source in doc.Sources)
        {
            Configure(source, session.Camera);
            foreach (JObject observation in source.Observations)
            {
                Status = $"Projecting {source.Id}: {observation["class_name"]}; {session.Features.Count} exported so far.";
                // Bad schemas/masks abort the operation; ordinary ray misses are recorded separately.
                UavBinaryMask mask = UavBinaryMask.Decode(observation, source.Width, source.Height);
                Vector2 point = mask.PointSample(observation);
                string label = $"{source.Id}: {observation["class_name"]}";
                if (session.Project(point, source, label, out JArray coordinates, out JObject surface, out Vector3 _))
                {
                    JObject properties = session.Properties(source, observation);
                    properties["surface_hit"] = surface;
                    if(collectMergeEvidence)
                    {
                        var samples=new JArray();
                        properties["mask_evidence"]=observation["mask"].DeepClone();
                        properties["world_to_clip_row_major"]=UavProjectionSession.Matrix(session.Camera.projectionMatrix*session.Camera.worldToCameraMatrix);
                        foreach(Vector2 pixel in mask.ForegroundSamples(foregroundSamplesPerObservation))
                        {
                            if(session.Project(pixel,source,label,out JArray _,out JObject sample,out Vector3 _,false)) samples.Add(sample);
                            if(++budget>=raysPerUpdate) { budget=0;yield return null; }
                        }
                        properties["surface_samples"]=samples;
                    }
                    session.Features.Add(new JObject { ["type"] = "Feature", ["properties"] = properties,
                        ["geometry"] = new JObject { ["type"] = "Point", ["coordinates"] = coordinates } });
                }
                else session.Skip(source, observation, "Foreground sample missed the map collider.");
                if (++budget >= raysPerUpdate) { budget = 0; yield return null; }
            }
            yield return null;
        }
    }
}
