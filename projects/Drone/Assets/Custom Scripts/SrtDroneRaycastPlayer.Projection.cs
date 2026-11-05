using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

// Capture the real SRT playback camera at an exact frame; restore it before returning.
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
        if (camera == null || droneViewCamera == null || camera == droneViewCamera || width <= 0 || height <= 0)
        {
            error = "Assign the SRT player's Drone View Camera and a separate snapshot destination with positive source dimensions.";
            return false;
        }
        // No yields or rendering here: source pose/lens are restored in this same call.
        using (var saved = new PlaybackCameraScope(this))
        {
            droneViewCamera.aspect = (float)width / height;
            ConfigurePose(frame, false); // The exact code path used by trajectory playback.
            CopyProjectionSnapshot(droneViewCamera, camera);
            RequireCameraRayParity(droneViewCamera, camera, width, height);
        }
        return true;
    }
    private static void CopyProjectionSnapshot(Camera source, Camera destination)
    {
        destination.CopyFrom(source);
        destination.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        // Freeze the actual camera matrices, including physical lens/gate fit and custom matrices.
        destination.projectionMatrix = source.projectionMatrix;
        destination.worldToCameraMatrix = source.worldToCameraMatrix;
        destination.enabled = false;
    }

    private static void RequireCameraRayParity(Camera source, Camera snapshot, int width, int height)
    {
        // Probe corners, edge centres and centre in original-image pixel coordinates.
        for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
        {
            float px = x == 0 ? 0.5f / width : x == 1 ? 0.5f : 1f - 0.5f / width;
            float py = y == 0 ? 0.5f / height : y == 1 ? 0.5f : 1f - 0.5f / height;
            var pixel = new Vector3(px, py, 0);
            Ray expected = source.ViewportPointToRay(pixel), actual = snapshot.ViewportPointToRay(pixel);
            if ((expected.origin - actual.origin).sqrMagnitude > 1e-8f ||
                (expected.direction - actual.direction).sqrMagnitude > 1e-10f)
                throw new InvalidOperationException($"SRT camera snapshot ray mismatch at viewport ({px}, {py}). Projection aborted.");
        }
    }

    [ContextMenu("Check SRT Camera Projection Parity")]
    public void ValidateProjectionCameraParity()
    {
        Camera snapshot = null;
        try
        {
            if (!TryPrepareProjection(true, out string error)) throw new InvalidOperationException(error);
            var obj = new GameObject("SRT Camera Parity Check") { hideFlags = HideFlags.HideAndDontSave };
            obj.SetActive(false);
            snapshot = obj.AddComponent<Camera>();
            int count = Math.Min(8, frames.Count);
            for (int i = 0; i < count; i++)
            {
                int index = count == 1 ? 0 : i * (frames.Count - 1) / (count - 1);
                // Includes a second aspect to catch stale physical-camera gate-fit state.
                foreach (Vector2Int size in new[] { new Vector2Int(3840, 2160), new Vector2Int(2048, 2048) })
                {
                    using (var saved = new PlaybackCameraScope(this))
                    {
                        if (!ConfigureProjectionCamera(snapshot, frames[index], size.x, size.y, out error))
                            throw new InvalidOperationException(error);
                        saved.RequireRestored();
                        droneViewCamera.aspect = (float)size.x / size.y;
                        ConfigurePose(frames[index], false);
                        RequireCameraRayParity(droneViewCamera, snapshot, size.x, size.y);
                    }
                }
            }
            Debug.Log($"[SrtDroneRaycastPlayer] PASS: {count} SRT frames × 2 aspects × 9 pixel rays; playback state preserved.", this);
        }
        catch (Exception ex) { Debug.LogError($"[SrtDroneRaycastPlayer] Camera parity check failed: {ex.Message}", this); }
        finally
        {
            if (snapshot != null)
            {
                if (Application.isPlaying) Destroy(snapshot.gameObject); else DestroyImmediate(snapshot.gameObject);
            }
        }
    }

    private sealed class PlaybackCameraScope : IDisposable
    {
        private readonly SrtDroneRaycastPlayer owner;
        private readonly Camera source, backup;
        private readonly Vector3 dronePosition, droneScale, cameraPosition, cameraScale;
        private readonly Quaternion droneRotation, cameraRotation;
        private readonly bool cameraEnabled;
        private readonly double playbackTime;
        private readonly int playbackIndex;
        private readonly Matrix4x4 projection, worldToCamera;

        public PlaybackCameraScope(SrtDroneRaycastPlayer player)
        {
            owner = player;
            source = player.droneViewCamera;
            dronePosition = owner.transform.localPosition; droneRotation = owner.transform.localRotation; droneScale = owner.transform.localScale;
            cameraPosition = source.transform.localPosition; cameraRotation = source.transform.localRotation; cameraScale = source.transform.localScale;
            cameraEnabled = source.enabled;
            projection = source.projectionMatrix; worldToCamera = source.worldToCameraMatrix;
            playbackTime = owner.timeCursor; playbackIndex = owner.playbackFrameIndex;
            var obj = new GameObject("SRT Camera State Backup") { hideFlags = HideFlags.HideAndDontSave };
            obj.SetActive(false);
            backup = obj.AddComponent<Camera>();
            backup.CopyFrom(source);
            backup.enabled = false;
        }
        public void RequireRestored()
        {
            if (owner.transform.localPosition != dronePosition || owner.transform.localRotation != droneRotation || owner.transform.localScale != droneScale ||
                source.transform.localPosition != cameraPosition || source.transform.localRotation != cameraRotation || source.transform.localScale != cameraScale ||
                source.enabled != cameraEnabled || owner.timeCursor != playbackTime || owner.playbackFrameIndex != playbackIndex ||
                source.usePhysicalProperties != backup.usePhysicalProperties || source.sensorSize != backup.sensorSize ||
                source.focalLength != backup.focalLength || source.gateFit != backup.gateFit || source.aspect != backup.aspect ||
                source.lensShift != backup.lensShift || source.nearClipPlane != backup.nearClipPlane || source.farClipPlane != backup.farClipPlane ||
                source.targetTexture != backup.targetTexture || source.rect != backup.rect)
                throw new InvalidOperationException("SRT playback camera or playback state was not restored.");
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++)
                if (Mathf.Abs(source.projectionMatrix[r,c] - projection[r,c]) > 1e-5f ||
                    Mathf.Abs(source.worldToCameraMatrix[r,c] - worldToCamera[r,c]) > 1e-5f)
                    throw new InvalidOperationException("SRT playback camera matrices were not restored.");
        }
        public void Dispose()
        {
            try
            {
                source.CopyFrom(backup);
                owner.transform.localPosition = dronePosition; owner.transform.localRotation = droneRotation; owner.transform.localScale = droneScale;
                source.transform.localPosition = cameraPosition; source.transform.localRotation = cameraRotation; source.transform.localScale = cameraScale;
                source.enabled = cameraEnabled;
                owner.timeCursor = playbackTime; owner.playbackFrameIndex = playbackIndex;
                RequireRestored();
            }
            finally
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(backup.gameObject);
                else UnityEngine.Object.DestroyImmediate(backup.gameObject);
            }
        }
    }
}
