using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public class SrtDroneRaycastPlayer : MonoBehaviour
{
    [Header("1. Build Inputs")]
    [Tooltip("SRT file path. Relative paths use Input Path Root.")]
    [SerializeField] private string srtFilePath = string.Empty;
    [Tooltip("Transform config JSON file path. Relative paths use Input Path Root.")]
    [SerializeField] private string transformConfigFilePath = string.Empty;
    [Tooltip("Root transform for the reconstructed map or terrain.")]
    [SerializeField] private Transform mapRoot;

    [Header("2. Build Paths")]
    [Tooltip("Base folder for relative SRT, transform config, and detection JSON paths.")]
    [SerializeField] private FilePathRoot inputPathRoot = FilePathRoot.StreamingAssets;
    [Tooltip("Base folder for relative CSV output paths.")]
    [SerializeField] private FilePathRoot outputPathRoot = FilePathRoot.PersistentDataPath;

    [Header("3. Playback")]
    [SerializeField] private bool useAbsAltitude = true;
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private bool loopPlayback = true;
    [SerializeField] private bool interpolateFrames = true;
    [SerializeField] private float detectionTimeEpsilon = 0.001f;

    [Header("4. Alignment Corrections")]
    [SerializeField] private bool flipPositionX = false;
    [SerializeField] private bool flipPositionY = false;
    [SerializeField] private bool flipDirectionX = true;
    [SerializeField] private bool flipDirectionY = false;

    [Header("5. Raycast Target")]
    [Tooltip("Maximum ray distance in Unity world units. Increase this if far objects miss.")]
    [SerializeField] private float raycastDistance = 5000f;
    [SerializeField] private LayerMask raycastMask = ~0;
    [Tooltip("Only accept hits on colliders with Target Collider Tag.")]
    [SerializeField] private bool requireTargetTag = false;
    [SerializeField] private string targetColliderTag = string.Empty;
    [Tooltip("Optional collider root for filtering ray hits.")]
    [SerializeField] private Collider targetCollider;
    [Tooltip("Only accept hits on Target Collider or its children.")]
    [SerializeField] private bool raycastTargetOnly = false;
    [SerializeField] private Vector3 cameraOffsetLocal = Vector3.zero;
    [SerializeField] private float rayOriginUpOffset = 0f;
    [SerializeField] private float rayOriginForwardOffset = 0f;

    [Header("6. DJI Gimbal")]
    [SerializeField] private float yawOffsetDegrees = 0f;
    [SerializeField] private Vector3 gimbalBaseRotationEuler = Vector3.zero;

    [SerializeField] private bool flipYaw = true;
    [SerializeField] private bool flipPitch = true;
    [SerializeField] private bool flipRoll = false;

    [Header("7. Drone View Camera")]
    [SerializeField] private Sprite droneSprite;
    [SerializeField] private float droneSpriteScale = 0.2f;
    [Tooltip("Camera used to convert detection pixels into world rays.")]
    [SerializeField] private Camera droneViewCamera;
    [SerializeField] private bool autoCreateDroneViewCamera = false;
    [Tooltip("Use SRT focal_len to set camera FOV. Recommended when SRT has focal_len.")]
    [SerializeField] private bool estimateFovFromFocalLength = true;
    [SerializeField] private float sensorHeightMm = 7.66f;
    [SerializeField] private float fixedVerticalFovDegrees = 60f;

    [Header("8. Optional Full-Frame Pixel Export")]
    [SerializeField] private bool capturePixelCoordinates = false;
    [SerializeField] private bool capturePixelUsingJobs = true;
    [SerializeField] private int pixelStep = 1;
    [SerializeField] private bool captureOnlyHits = true;
    [SerializeField] private int captureEveryNFrames = 1;
    [SerializeField] private bool captureFullFrame = true;
    [SerializeField] private Vector2 captureRegionNormalizedSize = new Vector2(0.5f, 0.5f);
    [SerializeField] private int maxPixelSamplesPerFrame = 4096;
    [SerializeField] private bool invertPixelY = true;
    [SerializeField] private string pixelCsvPath = "Exports/pixel_coordinates.csv";
    [SerializeField] private int pixelCsvFlushEveryNFrames = 30;

    [Header("9. Outputs")]
    [SerializeField] private bool recordRaycastSamples = false;
    [SerializeField] private bool recordMisses = true;
    [SerializeField] private string outputCsvPath = "Exports/raycast_samples.csv";
    [SerializeField] private bool recordDetectionsToCsv = false;
    [SerializeField] private string detectionCsvPath = "Exports/detection_raycast_samples.csv";

    [Header("10. Detection Import")]
    [Tooltip("Detection JSON path. Relative paths use Input Path Root.")]
    [SerializeField] private string detectionJsonPath = "campus_video_detections.json";
    [Tooltip("Automatically process detections at Start. Keep off while tuning.")]
    [SerializeField] private bool processDetectionsOnStart = false;
    [SerializeField] private bool useDetectionRaycastJobs = true;
    [Tooltip("Centroid is general-purpose. BboxBottomCenter is usually better for objects on the ground.")]
    [SerializeField] private DetectionPointMode detectionPointMode = DetectionPointMode.Centroid;
    [Tooltip("Recommended when detections are in original video pixels.")]
    [SerializeField] private bool normalizeDetectionPixels = true;

    [Header("11. Debug")]
    [SerializeField] private bool drawDebug = true;
    [SerializeField] private bool drawPixelRayVisualization = false;
    [SerializeField] private int drawPixelRayEveryN = 16;
    [SerializeField] private Color pixelRayHitColor = new Color(0.15f, 0.95f, 0.25f, 1f);
    [SerializeField] private Color pixelRayMissColor = new Color(1f, 0.25f, 0.25f, 1f);
    [SerializeField] private Color pixelHitNormalColor = new Color(0.2f, 0.75f, 1f, 1f);
    [SerializeField] private float pixelDebugRayDuration = 0.05f;
    [SerializeField] private float pixelDebugNormalLength = 0.2f;
    [SerializeField] private bool drawDetectionHitVisualization = true;
    [SerializeField] private Color detectionHitColor = new Color(0.15f, 0.95f, 0.25f, 1f);
    [SerializeField] private Color detectionHitNormalColor = new Color(0.2f, 0.75f, 1f, 1f);
    [SerializeField] private float detectionDebugRayDuration = 0.1f;

    private readonly List<SrtFrame> frames = new List<SrtFrame>();
    private readonly List<RaycastSample> samples = new List<RaycastSample>();
    private readonly List<DetectionSample> detectionSamples = new List<DetectionSample>();
    private TransformConfig transformData;
    private float timeCursor;
    private int frameIndex;
    private SpriteRenderer droneSpriteRenderer;
    private Camera createdDroneViewCamera;
    private StreamWriter pixelCsvWriter;
    private int pixelCsvFrameCounter;
    private bool outputsSaved;
    

    private static readonly Regex TimeRegex = new Regex(@"(?<h>\d{2}):(?<m>\d{2}):(?<s>\d{2}),(?<ms>\d{3})", RegexOptions.Compiled);
    private static readonly Regex LatRegex = new Regex(@"latitude:\s*(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex LonRegex = new Regex(@"longitude:\s*(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex RelAltRegex = new Regex(@"rel_alt:\s*(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex AbsAltRegex = new Regex(@"abs_alt:\s*(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex YawRegex = new Regex(@"gb_yaw:\s*(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex PitchRegex = new Regex(@"gb_pitch:\s*(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex RollRegex = new Regex(@"gb_roll:\s*(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex FocalLenRegex = new Regex(@"focal_len:\s*(?<v>-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly Vector3 EnuEast = new Vector3(1f, 0f, 0f);
    private static readonly Vector3 EnuNorth = new Vector3(0f, 1f, 0f);
    private static readonly Vector3 EnuUp = new Vector3(0f, 0f, 1f);

    private void Awake()
    {
        LoadTransformConfig();
        ParseSrt();
        EnsureDroneSprite();
        EnsureDroneViewCamera();
        EnsurePixelCsvWriter();

        if (mapRoot == null)
            Debug.LogWarning("[SrtDroneRaycastPlayer] Map Root is not assigned. Coordinates will be treated as Unity world-space coordinates.");
        if (droneViewCamera == null)
            Debug.LogWarning("[SrtDroneRaycastPlayer] Drone View Camera is unavailable. Pixel and detection processing will be skipped.");
        if (!HasValidRaycastDistance())
            Debug.LogWarning("[SrtDroneRaycastPlayer] Raycast Distance must be finite and greater than zero. Raycasts will be skipped.");
    }

    private void Start()
    {
        if (processDetectionsOnStart)
            ProcessDetectionJsonAndExport();
    }

#if UNITY_EDITOR
    private const string InitialConfigurationAssetPath = "Assets/Settings/DroneInitialConfiguration.asset";

    [ContextMenu("Save Current Inspector As Initial Configuration")]
    private void SaveCurrentInspectorAsInitialConfiguration()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("[SrtDroneRaycastPlayer] Save the initial configuration in Edit Mode.", this);
            return;
        }

        DroneInitialConfiguration preset = LoadOrCreateInitialConfiguration();
        if (preset == null)
            return;

        UnityEditor.Undo.RecordObject(preset, "Save Drone Initial Configuration");
        CaptureCurrentInspectorValues(preset);
        UnityEditor.EditorUtility.SetDirty(preset);
        UnityEditor.AssetDatabase.SaveAssets();

        Debug.Log($"[SrtDroneRaycastPlayer] Saved current inspector values to {InitialConfigurationAssetPath}.", preset);
    }

    [ContextMenu("Apply initial configuration")]
    private void ApplyInitialConfiguration()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("[SrtDroneRaycastPlayer] Apply the initial configuration in Edit Mode so the scene can be saved.", this);
            return;
        }

        DroneInitialConfiguration preset = UnityEditor.AssetDatabase.LoadAssetAtPath<DroneInitialConfiguration>(InitialConfigurationAssetPath);
        if (preset == null)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Initial configuration asset was not found at {InitialConfigurationAssetPath}.", this);
            return;
        }

        UnityEditor.Undo.RecordObject(this, "Apply initial configuration");
        ApplyInspectorValues(preset);
        RestoreMissingSceneReferences(preset);
        UnityEditor.EditorUtility.SetDirty(this);
        if (gameObject.scene.IsValid())
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);

        Debug.Log("[SrtDroneRaycastPlayer] Applied the saved initial configuration. Existing object references were preserved.", this);
    }

    private DroneInitialConfiguration LoadOrCreateInitialConfiguration()
    {
        DroneInitialConfiguration preset = UnityEditor.AssetDatabase.LoadAssetAtPath<DroneInitialConfiguration>(InitialConfigurationAssetPath);
        if (preset != null)
            return preset;

        if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/Settings"))
            UnityEditor.AssetDatabase.CreateFolder("Assets", "Settings");

        preset = ScriptableObject.CreateInstance<DroneInitialConfiguration>();
        UnityEditor.AssetDatabase.CreateAsset(preset, InitialConfigurationAssetPath);
        return preset;
    }

    private void CaptureCurrentInspectorValues(DroneInitialConfiguration preset)
    {
        preset.srtFilePath = MakePortablePresetPath(srtFilePath, "Harokopio", "SRT");
        preset.transformConfigFilePath = MakePortablePresetPath(transformConfigFilePath, "Harokopio", "transform config");
        preset.inputPathRoot = inputPathRoot;
        preset.outputPathRoot = outputPathRoot;
        preset.useAbsAltitude = useAbsAltitude;
        preset.playbackSpeed = playbackSpeed;
        preset.loopPlayback = loopPlayback;
        preset.interpolateFrames = interpolateFrames;
        preset.detectionTimeEpsilon = detectionTimeEpsilon;
        preset.flipPositionX = flipPositionX;
        preset.flipPositionY = flipPositionY;
        preset.flipDirectionX = flipDirectionX;
        preset.flipDirectionY = flipDirectionY;
        preset.raycastDistance = raycastDistance;
        preset.raycastMask = raycastMask;
        preset.requireTargetTag = requireTargetTag;
        preset.targetColliderTag = targetColliderTag;
        preset.raycastTargetOnly = raycastTargetOnly;
        preset.cameraOffsetLocal = cameraOffsetLocal;
        preset.rayOriginUpOffset = rayOriginUpOffset;
        preset.rayOriginForwardOffset = rayOriginForwardOffset;
        preset.yawOffsetDegrees = yawOffsetDegrees;
        preset.gimbalBaseRotationEuler = gimbalBaseRotationEuler;
        preset.flipYaw = flipYaw;
        preset.flipPitch = flipPitch;
        preset.flipRoll = flipRoll;
        preset.droneSpriteScale = droneSpriteScale;
        preset.autoCreateDroneViewCamera = autoCreateDroneViewCamera;
        preset.estimateFovFromFocalLength = estimateFovFromFocalLength;
        preset.sensorHeightMm = sensorHeightMm;
        preset.fixedVerticalFovDegrees = fixedVerticalFovDegrees;
        preset.capturePixelCoordinates = capturePixelCoordinates;
        preset.capturePixelUsingJobs = capturePixelUsingJobs;
        preset.pixelStep = pixelStep;
        preset.captureOnlyHits = captureOnlyHits;
        preset.captureEveryNFrames = captureEveryNFrames;
        preset.captureFullFrame = captureFullFrame;
        preset.captureRegionNormalizedSize = captureRegionNormalizedSize;
        preset.maxPixelSamplesPerFrame = maxPixelSamplesPerFrame;
        preset.invertPixelY = invertPixelY;
        preset.pixelCsvPath = MakePortablePresetPath(pixelCsvPath, "Exports", "pixel CSV");
        preset.pixelCsvFlushEveryNFrames = pixelCsvFlushEveryNFrames;
        preset.recordRaycastSamples = recordRaycastSamples;
        preset.recordMisses = recordMisses;
        preset.outputCsvPath = MakePortablePresetPath(outputCsvPath, "Exports", "raycast CSV");
        preset.recordDetectionsToCsv = recordDetectionsToCsv;
        preset.detectionCsvPath = MakePortablePresetPath(detectionCsvPath, "Exports", "detection CSV");
        preset.detectionJsonPath = MakePortablePresetPath(detectionJsonPath, string.Empty, "detection JSON");
        preset.processDetectionsOnStart = processDetectionsOnStart;
        preset.useDetectionRaycastJobs = useDetectionRaycastJobs;
        preset.detectionPointMode = detectionPointMode;
        preset.normalizeDetectionPixels = normalizeDetectionPixels;
        preset.drawDebug = drawDebug;
        preset.drawPixelRayVisualization = drawPixelRayVisualization;
        preset.drawPixelRayEveryN = drawPixelRayEveryN;
        preset.pixelRayHitColor = pixelRayHitColor;
        preset.pixelRayMissColor = pixelRayMissColor;
        preset.pixelHitNormalColor = pixelHitNormalColor;
        preset.pixelDebugRayDuration = pixelDebugRayDuration;
        preset.pixelDebugNormalLength = pixelDebugNormalLength;
        preset.drawDetectionHitVisualization = drawDetectionHitVisualization;
        preset.detectionHitColor = detectionHitColor;
        preset.detectionHitNormalColor = detectionHitNormalColor;
        preset.detectionDebugRayDuration = detectionDebugRayDuration;
        preset.mapRootObjectName = mapRoot != null ? mapRoot.name : string.Empty;
        preset.targetColliderObjectName = targetCollider != null ? targetCollider.name : string.Empty;
        preset.droneViewCameraObjectName = droneViewCamera != null ? droneViewCamera.name : string.Empty;
        preset.droneSprite = droneSprite;
    }

    private void ApplyInspectorValues(DroneInitialConfiguration preset)
    {
        srtFilePath = MakePortablePresetPath(preset.srtFilePath, "Harokopio", "SRT");
        transformConfigFilePath = MakePortablePresetPath(preset.transformConfigFilePath, "Harokopio", "transform config");
        inputPathRoot = preset.inputPathRoot;
        outputPathRoot = preset.outputPathRoot;
        useAbsAltitude = preset.useAbsAltitude;
        playbackSpeed = preset.playbackSpeed;
        loopPlayback = preset.loopPlayback;
        interpolateFrames = preset.interpolateFrames;
        detectionTimeEpsilon = preset.detectionTimeEpsilon;
        flipPositionX = preset.flipPositionX;
        flipPositionY = preset.flipPositionY;
        flipDirectionX = preset.flipDirectionX;
        flipDirectionY = preset.flipDirectionY;
        raycastDistance = preset.raycastDistance;
        raycastMask = preset.raycastMask;
        requireTargetTag = preset.requireTargetTag;
        targetColliderTag = preset.targetColliderTag;
        raycastTargetOnly = preset.raycastTargetOnly;
        cameraOffsetLocal = preset.cameraOffsetLocal;
        rayOriginUpOffset = preset.rayOriginUpOffset;
        rayOriginForwardOffset = preset.rayOriginForwardOffset;
        yawOffsetDegrees = preset.yawOffsetDegrees;
        gimbalBaseRotationEuler = preset.gimbalBaseRotationEuler;
        flipYaw = preset.flipYaw;
        flipPitch = preset.flipPitch;
        flipRoll = preset.flipRoll;
        droneSpriteScale = preset.droneSpriteScale;
        autoCreateDroneViewCamera = preset.autoCreateDroneViewCamera;
        estimateFovFromFocalLength = preset.estimateFovFromFocalLength;
        sensorHeightMm = preset.sensorHeightMm;
        fixedVerticalFovDegrees = preset.fixedVerticalFovDegrees;
        capturePixelCoordinates = preset.capturePixelCoordinates;
        capturePixelUsingJobs = preset.capturePixelUsingJobs;
        pixelStep = preset.pixelStep;
        captureOnlyHits = preset.captureOnlyHits;
        captureEveryNFrames = preset.captureEveryNFrames;
        captureFullFrame = preset.captureFullFrame;
        captureRegionNormalizedSize = preset.captureRegionNormalizedSize;
        maxPixelSamplesPerFrame = preset.maxPixelSamplesPerFrame;
        invertPixelY = preset.invertPixelY;
        pixelCsvPath = MakePortablePresetPath(preset.pixelCsvPath, "Exports", "pixel CSV");
        pixelCsvFlushEveryNFrames = preset.pixelCsvFlushEveryNFrames;
        recordRaycastSamples = preset.recordRaycastSamples;
        recordMisses = preset.recordMisses;
        outputCsvPath = MakePortablePresetPath(preset.outputCsvPath, "Exports", "raycast CSV");
        recordDetectionsToCsv = preset.recordDetectionsToCsv;
        detectionCsvPath = MakePortablePresetPath(preset.detectionCsvPath, "Exports", "detection CSV");
        detectionJsonPath = MakePortablePresetPath(preset.detectionJsonPath, string.Empty, "detection JSON");
        processDetectionsOnStart = preset.processDetectionsOnStart;
        useDetectionRaycastJobs = preset.useDetectionRaycastJobs;
        detectionPointMode = preset.detectionPointMode;
        normalizeDetectionPixels = preset.normalizeDetectionPixels;
        drawDebug = preset.drawDebug;
        drawPixelRayVisualization = preset.drawPixelRayVisualization;
        drawPixelRayEveryN = preset.drawPixelRayEveryN;
        pixelRayHitColor = preset.pixelRayHitColor;
        pixelRayMissColor = preset.pixelRayMissColor;
        pixelHitNormalColor = preset.pixelHitNormalColor;
        pixelDebugRayDuration = preset.pixelDebugRayDuration;
        pixelDebugNormalLength = preset.pixelDebugNormalLength;
        drawDetectionHitVisualization = preset.drawDetectionHitVisualization;
        detectionHitColor = preset.detectionHitColor;
        detectionHitNormalColor = preset.detectionHitNormalColor;
        detectionDebugRayDuration = preset.detectionDebugRayDuration;
    }

    private void RestoreMissingSceneReferences(DroneInitialConfiguration preset)
    {
        RestoreMissingSceneReference(ref mapRoot, preset.mapRootObjectName, "Map Root");
        RestoreMissingSceneReference(ref targetCollider, preset.targetColliderObjectName, "Target Collider");
        RestoreMissingSceneReference(ref droneViewCamera, preset.droneViewCameraObjectName, "Drone View Camera");

        if (droneSprite == null && preset.droneSprite != null)
            droneSprite = preset.droneSprite;
        else if (droneSprite != null && droneSprite != preset.droneSprite)
            Debug.LogWarning("[SrtDroneRaycastPlayer] Existing Drone Sprite was preserved instead of overwriting it from the initial configuration.", this);
    }

    private void RestoreMissingSceneReference<T>(ref T reference, string objectName, string label) where T : Component
    {
        if (reference != null)
        {
            if (!string.IsNullOrWhiteSpace(objectName) && !string.Equals(reference.gameObject.name, objectName, StringComparison.Ordinal))
                Debug.LogWarning($"[SrtDroneRaycastPlayer] Existing {label} '{reference.gameObject.name}' was preserved; the initial configuration names '{objectName}'.", this);
            return;
        }

        GameObject sceneObject = FindUniqueSceneObjectByName(objectName, label);
        if (sceneObject == null)
            return;

        T found = sceneObject.GetComponent<T>();
        if (found == null)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Scene object '{objectName}' has no {typeof(T).Name}; {label} remains unassigned.", this);
            return;
        }

        reference = found;
    }

    private GameObject FindUniqueSceneObjectByName(string objectName, string label)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Initial configuration has no object name for {label}; the reference remains unassigned.", this);
            return null;
        }

        GameObject match = null;
        Transform[] sceneTransforms = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < sceneTransforms.Length; i++)
        {
            GameObject candidate = sceneTransforms[i].gameObject;
            if (!candidate.scene.IsValid() || !string.Equals(candidate.name, objectName, StringComparison.Ordinal))
                continue;

            if (match != null && match != candidate)
            {
                Debug.LogWarning($"[SrtDroneRaycastPlayer] Multiple scene objects named '{objectName}' were found; {label} was not changed.", this);
                return null;
            }

            match = candidate;
        }

        if (match == null)
            Debug.LogWarning($"[SrtDroneRaycastPlayer] No scene object named '{objectName}' was found; {label} remains unassigned.", this);

        return match;
    }

    private string MakePortablePresetPath(string rawPath, string rootedFallbackDirectory, string label)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        string normalized = rawPath.Replace('\\', '/');
        if (!Path.IsPathRooted(rawPath))
            return normalized;

        string fileName = Path.GetFileName(rawPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] The absolute {label} path could not be converted to a portable path and was cleared.", this);
            return string.Empty;
        }

        string portable = string.IsNullOrWhiteSpace(rootedFallbackDirectory)
            ? fileName
            : $"{rootedFallbackDirectory}/{fileName}";
        Debug.LogWarning($"[SrtDroneRaycastPlayer] Converted absolute {label} path to portable preset path '{portable}'.", this);
        return portable;
    }
#endif

    [ContextMenu("Validate Setup")]
    private void ValidateSetup()
    {
        bool ok = true;
        string resolvedSrtPath = ResolveInputPath(srtFilePath);
        string resolvedConfigPath = ResolveInputPath(transformConfigFilePath);

        if (!File.Exists(resolvedSrtPath))
        {
            ok = false;
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Missing SRT file at: {resolvedSrtPath}");
        }
        else
        {
            if (frames.Count == 0)
                ParseSrt();
            if (frames.Count == 0)
                ok = false;
        }

        if (!File.Exists(resolvedConfigPath))
        {
            ok = false;
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Missing transform config file at: {resolvedConfigPath}");
        }
        else
        {
            if (!GeoUtils.IsValidTransformData(transformData))
                LoadTransformConfig();
            if (!GeoUtils.IsValidTransformData(transformData))
                ok = false;
        }

        if (droneViewCamera == null && !autoCreateDroneViewCamera)
        {
            ok = false;
            Debug.LogWarning("[SrtDroneRaycastPlayer] Missing Drone View Camera. Assign one or enable Auto Create Drone View Camera.");
        }

        if (!File.Exists(ResolveInputPath(detectionJsonPath)))
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Detection JSON not found yet: {ResolveInputPath(detectionJsonPath)}");

        if (mapRoot == null)
        {
            ok = false;
            Debug.LogWarning("[SrtDroneRaycastPlayer] Missing Map Root. World-space fallback is safe, but map alignment must be verified.");
        }

        if (raycastTargetOnly && targetCollider == null)
        {
            ok = false;
            Debug.LogWarning("[SrtDroneRaycastPlayer] Raycast Target Only is enabled, but Target Collider is not assigned.");
        }

        if (requireTargetTag && string.IsNullOrWhiteSpace(targetColliderTag))
        {
            ok = false;
            Debug.LogWarning("[SrtDroneRaycastPlayer] Require Target Tag is enabled, but Target Collider Tag is empty.");
        }

        if (!HasValidRaycastDistance())
        {
            ok = false;
            Debug.LogWarning("[SrtDroneRaycastPlayer] Raycast Distance must be finite and greater than zero.");
        }

        if (ok)
            Debug.Log("[SrtDroneRaycastPlayer] Setup looks ready for build/runtime processing.");
    }

    private void Update()
    {
        if (frames.Count == 0 || transformData == null)
            return;

        timeCursor += Time.deltaTime * Mathf.Max(0f, playbackSpeed);

        if (timeCursor > frames[frames.Count - 1].timeSeconds)
        {
            if (loopPlayback)
            {
                timeCursor = 0f;
                frameIndex = 0;
            }
            else
            {
                timeCursor = frames[frames.Count - 1].timeSeconds;
                frameIndex = frames.Count - 1;
            }
        }

        while (frameIndex < frames.Count - 1 && frames[frameIndex + 1].timeSeconds <= timeCursor)
            frameIndex++;

        SrtFrame frame = interpolateFrames ? GetInterpolatedFrame(timeCursor) : frames[frameIndex];
        ApplyFrame(frame);
    }

    private void ApplyFrame(SrtFrame frame)
    {
        Vector3 localPos = GeoUtils.GetColmapPosition(frame.lon, frame.lat, frame.alt, transformData);
        if (flipPositionX)
            localPos.x = -localPos.x;
        if (flipPositionY)
            localPos.y = -localPos.y;
        Vector3 worldPos = mapRoot != null ? mapRoot.TransformPoint(localPos) : localPos;
        Quaternion rootRotation = mapRoot != null ? mapRoot.rotation : Quaternion.identity;

        transform.position = worldPos;
        UpdateDroneSpriteVisual();

        Vector3 worldDir = BuildWorldRayDirection(frame);
        Vector3 rayOrigin = worldPos
            + (rootRotation * cameraOffsetLocal)
            + Vector3.up * rayOriginUpOffset
            + worldDir * rayOriginForwardOffset;

        UpdateDroneViewCamera(rayOrigin, worldDir, frame);
        CapturePixelCoordinatesForFrame(frame, rayOrigin, worldDir);

        bool hit = TryRaycast(rayOrigin, worldDir, out RaycastHit hitInfo);

        if (hit && requireTargetTag && !string.IsNullOrWhiteSpace(targetColliderTag) && !hitInfo.collider.CompareTag(targetColliderTag))
            hit = false;

        if (drawDebug)
        {
            if (hit)
            {
                Debug.DrawLine(rayOrigin, hitInfo.point, Color.green);
                Debug.DrawRay(hitInfo.point, hitInfo.normal * 0.5f, Color.cyan);
            }
            else
            {
                Debug.DrawRay(rayOrigin, worldDir * raycastDistance, Color.red);
            }
        }

        if (recordRaycastSamples && (hit || recordMisses))
        {
            samples.Add(new RaycastSample
            {
                timeSeconds = frame.timeSeconds,
                lat = frame.lat,
                lon = frame.lon,
                alt = frame.alt,
                yaw = frame.yaw,
                pitch = frame.pitch,
                roll = frame.roll,
                origin = rayOrigin,
                direction = worldDir,
                hit = hit,
                hitPoint = hit ? GetColmapHitPoint(hitInfo.point) : Vector3.zero,
                hitNormal = hit ? hitInfo.normal : Vector3.zero,
                hitDistance = hit ? hitInfo.distance : 0f
            });
        }
    }

    private Vector3 BuildWorldRayDirection(SrtFrame frame)
    {
        Quaternion enuRot = BuildEnuRotation(frame.yaw, frame.pitch, frame.roll);
        Vector3 enuForward = enuRot * EnuNorth;

        Vector3 colmapDir = EnuToColmapDirection(enuForward);
        if (flipDirectionX)
            colmapDir.x = -colmapDir.x;
        if (flipDirectionY)
            colmapDir.y = -colmapDir.y;
        Vector3 worldDir = mapRoot != null ? mapRoot.TransformDirection(colmapDir) : colmapDir;
        return worldDir.sqrMagnitude > 0.0001f ? worldDir.normalized : Vector3.forward;
    }

    private void BuildRayOriginAndDirection(SrtFrame frame, out Vector3 worldPos, out Vector3 worldDir, out Vector3 rayOrigin)
    {
        Vector3 localPos = GeoUtils.GetColmapPosition(frame.lon, frame.lat, frame.alt, transformData);
        if (flipPositionX)
            localPos.x = -localPos.x;
        if (flipPositionY)
            localPos.y = -localPos.y;

        worldPos = mapRoot != null ? mapRoot.TransformPoint(localPos) : localPos;
        Quaternion rootRotation = mapRoot != null ? mapRoot.rotation : Quaternion.identity;
        worldDir = BuildWorldRayDirection(frame);
        rayOrigin = worldPos
            + (rootRotation * cameraOffsetLocal)
            + Vector3.up * rayOriginUpOffset
            + worldDir * rayOriginForwardOffset;
    }

    private Quaternion BuildEnuRotation(float yawDeg, float pitchDeg, float rollDeg)
    {
        float yaw = (yawDeg + yawOffsetDegrees) * (flipYaw ? -1f : 1f);
        float pitch = pitchDeg * (flipPitch ? -1f : 1f);
        float roll = rollDeg * (flipRoll ? -1f : 1f);

        Quaternion yawQ = Quaternion.AngleAxis(yaw, EnuUp);
        Vector3 rightAxis = yawQ * EnuEast;
        Quaternion pitchQ = Quaternion.AngleAxis(pitch, rightAxis);
        Vector3 forwardAxis = (pitchQ * yawQ) * EnuNorth;
        Quaternion rollQ = Quaternion.AngleAxis(roll, forwardAxis);

        Quaternion baseQ = Quaternion.Euler(gimbalBaseRotationEuler);
        return baseQ * rollQ * pitchQ * yawQ;
    }

    private Vector3 EnuToColmapDirection(Vector3 enuDir)
    {
        if (!GeoUtils.IsValidTransformData(transformData))
            return enuDir;

        float[] r = transformData.colmap_to_enu.R_rowmajor;
        float colX = r[0] * enuDir.x + r[3] * enuDir.y + r[6] * enuDir.z;
        float colY = r[1] * enuDir.x + r[4] * enuDir.y + r[7] * enuDir.z;
        float colZ = r[2] * enuDir.x + r[5] * enuDir.y + r[8] * enuDir.z;
        return new Vector3(colX, colY, colZ);
    }

    private void EnsureDroneSprite()
    {
        if (droneSprite == null)
            return;

        droneSpriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (droneSpriteRenderer == null)
        {
            GameObject go = new GameObject("DroneSprite");
            go.transform.SetParent(transform, false);
            droneSpriteRenderer = go.AddComponent<SpriteRenderer>();
        }

        droneSpriteRenderer.sprite = droneSprite;
        droneSpriteRenderer.transform.localScale = Vector3.one * Mathf.Max(0.01f, droneSpriteScale);
    }

    private void UpdateDroneSpriteVisual()
    {
        if (droneSpriteRenderer == null)
            return;

        if (droneSpriteRenderer.sprite != droneSprite)
            droneSpriteRenderer.sprite = droneSprite;

        float scale = Mathf.Max(0.01f, droneSpriteScale);
        if (!Mathf.Approximately(droneSpriteRenderer.transform.localScale.x, scale))
            droneSpriteRenderer.transform.localScale = Vector3.one * scale;
    }

    private void EnsureDroneViewCamera()
    {
        if (droneViewCamera != null)
            return;

        if (!autoCreateDroneViewCamera)
            return;

        GameObject cameraObject = new GameObject("DroneViewCamera");
        cameraObject.transform.SetParent(transform, false);
        createdDroneViewCamera = cameraObject.AddComponent<Camera>();
        createdDroneViewCamera.depth = Camera.main != null ? Camera.main.depth + 1f : 1f;
        createdDroneViewCamera.clearFlags = CameraClearFlags.Depth;
        createdDroneViewCamera.cullingMask = ~0;
        droneViewCamera = createdDroneViewCamera;
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
        ApplyDroneViewCameraFov(frame);
    }

    private void ApplyDroneViewCameraFov(SrtFrame frame)
    {
        if (droneViewCamera == null)
            return;

        float verticalFov = fixedVerticalFovDegrees;
        if (estimateFovFromFocalLength && frame.focalLengthMm > 0.0001f && sensorHeightMm > 0.0001f)
            verticalFov = Mathf.Rad2Deg * 2f * Mathf.Atan(sensorHeightMm / (2f * frame.focalLengthMm));

        droneViewCamera.fieldOfView = Mathf.Clamp(verticalFov, 1f, 179f);
        droneViewCamera.aspect = GetCaptureAspectRatio();
    }

    private float GetCaptureAspectRatio()
    {
        if (droneViewCamera == null)
            return 16f / 9f;

        if (droneViewCamera.pixelHeight <= 0)
            return 16f / 9f;

        return Mathf.Max(0.01f, (float)droneViewCamera.pixelWidth / droneViewCamera.pixelHeight);
    }

    private void CapturePixelCoordinatesForFrame(SrtFrame frame, Vector3 rayOrigin, Vector3 worldDir)
    {
        if (!capturePixelCoordinates || droneViewCamera == null || !HasValidRaycastDistance())
            return;

        if (captureEveryNFrames > 1 && (frameIndex % captureEveryNFrames) != 0)
            return;

        int step = Mathf.Max(1, pixelStep);
        int width = droneViewCamera.pixelWidth;
        int height = droneViewCamera.pixelHeight;
        if (width <= 0 || height <= 0)
            return;

        EnsurePixelCsvWriter();

        int samplesThisFrame = 0;
        int xMin = 0;
        int xMax = width - 1;
        int yMin = 0;
        int yMax = height - 1;

        if (!captureFullFrame)
        {
            float regionWidth = Mathf.Clamp01(captureRegionNormalizedSize.x);
            float regionHeight = Mathf.Clamp01(captureRegionNormalizedSize.y);
            int regionPixelWidth = Mathf.Max(1, Mathf.RoundToInt(width * regionWidth));
            int regionPixelHeight = Mathf.Max(1, Mathf.RoundToInt(height * regionHeight));
            int halfW = regionPixelWidth / 2;
            int halfH = regionPixelHeight / 2;
            int centerX = width / 2;
            int centerY = height / 2;
            xMin = Mathf.Clamp(centerX - halfW, 0, width - 1);
            xMax = Mathf.Clamp(centerX + halfW, 0, width - 1);
            yMin = Mathf.Clamp(centerY - halfH, 0, height - 1);
            yMax = Mathf.Clamp(centerY + halfH, 0, height - 1);
        }
        if (capturePixelUsingJobs)
        {
            CapturePixelCoordinatesJobs(frame, width, height, xMin, xMax, yMin, yMax, step);
            return;
        }

        bool truncated = false;
        int drawnPixelCounter = 0;
        for (int y = yMin; y <= yMax; y += step)
        {
            for (int x = xMin; x <= xMax; x += step)
            {
                if (samplesThisFrame >= maxPixelSamplesPerFrame)
                {
                    truncated = true;
                    break;
                }

                Ray pixelRay = droneViewCamera.ScreenPointToRay(new Vector3(x + 0.5f, y + 0.5f, 0f));
                Vector3 direction = pixelRay.direction.sqrMagnitude > 0.0001f ? pixelRay.direction.normalized : worldDir;

                bool hit = TryRaycast(pixelRay.origin, direction, out RaycastHit hitInfo);

                if (hit && requireTargetTag && !string.IsNullOrWhiteSpace(targetColliderTag) && !hitInfo.collider.CompareTag(targetColliderTag))
                    hit = false;

                if (drawPixelRayVisualization && (drawPixelRayEveryN <= 1 || (drawnPixelCounter % drawPixelRayEveryN) == 0))
                {
                    Color rayColor = hit ? pixelRayHitColor : pixelRayMissColor;
                    if (hit)
                    {
                        Debug.DrawLine(pixelRay.origin, hitInfo.point, rayColor, pixelDebugRayDuration);
                        Debug.DrawRay(hitInfo.point, hitInfo.normal * pixelDebugNormalLength, pixelHitNormalColor, pixelDebugRayDuration);
                    }
                    else
                    {
                        Debug.DrawRay(pixelRay.origin, direction * raycastDistance, rayColor, pixelDebugRayDuration);
                    }
                }

                drawnPixelCounter++;

                if (!hit && captureOnlyHits)
                    continue;

                AppendPixelCsvSample(frame, x, y, pixelRay.origin, direction, hit, hitInfo);
                samplesThisFrame++;
            }

            if (samplesThisFrame >= maxPixelSamplesPerFrame)
                break;
        }

        if (truncated)
            Debug.LogWarning("[SrtDroneRaycastPlayer] Pixel capture reached maxPixelSamplesPerFrame; output is truncated.");

        FlushPixelCsvIfNeeded();
    }

    private void CapturePixelCoordinatesJobs(SrtFrame frame, int width, int height, int xMin, int xMax, int yMin, int yMax, int step)
    {
        List<int> pixelXs = new List<int>();
        List<int> pixelYs = new List<int>();
        List<Vector3> origins = new List<Vector3>();
        List<Vector3> directions = new List<Vector3>();

        bool truncated = false;
        int drawnPixelCounter = 0;
        for (int y = yMin; y <= yMax; y += step)
        {
            for (int x = xMin; x <= xMax; x += step)
            {
                if (pixelXs.Count >= maxPixelSamplesPerFrame)
                {
                    truncated = true;
                    break;
                }

                Ray pixelRay = droneViewCamera.ScreenPointToRay(new Vector3(x + 0.5f, y + 0.5f, 0f));
                Vector3 dir = pixelRay.direction.sqrMagnitude > 0.0001f ? pixelRay.direction.normalized : Vector3.forward;
                pixelXs.Add(x);
                pixelYs.Add(y);
                origins.Add(pixelRay.origin);
                directions.Add(dir);
            }

            if (pixelXs.Count >= maxPixelSamplesPerFrame)
                break;
        }

        if (pixelXs.Count == 0)
            return;

        int count = pixelXs.Count;
        NativeArray<RaycastCommand> commands = default;
        NativeArray<RaycastHit> hits = default;

        try
        {
            commands = new NativeArray<RaycastCommand>(count, Allocator.TempJob);
            hits = new NativeArray<RaycastHit>(count, Allocator.TempJob);
#if UNITY_2022_2_OR_NEWER
            QueryParameters query = new QueryParameters(raycastMask, false, QueryTriggerInteraction.Ignore);
#endif

            for (int i = 0; i < count; i++)
            {
                Vector3 dir = directions[i].sqrMagnitude > 0.0001f ? directions[i].normalized : Vector3.forward;
#if UNITY_2022_2_OR_NEWER
                commands[i] = new RaycastCommand(origins[i], dir, query, raycastDistance);
#else
                commands[i] = new RaycastCommand(origins[i], dir, raycastDistance, raycastMask, 1);
#endif
            }

            JobHandle handle = RaycastCommand.ScheduleBatch(commands, hits, 32);
            handle.Complete();

            int samplesThisFrame = 0;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hitInfo = hits[i];
                bool hit = hitInfo.collider != null && PassesTargetFilter(hitInfo.collider);

                if (drawPixelRayVisualization && (drawPixelRayEveryN <= 1 || (drawnPixelCounter % drawPixelRayEveryN) == 0))
                {
                    Color rayColor = hit ? pixelRayHitColor : pixelRayMissColor;
                    if (hit)
                    {
                        Debug.DrawLine(origins[i], hitInfo.point, rayColor, pixelDebugRayDuration);
                        Debug.DrawRay(hitInfo.point, hitInfo.normal * pixelDebugNormalLength, pixelHitNormalColor, pixelDebugRayDuration);
                    }
                    else
                    {
                        Debug.DrawRay(origins[i], directions[i] * raycastDistance, rayColor, pixelDebugRayDuration);
                    }
                }

                drawnPixelCounter++;

                if (!hit && captureOnlyHits)
                    continue;

                AppendPixelCsvSample(frame, pixelXs[i], pixelYs[i], origins[i], directions[i], hit, hitInfo);
                samplesThisFrame++;
                if (samplesThisFrame >= maxPixelSamplesPerFrame)
                {
                    truncated = true;
                    break;
                }
            }
        }
        finally
        {
            if (commands.IsCreated)
                commands.Dispose();
            if (hits.IsCreated)
                hits.Dispose();
        }

        if (truncated)
            Debug.LogWarning("[SrtDroneRaycastPlayer] Pixel capture reached maxPixelSamplesPerFrame; output is truncated.");

        FlushPixelCsvIfNeeded();
    }

    private void FlushPixelCsvIfNeeded()
    {
        if (pixelCsvWriter == null)
            return;

        pixelCsvFrameCounter++;
        int flushEvery = Mathf.Max(1, pixelCsvFlushEveryNFrames);
        if (pixelCsvFrameCounter >= flushEvery)
        {
            pixelCsvWriter.Flush();
            pixelCsvFrameCounter = 0;
        }
    }

    private void LoadTransformConfig()
    {
        transformData = null;
        string configText = GetTransformConfigText();
        if (string.IsNullOrWhiteSpace(configText))
            return;

        try
        {
            TransformConfig parsed = JsonConvert.DeserializeObject<TransformConfig>(configText);
            if (!GeoUtils.IsValidTransformData(parsed))
            {
                Debug.LogWarning("[SrtDroneRaycastPlayer] Transform config is missing valid origin_wgs84, colmap_to_enu, scale, R_rowmajor, or t data.");
                return;
            }

            transformData = parsed;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Failed to parse transform config: {ex.Message}");
        }
    }

    private string GetTransformConfigText()
    {
        string resolvedConfigPath = ResolveInputPath(transformConfigFilePath);
        if (!string.IsNullOrWhiteSpace(resolvedConfigPath) && File.Exists(resolvedConfigPath))
        {
            try
            {
                return File.ReadAllText(resolvedConfigPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SrtDroneRaycastPlayer] Failed to read transform config '{resolvedConfigPath}': {ex.Message}");
                return string.Empty;
            }
        }

        Debug.LogWarning("[SrtDroneRaycastPlayer] No transform config file found. Assign Transform Config File Path.");
        return string.Empty;
    }

    private void ParseSrt()
    {
        frames.Clear();

        string srtText = GetSrtText();
        if (string.IsNullOrWhiteSpace(srtText))
            return;

        foreach (string block in Regex.Split(srtText.Trim(), @"\r?\n\r?\n"))
        {
            string[] lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
                continue;

            Match timeMatch = TimeRegex.Match(lines[1]);
            if (!timeMatch.Success)
                continue;

            string payload = string.Join(" ", lines);
            if (!TryParseDouble(LatRegex, payload, out double lat))
                continue;
            if (!TryParseDouble(LonRegex, payload, out double lon))
                continue;

            TryParseDouble(RelAltRegex, payload, out double relAlt);
            TryParseDouble(AbsAltRegex, payload, out double absAlt);
            TryParseFloat(YawRegex, payload, out float yaw);
            TryParseFloat(PitchRegex, payload, out float pitch);
            TryParseFloat(RollRegex, payload, out float roll);
            TryParseFloat(FocalLenRegex, payload, out float focalLen);

            frames.Add(new SrtFrame
            {
                timeSeconds = ParseTimeToSeconds(timeMatch),
                lat = lat,
                lon = lon,
                alt = useAbsAltitude ? absAlt : relAlt,
                yaw = yaw,
                pitch = pitch,
                roll = roll,
                focalLengthMm = focalLen
            });
        }

        frames.Sort((a, b) => a.timeSeconds.CompareTo(b.timeSeconds));
        if (frames.Count == 0)
        {
            Debug.LogWarning("[SrtDroneRaycastPlayer] SRT file contained no usable frames.");
            return;
        }

        ValidateFrameTimes();
        if (samples.Capacity < frames.Count)
            samples.Capacity = frames.Count;
        timeCursor = 0f;
        frameIndex = 0;
    }

    private static float ParseTimeToSeconds(Match match)
    {
        int h = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        int m = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        int s = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        int ms = int.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture);
        return h * 3600f + m * 60f + s + ms / 1000f;
    }

    private static bool TryParseFloat(Regex regex, string input, out float value)
    {
        value = 0f;
        Match match = regex.Match(input);
        if (!match.Success)
            return false;

        return float.TryParse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDouble(Regex regex, string input, out double value)
    {
        value = 0.0;
        Match match = regex.Match(input);
        if (!match.Success)
            return false;

        return double.TryParse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private string GetSrtText()
    {
        string resolvedSrtPath = ResolveInputPath(srtFilePath);
        if (!string.IsNullOrWhiteSpace(resolvedSrtPath) && File.Exists(resolvedSrtPath))
        {
            try
            {
                return File.ReadAllText(resolvedSrtPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SrtDroneRaycastPlayer] Failed to read SRT file '{resolvedSrtPath}': {ex.Message}");
                return string.Empty;
            }
        }

        Debug.LogWarning("[SrtDroneRaycastPlayer] No SRT file found. Assign Srt File Path.");
        return string.Empty;
    }

    private void OnDisable()
    {
        SaveOutputsOnce();
    }

    private void OnApplicationQuit()
    {
        SaveOutputsOnce();
    }

    private void SaveOutputsOnce()
    {
        if (outputsSaved)
            return;

        outputsSaved = true;
        SaveSamplesToCsv();
        SaveDetectionsToCsv();
        ClosePixelCsvWriter();
    }

    private void SaveSamplesToCsv()
    {
        if (!recordRaycastSamples || samples.Count == 0 || string.IsNullOrWhiteSpace(outputCsvPath))
            return;

        string resolved = ResolveOutputPath(outputCsvPath);

        try
        {
            using (var sw = OpenCsvWriterWithFallback(resolved, out string actualPath))
            {
                sw.WriteLine("time_s,lat,lon,alt,hit,hit_dist,hit_x,hit_y,hit_z,class_id,label,detection_id");
                foreach (RaycastSample s in samples)
                {
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:F3},{1:F7},{2:F7},{3:F3},{4},{5:F3},{6:F3},{7:F3},{8:F3},{9},{10},{11}",
                        s.timeSeconds,
                        s.lat,
                        s.lon,
                        s.alt,
                        s.hit ? 1 : 0,
                        s.hit ? s.hitDistance : 0f,
                        s.hit ? s.hitPoint.x : 0f,
                        s.hit ? s.hitPoint.y : 0f,
                        s.hit ? s.hitPoint.z : 0f,
                        s.classId,
                        s.label ?? string.Empty,
                        s.detectionId ?? string.Empty));
                }

                sw.Flush();
                ReportExport(actualPath);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Failed to save samples: {ex.Message}");
        }
    }

    

    private void EnsurePixelCsvWriter()
    {
        if (!capturePixelCoordinates || pixelCsvWriter != null || string.IsNullOrWhiteSpace(pixelCsvPath))
            return;

        string resolved = ResolveOutputPath(pixelCsvPath);
        try
        {
            pixelCsvWriter = OpenCsvWriterWithFallback(resolved, out string actualPath);
            pixelCsvWriter.AutoFlush = false;
            pixelCsvWriter.WriteLine("time_s,lat,lon,alt,yaw,pitch,roll,pixel_x,pixel_y,origin_x,origin_y,origin_z,dir_x,dir_y,dir_z,hit,hit_x,hit_y,hit_z,hit_nx,hit_ny,hit_nz,hit_dist");
            ReportExport(actualPath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Failed to open pixel CSV writer: {ex.Message}");
            ClosePixelCsvWriter();
        }
    }

    private void ClosePixelCsvWriter()
    {
        if (pixelCsvWriter == null)
            return;

        try
        {
            pixelCsvWriter.Flush();
            pixelCsvWriter.Close();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Failed to close pixel CSV writer: {ex.Message}");
        }
        finally
        {
            pixelCsvWriter = null;
            pixelCsvFrameCounter = 0;
        }
    }

    private string ResolveOutputPath(string rawPath)
    {
        return ResolvePath(rawPath, outputPathRoot);
    }

    private string ResolveDateStampedOutputPath(string rawPath, string baseFileName)
    {
        string resolved = ResolveOutputPath(rawPath);
        if (string.IsNullOrWhiteSpace(resolved))
            return resolved;

        string directory = Path.GetDirectoryName(resolved);
        string extension = Path.GetExtension(resolved);
        string stamp = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string stampedName = $"{baseFileName}_{stamp}{extension}";

        return string.IsNullOrWhiteSpace(directory)
            ? stampedName
            : Path.Combine(directory, stampedName);
    }

    private StreamWriter OpenCsvWriterWithFallback(string preferredPath, out string actualPath)
    {
        try
        {
            actualPath = preferredPath;
            return CreateCsvWriter(preferredPath);
        }
        catch (IOException)
        {
            actualPath = BuildFallbackCsvPath(preferredPath);
            return CreateCsvWriter(actualPath);
        }
    }

    private static StreamWriter CreateCsvWriter(string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        return new StreamWriter(fileStream, Encoding.UTF8);
    }

    private static string BuildFallbackCsvPath(string preferredPath)
    {
        string directory = Path.GetDirectoryName(preferredPath);
        string fileName = Path.GetFileNameWithoutExtension(preferredPath);
        string extension = Path.GetExtension(preferredPath);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string fallbackFileName = string.IsNullOrWhiteSpace(fileName)
            ? $"export_locked_{stamp}{extension}"
            : $"{fileName}_locked_{stamp}{extension}";

        return string.IsNullOrWhiteSpace(directory)
            ? fallbackFileName
            : Path.Combine(directory, fallbackFileName);
    }

    private string ResolveInputPath(string rawPath)
    {
        return ResolvePath(rawPath, inputPathRoot);
    }

    private string ResolvePath(string rawPath, FilePathRoot rootMode)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        string resolved = rawPath;
        if (!Path.IsPathRooted(resolved))
        {
            string root = GetPathRoot(rootMode);
            if (!string.IsNullOrWhiteSpace(root))
                resolved = Path.Combine(root, rawPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        }

        return resolved;
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
                return Directory.GetParent(Application.dataPath)?.FullName;
        }
    }

    

    private void AppendPixelCsvSample(SrtFrame frame, int pixelX, int pixelY, Vector3 origin, Vector3 direction, bool hit, RaycastHit hitInfo)
    {
        if (pixelCsvWriter == null)
            return;

        Vector3 localHitPoint = hit ? GetColmapHitPoint(hitInfo.point) : Vector3.zero;

        pixelCsvWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0:F3},{1:F7},{2:F7},{3:F3},{4:F2},{5:F2},{6:F2},{7},{8},{9:F3},{10:F3},{11:F3},{12:F4},{13:F4},{14:F4},{15},{16:F3},{17:F3},{18:F3},{19:F4},{20:F4},{21:F4},{22:F3}",
            frame.timeSeconds,
            frame.lat,
            frame.lon,
            frame.alt,
            frame.yaw,
            frame.pitch,
            frame.roll,
            pixelX,
            pixelY,
            origin.x,
            origin.y,
            origin.z,
            direction.x,
            direction.y,
            direction.z,
            hit ? 1 : 0,
            localHitPoint.x,
            localHitPoint.y,
            localHitPoint.z,
            hit ? hitInfo.normal.x : 0f,
            hit ? hitInfo.normal.y : 0f,
            hit ? hitInfo.normal.z : 0f,
            hit ? hitInfo.distance : 0f));
    }

    private void SaveDetectionsToCsv()
    {
        if (!recordDetectionsToCsv || detectionSamples.Count == 0 || string.IsNullOrWhiteSpace(detectionCsvPath))
            return;

        string resolved = ResolveDateStampedOutputPath(detectionCsvPath, "detection_raycast_samples");
        try
        {
            using (var sw = OpenCsvWriterWithFallback(resolved, out string actualPath))
            {
                sw.WriteLine("time_s,class_id,label,detection_id,pixel_x,pixel_y,normalized,hit,hit_x,hit_y,hit_z,hit_dist,lon,lat,alt");
                foreach (DetectionSample s in detectionSamples)
                {
                    sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:F3},{1},{2},{3},{4:F2},{5:F2},{6},{7},{8:F3},{9:F3},{10:F3},{11:F3},{12:0.0000000},{13:0.0000000},{14:F3}",
                        s.timeSeconds,
                        s.classId,
                        s.label ?? string.Empty,
                        s.detectionId ?? string.Empty,
                        s.pixelX,
                        s.pixelY,
                        s.normalized ? 1 : 0,
                        s.hit ? 1 : 0,
                        s.hitPoint.x,
                        s.hitPoint.y,
                        s.hitPoint.z,
                        s.hitDistance,
                        s.lon,
                        s.lat,
                        s.alt));
                }

                sw.Flush();
                ReportExport(actualPath);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Failed to save detections: {ex.Message}");
        }
    }

    private Vector2 ResolvePixelCoords(Vector2 pixelCoords, bool normalized)
    {
        if (droneViewCamera == null)
            return pixelCoords;

        float width = Mathf.Max(1f, droneViewCamera.pixelWidth);
        float height = Mathf.Max(1f, droneViewCamera.pixelHeight);
        float x = normalized ? pixelCoords.x * (width - 1f) : pixelCoords.x;
        float y = normalized ? pixelCoords.y * (height - 1f) : pixelCoords.y;
        if (invertPixelY)
            y = (height - 1f) - y;

        return new Vector2(Mathf.Clamp(x, 0f, width - 1f), Mathf.Clamp(y, 0f, height - 1f));
    }

    private bool HasValidRaycastDistance()
    {
        return raycastDistance > 0f && !float.IsNaN(raycastDistance) && !float.IsInfinity(raycastDistance);
    }

    

    private bool TryRaycast(Vector3 origin, Vector3 direction, out RaycastHit bestHit)
    {
        bestHit = default;

        if (!HasValidRaycastDistance() || direction.sqrMagnitude <= 0.0001f)
            return false;

        Vector3 normalizedDirection = direction.normalized;
        if (targetCollider == null && !raycastTargetOnly)
        {
            return Physics.Raycast(origin, normalizedDirection, out bestHit,
                raycastDistance, raycastMask, QueryTriggerInteraction.Ignore) &&
                PassesTargetFilter(bestHit.collider);
        }

        RaycastHit[] hits = Physics.RaycastAll(origin, normalizedDirection, raycastDistance, raycastMask, QueryTriggerInteraction.Ignore);
        float closestDistance = float.MaxValue;

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit candidate = hits[i];
            if (!PassesTargetFilter(candidate.collider))
                continue;

            if (candidate.distance < closestDistance)
            {
                closestDistance = candidate.distance;
                bestHit = candidate;
            }
        }

        return closestDistance < float.MaxValue;
    }

    private void ConvertWorldToWgs84(Vector3 worldPoint, out double lon, out double lat, out double alt)
    {
        Vector3 colmapPos = mapRoot != null ? mapRoot.InverseTransformPoint(worldPoint) : worldPoint;
        if (flipPositionX)
            colmapPos.x = -colmapPos.x;
        if (flipPositionY)
            colmapPos.y = -colmapPos.y;

        GeoUtils.ColmapToWgs84(colmapPos, transformData, out lon, out lat, out alt);
    }

    private void DrawDetectionHitVisualization(Vector3 rayOrigin, RaycastHit hitInfo)
    {
        Debug.DrawLine(rayOrigin, hitInfo.point, detectionHitColor, detectionDebugRayDuration);
        Debug.DrawRay(hitInfo.point, hitInfo.normal * pixelDebugNormalLength, detectionHitNormalColor, detectionDebugRayDuration);
    }

    private Vector3 GetColmapHitPoint(Vector3 worldPoint)
    {
        Vector3 colmapPos = mapRoot != null ? mapRoot.InverseTransformPoint(worldPoint) : worldPoint;
        if (flipPositionX)
            colmapPos.x = -colmapPos.x;
        if (flipPositionY)
            colmapPos.y = -colmapPos.y;

        return colmapPos;
    }

    private SrtFrame GetInterpolatedFrame(float timeSeconds)
    {
        if (frames.Count == 0)
            return default;

        if (timeSeconds <= frames[0].timeSeconds)
            return frames[0];

        if (timeSeconds >= frames[frames.Count - 1].timeSeconds)
            return frames[frames.Count - 1];

        int index = GetFrameIndexForTime(timeSeconds);
        int nextIndex = Mathf.Min(index + 1, frames.Count - 1);
        if (nextIndex == index)
            return frames[index];

        SrtFrame a = frames[index];
        SrtFrame b = frames[nextIndex];
        float span = Mathf.Max(0.0001f, b.timeSeconds - a.timeSeconds);
        float t = Mathf.Clamp01((timeSeconds - a.timeSeconds) / span);

        float yawA = NormalizeYawDegrees(a.yaw);
        float yawB = NormalizeYawDegrees(b.yaw);
        float pitchA = NormalizeYawDegrees(a.pitch);
        float pitchB = NormalizeYawDegrees(b.pitch);
        float rollA = NormalizeYawDegrees(a.roll);
        float rollB = NormalizeYawDegrees(b.roll);

        return new SrtFrame
        {
            timeSeconds = Mathf.Lerp(a.timeSeconds, b.timeSeconds, t),
            lat = LerpDouble(a.lat, b.lat, t),
            lon = LerpDouble(a.lon, b.lon, t),
            alt = LerpDouble(a.alt, b.alt, t),
            yaw = NormalizeYawDegrees(Mathf.LerpAngle(yawA, yawB, t)),
            pitch = NormalizeYawDegrees(Mathf.LerpAngle(pitchA, pitchB, t)),
            roll = NormalizeYawDegrees(Mathf.LerpAngle(rollA, rollB, t)),
            focalLengthMm = Mathf.Lerp(a.focalLengthMm, b.focalLengthMm, t)
        };
    }

    private int GetFrameIndexForTime(float timeSeconds)
    {
        int low = 0;
        int high = frames.Count - 1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (frames[mid].timeSeconds <= timeSeconds)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return Mathf.Clamp(high, 0, frames.Count - 1);
    }

    private static float NormalizeYawDegrees(float yaw)
    {
        if (float.IsNaN(yaw) || float.IsInfinity(yaw))
            return 0f;

        yaw %= 360f;
        if (yaw > 180f)
            yaw -= 360f;
        if (yaw <= -180f)
            yaw += 360f;

        return yaw;
    }

    private static double LerpDouble(double a, double b, float t)
    {
        return a + (b - a) * t;
    }

    private void ValidateFrameTimes()
    {
        if (frames.Count < 2)
            return;

        bool hasDuplicates = false;
        for (int i = 1; i < frames.Count; i++)
        {
            if (Math.Abs(frames[i].timeSeconds - frames[i - 1].timeSeconds) <= 0.000001f)
            {
                hasDuplicates = true;
                break;
            }
        }

        if (hasDuplicates)
            Debug.LogWarning("[SrtDroneRaycastPlayer] Duplicate timestamps detected in SRT; interpolation may be unstable.");
    }

    public bool TryPrepareForPixelProjection(out string reason)
    {
        if (!GeoUtils.IsValidTransformData(transformData))
            LoadTransformConfig();
        if (frames.Count == 0)
            ParseSrt();
        if (droneViewCamera == null)
            EnsureDroneViewCamera();

        if (!GeoUtils.IsValidTransformData(transformData))
        {
            reason = "Transform config data is missing or invalid.";
            return false;
        }

        if (frames.Count == 0)
        {
            reason = "No usable SRT frames are loaded.";
            return false;
        }

        if (droneViewCamera == null)
        {
            reason = "Drone View Camera is missing.";
            return false;
        }

        if (!HasValidRaycastDistance())
        {
            reason = "Raycast Distance must be finite and greater than zero.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public Vector3? GetWorldPositionFromPixel(Vector2 pixelCoords)
    {
        if (droneViewCamera == null)
            return null;

        Vector2 resolved = ResolvePixelCoords(pixelCoords, false);
        Ray ray = droneViewCamera.ScreenPointToRay(new Vector3(resolved.x + 0.5f, resolved.y + 0.5f, 0f));
        if (TryRaycast(ray.origin, ray.direction, out RaycastHit hitInfo))
            return hitInfo.point;

        return null;
    }

    public List<PixelGeoResult> MapPixelsToGeo(List<Vector2> pixels, bool normalized)
    {
        if (pixels == null || pixels.Count == 0 || droneViewCamera == null || frames.Count == 0 ||
            transformData == null || !HasValidRaycastDistance())
            return new List<PixelGeoResult>();

        float timeSeconds = interpolateFrames ? timeCursor : frames[Mathf.Clamp(frameIndex, 0, frames.Count - 1)].timeSeconds;
        return MapPixelsToGeoAtTime(pixels, normalized, timeSeconds, false, 1);
    }

    public List<PixelGeoResult> MapPixelsToGeoAtTime(List<Vector2> pixels, bool normalized, float timeSeconds)
    {
        return MapPixelsToGeoAtTime(pixels, normalized, timeSeconds, false, 1);
    }

    public List<PixelGeoResult> MapPixelsToGeoAtTime(List<Vector2> pixels, bool normalized, float timeSeconds, bool useRaycastCommands, int cachePixelStride)
    {
        List<PixelGeoResult> results = new List<PixelGeoResult>();
        if (pixels == null || pixels.Count == 0 || droneViewCamera == null || frames.Count == 0 ||
            transformData == null || !HasValidRaycastDistance())
            return results;

        if (useRaycastCommands)
            return MapPixelsToGeoAtTimeJobs(pixels, normalized, timeSeconds);

        SrtFrame frame = GetInterpolatedFrame(timeSeconds);
        BuildRayOriginAndDirection(frame, out Vector3 worldPos, out Vector3 worldDir, out Vector3 rayOrigin);
        transform.position = worldPos;
        UpdateDroneViewCamera(rayOrigin, worldDir, frame);

        Dictionary<long, CachedHit> cache = cachePixelStride > 1 ? new Dictionary<long, CachedHit>() : null;

        for (int i = 0; i < pixels.Count; i++)
        {
            Vector2 resolved = ResolvePixelCoords(pixels[i], normalized);
            int qx = cachePixelStride > 1 ? Mathf.RoundToInt(resolved.x / cachePixelStride) : 0;
            int qy = cachePixelStride > 1 ? Mathf.RoundToInt(resolved.y / cachePixelStride) : 0;
            long key = cachePixelStride > 1 ? ((long)qx << 32) ^ (uint)qy : 0L;

            bool hit;
            RaycastHit hitInfo;

            if (cache != null && cache.TryGetValue(key, out CachedHit cached))
            {
                hit = cached.hit;
                hitInfo = cached.hitInfo;
            }
            else
            {
                Ray ray = droneViewCamera.ScreenPointToRay(new Vector3(resolved.x + 0.5f, resolved.y + 0.5f, 0f));
                hit = TryRaycast(ray.origin, ray.direction, out hitInfo);
                if (cache != null)
                    cache[key] = new CachedHit { hit = hit, hitInfo = hitInfo };
            }

            double lon = 0.0;
            double lat = 0.0;
            double alt = 0.0;
            if (hit)
                ConvertWorldToWgs84(hitInfo.point, out lon, out lat, out alt);

            results.Add(new PixelGeoResult
            {
                pixel = resolved,
                normalized = normalized,
                hit = hit,
                worldPoint = hit ? hitInfo.point : Vector3.zero,
                hitDistance = hit ? hitInfo.distance : 0f,
                lon = lon,
                lat = lat,
                alt = alt
            });
        }

        return results;
    }

    private List<PixelGeoResult> MapPixelsToGeoAtTimeJobs(List<Vector2> pixels, bool normalized, float timeSeconds)
    {
        List<PixelGeoResult> results = new List<PixelGeoResult>();
        if (pixels == null || pixels.Count == 0 || droneViewCamera == null)
            return results;

        SrtFrame frame = GetInterpolatedFrame(timeSeconds);
        BuildRayOriginAndDirection(frame, out Vector3 worldPos, out Vector3 worldDir, out Vector3 rayOrigin);
        transform.position = worldPos;
        UpdateDroneViewCamera(rayOrigin, worldDir, frame);

        int count = pixels.Count;
        NativeArray<RaycastCommand> commands = default;
        NativeArray<RaycastHit> hits = default;
        Vector2[] resolvedPixels = new Vector2[count];

        try
        {
            commands = new NativeArray<RaycastCommand>(count, Allocator.TempJob);
            hits = new NativeArray<RaycastHit>(count, Allocator.TempJob);
#if UNITY_2022_2_OR_NEWER
            QueryParameters query = new QueryParameters(raycastMask, false, QueryTriggerInteraction.Ignore);
#endif

            for (int i = 0; i < count; i++)
            {
                Vector2 resolved = ResolvePixelCoords(pixels[i], normalized);
                Ray ray = droneViewCamera.ScreenPointToRay(new Vector3(resolved.x + 0.5f, resolved.y + 0.5f, 0f));
                Vector3 dir = ray.direction.sqrMagnitude > 0.0001f ? ray.direction.normalized : Vector3.forward;
                resolvedPixels[i] = resolved;

#if UNITY_2022_2_OR_NEWER
                commands[i] = new RaycastCommand(ray.origin, dir, query, raycastDistance);
#else
                commands[i] = new RaycastCommand(ray.origin, dir, raycastDistance, raycastMask, 1);
#endif
            }

            JobHandle handle = RaycastCommand.ScheduleBatch(commands, hits, 32);
            handle.Complete();

            for (int i = 0; i < count; i++)
            {
                RaycastHit hitInfo = hits[i];
                bool hit = hitInfo.collider != null && PassesTargetFilter(hitInfo.collider);

                double lon = 0.0;
                double lat = 0.0;
                double alt = 0.0;
                if (hit)
                    ConvertWorldToWgs84(hitInfo.point, out lon, out lat, out alt);

                results.Add(new PixelGeoResult
                {
                    pixel = resolvedPixels[i],
                    normalized = normalized,
                    hit = hit,
                    worldPoint = hit ? hitInfo.point : Vector3.zero,
                    hitDistance = hit ? hitInfo.distance : 0f,
                    lon = lon,
                    lat = lat,
                    alt = alt
                });
            }
        }
        finally
        {
            if (commands.IsCreated)
                commands.Dispose();
            if (hits.IsCreated)
                hits.Dispose();
        }

        return results;
    }

    [ContextMenu("Process Detection JSON And Export")]
    public void ProcessDetectionJsonAndExport()
    {
        if (transformData == null)
            LoadTransformConfig();
        if (frames.Count == 0)
            ParseSrt();
        EnsureDroneViewCamera();

        if (frames.Count == 0)
        {
            Debug.LogWarning("[SrtDroneRaycastPlayer] Cannot process detections without parsed SRT frames.");
            return;
        }

        if (transformData == null)
        {
            Debug.LogWarning("[SrtDroneRaycastPlayer] Cannot process detections without transform config data.");
            return;
        }

        if (droneViewCamera == null)
        {
            Debug.LogWarning("[SrtDroneRaycastPlayer] Cannot process detections without a droneViewCamera.");
            return;
        }

        string resolvedPath = ResolveInputPath(detectionJsonPath);
        List<Detection> detections = LoadDetectionsFromJson(resolvedPath);
        if (detections.Count == 0)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] No detections loaded from: {resolvedPath}");
            return;
        }

        bool previousRecordDetections = recordDetectionsToCsv;
        try
        {
            recordDetectionsToCsv = true;
            detectionSamples.Clear();

            ProcessDetections(detections, useDetectionRaycastJobs);
            SaveDetectionsToCsv();
        }
        finally
        {
            recordDetectionsToCsv = previousRecordDetections;
        }

        Debug.Log($"[SrtDroneRaycastPlayer] Processed {detections.Count} detections from: {resolvedPath}");
    }

    private List<Detection> LoadDetectionsFromJson(string path)
    {
        List<Detection> detections = new List<Detection>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Detection JSON not found: {path}");
            return detections;
        }

        float frameWidth = 0f;
        float frameHeight = 0f;

        try
        {
            using (var sr = new StreamReader(path, Encoding.UTF8))
            using (var reader = new JsonTextReader(sr))
            {
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.PropertyName)
                        continue;

                    string propertyName = (string)reader.Value;
                    if (propertyName == "frame_width")
                    {
                        reader.Read();
                        frameWidth = Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture);
                    }
                    else if (propertyName == "frame_height")
                    {
                        reader.Read();
                        frameHeight = Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture);
                    }
                    else if (propertyName == "frames")
                    {
                        reader.Read();
                        if (reader.TokenType != JsonToken.StartArray)
                            continue;

                        while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                        {
                            if (reader.TokenType == JsonToken.StartObject)
                                ReadDetectionFrame(reader, detections, frameWidth, frameHeight);
                            else
                                reader.Skip();
                        }
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SrtDroneRaycastPlayer] Failed to load detection JSON: {ex.Message}");
        }

        return detections;
    }

    private void ReadDetectionFrame(JsonTextReader reader, List<Detection> detections, float frameWidth, float frameHeight)
    {
        float timestampSeconds = 0f;

        while (reader.Read() && reader.TokenType != JsonToken.EndObject)
        {
            if (reader.TokenType != JsonToken.PropertyName)
                continue;

            string propertyName = (string)reader.Value;
            if (propertyName == "timestamp_s")
            {
                reader.Read();
                timestampSeconds = Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture);
            }
            else if (propertyName == "detections")
            {
                reader.Read();
                if (reader.TokenType != JsonToken.StartArray)
                    continue;

                while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                {
                    if (reader.TokenType == JsonToken.StartObject)
                        ReadDetectionObject(reader, detections, timestampSeconds, frameWidth, frameHeight);
                    else
                        reader.Skip();
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }

    private void ReadDetectionObject(JsonTextReader reader, List<Detection> detections, float timestampSeconds, float frameWidth, float frameHeight)
    {
        int classId = 0;
        string label = string.Empty;
        string detectionId = string.Empty;
        Vector2 centroid = Vector2.zero;
        bool hasCentroid = false;
        float[] bbox = null;

        while (reader.Read() && reader.TokenType != JsonToken.EndObject)
        {
            if (reader.TokenType != JsonToken.PropertyName)
                continue;

            string propertyName = (string)reader.Value;
            if (propertyName == "id")
            {
                reader.Read();
                detectionId = Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
            }
            else if (propertyName == "label")
            {
                reader.Read();
                label = Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
            }
            else if (propertyName == "class_id")
            {
                reader.Read();
                classId = Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture);
            }
            else if (propertyName == "centroid")
            {
                reader.Read();
                hasCentroid = TryReadFloatArray(reader, 2, out float[] values);
                if (hasCentroid)
                    centroid = new Vector2(values[0], values[1]);
            }
            else if (propertyName == "bbox")
            {
                reader.Read();
                TryReadFloatArray(reader, 4, out bbox);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        if (!TryGetDetectionPoint(centroid, hasCentroid, bbox, out Vector2 pixel))
            return;

        bool normalized = false;
        if (normalizeDetectionPixels && frameWidth > 1f && frameHeight > 1f)
        {
            pixel = new Vector2(pixel.x / (frameWidth - 1f), pixel.y / (frameHeight - 1f));
            normalized = true;
        }

        detections.Add(new Detection
        {
            pixel = pixel,
            normalized = normalized,
            timeSeconds = timestampSeconds,
            classId = classId,
            label = label,
            detectionId = detectionId
        });
    }

    private bool TryGetDetectionPoint(Vector2 centroid, bool hasCentroid, float[] bbox, out Vector2 pixel)
    {
        if (detectionPointMode == DetectionPointMode.BboxBottomCenter && bbox != null && bbox.Length >= 4)
        {
            pixel = new Vector2((bbox[0] + bbox[2]) * 0.5f, bbox[3]);
            return true;
        }

        if (hasCentroid)
        {
            pixel = centroid;
            return true;
        }

        if (bbox != null && bbox.Length >= 4)
        {
            pixel = new Vector2((bbox[0] + bbox[2]) * 0.5f, (bbox[1] + bbox[3]) * 0.5f);
            return true;
        }

        pixel = Vector2.zero;
        return false;
    }

    private static bool TryReadFloatArray(JsonTextReader reader, int expectedCount, out float[] values)
    {
        values = null;
        if (reader.TokenType != JsonToken.StartArray)
        {
            reader.Skip();
            return false;
        }

        List<float> parsed = new List<float>(expectedCount);
        while (reader.Read() && reader.TokenType != JsonToken.EndArray)
        {
            if (reader.TokenType == JsonToken.Integer || reader.TokenType == JsonToken.Float)
                parsed.Add(Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture));
            else
                reader.Skip();
        }

        if (parsed.Count < expectedCount)
            return false;

        values = parsed.ToArray();
        return true;
    }

    public List<DetectionResult> ProcessDetections(List<Detection> detections, bool useRaycastCommands = false, int cachePixelStride = 1)
    {
        List<DetectionResult> results = new List<DetectionResult>();
        if (detections == null || detections.Count == 0 || droneViewCamera == null || frames.Count == 0 ||
            transformData == null || !HasValidRaycastDistance())
            return results;

        if (useRaycastCommands)
            return ProcessDetectionsWithRaycastCommands(detections);

        DetectionResult[] orderedResults = new DetectionResult[detections.Count];
        List<int> order = BuildDetectionOrder(detections);
        int groupStart = 0;

        while (groupStart < order.Count)
        {
            int groupEnd = groupStart;
            float groupTime = detections[order[groupStart]].timeSeconds;
            while (groupEnd < order.Count && Mathf.Abs(detections[order[groupEnd]].timeSeconds - groupTime) <= detectionTimeEpsilon)
                groupEnd++;

            SrtFrame frame = GetInterpolatedFrame(groupTime);
            BuildRayOriginAndDirection(frame, out Vector3 worldPos, out Vector3 worldDir, out Vector3 rayOrigin);
            transform.position = worldPos;
            UpdateDroneViewCamera(rayOrigin, worldDir, frame);

            Dictionary<long, CachedHit> cache = cachePixelStride > 1 ? new Dictionary<long, CachedHit>() : null;

            for (int i = groupStart; i < groupEnd; i++)
            {
                int detectionIndex = order[i];
                Detection detection = detections[detectionIndex];

                Vector2 resolved = ResolvePixelCoords(detection.pixel, detection.normalized);
                int qx = cachePixelStride > 1 ? Mathf.RoundToInt(resolved.x / cachePixelStride) : 0;
                int qy = cachePixelStride > 1 ? Mathf.RoundToInt(resolved.y / cachePixelStride) : 0;
                long key = cachePixelStride > 1 ? ((long)qx << 32) ^ (uint)qy : 0L;

                bool hit;
                RaycastHit hitInfo;

                if (cache != null && cache.TryGetValue(key, out CachedHit cached))
                {
                    hit = cached.hit;
                    hitInfo = cached.hitInfo;
                }
                else
                {
                    Ray ray = droneViewCamera.ScreenPointToRay(new Vector3(resolved.x + 0.5f, resolved.y + 0.5f, 0f));
                    hit = TryRaycast(ray.origin, ray.direction, out hitInfo);
                    if (cache != null)
                        cache[key] = new CachedHit { hit = hit, hitInfo = hitInfo };
                }

                double lon = 0.0;
                double lat = 0.0;
                double alt = 0.0;
                if (hit)
                    ConvertWorldToWgs84(hitInfo.point, out lon, out lat, out alt);

                if (drawDebug && drawDetectionHitVisualization && hit)
                    DrawDetectionHitVisualization(rayOrigin, hitInfo);

                DetectionResult result = new DetectionResult
                {
                    detection = detection,
                    hit = hit,
                    hitPoint = hit ? GetColmapHitPoint(hitInfo.point) : Vector3.zero,
                    hitNormal = hit ? hitInfo.normal : Vector3.zero,
                    hitDistance = hit ? hitInfo.distance : 0f,
                    lon = lon,
                    lat = lat,
                    alt = alt
                };

                orderedResults[detectionIndex] = result;

                if (recordDetectionsToCsv)
                {
                    detectionSamples.Add(new DetectionSample
                    {
                        timeSeconds = detection.timeSeconds,
                        classId = detection.classId,
                        label = detection.label,
                        detectionId = detection.detectionId,
                        pixelX = resolved.x,
                        pixelY = resolved.y,
                        normalized = detection.normalized,
                        hit = hit,
                        hitPoint = hit ? GetColmapHitPoint(hitInfo.point) : Vector3.zero,
                        hitDistance = hit ? hitInfo.distance : 0f,
                        lon = lon,
                        lat = lat,
                        alt = alt
                    });
                }
            }

            groupStart = groupEnd;
        }

        results.AddRange(orderedResults);
        return results;
    }

    private List<DetectionResult> ProcessDetectionsWithRaycastCommands(List<Detection> detections)
    {
        List<DetectionResult> results = new List<DetectionResult>();
        if (detections == null || detections.Count == 0 || droneViewCamera == null || frames.Count == 0 ||
            transformData == null || !HasValidRaycastDistance())
            return results;

        DetectionResult[] orderedResults = new DetectionResult[detections.Count];
        List<int> order = BuildDetectionOrder(detections);
        int groupStart = 0;

        while (groupStart < order.Count)
        {
            int groupEnd = groupStart;
            float groupTime = detections[order[groupStart]].timeSeconds;
            while (groupEnd < order.Count && Mathf.Abs(detections[order[groupEnd]].timeSeconds - groupTime) <= detectionTimeEpsilon)
                groupEnd++;

            int groupCount = groupEnd - groupStart;
            NativeArray<RaycastCommand> commands = default;
            NativeArray<RaycastHit> hits = default;
            Vector2[] resolvedPixels = new Vector2[groupCount];

            SrtFrame frame = GetInterpolatedFrame(groupTime);
            BuildRayOriginAndDirection(frame, out Vector3 worldPos, out Vector3 worldDir, out Vector3 rayOrigin);
            transform.position = worldPos;
            UpdateDroneViewCamera(rayOrigin, worldDir, frame);

#if UNITY_2022_2_OR_NEWER
            QueryParameters query = new QueryParameters(raycastMask, false, QueryTriggerInteraction.Ignore);
#endif

            try
            {
                commands = new NativeArray<RaycastCommand>(groupCount, Allocator.TempJob);
                hits = new NativeArray<RaycastHit>(groupCount, Allocator.TempJob);

                for (int i = 0; i < groupCount; i++)
                {
                    Detection detection = detections[order[groupStart + i]];
                    Vector2 resolved = ResolvePixelCoords(detection.pixel, detection.normalized);
                    resolvedPixels[i] = resolved;
                    Ray ray = droneViewCamera.ScreenPointToRay(new Vector3(resolved.x + 0.5f, resolved.y + 0.5f, 0f));
                    Vector3 dir = ray.direction.sqrMagnitude > 0.0001f ? ray.direction.normalized : Vector3.forward;
#if UNITY_2022_2_OR_NEWER
                    commands[i] = new RaycastCommand(ray.origin, dir, query, raycastDistance);
#else
                    commands[i] = new RaycastCommand(ray.origin, dir, raycastDistance, raycastMask, 1);
#endif
                }

                JobHandle handle = RaycastCommand.ScheduleBatch(commands, hits, 32);
                handle.Complete();

                for (int i = 0; i < groupCount; i++)
                {
                    int detectionIndex = order[groupStart + i];
                    Detection detection = detections[detectionIndex];
                    RaycastHit hitInfo = hits[i];
                    bool hit = hitInfo.collider != null && PassesTargetFilter(hitInfo.collider);

                    double lon = 0.0;
                    double lat = 0.0;
                    double alt = 0.0;
                    if (hit)
                        ConvertWorldToWgs84(hitInfo.point, out lon, out lat, out alt);

                    if (drawDebug && drawDetectionHitVisualization && hit)
                        DrawDetectionHitVisualization(rayOrigin, hitInfo);

                    orderedResults[detectionIndex] = new DetectionResult
                    {
                        detection = detection,
                        hit = hit,
                        hitPoint = hit ? GetColmapHitPoint(hitInfo.point) : Vector3.zero,
                        hitNormal = hit ? hitInfo.normal : Vector3.zero,
                        hitDistance = hit ? hitInfo.distance : 0f,
                        lon = lon,
                        lat = lat,
                        alt = alt
                    };

                    if (recordDetectionsToCsv)
                    {
                        detectionSamples.Add(new DetectionSample
                        {
                            timeSeconds = detection.timeSeconds,
                            classId = detection.classId,
                            label = detection.label,
                            detectionId = detection.detectionId,
                            pixelX = resolvedPixels[i].x,
                            pixelY = resolvedPixels[i].y,
                            normalized = detection.normalized,
                            hit = hit,
                            hitPoint = hit ? GetColmapHitPoint(hitInfo.point) : Vector3.zero,
                            hitDistance = hit ? hitInfo.distance : 0f,
                            lon = lon,
                            lat = lat,
                            alt = alt
                        });
                    }
                }
            }
            finally
            {
                if (commands.IsCreated)
                    commands.Dispose();
                if (hits.IsCreated)
                    hits.Dispose();
            }

            groupStart = groupEnd;
        }

        results.AddRange(orderedResults);
        return results;
    }

    public Dictionary<string, DetectionResult> ProcessDetectionsToMap(List<Detection> detections, bool useRaycastCommands = false, int cachePixelStride = 1)
    {
        List<DetectionResult> results = ProcessDetections(detections, useRaycastCommands, cachePixelStride);
        Dictionary<string, DetectionResult> map = new Dictionary<string, DetectionResult>();

        for (int i = 0; i < results.Count; i++)
        {
            DetectionResult result = results[i];
            if (string.IsNullOrWhiteSpace(result.detection.detectionId))
                continue;

            map[result.detection.detectionId] = result;
        }

        return map;
    }

    private List<int> BuildDetectionOrder(List<Detection> detections)
    {
        List<int> order = new List<int>(detections.Count);
        for (int i = 0; i < detections.Count; i++)
            order.Add(i);

        order.Sort((a, b) => detections[a].timeSeconds.CompareTo(detections[b].timeSeconds));
        return order;
    }

    private bool PassesTargetFilter(Collider collider)
    {
        if (collider == null)
            return false;

        if (requireTargetTag)
        {
            if (string.IsNullOrWhiteSpace(targetColliderTag) || !collider.CompareTag(targetColliderTag))
                return false;
        }

        if (raycastTargetOnly)
        {
            if (targetCollider == null)
                return false;

            Transform targetTransform = targetCollider.transform;
            return collider == targetCollider || collider.transform == targetTransform || collider.transform.IsChildOf(targetTransform);
        }

        return true;
    }

    

    private void ReportExport(string resolvedPath)
    {
        Debug.Log($"[SrtDroneRaycastPlayer] Exported: {resolvedPath}");

    #if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
    #endif
        }

    [Serializable]
    private struct SrtFrame
    {
        public float timeSeconds;
        public double lat;
        public double lon;
        public double alt;
        public float yaw;
        public float pitch;
        public float roll;
        public float focalLengthMm;
    }

    private struct RaycastSample
    {
        public float timeSeconds;
        public double lat;
        public double lon;
        public double alt;
        public float yaw;
        public float pitch;
        public float roll;
        public Vector3 origin;
        public Vector3 direction;
        public bool hit;
        public Vector3 hitPoint;
        public Vector3 hitNormal;
        public float hitDistance;
        public int classId;
        public string label;
        public string detectionId;
    }

    private struct DetectionSample
    {
        public float timeSeconds;
        public int classId;
        public string label;
        public string detectionId;
        public float pixelX;
        public float pixelY;
        public bool normalized;
        public bool hit;
        public Vector3 hitPoint;
        public float hitDistance;
        public double lon;
        public double lat;
        public double alt;
    }

    [Serializable]
    public struct Detection
    {
        public Vector2 pixel;
        public bool normalized;
        public float timeSeconds;
        public int classId;
        public string label;
        public string detectionId;
    }

    [Serializable]
    public struct DetectionResult
    {
        public Detection detection;
        public bool hit;
        public Vector3 hitPoint;
        public Vector3 hitNormal;
        public float hitDistance;
        public double lon;
        public double lat;
        public double alt;
    }

    [Serializable]
    public struct PixelGeoResult
    {
        public Vector2 pixel;
        public bool normalized;
        public bool hit;
        public Vector3 worldPoint;
        public float hitDistance;
        public double lon;
        public double lat;
        public double alt;
    }

    private struct CachedHit
    {
        public bool hit;
        public RaycastHit hitInfo;
    }

    public enum DetectionPointMode
    {
        Centroid,
        BboxBottomCenter
    }

    public enum FilePathRoot
    {
        ProjectRoot,
        StreamingAssets,
        PersistentDataPath,
        DataPath
    }

}
