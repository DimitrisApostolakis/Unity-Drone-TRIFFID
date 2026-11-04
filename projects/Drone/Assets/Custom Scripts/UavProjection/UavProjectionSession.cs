using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Triffid.Uav
{
    public sealed class UavProjectionSession : IDisposable
    {
        public readonly Camera Camera;
        public readonly JArray Features = new JArray();
        public readonly JArray Skipped = new JArray();
        public int AttemptedRays, SuccessfulRays;
        private readonly SrtDroneRaycastPlayer player;
        private readonly UavProjectionDebug debug;
        private readonly Matrix4x4 worldToMetric;
        public readonly bool HasMetricFrame;
        public JArray MetricFrame => Matrix(worldToMetric);
        public static JArray Matrix(Matrix4x4 matrix)
        {
            var values=new JArray();
            for(int row=0;row<4;row++) for(int col=0;col<4;col++) values.Add(matrix[row,col]);
            return values;
        }

        public UavProjectionSession(SrtDroneRaycastPlayer calibration, UavProjectionDebug debug)
        {
            player = calibration;
            this.debug = debug;
            var metricToWorld=Matrix4x4.identity;
            if(player.TryConvertEnuOffsetToWorldVector(new Vector3(1,0,0),out Vector3 east) &&
                player.TryConvertEnuOffsetToWorldVector(new Vector3(0,1,0),out Vector3 north) &&
                player.TryConvertEnuOffsetToWorldVector(new Vector3(0,0,1),out Vector3 up))
            {
                metricToWorld.SetColumn(0,new Vector4(east.x,east.y,east.z,0));
                metricToWorld.SetColumn(1,new Vector4(north.x,north.y,north.z,0));
                metricToWorld.SetColumn(2,new Vector4(up.x,up.y,up.z,0));
                HasMetricFrame=Mathf.Abs(metricToWorld.determinant)>1e-12f;
            }
            worldToMetric=metricToWorld.inverse;
            var obj = new GameObject("UAV Projection Camera") { hideFlags = HideFlags.HideAndDontSave };
            Camera = obj.AddComponent<Camera>();
            if (player.DroneViewCamera != null) Camera.CopyFrom(player.DroneViewCamera);
            Camera.enabled = false;
            Camera.targetTexture = null;
            Camera.rect = new Rect(0, 0, 1, 1);
        }
        public bool Project(Vector2 pixelEdgePosition, UavObservationSource source, string label,
            out JArray coordinates, out JObject surface, out Vector3 world, bool recordDebug=true)
        {
            AttemptedRays++;
            Ray ray = Camera.ViewportPointToRay(new Vector3(pixelEdgePosition.x / source.Width,
                1f - pixelEdgePosition.y / source.Height, 0));
            coordinates = null; surface = null; world = default;
            if (!player.TryRaycastMap(ray, out RaycastHit hit))
            {
                if(recordDebug) debug.Ray(ray.origin, ray.GetPoint(25f), false, label);
                return false;
            }
            if (!player.TryConvertWorldToWgs84(hit.point, out double lon, out double lat, out double alt)) return false;
            SuccessfulRays++;
            world = hit.point;
            coordinates = new JArray(lon, lat, alt);
            surface = new JObject
            {
                ["pixel_xy_edge"] = new JArray(pixelEdgePosition.x, pixelEdgePosition.y),
                ["world_xyz"] = Vector(hit.point), ["normal_xyz"] = Vector(hit.normal),
                ["distance"] = hit.distance, ["triangle_index"] = hit.triangleIndex,
                ["collider"] = hit.collider.name
            };
            if(HasMetricFrame) surface["enu_xyz_m"]=Vector(worldToMetric.MultiplyPoint3x4(hit.point));
            if(recordDebug) debug.Ray(ray.origin, hit.point, true, label);
            return true;
        }
        public JObject Properties(UavObservationSource source, JObject observation)
        {
            var props = new JObject
            {
                ["observation_id"] = observation["observation_id"].DeepClone(),
                ["class_id"] = observation["class_id"].DeepClone(),
                ["class_name"] = observation["class_name"].DeepClone(),
                ["source_id"] = source.Id, ["image_width"] = source.Width, ["image_height"] = source.Height,
                ["camera_world_xyz"] = Vector(Camera.transform.position),
                ["camera_rotation_xyzw"] = new JArray(Camera.transform.rotation.x, Camera.transform.rotation.y,
                    Camera.transform.rotation.z, Camera.transform.rotation.w)
            };
            foreach (string key in new[] { "source_class_ids", "target_geometry", "area_px", "bbox_xyxy", "centroid_xy", "raycast_point_xy", "score", "score_type" })
                if (observation[key] != null) props[key] = observation[key].DeepClone();
            foreach (string key in new[] { "frame_index", "timestamp_s", "telemetry" })
                if (source.Metadata[key] != null) props[key] = source.Metadata[key].DeepClone();
            return props;
        }
        public void Skip(UavObservationSource source, JObject observation, string reason)
        {
            Skipped.Add(new JObject { ["source_id"] = source.Id,
                ["observation_id"] = observation["observation_id"].DeepClone(), ["reason"] = reason });
        }
        public JObject Document(string input, string kind)
        {
            return new JObject
            {
                ["type"] = "FeatureCollection", ["features"] = Features,
                ["projection"] = new JObject
                {
                    ["schema_version"] = "triffid.uav.projected.v1", ["kind"] = kind,
                    ["source_json"] = input, ["coordinate_order"] = "longitude,latitude,altitude",
                    ["deduplication"] = "none; features are observations, not fused world objects",
                    ["attempted_rays"] = AttemptedRays, ["successful_rays"] = SuccessfulRays,
                    ["skipped_observations"] = Skipped
                }
            };
        }
        public static JArray Vector(Vector3 v) => new JArray(v.x, v.y, v.z);
        public static void WriteAtomically(string path, JObject document)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, document.ToString(Formatting.Indented));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void Dispose()
        {
            if (Camera == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(Camera.gameObject);
            else UnityEngine.Object.DestroyImmediate(Camera.gameObject);
        }
    }

    /// <summary>Bounded scene-view samples: no marker GameObjects and no per-frame raycasts.</summary>
    public sealed class UavProjectionDebug
    {
        private readonly List<(Vector3 From, Vector3 To, bool Hit, string Label)> rays =
            new List<(Vector3, Vector3, bool, string)>();
        private readonly List<List<Vector3>> rings = new List<List<Vector3>>();
        public int Limit;
        private int ringVertices;
        public void Clear() { rays.Clear(); rings.Clear(); ringVertices = 0; }
        public void Ray(Vector3 from, Vector3 to, bool hit, string label)
        { if (rays.Count < Limit) rays.Add((from, to, hit, label)); }
        public void Ring(List<Vector3> ring)
        {
            if (ring.Count > Limit - ringVertices) return;
            rings.Add(ring);
            ringVertices += ring.Count;
        }
        public void Draw(bool showRays, bool labels, float size)
        {
            foreach (var ray in rays)
            {
                Gizmos.color = ray.Hit ? Color.cyan : Color.red;
                if (showRays) Gizmos.DrawLine(ray.From, ray.To);
                if (ray.Hit) Gizmos.DrawSphere(ray.To, size);
#if UNITY_EDITOR
                if (labels) UnityEditor.Handles.Label(ray.To, ray.Label);
#endif
            }
            Gizmos.color = Color.yellow;
            foreach (List<Vector3> ring in rings)
                for (int i = 0; i < ring.Count; i++) Gizmos.DrawLine(ring[i], ring[(i + 1) % ring.Count]);
        }
    }
}
