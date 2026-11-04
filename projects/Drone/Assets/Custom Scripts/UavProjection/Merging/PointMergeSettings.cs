using System;
using System.Collections.Generic;

namespace Triffid.Uav
{
    [Serializable]
    public sealed class PointMergeRule
    {
        public string className="person";
        public float maximumCentreDistanceMetres=0.75f;
        public float surfaceToleranceMetres=0.2f;
    }
    [Serializable]
    public sealed class PointMergeSettings
    {
        public float gridCellMetres=1f;
        public float maximumCentreDistanceMetres=1f;
        public float surfaceToleranceMetres=0.25f;
        public float minimumMaskAgreement=0.6f;
        public float maximumExtentRatio=3f;
        public float ambiguityMargin=0.1f;
        public int minimumSamples=4;
        public List<PointMergeRule> classRules=new List<PointMergeRule>
        {
            new PointMergeRule { className="person", maximumCentreDistanceMetres=0.75f, surfaceToleranceMetres=0.2f },
            new PointMergeRule { className="window", maximumCentreDistanceMetres=0.6f, surfaceToleranceMetres=0.15f },
            new PointMergeRule { className="civilian vehicle", maximumCentreDistanceMetres=2.5f, surfaceToleranceMetres=0.5f }
        };
        public PointMergeRule Rule(string name)
        {
            foreach(var rule in classRules) if(string.Equals(rule.className,name,StringComparison.OrdinalIgnoreCase)) return rule;
            return new PointMergeRule { className=name, maximumCentreDistanceMetres=maximumCentreDistanceMetres,
                surfaceToleranceMetres=surfaceToleranceMetres };
        }
        public void Validate()
        {
            Positive(gridCellMetres); Positive(maximumCentreDistanceMetres); Positive(surfaceToleranceMetres);
            if(maximumCentreDistanceMetres/gridCellMetres>16) throw new ArgumentException("Centre gate must be at most 16 grid cells.");
            if(float.IsNaN(minimumMaskAgreement)||minimumMaskAgreement<=0||minimumMaskAgreement>1 ||
                float.IsNaN(ambiguityMargin)||ambiguityMargin<0||ambiguityMargin>=1 ||
                float.IsNaN(maximumExtentRatio)||float.IsInfinity(maximumExtentRatio)||maximumExtentRatio<1 ||
                minimumSamples<2 || minimumSamples>256 || classRules==null) throw new ArgumentException("Invalid merger settings.");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var rule in classRules)
            {
                if(rule==null || string.IsNullOrWhiteSpace(rule.className)||!names.Add(rule.className)) throw new ArgumentException("Invalid or repeated class rule.");
                Positive(rule.maximumCentreDistanceMetres); Positive(rule.surfaceToleranceMetres);
                if(rule.maximumCentreDistanceMetres/gridCellMetres>16) throw new ArgumentException("Class centre gate must be at most 16 grid cells.");
            }
        }
        private static void Positive(float n)
        { if(float.IsNaN(n)||float.IsInfinity(n)||n<=0) throw new ArgumentException("Grid sizes and distance gates must be finite and positive."); }
    }
}
