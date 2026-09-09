using UnityEngine;

public class MapColliderSync : MonoBehaviour
{
    [Header("References")]
    public Transform MapTransform;

    public Transform GaussianRenderer;

    public Transform MeshColliderObject;

    [Header("Alignment")]
    [SerializeField] private Vector3 rotationOffsetEuler = new Vector3(123f, 0f, 0f);

    [SerializeField] private bool copyPositionFromMapRoot = true;

    [SerializeField] private bool copyScaleFromMapRoot = true;

    [SerializeField] private bool useGaussianWorldRotation = true;

    private bool missingMapWarningLogged;
    private bool missingGaussianWarningLogged;
    private bool missingColliderWarningLogged;

    [ContextMenu("Sync To Map")]
    public void SyncToMap()
    {
        bool canSync = true;

        if (MapTransform == null)
        {
            if (!missingMapWarningLogged)
            {
                Debug.LogWarning("[MapColliderSync] Map transform is missing; collider sync cannot run.", this);
                missingMapWarningLogged = true;
            }

            canSync = false;
        }
        else
        {
            missingMapWarningLogged = false;
        }

        if (GaussianRenderer == null)
        {
            if (!missingGaussianWarningLogged)
            {
                Debug.LogWarning("[MapColliderSync] Gaussian Renderer transform is missing; collider sync cannot run.", this);
                missingGaussianWarningLogged = true;
            }

            canSync = false;
        }
        else
        {
            missingGaussianWarningLogged = false;
        }

        if (MeshColliderObject == null)
        {
            if (!missingColliderWarningLogged)
            {
                Debug.LogWarning("[MapColliderSync] Mesh collider transform is missing; collider sync cannot run.", this);
                missingColliderWarningLogged = true;
            }

            canSync = false;
        }
        else
        {
            missingColliderWarningLogged = false;
        }

        if (!canSync)
        {
            return;
        }

        if (copyPositionFromMapRoot)
        {
            MeshColliderObject.position = MapTransform.position;
        }
        else
        {
            MeshColliderObject.position = GaussianRenderer.position;
        }

        if (copyScaleFromMapRoot)
        {
            MeshColliderObject.localScale = MapTransform.localScale;
        }
        else
        {
            MeshColliderObject.localScale = GaussianRenderer.localScale;
        }

        Quaternion baseRotation = useGaussianWorldRotation
            ? GaussianRenderer.rotation
            : MapTransform.rotation * GaussianRenderer.localRotation;

        MeshColliderObject.rotation = baseRotation * Quaternion.Euler(rotationOffsetEuler);
    }
}
