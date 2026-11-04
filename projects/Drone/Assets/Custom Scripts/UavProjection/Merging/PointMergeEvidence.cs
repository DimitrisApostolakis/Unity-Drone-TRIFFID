using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Triffid.Uav
{
    public readonly struct MergePoint
    {
        public readonly double X, Y, Z;
        public MergePoint(double x, double y, double z) { X=x; Y=y; Z=z; }
        public static MergePoint Read(JToken a)
        {
            UavObservationDocument.Require(a is JArray && ((JArray)a).Count == 3, "Expected three coordinates.");
            return new MergePoint(UavObservationDocument.Number(a[0]), UavObservationDocument.Number(a[1]), UavObservationDocument.Number(a[2]));
        }
        public double Distance(MergePoint b) { double x=X-b.X,y=Y-b.Y,z=Z-b.Z; return Math.Sqrt(x*x+y*y+z*z); }
    }

    public sealed class PointMergeEvidence
    {
        public string Id, View, ClassName;
        public int ClassId, Width, Height;
        public MergePoint Centre, World;
        public readonly List<MergePoint> MetricSamples = new List<MergePoint>();
        public readonly List<MergePoint> WorldSamples = new List<MergePoint>();
        public UavBinaryMask Mask;
        public double[] WorldToClip;
        public JObject Feature;
        public double Extent;

        public bool ContainsWorld(MergePoint p)
        {
            double[] m=WorldToClip;
            double w=m[12]*p.X+m[13]*p.Y+m[14]*p.Z+m[15];
            if (w <= 1e-9) return false;
            double nx=(m[0]*p.X+m[1]*p.Y+m[2]*p.Z+m[3])/w;
            double ny=(m[4]*p.X+m[5]*p.Y+m[6]*p.Z+m[7])/w;
            if (nx < -1 || nx >= 1 || ny <= -1 || ny > 1) return false;
            int x=(int)Math.Floor((nx+1)*0.5*Width), y=(int)Math.Floor((1-ny)*0.5*Height);
            return Mask.Contains(x-Mask.X,y-Mask.Y);
        }

        public static List<PointMergeEvidence> Read(JObject document)
        {
            UavObservationDocument.Require((string)document["type"] == "FeatureCollection" &&
                (string)document["projection"]?["schema_version"] == "triffid.uav.projected.v1" &&
                (string)document["projection"]?["kind"] == "point_detections", "Expected a PointProjector export.");
            UavObservationDocument.Require((string)document["projection"]?["evidence_space"] == "local_enu_metres_v1",
                "Re-run PointProjector with mask sampling enabled to export merger evidence.");
            var features=document["features"] as JArray;
            UavObservationDocument.Require(features != null, "Missing point features.");
            var ids=new HashSet<string>();
            var result=new List<PointMergeEvidence>();
            foreach (JObject feature in features)
            {
                JObject p=feature["properties"] as JObject;
                UavObservationDocument.Require(p != null && (string)feature["geometry"]?["type"] == "Point", "Expected point observations.");
                var e=new PointMergeEvidence { Feature=feature, Id=UavObservationDocument.Text(p,"observation_id"),
                    View=UavObservationDocument.Text(p,"source_id"), ClassName=UavObservationDocument.Text(p,"class_name"),
                    ClassId=UavObservationDocument.Integer(p["class_id"]), Width=UavObservationDocument.Integer(p["image_width"]),
                    Height=UavObservationDocument.Integer(p["image_height"]), Centre=MergePoint.Read(p["surface_hit"]?["enu_xyz_m"]),
                    World=MergePoint.Read(p["surface_hit"]?["world_xyz"]) };
                UavObservationDocument.Require(ids.Add(e.Id), "Repeated observation ID.");
                UavObservationDocument.Require(e.Width>0 && e.Height>0, "Invalid source dimensions.");
                // This wrapper reuses the perception crop/bounding-box validator.
                e.Mask=UavBinaryMask.Decode(new JObject { ["mask"]=p["mask_evidence"]?.DeepClone(),
                    ["bbox_xyxy"]=p["bbox_xyxy"]?.DeepClone() },e.Width,e.Height);
                var matrix=p["world_to_clip_row_major"] as JArray;
                UavObservationDocument.Require(matrix?.Count==16, "Missing capture projection matrix.");
                e.WorldToClip=new double[16];
                for(int i=0;i<16;i++) e.WorldToClip[i]=UavObservationDocument.Number(matrix[i]);
                var samples=p["surface_samples"] as JArray;
                UavObservationDocument.Require(samples != null && samples.Count<=256, "Invalid surface sample evidence.");
                foreach(JObject sample in samples)
                { e.MetricSamples.Add(MergePoint.Read(sample["enu_xyz_m"])); e.WorldSamples.Add(MergePoint.Read(sample["world_xyz"])); }
                // A small/no-hit mask remains a singleton rather than being removed.
                for(int i=0;i<e.MetricSamples.Count;i++)
                    for(int j=0;j<i;j++) e.Extent=Math.Max(e.Extent,e.MetricSamples[i].Distance(e.MetricSamples[j]));
                MergePoint.Read(feature["geometry"]["coordinates"]);
                result.Add(e);
            }
            result.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Id,b.Id));
            return result;
        }
    }
}
