using System;
using UnityEngine;

/// <summary>
/// Geometry and provenance retained for one semantic-mask raycast.
/// TriangleIndex must be interpreted together with ColliderInstanceId (or ColliderPath),
/// because triangle indices are local to a MeshCollider.
/// </summary>
[Serializable]
public struct SurfaceHit
{
    public Vector3 WorldPoint;
    public int TriangleIndex;
    public Vector3 Normal;
    public float Distance;
    public Vector3 RayDirection;

    public int ColliderInstanceId;
    public string ColliderPath;

    public int ViewIndex;
    public int FrameIndex;
    public string LocalDetectionId;
    public string ClassName;
    public float Confidence;

    public SurfaceHit(
        RaycastHit raycastHit,
        Ray sourceRay,
        int viewIndex,
        int frameIndex,
        string localDetectionId,
        string className,
        float confidence,
        string colliderPath = null)
    {
        WorldPoint = raycastHit.point;
        TriangleIndex = raycastHit.triangleIndex;
        Normal = raycastHit.normal;
        Distance = raycastHit.distance;
        RayDirection = sourceRay.direction.sqrMagnitude > 0f
            ? sourceRay.direction.normalized
            : Vector3.zero;

        Collider collider = raycastHit.collider;
        ColliderInstanceId = collider != null ? collider.GetInstanceID() : 0;
        ColliderPath = colliderPath ?? BuildColliderPath(collider);

        ViewIndex = viewIndex;
        FrameIndex = frameIndex;
        LocalDetectionId = localDetectionId ?? string.Empty;
        ClassName = className ?? string.Empty;
        Confidence = confidence;
    }

    public static string BuildColliderPath(Collider collider)
    {
        Transform transform = collider != null ? collider.transform : null;
        if (transform == null)
            return string.Empty;

        string path = transform.name;
        Transform parent = transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }
}
