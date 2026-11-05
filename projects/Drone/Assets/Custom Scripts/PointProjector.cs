using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using Triffid.Uav;
using UnityEngine;

/// <summary>Point observations from the selected drone views, projected onto the map collider.</summary>
public sealed class PointProjector : UavProjector
{
    protected override string TaskName => "point_detections";
    protected override string Geometry => "Point";
    protected override string DefaultOutput => "Exports/uav_points.geojson";

    protected override void Prepare(UavObservationDocument doc)
    {
        if (!player.TryPrepareProjection(true, out string error)) throw new InvalidDataException(error);
        using (var session = new UavProjectionSession(player, new UavProjectionDebug()))
            foreach (UavObservationSource source in doc.Sources) Configure(source, session.Camera);
    }
    protected override void AddProvenance(JObject document)
    { document["projection"]["trajectory_srt"] = player.ProjectionSrtPath; }
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
