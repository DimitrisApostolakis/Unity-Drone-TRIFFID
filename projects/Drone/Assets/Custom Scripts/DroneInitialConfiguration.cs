using UnityEngine;

[CreateAssetMenu(
    fileName = "DroneInitialConfiguration",
    menuName = "TRIFFID/Drone Initial Configuration")]
public sealed class DroneInitialConfiguration : ScriptableObject
{
    [Header("Build Inputs and Paths")]
    public string srtFilePath;
    public string transformConfigFilePath;
    public SrtDroneRaycastPlayer.FilePathRoot inputPathRoot;
    public SrtDroneRaycastPlayer.FilePathRoot outputPathRoot;

    [Header("Playback")]
    public bool useAbsAltitude;
    public float playbackSpeed;
    public bool loopPlayback;
    public bool interpolateFrames;
    public float detectionTimeEpsilon;

    [Header("Alignment Corrections")]
    public bool flipPositionX;
    public bool flipPositionY;
    public bool flipDirectionX;
    public bool flipDirectionY;

    [Header("Raycast Target")]
    public float raycastDistance;
    public LayerMask raycastMask;
    public bool requireTargetTag;
    public string targetColliderTag;
    public bool raycastTargetOnly;
    public Vector3 cameraOffsetLocal;
    public float rayOriginUpOffset;
    public float rayOriginForwardOffset;

    [Header("DJI Gimbal")]
    public float yawOffsetDegrees;
    public Vector3 gimbalBaseRotationEuler;
    public bool flipYaw;
    public bool flipPitch;
    public bool flipRoll;

    [Header("Drone View Camera")]
    public float droneSpriteScale;
    public bool autoCreateDroneViewCamera;
    public bool estimateFovFromFocalLength;
    public float sensorHeightMm;
    public float fixedVerticalFovDegrees;

    [Header("Optional Full-Frame Pixel Export")]
    public bool capturePixelCoordinates;
    public bool capturePixelUsingJobs;
    public int pixelStep;
    public bool captureOnlyHits;
    public int captureEveryNFrames;
    public bool captureFullFrame;
    public Vector2 captureRegionNormalizedSize;
    public int maxPixelSamplesPerFrame;
    public bool invertPixelY;
    public string pixelCsvPath;
    public int pixelCsvFlushEveryNFrames;

    [Header("Outputs")]
    public bool recordRaycastSamples;
    public bool recordMisses;
    public string outputCsvPath;
    public bool recordDetectionsToCsv;
    public string detectionCsvPath;

    [Header("Detection Import")]
    public string detectionJsonPath;
    public bool processDetectionsOnStart;
    public bool useDetectionRaycastJobs;
    public SrtDroneRaycastPlayer.DetectionPointMode detectionPointMode;
    public bool normalizeDetectionPixels;

    [Header("Debug")]
    public bool drawDebug;
    public bool drawPixelRayVisualization;
    public int drawPixelRayEveryN;
    public Color pixelRayHitColor;
    public Color pixelRayMissColor;
    public Color pixelHitNormalColor;
    public float pixelDebugRayDuration;
    public float pixelDebugNormalLength;
    public bool drawDetectionHitVisualization;
    public Color detectionHitColor;
    public Color detectionHitNormalColor;
    public float detectionDebugRayDuration;

    [Header("Safe Object Reference Restoration")]
    public string mapRootObjectName;
    public string targetColliderObjectName;
    public string droneViewCameraObjectName;
    public Sprite droneSprite;
}
