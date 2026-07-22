using UnityEngine;

[CreateAssetMenu(
    fileName = "DroneInitialConfiguration",
    menuName = "TRIFFID/Drone Initial Configuration")]
public sealed class DroneInitialConfiguration : ScriptableObject
{
    [Header("Inputs")]
    public string srtFilePath;
    public string transformConfigFilePath;
    public SrtDroneRaycastPlayer.FilePathRoot inputPathRoot;

    [Header("Playback")]
    public bool useAbsAltitude;
    public float playbackSpeed;
    public bool loopPlayback;
    public bool interpolateFrames;

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

    [Header("Debug")]
    public bool drawDebug;

    [Header("Safe Object Reference Restoration")]
    public string mapRootObjectName;
    public string targetColliderObjectName;
    public string droneViewCameraObjectName;
    public Sprite droneSprite;
}
