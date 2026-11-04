using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Triffid.Uav;
using UnityEditor;
using UnityEngine;

public static class PointMergeChecks
{
    [MenuItem("TRIFFID/UAV/Run Duplicate Merge Checks")]
    public static void RunAll()
    {
        int checks=0;
        var settings=new PointMergeSettings();
        var people=new List<PointMergeEvidence>();
        for(int i=0;i<3;i++) { people.Add(Person("a"+i,"view_a",i*0.35));people.Add(Person("b"+i,"view_b",i*0.35+0.01)); }
        var result=PointMergeEngine.Merge(people,settings);
        Check(result.Groups.Count==3 && result.Groups.All(g=>g.Members.Count==2),"Three nearby people in one grid cell must remain three objects.");checks++;
        Check(result.Groups.All(g=>g.Members.Select(i=>people[i].View).Distinct().Count()==g.Members.Count),"One-view constraint failed.");checks++;
        Check(result.Groups.SelectMany(g=>g.Members).OrderBy(i=>i).SequenceEqual(Enumerable.Range(0,people.Count)),"Some observations were lost or duplicated.");checks++;
        string[] ids=result.Groups.Select(g=>g.Id).OrderBy(i=>i,StringComparer.Ordinal).ToArray();
        people.Reverse();var reordered=PointMergeEngine.Merge(people,settings);
        Check(ids.SequenceEqual(reordered.Groups.Select(g=>g.Id).OrderBy(i=>i,StringComparer.Ordinal)),"IDs depend on input order.");checks++;
        settings.gridCellMetres=0.2f;
        Check(ids.SequenceEqual(PointMergeEngine.Merge(people,settings).Groups.Select(g=>g.Id).OrderBy(i=>i,StringComparer.Ordinal)),"Grid cell size changed identity decisions.");checks++;
        settings=new PointMergeSettings();
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","a",-0.01),Person("b","b",0.01)},settings).Groups.Count==1,
            "Matching failed across negative/positive grid boundary.");checks++;
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","same",0),Person("b","same",0.01)},settings).Groups.Count==2,
            "Distinct same-frame people were merged.");checks++;
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","a",0),Person("b","a",0.05),Person("c","b",0.025)},settings).Groups.Count==3,
            "Ambiguous person association should stay separate.");checks++;
        settings.classRules[0].surfaceToleranceMetres=0.17f;
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","a",0),Person("b","b",0.15),Person("c","c",0.3)},settings).Groups.Count==2,
            "A-B-C chain merged without A-C support.");checks++;
        var sparse=Person("b","b",0.01); sparse.MetricSamples.Clear();sparse.WorldSamples.Clear();
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","a",0),sparse},new PointMergeSettings()).Groups.Count==2,
            "Missing evidence removed or incorrectly merged an observation.");checks++;
        var car=Person("b","b",0.01);car.ClassName="civilian vehicle";car.ClassId=21;
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","a",0),car},new PointMergeSettings()).Groups.Count==2,"Different classes merged.");checks++;
        var upstairs=Person("b","b",0); upstairs.Centre=new MergePoint(0,0,3);
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","a",0),upstairs},new PointMergeSettings()).Groups.Count==2,"Height was ignored.");checks++;
        var excluded=Person("b","b",0.01);
        var crop=new JObject { ["bbox_xyxy"]=new JArray(0,0,1,1),["mask"]=new JObject
            { ["encoding"]="binary_rle_row_major",["size_hw"]=new JArray(1,1),["offset_xy"]=new JArray(0,0),["counts"]=new JArray(0,1) } };
        excluded.Mask=UavBinaryMask.Decode(crop,64,64);
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","a",0),excluded},new PointMergeSettings()).Groups.Count==2,"Cross-view mask evidence was ignored.");checks++;
        var large=Person("b","b",0.01);large.Extent=5;
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>{Person("a","a",0),large},new PointMergeSettings()).Groups.Count==2,"Incompatible footprint extents merged.");checks++;
        var single=new List<PointMergeEvidence>{Person("a","one",0)};
        var singleton=PointMergeEngine.Merge(single,new PointMergeSettings());
        var objects=singleton.Objects(single,"raw.geojson","test-generation");
        Check(objects["features"].Count()==1 && JToken.DeepEquals(objects["features"][0]["geometry"],single[0].Feature["geometry"]),"Representative point drifted off the recorded surface hit.");checks++;
        Check(singleton.Report(single,new PointMergeSettings(),"test")["input_observations"].Value<int>()==1,"Report lost a singleton.");checks++;
        bool cancelled=false;
        try { PointMergeEngine.Merge(people,new PointMergeSettings(),(a,b)=>true); } catch(OperationCanceledException) { cancelled=true; }
        Check(cancelled,"Cancellation was ignored.");checks++;
        Check(PointMergeEngine.Merge(new List<PointMergeEvidence>(),new PointMergeSettings()).Groups.Count==0,"Empty export failed.");checks++;
        var raw=new JObject { ["type"]="FeatureCollection",["projection"]=new JObject
            { ["schema_version"]="triffid.uav.projected.v1",["kind"]="point_detections",["evidence_space"]="local_enu_metres_v1" },
            ["features"]=new JArray(single[0].Feature.DeepClone()) };
        Check(PointMergeEvidence.Read(raw).Count==1,"Projection evidence schema roundtrip failed.");checks++;
        raw["projection"].Remove();bool rejected=false;
        try { PointMergeEvidence.Read(raw); } catch(System.IO.InvalidDataException) { rejected=true; }
        Check(rejected,"Legacy/missing evidence should request a fresh point projection.");checks++;
        var samples=single[0].Mask.ForegroundSamples(25).ToList();
        Check(samples.Count==25 && samples.Select(v=>$"{v.x}:{v.y}").Distinct().Count()==25 && samples.All(v=>single[0].Mask.Contains((int)v.x,(int)v.y)),
            "Foreground sampling must produce distinct in-mask pixel centres.");checks++;
        Debug.Log($"[Duplicate Checks] PASS: {checks} cases: people sharing cells, cross-view association, boundaries, ambiguity, chain prevention, mask evidence, height, singleton retention, stable IDs and cancellation.");
    }
    private static PointMergeEvidence Person(string id,string view,double x)
    {
        var mask=new JObject { ["bbox_xyxy"]=new JArray(0,0,64,64),["mask"]=new JObject
            { ["encoding"]="binary_rle_row_major",["size_hw"]=new JArray(64,64),["offset_xy"]=new JArray(0,0),["counts"]=new JArray(0,4096) } };
        var matrix=new double[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
        var samples=new JArray();
        foreach(double dx in new[]{-0.02,0.02}) foreach(double dy in new[]{-0.02,0.02})
            samples.Add(new JObject { ["world_xyz"]=new JArray(x+dx,dy,0),["enu_xyz_m"]=new JArray(x+dx,dy,0) });
        var feature=new JObject { ["type"]="Feature",["geometry"]=new JObject { ["type"]="Point",["coordinates"]=new JArray(23.7,37.9,1) },
            ["properties"]=new JObject { ["observation_id"]=id,["source_id"]=view,["class_id"]=1,["class_name"]="person",
                ["image_width"]=64,["image_height"]=64,["bbox_xyxy"]=mask["bbox_xyxy"].DeepClone(),["mask_evidence"]=mask["mask"].DeepClone(),
                ["world_to_clip_row_major"]=new JArray(matrix),["surface_samples"]=samples,
                ["surface_hit"]=new JObject { ["world_xyz"]=new JArray(x,0,0),["enu_xyz_m"]=new JArray(x,0,0) } } };
        var raw=new JObject { ["type"]="FeatureCollection",["projection"]=new JObject
            { ["schema_version"]="triffid.uav.projected.v1",["kind"]="point_detections",["evidence_space"]="local_enu_metres_v1" },["features"]=new JArray(feature) };
        return PointMergeEvidence.Read(raw)[0];
    }
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
}
