using UnityEngine;

public class MapColliderSync : MonoBehaviour
{
    public Transform MapTransform;
    public Transform MeshColliderObject;

    [SerializeField] private Vector3 rotationOffsetEuler = new Vector3(123f, 0f, 0f);

    public void SyncToMap()
    {
        if (MapTransform == null || MeshColliderObject == null) return;

        MeshColliderObject.position   = MapTransform.position;
        MeshColliderObject.localScale = MapTransform.localScale;
        MeshColliderObject.rotation   = MapTransform.rotation * Quaternion.Euler(rotationOffsetEuler);
    }
}