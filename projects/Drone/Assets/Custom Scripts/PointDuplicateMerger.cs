using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using Newtonsoft.Json.Linq;
using Triffid.Uav;
using UnityEngine;

[DisallowMultipleComponent]
[AddComponentMenu("TRIFFID/UAV/Point Duplicate Merger")]
public sealed class PointDuplicateMerger : MonoBehaviour
{
    public enum PreviewMode { Original, Merged, Both }
    [Header("Input")]
    [SerializeField] private string projectedPointsPath="Exports/uav_points.geojson";
    [SerializeField] private ProjectPathResolver.PathRoot inputPathRoot=ProjectPathResolver.PathRoot.PersistentDataPath;
    [Header("Matching")]
    [SerializeField] private PointMergeSettings matching=new PointMergeSettings();
    [Header("Output")]
    [SerializeField] private string objectsPath="Exports/uav_objects.geojson";
    [SerializeField] private string reportPath="Exports/dedup_report.json";
    [SerializeField] private ProjectPathResolver.PathRoot outputPathRoot=ProjectPathResolver.PathRoot.PersistentDataPath;
    [Header("Debug")]
    [SerializeField] private PreviewMode preview=PreviewMode.Both;
    [SerializeField] private bool showGrid;
    [SerializeField] private bool showMemberLinks=true;
    [SerializeField] private bool showRejectedLinks;
    [SerializeField] private bool showLabels;
    [Min(0)] [SerializeField] private int maximumDebugSamples=500;
    [Min(0.001f)] [SerializeField] private float markerRadiusMetres=0.08f;
    private List<PointMergeEvidence> observations;
    private PointMergeResult merged;
    private Matrix4x4 metricToWorld;
    private float previewCellSize;
    private int lastProgressTick;
    public string Status { get; private set; }="Re-run PointProjector first to capture sampled mask evidence.";
    public void SetInput(string path) { projectedPointsPath=path; }
    private string Resolve(string path,ProjectPathResolver.PathRoot root)
    {
        if(!ProjectPathResolver.TryResolvePath(path,root,out string result,out string error)) throw new InvalidDataException(error);
        return result;
    }
    [ContextMenu("Merge And Export")]
    public void MergeAndExport()
    {
        try
        {
            matching.Validate();
            string input=Resolve(projectedPointsPath,inputPathRoot), objects=Resolve(objectsPath,outputPathRoot),report=Resolve(reportPath,outputPathRoot);
            if(string.Equals(input,objects,StringComparison.OrdinalIgnoreCase)||string.Equals(input,report,StringComparison.OrdinalIgnoreCase)||
                string.Equals(objects,report,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Raw observations, merged objects and report must use different files.");
            JObject raw=JObject.Parse(File.ReadAllText(input));
            List<PointMergeEvidence> evidence=PointMergeEvidence.Read(raw);
            var frame=raw["projection"]?["world_to_enu_metres_row_major"] as JArray;
            UavObservationDocument.Require(frame?.Count==16,"Missing metric frame. Re-run PointProjector.");
            var worldToMetric=new Matrix4x4();
            for(int row=0;row<4;row++) for(int col=0;col<4;col++) worldToMetric[row,col]=(float)UavObservationDocument.Number(frame[row*4+col]);
            UavObservationDocument.Require(Mathf.Abs(worldToMetric.determinant)>1e-12f,"Invalid metric frame.");
            PointMergeResult result=PointMergeEngine.Merge(evidence,matching,Progress);
            string generation=Guid.NewGuid().ToString("N");
            JObject objectDocument=result.Objects(evidence,input,generation);
            objectDocument["merge"]["settings"]=JObject.FromObject(matching);
            objectDocument["merge"]["projection"]=raw["projection"].DeepClone();
            JObject reportDocument=result.Report(evidence,matching,generation);
            reportDocument["source_json"]=input;
            // Cancellation is checked only before publishing; each file is replaced atomically.
            UavProjectionSession.WriteAtomically(report,reportDocument);
            UavProjectionSession.WriteAtomically(objects,objectDocument);
            observations=evidence; merged=result;metricToWorld=worldToMetric.inverse;previewCellSize=matching.gridCellMetres;
            Status=$"{evidence.Count} observations → {result.Groups.Count} objects; {evidence.Count-result.Groups.Count} repeat sightings grouped. Objects: {objects}; report: {report}";
            Debug.Log($"[PointDuplicateMerger] {Status}",this);
        }
        catch(OperationCanceledException) { Status="Cancelled; previous outputs and preview preserved."; }
        catch(Exception ex) { Status=ex.Message;Debug.LogError($"[PointDuplicateMerger] {Status}",this); }
        finally
        {
#if UNITY_EDITOR
            UnityEditor.EditorUtility.ClearProgressBar();UnityEditor.SceneView.RepaintAll();
#endif
        }
    }
    private bool Progress(int current,int total)
    {
#if UNITY_EDITOR
        if(!Application.isPlaying && (current%32==0 || current==total) &&
            unchecked(Environment.TickCount-lastProgressTick)>=50)
        {
            lastProgressTick=Environment.TickCount;
            return UnityEditor.EditorUtility.DisplayCancelableProgressBar("UAV duplicate matching",
                "Checking cross-view mask and surface agreement",total==0?1f:(float)current/total);
        }
#endif
        return false;
    }
    [ContextMenu("Clear Debug")]
    public void ClearDebug() { observations=null; merged=null; }
    private static Vector3 V(MergePoint p) => new Vector3((float)p.X,(float)p.Y,(float)p.Z);
    private void OnDrawGizmosSelected()
    {
        if(observations==null||merged==null||maximumDebugSamples<=0) return;
        Matrix4x4 old=Gizmos.matrix;Gizmos.matrix=metricToWorld;
        try
        {
            int drawn=0;
            for(int g=0;g<merged.Groups.Count && drawn<maximumDebugSamples;g++)
            {
                var group=merged.Groups[g];var representative=observations[group.Representative];
                Gizmos.color=Color.HSVToRGB((g*0.61803398875f)%1f,0.8f,1f);
                foreach(int member in group.Members)
                {
                    if(drawn++>=maximumDebugSamples) break;
                    Vector3 point=V(observations[member].Centre);
                    if(preview!=PreviewMode.Merged) Gizmos.DrawSphere(point,markerRadiusMetres);
                    if(showMemberLinks) Gizmos.DrawLine(point,V(representative.Centre));
                }
                if(preview!=PreviewMode.Original) Gizmos.DrawWireSphere(V(representative.Centre),markerRadiusMetres*2f);
#if UNITY_EDITOR
                if(showLabels) UnityEditor.Handles.Label(V(representative.World),$"{representative.ClassName}: {group.Members.Count} view(s) {group.Id}");
#endif
            }
            if(showGrid)
            {
                var cells=new HashSet<(long,long,long)>();Gizmos.color=new Color(0.5f,0.5f,0.5f,0.4f);
                foreach(var e in observations)
                {
                    var key=((long)Math.Floor(e.Centre.X/previewCellSize),(long)Math.Floor(e.Centre.Y/previewCellSize),(long)Math.Floor(e.Centre.Z/previewCellSize));
                    if(!cells.Add(key)) continue;if(cells.Count>maximumDebugSamples) break;
                    Gizmos.DrawWireCube(new Vector3((key.Item1+0.5f)*previewCellSize,(key.Item2+0.5f)*previewCellSize,(key.Item3+0.5f)*previewCellSize),Vector3.one*previewCellSize);
                }
            }
            if(showRejectedLinks)
            {
                var lookup=new Dictionary<string,PointMergeEvidence>();foreach(var e in observations) lookup.Add(e.Id,e);
                int count=0;Gizmos.color=Color.red;
                foreach(JObject decision in merged.Decisions)
                {
                    if((string)decision["reason"]=="same_object") continue;
                    if(count++>=maximumDebugSamples) break;
                    Gizmos.DrawLine(V(lookup[(string)decision["a"]].Centre),V(lookup[(string)decision["b"]].Centre));
                }
            }
        }
        finally { Gizmos.matrix=old; }
    }
}
