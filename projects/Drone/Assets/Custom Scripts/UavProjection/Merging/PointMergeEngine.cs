using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Triffid.Uav
{
    public sealed class PointMergeGroup
    {
        public string Id;
        public readonly List<int> Members=new List<int>();
        public int Representative;
    }
    public sealed class PointMergeResult
    {
        public readonly List<PointMergeGroup> Groups=new List<PointMergeGroup>();
        public readonly JArray Decisions=new JArray();
        public int CandidatePairs;
        public JObject Objects(List<PointMergeEvidence> input,string sourceFile,string generation)
        {
            var features=new JArray();
            foreach(var group in Groups)
            {
                var representative=input[group.Representative];
                var properties=new JObject { ["object_id"]=group.Id,["class_id"]=representative.ClassId,
                    ["class_name"]=representative.ClassName,["observation_count"]=group.Members.Count,
                    ["representative_observation_id"]=representative.Id,
                    ["observation_ids"]=new JArray(group.Members.Select(i=>input[i].Id)),
                    ["source_ids"]=new JArray(group.Members.Select(i=>input[i].View)),
                    ["surface_hit"]=representative.Feature["properties"]["surface_hit"].DeepClone() };
                features.Add(new JObject { ["type"]="Feature",["properties"]=properties,
                    ["geometry"]=representative.Feature["geometry"].DeepClone() });
            }
            return new JObject { ["type"]="FeatureCollection",["features"]=features,
                ["merge"]=new JObject { ["schema_version"]="triffid.uav.objects.v1",["generation_id"]=generation,
                    ["source_json"]=sourceFile,["input_observations"]=input.Count,["output_objects"]=Groups.Count,
                    ["identity_scope"]="static capture; IDs depend on member observations, not persistent tracking IDs" } };
        }
        public JObject Report(List<PointMergeEvidence> input,PointMergeSettings settings,string generation)
        {
            return new JObject { ["schema_version"]="triffid.uav.dedup_report.v1",["generation_id"]=generation,
                ["input_observations"]=input.Count,["output_objects"]=Groups.Count,
                ["absorbed_repeat_observations"]=input.Count-Groups.Count,["candidate_pairs"]=CandidatePairs,
                ["settings"]=JObject.FromObject(settings),["groups"]=new JArray(Groups.Select(g=>new JObject
                { ["object_id"]=g.Id,["representative_observation_id"]=input[g.Representative].Id,
                    ["members"]=new JArray(g.Members.Select(i=>input[i].Id)),
                    ["views"]=new JArray(g.Members.Select(i=>input[i].View)) })),["pair_decisions"]=Decisions };
        }
    }
    /// <summary>Deterministic conservative association. Every successful input survives in exactly one group.</summary>
    public static class PointMergeEngine
    {
        private sealed class Pair
        { public int A,B; public double Score,Agreement,Distance; public bool Qualified,Mutual; public string Reason; }
        public static PointMergeResult Merge(List<PointMergeEvidence> input,PointMergeSettings settings,
            Func<int,int,bool> cancel=null)
        {
            settings.Validate();
            // Canonical order makes tie decisions and IDs independent of JSON/source order.
            input.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Id,b.Id));
            var result=new PointMergeResult();
            var grid=new PointSpatialGrid(settings.gridCellMetres);
            for(int i=0;i<input.Count;i++) grid.Add(input[i].Centre,i);
            var pairs=new List<Pair>();
            var byPair=new Dictionary<(int,int),Pair>();
            var choices=new Dictionary<(int,string),List<Pair>>();
            for(int i=0;i<input.Count;i++)
            {
                if(cancel!=null && cancel(i,input.Count)) throw new OperationCanceledException();
                PointMergeEvidence a=input[i]; PointMergeRule rule=settings.Rule(a.ClassName);
                foreach(int j in grid.Nearby(a.Centre,rule.maximumCentreDistanceMetres))
                {
                    if(j<=i) continue;
                    PointMergeEvidence b=input[j];
                    if(a.ClassId!=b.ClassId || !string.Equals(a.ClassName,b.ClassName,StringComparison.OrdinalIgnoreCase)) continue;
                    double distance=a.Centre.Distance(b.Centre);
                    if(distance>rule.maximumCentreDistanceMetres) continue;
                    var pair=new Pair { A=i,B=j,Distance=distance };
                    pairs.Add(pair); byPair.Add((i,j),pair);
                    if(a.View==b.View) pair.Reason="same_view_preserved";
                    else if(a.MetricSamples.Count<settings.minimumSamples || b.MetricSamples.Count<settings.minimumSamples)
                        pair.Reason="insufficient_surface_samples";
                    else if(Math.Max(a.Extent,b.Extent)/Math.Max(rule.surfaceToleranceMetres,Math.Min(a.Extent,b.Extent))>settings.maximumExtentRatio)
                        pair.Reason="incompatible_extent";
                    else
                    {
                        pair.Agreement=Math.Min(Coverage(a,b,rule.surfaceToleranceMetres),Coverage(b,a,rule.surfaceToleranceMetres));
                        pair.Score=pair.Agreement-0.1*distance/rule.maximumCentreDistanceMetres;
                        pair.Qualified=pair.Agreement>=settings.minimumMaskAgreement;
                        pair.Reason=pair.Qualified?"candidate":"mask_or_surface_disagreement";
                        if(pair.Qualified)
                        {
                            AddChoice(choices,(i,b.View),pair); AddChoice(choices,(j,a.View),pair);
                        }
                    }
                }
            }
            result.CandidatePairs=pairs.Count;
            var best=new Dictionary<(int,string),Pair>();
            foreach(var item in choices)
            {
                List<Pair> list=item.Value;
                list.Sort((a,b)=> { int cmp=b.Score.CompareTo(a.Score); return cmp!=0?cmp:a.A.CompareTo(b.A)!=0?a.A.CompareTo(b.A):a.B.CompareTo(b.B); });
                if(list.Count==1 || list[0].Score-list[1].Score>settings.ambiguityMargin) best.Add(item.Key,list[0]);
            }
            foreach(var pair in pairs.Where(p=>p.Qualified))
            {
                pair.Mutual=best.TryGetValue((pair.A,input[pair.B].View),out Pair ab) && ab==pair &&
                    best.TryGetValue((pair.B,input[pair.A].View),out Pair ba) && ba==pair;
                if(!pair.Mutual) pair.Reason="ambiguous_or_not_mutual_best";
            }
            var groupOf=new int[input.Count]; var groups=new List<List<int>>();
            for(int i=0;i<input.Count;i++) { groupOf[i]=i; groups.Add(new List<int>{i}); }
            foreach(var pair in pairs.Where(p=>p.Mutual).OrderByDescending(p=>p.Score).ThenBy(p=>p.A).ThenBy(p=>p.B))
            {
                if(cancel!=null && cancel(input.Count,input.Count)) throw new OperationCanceledException();
                int ga=groupOf[pair.A],gb=groupOf[pair.B];
                if(ga==gb) continue;
                bool consistent=true;
                foreach(int a in groups[ga]) foreach(int b in groups[gb])
                {
                    if(input[a].View==input[b].View || !byPair.TryGetValue((Math.Min(a,b),Math.Max(a,b)),out Pair support) || !support.Mutual)
                        consistent=false;
                }
                if(!consistent) { pair.Reason="cluster_view_or_complete_link_conflict"; continue; }
                foreach(int member in groups[gb]) { groups[ga].Add(member); groupOf[member]=ga; }
                groups[gb].Clear();
            }
            foreach(var members in groups.Where(g=>g.Count>0))
            {
                members.Sort(); int representative=members[0]; double smallest=double.MaxValue;
                foreach(int member in members)
                {
                    double sum=0; foreach(int other in members) sum+=input[member].Centre.Distance(input[other].Centre);
                    if(sum<smallest) { smallest=sum; representative=member; }
                }
                var group=new PointMergeGroup { Id=Id(members.Select(i=>input[i].Id)),Representative=representative };
                group.Members.AddRange(members);result.Groups.Add(group);
            }
            foreach(var pair in pairs)
                result.Decisions.Add(new JObject { ["a"]=input[pair.A].Id,["b"]=input[pair.B].Id,
                    ["distance_m"]=pair.Distance,["mask_surface_agreement"]=pair.Agreement,["score"]=pair.Score,
                    ["reason"]=groupOf[pair.A]==groupOf[pair.B]?"same_object":pair.Reason });
            return result;
        }
        private static double Coverage(PointMergeEvidence a,PointMergeEvidence b,double tolerance)
        {
            int supported=0;
            for(int i=0;i<a.MetricSamples.Count;i++)
            {
                if(!b.ContainsWorld(a.WorldSamples[i])) continue;
                foreach(var p in b.MetricSamples)
                    if(a.MetricSamples[i].Distance(p)<=tolerance) { supported++;break; }
            }
            return (double)supported/a.MetricSamples.Count;
        }
        private static void AddChoice(Dictionary<(int,string),List<Pair>> choices,(int,string) key,Pair pair)
        { if(!choices.TryGetValue(key,out var list)) choices.Add(key,list=new List<Pair>());list.Add(pair); }
        private static string Id(IEnumerable<string> members)
        {
            // JSON array encoding avoids delimiter collisions between observation IDs.
            using(var hash=SHA256.Create())
                return "obj_"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(new JArray(members).ToString(Newtonsoft.Json.Formatting.None))))
                    .Replace("-","").Substring(0,16).ToLowerInvariant();
        }
    }
}
