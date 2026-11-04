using System;
using System.Collections;
using System.IO;
using GaussianSplatting.Runtime;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Triffid.Uav
{
    /// <summary>Common job lifecycle, file paths, export and debug controls.</summary>
    public abstract class UavProjector : MonoBehaviour
    {
        [Header("Inputs")]
        [Tooltip("Shared trajectory player provides camera calibration, alignment and mesh raycasting.")]
        [SerializeField] protected SrtDroneRaycastPlayer player;
        [SerializeField] protected string observationsJsonPath = "";
        [SerializeField] protected ProjectPathResolver.PathRoot inputPathRoot = ProjectPathResolver.PathRoot.ProjectRoot;

        [Header("Output")]
        [SerializeField] private string outputGeoJsonPath = "";
        [SerializeField] private ProjectPathResolver.PathRoot outputPathRoot = ProjectPathResolver.PathRoot.PersistentDataPath;

        [Header("Performance")]
        [Min(1)] [Tooltip("Maximum projection rays per editor update or rendered frame.")]
        [SerializeField] protected int raysPerUpdate = 256;

        [Header("Debug")]
        [SerializeField] private bool showDebug = true;
        [SerializeField] private bool showRays;
        [SerializeField] private bool showLabels;
        [Min(0)] [SerializeField] private int maximumDebugSamples = 300;
        [Min(0.001f)] [SerializeField] private float markerSize = 0.08f;
        [SerializeField] private bool logSummary = true;

        protected readonly UavProjectionDebug DebugView = new UavProjectionDebug();
        private IEnumerator job;
        private Coroutine coroutine;
        public bool IsRunning => job != null;
        public string LastExportPath { get; private set; }
        public string Status { get; protected set; } = "Ready";
        protected abstract string TaskName { get; }
        protected abstract string Geometry { get; }
        protected abstract void Prepare(UavObservationDocument doc);
        protected abstract IEnumerator Project(UavObservationDocument doc, UavProjectionSession session);
        protected abstract string DefaultOutput { get; }
        protected string Input(string path)
        {
            if (!ProjectPathResolver.TryResolvePath(path, inputPathRoot, out string resolved, out string error))
                throw new InvalidDataException(error);
            if (!File.Exists(resolved)) throw new FileNotFoundException("Input file is missing.", resolved);
            return resolved;
        }
        private string Output()
        {
            string path = string.IsNullOrWhiteSpace(outputGeoJsonPath) ? DefaultOutput : outputGeoJsonPath;
            if (!ProjectPathResolver.TryResolvePath(path, outputPathRoot, out string resolved, out string error))
                throw new InvalidDataException(error);
            return resolved;
        }
        private UavObservationDocument Load()
        {
            if (player == null) throw new InvalidDataException("Assign the SRT trajectory player.");
            if (raysPerUpdate < 1) throw new InvalidDataException("Rays Per Update must be positive.");
            return UavObservationDocument.Read(Input(observationsJsonPath), TaskName, Geometry);
        }
        [ContextMenu("Validate Inputs")]
        public void ValidateInputs()
        {
            if (IsRunning) return;
            try
            {
                UavObservationDocument doc = Load();
                Prepare(doc);
                foreach (UavObservationSource source in doc.Sources)
                    foreach (JObject observation in source.Observations)
                    {
                        UavBinaryMask mask = UavBinaryMask.Decode(observation, source.Width, source.Height);
                        if (Geometry == "Point") mask.PointSample(observation);
                    }
                if (string.Equals(doc.FilePath, Output(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Output cannot overwrite input JSON.");
                Status = $"Validated {doc.Sources.Count} source(s).";
                Debug.Log($"[{GetType().Name}] {Status}", this);
            }
            catch (Exception ex) { Error(ex); }
        }
        [ContextMenu("Project And Export")]
        public void BeginProjection()
        {
            if (IsRunning) return;
            DebugView.Clear();
            DebugView.Limit = showDebug ? Mathf.Max(0, maximumDebugSamples) : 0;
            Status = "Projecting…";
            job = Run();
            if (Application.isPlaying) coroutine = StartCoroutine(PlayJob());
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.update += EditorTick;
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += CancelProjection;
            }
#endif
        }
        private IEnumerator Run()
        {
            UavObservationDocument doc = Load();
            Prepare(doc);
            string output = Output();
            if (string.Equals(doc.FilePath, output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Output cannot overwrite input JSON.");
            Physics.SyncTransforms();
            using (var session = new UavProjectionSession(player, DebugView))
            {
                IEnumerator projection = Project(doc, session);
                try { while (projection.MoveNext()) yield return projection.Current; }
                finally { (projection as IDisposable)?.Dispose(); }
                // Cancellation or a fatal input error never overwrites the previous export.
                JObject document = session.Document(doc.FilePath, TaskName);
                document["projection"]["transform_json"] = player.ProjectionTransformPath;
                AddProvenance(document);
                UavProjectionSession.WriteAtomically(output, document);
                LastExportPath=output;
                Status = $"{session.Features.Count} feature(s), {session.Skipped.Count} skipped; " +
                    $"{session.SuccessfulRays}/{session.AttemptedRays} rays hit. Saved: {output}";
                if (logSummary) Debug.Log($"[{GetType().Name}] {Status}", this);
            }
        }
        protected virtual void AddProvenance(JObject document) { }
        private IEnumerator PlayJob()
        {
            while (job != null)
            {
                Tick();
                yield return null;
            }
            coroutine = null;
        }
        private void Tick()
        {
            try
            {
                if (!job.MoveNext()) StopJob();
            }
            catch (Exception ex) { StopJob(); Error(ex); }
        }
        private void Error(Exception ex)
        {
            Status = ex.Message;
            Debug.LogError($"[{GetType().Name}] {ex.Message}", this);
        }
        [ContextMenu("Cancel Projection")]
        public void CancelProjection()
        {
            if (job == null) return;
            StopJob();
            if (coroutine != null) { StopCoroutine(coroutine); coroutine = null; }
            Status = "Cancelled; previous export preserved.";
        }
        private void StopJob()
        {
            (job as IDisposable)?.Dispose();
            job = null;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= CancelProjection;
            UnityEditor.SceneView.RepaintAll();
#endif
        }
#if UNITY_EDITOR
        private void EditorTick()
        {
            // Do not carry an edit-mode operation across a Play Mode transition.
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) { CancelProjection(); return; }
            Tick();
            UnityEditor.SceneView.RepaintAll();
        }
#endif
        [ContextMenu("Clear Debug")]
        public void ClearDebug() { DebugView.Clear(); }
        protected virtual void OnDisable() { CancelProjection(); }
        protected virtual void OnDrawGizmosSelected()
        { if (showDebug) DebugView.Draw(showRays, showLabels, markerSize); }
    }
}
