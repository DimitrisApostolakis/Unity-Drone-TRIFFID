using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Parses DJI SRT telemetry and owns the camera pose and map raycast used by live playback
/// and the point/polygon projectors.
/// </summary>
public sealed partial class SrtDroneRaycastPlayer : MonoBehaviour
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
    [Tooltip("Pause trajectory playback while retaining frame calibration and projection.")]
    [SerializeField] private bool playTrajectory = true;
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

    private static readonly Vector3 EnuEast = new Vector3(1f, 0f, 0f);
    private static readonly Vector3 EnuNorth = new Vector3(0f, 1f, 0f);
    private static readonly Vector3 EnuUp = new Vector3(0f, 0f, 1f);

    public Camera DroneViewCamera => droneViewCamera;

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
        if (!playTrajectory || frames.Count == 0 || !GeoUtils.IsValidTransformData(transformData))
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

    private bool TryGetGeoreferencingReadiness(out string reason, bool requireSrt = true)
    {
        if (requireSrt && (string.IsNullOrWhiteSpace(resolvedSrtPath) || !File.Exists(resolvedSrtPath)))
        {
            reason = $"Missing SRT file: '{resolvedSrtPath}'.";
            return false;
        }
        if (requireSrt && frames.Count == 0)
        {
            reason = "The SRT contains no valid telemetry frames.";
            return false;
        }
        if (requireSrt && hasDuplicateFrameCounts)
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
        Camera camera = droneViewCamera;
        if (camera == null)
            return;

        Vector3 forward = rayDirection.sqrMagnitude > 0.0001f ? rayDirection.normalized : transform.forward;
        Vector3 up = Vector3.up;
        if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.95f)
            up = transform.right;

        camera.transform.position = rayOrigin;
        camera.transform.rotation = Quaternion.LookRotation(forward, up);

        float sourceAspect = GetCaptureAspectRatio();
        bool hasValidFocalLength = frame.focalLengthMm > 0.0001f && IsFinite(frame.focalLengthMm);
        bool hasValidSensorHeight = sensorHeightMm > 0.0001f && IsFinite(sensorHeightMm);
        bool hasValidPhysicalLens = estimateFovFromFocalLength && hasValidFocalLength &&
                                    (srtFocalLengthIs35mmEquivalent || hasValidSensorHeight);
        if (hasValidPhysicalLens)
        {
            camera.usePhysicalProperties = true;
            camera.aspect = sourceAspect;
            if (srtFocalLengthIs35mmEquivalent)
            {
                camera.sensorSize = new Vector2(36f, 24f);
                camera.gateFit = Camera.GateFitMode.Horizontal;
            }
            else
            {
                camera.sensorSize = new Vector2(sensorHeightMm * sourceAspect, sensorHeightMm);
            }
            camera.focalLength = frame.focalLengthMm;
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

        camera.usePhysicalProperties = false;
        camera.aspect = sourceAspect;
        camera.fieldOfView = Mathf.Clamp(fixedVerticalFovDegrees, 1f, 179f);
    }

    private float GetCaptureAspectRatio()
    {
        if (droneViewCamera != null && IsFinite(droneViewCamera.aspect) && droneViewCamera.aspect > 0f)
            return droneViewCamera.aspect;
        return 16f / 9f;
    }

    private RaycastHit[] raycastHits = new RaycastHit[32];

    private bool TryRaycast(Vector3 origin, Vector3 direction, out RaycastHit bestHit)
    {
        bestHit = default;
        if (!HasValidRaycastDistance() || direction.sqrMagnitude <= 0.0001f)
            return false;

        int count;
        while (true)
        {
            count = Physics.RaycastNonAlloc(origin, direction.normalized, raycastHits,
                raycastDistance, raycastMask, QueryTriggerInteraction.Ignore);
            if (count < raycastHits.Length) break;
            Array.Resize(ref raycastHits, checked(raycastHits.Length * 2));
        }
        float closestDistance = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            RaycastHit candidate = raycastHits[i];
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

