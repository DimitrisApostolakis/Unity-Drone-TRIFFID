using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using GaussianSplatting.Runtime;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Parses DJI SRT telemetry and owns the camera pose and map raycast used by live playback
/// and the top-view splat projector.
/// </summary>
public sealed class SrtDroneRaycastPlayer : MonoBehaviour
{
    [Header("1. Input Path Loading")]
    [Tooltip("SRT file path. Relative paths use Input Path Root.")]
    [SerializeField] private string srtFilePath = string.Empty;
    [Tooltip("Transform config JSON path. Relative paths use Input Path Root.")]
    [SerializeField] private string transformConfigFilePath = string.Empty;
    [SerializeField] private ProjectPathResolver.PathRoot inputPathRoot =
        ProjectPathResolver.PathRoot.StreamingAssets;
    [SerializeField] private bool useSharedInputPathsJson;
    [SerializeField] private string sharedInputPathsJsonFile = "project_paths.json";
    [SerializeField] private string srtPathJsonKey = "srt_path";
    [SerializeField] private string transformPathJsonKey = "transform_json_path";

    [Header("2. Scene Reference")]
    [Tooltip("Root transform for the reconstructed COLMAP map or terrain.")]
    [SerializeField] private Transform mapRoot;

    [Header("3. Playback")]
    [SerializeField] private bool useAbsAltitude = true;
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private bool loopPlayback = true;
    [SerializeField] private bool interpolateFrames = true;

    [Header("4. Alignment Corrections")]
    [SerializeField] private bool flipPositionX;
    [SerializeField] private bool flipPositionY;
    [SerializeField] private bool flipDirectionX = true;
    [SerializeField] private bool flipDirectionY;

    [Header("5. Raycast Target")]
    [SerializeField] private float raycastDistance = 5000f;
    [SerializeField] private LayerMask raycastMask = ~0;
    [SerializeField] private Collider targetCollider;
    [SerializeField] private bool raycastTargetOnly;

    [Header("6. DJI Gimbal")]
    [SerializeField] private float yawOffsetDegrees;
    [SerializeField] private Vector3 gimbalBaseRotationEuler = Vector3.zero;
    [SerializeField] private bool flipYaw = true;
    [SerializeField] private bool flipPitch = true;
    [SerializeField] private bool flipRoll;

    [Header("7. Drone View Camera")]
    [SerializeField] private Camera droneViewCamera;
    [Tooltip("Make Drone View Camera the full-screen Display 1 camera when Play Mode starts.")]
    [SerializeField] private bool showInGameView = true;
    [SerializeField] private bool estimateFovFromFocalLength = true;
    [SerializeField] private bool srtFocalLengthIs35mmEquivalent = true;
    [SerializeField] private float sensorHeightMm = 7.66f;
    [SerializeField] private float fixedVerticalFovDegrees = 60f;

    [Header("8. Debug")]
    [SerializeField] private bool drawDebug = true;

    private readonly List<SrtFrame> frames = new List<SrtFrame>();
    private readonly Dictionary<int, int> frameIndexByCount = new Dictionary<int, int>();
    private readonly HashSet<int> missingFrameCounts = new HashSet<int>();
    private TransformConfig transformData;
    private double timeCursor;
    private int playbackFrameIndex;
    private bool hasDuplicateFrameCounts;
    private string resolvedSrtPath = string.Empty;
    private string resolvedTransformConfigPath = string.Empty;
    private bool loggedInvalidFocalLengthFallback;

    private static readonly Regex TimeRegex = new Regex(
        @"(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2}),(?<ms>\d{3})", RegexOptions.Compiled);
    private static readonly Regex FrameCntRegex = new Regex(
        @"FrameCnt\s*:\s*(?<v>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LatRegex = NumberRegex("latitude");
    private static readonly Regex LonRegex = NumberRegex("longitude");
    private static readonly Regex RelAltRegex = NumberRegex("rel_alt");
    private static readonly Regex AbsAltRegex = NumberRegex("abs_alt");
    private static readonly Regex YawRegex = NumberRegex("gb_yaw");
    private static readonly Regex PitchRegex = NumberRegex("gb_pitch");
    private static readonly Regex RollRegex = NumberRegex("gb_roll");
    private static readonly Regex FocalLenRegex = NumberRegex("focal_len");

    private static readonly Vector3 EnuEast = new Vector3(1f, 0f, 0f);
    private static readonly Vector3 EnuNorth = new Vector3(0f, 1f, 0f);
    private static readonly Vector3 EnuUp = new Vector3(0f, 0f, 1f);

    public Camera DroneViewCamera => droneViewCamera;

    private static Regex NumberRegex(string fieldName)
    {
        return new Regex(
            Regex.Escape(fieldName) + @"\s*:\s*(?<v>[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    private void Awake()
    {
        ConfigureGameViewCamera();
        ReloadInputs();
    }

    private void OnEnable()
    {
        if (Application.isPlaying)
            ConfigureGameViewCamera();
    }

    private void ConfigureGameViewCamera()
    {
        if (!showInGameView || droneViewCamera == null)
            return;

        droneViewCamera.targetTexture = null;
        droneViewCamera.targetDisplay = 0;
        droneViewCamera.rect = new Rect(0f, 0f, 1f, 1f);
        droneViewCamera.enabled = true;
    }

    private void Update()
    {
        if (frames.Count == 0 || !GeoUtils.IsValidTransformData(transformData))
            return;

        timeCursor += Time.deltaTime * Mathf.Max(0f, playbackSpeed);
        double endTime = frames[frames.Count - 1].timeSeconds;
        if (timeCursor > endTime)
        {
            if (loopPlayback)
            {
                timeCursor = frames[0].timeSeconds;
                playbackFrameIndex = 0;
            }
            else
            {
                timeCursor = endTime;
                playbackFrameIndex = frames.Count - 1;
            }
        }

        while (playbackFrameIndex < frames.Count - 1 &&
               frames[playbackFrameIndex + 1].timeSeconds <= timeCursor)
            playbackFrameIndex++;

        SrtFrame frame = interpolateFrames
            ? GetInterpolatedFrame(timeCursor)
            : frames[playbackFrameIndex];
        ConfigurePose(frame, true);
    }

    [ContextMenu("Reload SRT And Transform")]
    public void ReloadInputs()
    {
        ParseSrt();
        LoadTransformConfig();
    }

    /// <summary>
    /// Synchronously prepares all file-backed and scene-backed state needed for projection.
    /// In Edit Mode an already assigned camera is mandatory; no Camera GameObject is created.
    /// </summary>
    public bool TryPrepareForGeoreferencing(out string reason)
    {
        ParseSrt();
        LoadTransformConfig();

        if (droneViewCamera == null)
        {
            reason = "Drone View Camera must be assigned.";
            return false;
        }

        return TryGetGeoreferencingReadiness(out reason);
    }

    [ContextMenu("Validate Setup")]
    public void ValidateSetup()
    {
        ReloadInputs();
        if (TryGetGeoreferencingReadiness(out string reason))
            Debug.Log($"[SrtDroneRaycastPlayer] Ready. Parsed {frames.Count} SRT frames.", this);
        else
            Debug.LogError($"[SrtDroneRaycastPlayer] Not ready: {reason}", this);
    }

    private bool TryGetGeoreferencingReadiness(out string reason)
    {
        if (string.IsNullOrWhiteSpace(resolvedSrtPath) || !File.Exists(resolvedSrtPath))
        {
            reason = $"Missing SRT file: '{resolvedSrtPath}'.";
            return false;
        }
        if (frames.Count == 0)
        {
            reason = "The SRT contains no valid telemetry frames.";
            return false;
        }
        if (hasDuplicateFrameCounts)
        {
            reason = "The SRT contains duplicate FrameCnt values.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(resolvedTransformConfigPath) ||
            !File.Exists(resolvedTransformConfigPath) || !GeoUtils.IsValidTransformData(transformData))
        {
            reason = $"Missing or invalid transform configuration: '{resolvedTransformConfigPath}'.";
            return false;
        }
        if (droneViewCamera == null)
        {
            reason = "Drone View Camera is not assigned or could not be created.";
            return false;
        }
        if (mapRoot == null)
        {
            reason = "Map Root is not assigned.";
            return false;
        }
        if (!HasValidRaycastDistance())
        {
            reason = "Raycast Distance must be finite and greater than zero.";
            return false;
        }
        if (raycastMask.value == 0)
        {
            reason = "Raycast Mask contains no layers.";
            return false;
        }
        if (raycastTargetOnly && targetCollider == null)
        {
            reason = "Raycast Target Only is enabled but Target Collider is not assigned.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    public bool TryConfigureForFrameCnt(int frameCnt, out string reason)
    {
        if (!TryGetGeoreferencingReadiness(out reason))
            return false;
        if (!frameIndexByCount.TryGetValue(frameCnt, out int index))
        {
            reason = missingFrameCounts.Contains(frameCnt)
                ? $"SRT FrameCnt {frameCnt} is missing or malformed."
                : $"SRT FrameCnt {frameCnt} does not exist.";
            return false;
        }

        ConfigurePose(frames[index], false);
        Physics.SyncTransforms();
        reason = string.Empty;
        return true;
    }

    public ProjectionState CaptureProjectionState()
    {
        Camera camera = droneViewCamera;
        return new ProjectionState(
            transform.position,
            transform.rotation,
            camera,
            camera != null ? camera.transform.position : Vector3.zero,
            camera != null ? camera.transform.rotation : Quaternion.identity,
            camera != null && camera.usePhysicalProperties,
            camera != null ? camera.sensorSize : Vector2.zero,
            camera != null ? camera.focalLength : 0f,
            camera != null ? camera.gateFit : Camera.GateFitMode.None,
            camera != null ? camera.fieldOfView : 0f,
            camera != null ? camera.aspect : 0f,
            timeCursor,
            playbackFrameIndex);
    }

    public void RestoreProjectionState(ProjectionState state)
    {
        transform.position = state.DronePosition;
        transform.rotation = state.DroneRotation;
        if (state.Camera != null)
        {
            state.Camera.transform.position = state.CameraPosition;
            state.Camera.transform.rotation = state.CameraRotation;
            state.Camera.usePhysicalProperties = false;
            state.Camera.fieldOfView = state.CameraFieldOfView;
            state.Camera.aspect = state.CameraAspect;
            state.Camera.sensorSize = state.CameraSensorSize;
            state.Camera.focalLength = state.CameraFocalLength;
            state.Camera.gateFit = state.CameraGateFit;
            state.Camera.usePhysicalProperties = state.CameraUsePhysicalProperties;
        }

        timeCursor = state.PlaybackTimeSeconds;
        playbackFrameIndex = state.PlaybackFrameIndex;
    }

    public bool TryRaycastMap(Ray ray, out RaycastHit hit)
    {
        return TryRaycast(ray.origin, ray.direction, out hit);
    }

    public bool TryConvertWorldToWgs84(Vector3 worldPoint, out double longitude, out double latitude, out double altitude)
    {
        longitude = latitude = altitude = 0.0;
        if (!GeoUtils.IsValidTransformData(transformData) || !IsFinite(worldPoint))
            return false;

        Vector3 colmapPos = mapRoot != null ? mapRoot.InverseTransformPoint(worldPoint) : worldPoint;
        if (flipPositionX)
            colmapPos.x = -colmapPos.x;
        if (flipPositionY)
            colmapPos.y = -colmapPos.y;

        GeoUtils.ColmapToWgs84(colmapPos, transformData, out longitude, out latitude, out altitude);
        return IsFinite(longitude) && IsFinite(latitude) && IsFinite(altitude);
    }

    /// <summary>
    /// Converts an ENU offset expressed in metres to the corresponding world-space vector.
    /// This preserves the COLMAP similarity scale and the Map Root transform, so callers can
    /// construct geographically aligned cameras without assuming that Unity Y is geodetic up.
    /// </summary>
    public bool TryConvertEnuOffsetToWorldVector(Vector3 enuOffsetMeters, out Vector3 worldVector)
    {
        worldVector = Vector3.zero;
        if (!GeoUtils.IsValidTransformData(transformData) || !IsFinite(enuOffsetMeters))
            return false;

        Vector3 colmapVector = EnuToColmapDirection(enuOffsetMeters) /
                               transformData.colmap_to_enu.scale;
        if (flipPositionX)
            colmapVector.x = -colmapVector.x;
        if (flipPositionY)
            colmapVector.y = -colmapVector.y;

        worldVector = mapRoot != null
            ? mapRoot.TransformVector(colmapVector)
            : colmapVector;
        return IsFinite(worldVector) && worldVector.sqrMagnitude > 0.00000001f;
    }

    /// <summary>
    /// Returns normalized world-space East, North and Up directions for the active map.
    /// </summary>
    public bool TryGetWorldEnuAxes(
        out Vector3 worldEast, out Vector3 worldNorth, out Vector3 worldUp)
    {
        worldEast = worldNorth = worldUp = Vector3.zero;
        if (!TryConvertEnuOffsetToWorldVector(EnuEast, out Vector3 east) ||
            !TryConvertEnuOffsetToWorldVector(EnuNorth, out Vector3 north) ||
            !TryConvertEnuOffsetToWorldVector(EnuUp, out Vector3 up))
            return false;

        worldEast = east.normalized;
        worldNorth = north.normalized;
        worldUp = up.normalized;
        return true;
    }

    private void ConfigurePose(SrtFrame frame, bool drawCentralRay)
    {
        BuildRayOriginAndDirection(frame, out Vector3 worldPos, out Vector3 worldDirection, out Vector3 rayOrigin);
        transform.position = worldPos;
        UpdateDroneViewCamera(rayOrigin, worldDirection, frame);

        if (!drawCentralRay || !drawDebug)
            return;

        if (TryRaycast(rayOrigin, worldDirection, out RaycastHit hit))
        {
            Debug.DrawLine(rayOrigin, hit.point, Color.green);
            Debug.DrawRay(hit.point, hit.normal * 0.5f, Color.cyan);
        }
        else
        {
            Debug.DrawRay(rayOrigin, worldDirection * raycastDistance, Color.red);
        }
    }

    private void BuildRayOriginAndDirection(
        SrtFrame frame, out Vector3 worldPos, out Vector3 worldDirection, out Vector3 rayOrigin)
    {
        double altitude = useAbsAltitude ? frame.absoluteAltitude : frame.relativeAltitude;
        Vector3 localPos = GeoUtils.GetColmapPosition(frame.longitude, frame.latitude, altitude, transformData);
        if (flipPositionX)
            localPos.x = -localPos.x;
        if (flipPositionY)
            localPos.y = -localPos.y;

        worldPos = mapRoot != null ? mapRoot.TransformPoint(localPos) : localPos;
        worldDirection = BuildWorldRayDirection(frame);
        rayOrigin = worldPos;
    }

    private Vector3 BuildWorldRayDirection(SrtFrame frame)
    {
        Quaternion enuRotation = BuildEnuRotation(frame.yaw, frame.pitch, frame.roll);
        Vector3 enuForward = enuRotation * EnuNorth;
        Vector3 colmapDirection = EnuToColmapDirection(enuForward);
        if (flipDirectionX)
            colmapDirection.x = -colmapDirection.x;
        if (flipDirectionY)
            colmapDirection.y = -colmapDirection.y;

        Vector3 worldDirection = mapRoot != null
            ? mapRoot.TransformDirection(colmapDirection)
            : colmapDirection;
        return worldDirection.sqrMagnitude > 0.0001f ? worldDirection.normalized : Vector3.forward;
    }

    private Quaternion BuildEnuRotation(float yawDegrees, float pitchDegrees, float rollDegrees)
    {
        float yaw = (yawDegrees + yawOffsetDegrees) * (flipYaw ? -1f : 1f);
        float pitch = pitchDegrees * (flipPitch ? -1f : 1f);
        float roll = rollDegrees * (flipRoll ? -1f : 1f);

        Quaternion yawRotation = Quaternion.AngleAxis(yaw, EnuUp);
        Vector3 rightAxis = yawRotation * EnuEast;
        Quaternion pitchRotation = Quaternion.AngleAxis(pitch, rightAxis);
        Vector3 forwardAxis = (pitchRotation * yawRotation) * EnuNorth;
        Quaternion rollRotation = Quaternion.AngleAxis(roll, forwardAxis);
        return Quaternion.Euler(gimbalBaseRotationEuler) * rollRotation * pitchRotation * yawRotation;
    }

    private Vector3 EnuToColmapDirection(Vector3 enuDirection)
    {
        if (!GeoUtils.IsValidTransformData(transformData))
            return enuDirection;

        float[] rotation = transformData.colmap_to_enu.R_rowmajor;
        return new Vector3(
            rotation[0] * enuDirection.x + rotation[3] * enuDirection.y + rotation[6] * enuDirection.z,
            rotation[1] * enuDirection.x + rotation[4] * enuDirection.y + rotation[7] * enuDirection.z,
            rotation[2] * enuDirection.x + rotation[5] * enuDirection.y + rotation[8] * enuDirection.z);
    }

    private void UpdateDroneViewCamera(Vector3 rayOrigin, Vector3 rayDirection, SrtFrame frame)
    {
        if (droneViewCamera == null)
            return;

        Vector3 forward = rayDirection.sqrMagnitude > 0.0001f ? rayDirection.normalized : transform.forward;
        Vector3 up = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.95f)
            up = transform.right;

        droneViewCamera.transform.position = rayOrigin;
        droneViewCamera.transform.rotation = Quaternion.LookRotation(forward, up);

        float sourceAspect = GetCaptureAspectRatio();
        bool hasValidFocalLength = frame.focalLengthMm > 0.0001f && IsFinite(frame.focalLengthMm);
        bool hasValidSensorHeight = sensorHeightMm > 0.0001f && IsFinite(sensorHeightMm);
        bool hasValidPhysicalLens = estimateFovFromFocalLength && hasValidFocalLength &&
                                    (srtFocalLengthIs35mmEquivalent || hasValidSensorHeight);
        if (hasValidPhysicalLens)
        {
            droneViewCamera.usePhysicalProperties = true;
            droneViewCamera.aspect = sourceAspect;
            if (srtFocalLengthIs35mmEquivalent)
            {
                droneViewCamera.sensorSize = new Vector2(36f, 24f);
                droneViewCamera.gateFit = Camera.GateFitMode.Horizontal;
            }
            else
            {
                droneViewCamera.sensorSize = new Vector2(sensorHeightMm * sourceAspect, sensorHeightMm);
            }
            droneViewCamera.focalLength = frame.focalLengthMm;
            return;
        }

        if (estimateFovFromFocalLength && !loggedInvalidFocalLengthFallback)
        {
            loggedInvalidFocalLengthFallback = true;
            string invalidLensReason = srtFocalLengthIs35mmEquivalent
                ? "focal_len is invalid"
                : "focal_len or Sensor Height Mm is invalid";
            Debug.LogWarning(
                $"[SrtDroneRaycastPlayer] Focal-length FOV is enabled, but {invalidLensReason}. " +
                "Using Fixed Vertical FOV Degrees.",
                this);
        }

        droneViewCamera.usePhysicalProperties = false;
        droneViewCamera.aspect = sourceAspect;
        droneViewCamera.fieldOfView = Mathf.Clamp(fixedVerticalFovDegrees, 1f, 179f);
    }

    private float GetCaptureAspectRatio()
    {
        if (droneViewCamera != null && IsFinite(droneViewCamera.aspect) && droneViewCamera.aspect > 0f)
            return droneViewCamera.aspect;
        return 16f / 9f;
    }

    private bool TryRaycast(Vector3 origin, Vector3 direction, out RaycastHit bestHit)
    {
        bestHit = default;
        if (!HasValidRaycastDistance() || direction.sqrMagnitude <= 0.0001f)
            return false;

        RaycastHit[] hits = Physics.RaycastAll(
            origin, direction.normalized, raycastDistance, raycastMask, QueryTriggerInteraction.Ignore);
        float closestDistance = float.MaxValue;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit candidate = hits[i];
            if (!PassesTargetFilter(candidate.collider) || candidate.distance >= closestDistance)
                continue;

            closestDistance = candidate.distance;
            bestHit = candidate;
        }

        return closestDistance < float.MaxValue;
    }

    private bool PassesTargetFilter(Collider collider)
    {
        if (collider == null)
            return false;
        if (!raycastTargetOnly)
            return true;
        if (targetCollider == null)
            return false;

        Transform targetTransform = targetCollider.transform;
        return collider == targetCollider || collider.transform == targetTransform ||
               collider.transform.IsChildOf(targetTransform);
    }

    private bool HasValidRaycastDistance()
    {
        return raycastDistance > 0f && IsFinite(raycastDistance);
    }

    private void LoadTransformConfig()
    {
        transformData = null;
        if (!TryResolveInputPath(
                transformConfigFilePath,
                transformPathJsonKey,
                out resolvedTransformConfigPath,
                out string resolveError))
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] Could not resolve transform config: {resolveError}", this);
            return;
        }
        if (string.IsNullOrWhiteSpace(resolvedTransformConfigPath) || !File.Exists(resolvedTransformConfigPath))
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] Missing transform config: '{resolvedTransformConfigPath}'.", this);
            return;
        }

        try
        {
            TransformConfig parsed = JsonConvert.DeserializeObject<TransformConfig>(
                File.ReadAllText(resolvedTransformConfigPath));
            if (!GeoUtils.IsValidTransformData(parsed))
            {
                Debug.LogError(
                    $"[SrtDroneRaycastPlayer] Invalid transform config: '{resolvedTransformConfigPath}'.", this);
                return;
            }
            transformData = parsed;
        }
        catch (Exception exception)
        {
            Debug.LogError(
                $"[SrtDroneRaycastPlayer] Failed to load transform config '{resolvedTransformConfigPath}': " +
                exception.Message, this);
        }
    }

    private void ParseSrt()
    {
        frames.Clear();
        frameIndexByCount.Clear();
        missingFrameCounts.Clear();
        hasDuplicateFrameCounts = false;
        if (!TryResolveInputPath(
                srtFilePath,
                srtPathJsonKey,
                out resolvedSrtPath,
                out string resolveError))
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] Could not resolve SRT path: {resolveError}", this);
            return;
        }

        if (string.IsNullOrWhiteSpace(resolvedSrtPath) || !File.Exists(resolvedSrtPath))
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] Missing SRT file: '{resolvedSrtPath}'.", this);
            return;
        }

        string srtText;
        try
        {
            srtText = File.ReadAllText(resolvedSrtPath);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] Failed to read SRT '{resolvedSrtPath}': {exception.Message}", this);
            return;
        }

        string[] blocks = Regex.Split(srtText.Trim(), @"\r?\n\s*\r?\n");
        var seenFrameCounts = new HashSet<int>();
        for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            if (!TryParseSrtBlock(blocks[blockIndex], out SrtFrame frame, out string error))
            {
                Debug.LogError(
                    $"[SrtDroneRaycastPlayer] Malformed SRT block {blockIndex + 1} in '{resolvedSrtPath}': {error}",
                    this);
                continue;
            }

            if (!seenFrameCounts.Add(frame.frameCnt))
            {
                hasDuplicateFrameCounts = true;
                Debug.LogError(
                    $"[SrtDroneRaycastPlayer] Duplicate FrameCnt {frame.frameCnt} in '{resolvedSrtPath}'.", this);
                continue;
            }
            frames.Add(frame);
        }

        frames.Sort((left, right) => left.timeSeconds.CompareTo(right.timeSeconds));
        for (int i = 0; i < frames.Count; i++)
            frameIndexByCount[frames[i].frameCnt] = i;

        if (frames.Count == 0)
        {
            Debug.LogError($"[SrtDroneRaycastPlayer] SRT '{resolvedSrtPath}' contains no valid frames.", this);
            return;
        }

        int maximumFrameCount = 0;
        for (int i = 0; i < frames.Count; i++)
            maximumFrameCount = Math.Max(maximumFrameCount, frames[i].frameCnt);
        for (int frameCnt = 1; frameCnt <= maximumFrameCount; frameCnt++)
        {
            if (!frameIndexByCount.ContainsKey(frameCnt))
                missingFrameCounts.Add(frameCnt);
        }
        if (missingFrameCounts.Count > 0)
            Debug.LogError(
                $"[SrtDroneRaycastPlayer] SRT '{resolvedSrtPath}' has {missingFrameCounts.Count} missing or malformed FrameCnt value(s).",
                this);

        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].timeSeconds <= frames[i - 1].timeSeconds)
                Debug.LogWarning(
                    $"[SrtDroneRaycastPlayer] Non-increasing SRT timestamps near FrameCnt {frames[i].frameCnt}.", this);
        }

        timeCursor = frames[0].timeSeconds;
        playbackFrameIndex = 0;
    }

    private static bool TryParseSrtBlock(string block, out SrtFrame frame, out string error)
    {
        frame = default;
        error = string.Empty;
        string[] lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            error = "missing subtitle timestamp or telemetry payload";
            return false;
        }

        Match timeMatch = TimeRegex.Match(lines[1]);
        string payload = Regex.Replace(string.Join(" ", lines), "<[^>]*>", " ");
        if (!timeMatch.Success)
            return Fail("missing or malformed SRT start timestamp", out frame, out error);
        if (!TryParseInt(FrameCntRegex, payload, out int frameCnt) || frameCnt <= 0)
            return Fail("missing or invalid FrameCnt", out frame, out error);
        if (!TryParseDouble(LatRegex, payload, out double latitude))
            return Fail("missing or invalid latitude", out frame, out error);
        if (!TryParseDouble(LonRegex, payload, out double longitude))
            return Fail("missing or invalid longitude", out frame, out error);
        if (!TryParseDouble(RelAltRegex, payload, out double relativeAltitude))
            return Fail("missing or invalid rel_alt", out frame, out error);
        if (!TryParseDouble(AbsAltRegex, payload, out double absoluteAltitude))
            return Fail("missing or invalid abs_alt", out frame, out error);
        if (!TryParseFloat(YawRegex, payload, out float yaw))
            return Fail("missing or invalid gb_yaw", out frame, out error);
        if (!TryParseFloat(PitchRegex, payload, out float pitch))
            return Fail("missing or invalid gb_pitch", out frame, out error);
        if (!TryParseFloat(RollRegex, payload, out float roll))
            return Fail("missing or invalid gb_roll", out frame, out error);
        if (!TryParseFloat(FocalLenRegex, payload, out float focalLength))
            return Fail("missing or invalid focal_len", out frame, out error);

        frame = new SrtFrame
        {
            frameCnt = frameCnt,
            timeSeconds = ParseTimeToSeconds(timeMatch),
            latitude = latitude,
            longitude = longitude,
            relativeAltitude = relativeAltitude,
            absoluteAltitude = absoluteAltitude,
            yaw = yaw,
            pitch = pitch,
            roll = roll,
            focalLengthMm = focalLength
        };
        return true;
    }

    private static bool Fail(string message, out SrtFrame frame, out string error)
    {
        frame = default;
        error = message;
        return false;
    }

    private static double ParseTimeToSeconds(Match match)
    {
        int hours = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        int milliseconds = int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture);
        return hours * 3600.0 + minutes * 60.0 + seconds + milliseconds / 1000.0;
    }

    private static bool TryParseInt(Regex regex, string input, out int value)
    {
        value = 0;
        Match match = regex.Match(input);
        return match.Success && int.TryParse(
            match.Groups["v"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseFloat(Regex regex, string input, out float value)
    {
        value = 0f;
        Match match = regex.Match(input);
        return match.Success && float.TryParse(
            match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               IsFinite(value);
    }

    private static bool TryParseDouble(Regex regex, string input, out double value)
    {
        value = 0.0;
        Match match = regex.Match(input);
        return match.Success && double.TryParse(
            match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               IsFinite(value);
    }

    private SrtFrame GetInterpolatedFrame(double timeSeconds)
    {
        if (timeSeconds <= frames[0].timeSeconds)
            return frames[0];
        if (timeSeconds >= frames[frames.Count - 1].timeSeconds)
            return frames[frames.Count - 1];

        int index = GetFrameIndexForTime(timeSeconds);
        int nextIndex = Mathf.Min(index + 1, frames.Count - 1);
        SrtFrame left = frames[index];
        SrtFrame right = frames[nextIndex];
        double span = right.timeSeconds - left.timeSeconds;
        if (span <= 0.000001)
            return left;

        float t = (float)Math.Max(0.0, Math.Min(1.0, (timeSeconds - left.timeSeconds) / span));
        return new SrtFrame
        {
            frameCnt = left.frameCnt,
            timeSeconds = LerpDouble(left.timeSeconds, right.timeSeconds, t),
            latitude = LerpDouble(left.latitude, right.latitude, t),
            longitude = LerpDouble(left.longitude, right.longitude, t),
            relativeAltitude = LerpDouble(left.relativeAltitude, right.relativeAltitude, t),
            absoluteAltitude = LerpDouble(left.absoluteAltitude, right.absoluteAltitude, t),
            yaw = NormalizeDegrees(Mathf.LerpAngle(left.yaw, right.yaw, t)),
            pitch = NormalizeDegrees(Mathf.LerpAngle(left.pitch, right.pitch, t)),
            roll = NormalizeDegrees(Mathf.LerpAngle(left.roll, right.roll, t)),
            focalLengthMm = Mathf.Lerp(left.focalLengthMm, right.focalLengthMm, t)
        };
    }

    private int GetFrameIndexForTime(double timeSeconds)
    {
        int low = 0;
        int high = frames.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (frames[middle].timeSeconds <= timeSeconds)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return Mathf.Clamp(high, 0, frames.Count - 1);
    }

    private static double LerpDouble(double left, double right, float t)
    {
        return left + (right - left) * t;
    }

    private static float NormalizeDegrees(float value)
    {
        value %= 360f;
        if (value > 180f)
            value -= 360f;
        if (value <= -180f)
            value += 360f;
        return value;
    }

    private bool TryResolveInputPath(
        string customPath,
        string pathJsonKey,
        out string resolvedPath,
        out string error)
    {
        return ProjectPathResolver.TryResolveConfiguredPath(
            customPath,
            inputPathRoot,
            useSharedInputPathsJson,
            sharedInputPathsJsonFile,
            pathJsonKey,
            out resolvedPath,
            out error);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    [Serializable]
    private struct SrtFrame
    {
        public int frameCnt;
        public double timeSeconds;
        public double latitude;
        public double longitude;
        public double relativeAltitude;
        public double absoluteAltitude;
        public float yaw;
        public float pitch;
        public float roll;
        public float focalLengthMm;
    }

    public readonly struct ProjectionState
    {
        public Vector3 DronePosition { get; }
        public Quaternion DroneRotation { get; }
        public Camera Camera { get; }
        public Vector3 CameraPosition { get; }
        public Quaternion CameraRotation { get; }
        public bool CameraUsePhysicalProperties { get; }
        public Vector2 CameraSensorSize { get; }
        public float CameraFocalLength { get; }
        public Camera.GateFitMode CameraGateFit { get; }
        public float CameraFieldOfView { get; }
        public float CameraAspect { get; }
        public double PlaybackTimeSeconds { get; }
        public int PlaybackFrameIndex { get; }

        public ProjectionState(
            Vector3 dronePosition,
            Quaternion droneRotation,
            Camera camera,
            Vector3 cameraPosition,
            Quaternion cameraRotation,
            bool cameraUsePhysicalProperties,
            Vector2 cameraSensorSize,
            float cameraFocalLength,
            Camera.GateFitMode cameraGateFit,
            float cameraFieldOfView,
            float cameraAspect,
            double playbackTimeSeconds,
            int playbackFrameIndex)
        {
            DronePosition = dronePosition;
            DroneRotation = droneRotation;
            Camera = camera;
            CameraPosition = cameraPosition;
            CameraRotation = cameraRotation;
            CameraUsePhysicalProperties = cameraUsePhysicalProperties;
            CameraSensorSize = cameraSensorSize;
            CameraFocalLength = cameraFocalLength;
            CameraGateFit = cameraGateFit;
            CameraFieldOfView = cameraFieldOfView;
            CameraAspect = cameraAspect;
            PlaybackTimeSeconds = playbackTimeSeconds;
            PlaybackFrameIndex = playbackFrameIndex;
        }
    }

}
