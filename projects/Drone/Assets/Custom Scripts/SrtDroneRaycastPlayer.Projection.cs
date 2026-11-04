using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

// Shared calibration API. Projectors configure a private camera, never the playback camera.
public sealed partial class SrtDroneRaycastPlayer
{
    public string ProjectionSrtPath => resolvedSrtPath;
    public string ProjectionTransformPath => resolvedTransformConfigPath;

    public bool TryPrepareProjection(bool usePlaybackSrt, out string error)
    {
        if (usePlaybackSrt)
        {
            double savedTime = timeCursor;
            bool hadFrames = frames.Count > 0;
            ParseSrt();
            if (hadFrames && frames.Count > 0)
            {
                timeCursor = Math.Max(frames[0].timeSeconds, Math.Min(savedTime, frames[frames.Count - 1].timeSeconds));
                playbackFrameIndex = GetFrameIndexForTime(timeCursor);
            }
        }
        LoadTransformConfig();
        return TryGetGeoreferencingReadiness(out error, usePlaybackSrt);
    }

    public bool TryConfigureProjectionCamera(Camera camera, int frameCount, double telemetryTime,
        int width, int height, out string error)
    {
        error = string.Empty;
        if (!frameIndexByCount.TryGetValue(frameCount, out int index))
        {
            error = $"Telemetry FrameCnt {frameCount} is missing. Use the SRT exported with these frames.";
            return false;
        }
        SrtFrame frame = frames[index];
        if (Math.Abs(frame.timeSeconds - telemetryTime) > 0.002)
        {
            error = $"FrameCnt {frameCount} timestamp differs from the detection JSON. Check the SRT.";
            return false;
        }
        return ConfigureProjectionCamera(camera, frame, width, height, out error);
    }

    public bool TryConfigurePhotoCamera(Camera camera, string photoSrt, int frameCount,
        int width, int height, out string error)
    {
        // Read only the requested exact capture. Do not replace the trajectory's frames/time.
        error = string.Empty;
        SrtFrame selected = default;
        bool found = false;
        var seen = new HashSet<int>();
        foreach (string block in Regex.Split(File.ReadAllText(photoSrt).Trim(), @"\r?\n\s*\r?\n"))
        {
            if (!TryParseSrtBlock(block, out SrtFrame frame, out error)) return false;
            if (!seen.Add(frame.frameCnt))
            {
                error = $"Duplicate FrameCnt {frame.frameCnt} in photo SRT.";
                return false;
            }
            if (frame.frameCnt != frameCount) continue;
            selected = frame;
            found = true;
        }
        if (!found)
        {
            error = $"Capture FrameCnt {frameCount} is absent from the photo SRT.";
            return false;
        }
        return ConfigureProjectionCamera(camera, selected, width, height, out error);
    }

    private bool ConfigureProjectionCamera(Camera camera, SrtFrame frame,
        int width, int height, out string error)
    {
        error = string.Empty;
        if (camera == null || width <= 0 || height <= 0)
        {
            error = "A projection camera and positive source dimensions are required.";
            return false;
        }
        BuildRayOriginAndDirection(frame, out Vector3 _, out Vector3 direction, out Vector3 origin);
        UpdateDroneViewCamera(origin, direction, frame, camera, (float)width / height);
        return true;
    }
}
