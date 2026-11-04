using Triffid.Uav;
using UnityEditor;
using UnityEngine;

internal static class DroneInspectorSections
{
    public static void Draw(SerializedObject obj, string title, bool initial, params string[] fields)
    {
        string key = "TRIFFID." + obj.targetObject.GetType().Name + "." + title;
        bool open = SessionState.GetBool(key, initial);
        open = EditorGUILayout.Foldout(open, title, true, EditorStyles.foldoutHeader);
        SessionState.SetBool(key, open);
        if (open)
            using (new EditorGUI.IndentLevelScope())
                foreach (string field in fields)
                {
                    SerializedProperty property = obj.FindProperty(field);
                    if (property != null) EditorGUILayout.PropertyField(property, true);
                }
        EditorGUILayout.Space(5);
    }
}

[CustomEditor(typeof(UavProjector), true)]
public sealed class UavProjectorInspector : Editor
{
    public override bool RequiresConstantRepaint() => ((UavProjector)target).IsRunning;
    public override void OnInspectorGUI()
    {
        var projector = (UavProjector)target;
        bool polygons = projector is PolygonProjector;
        serializedObject.Update();
        EditorGUILayout.HelpBox(polygons
            ? "Photo + matching capture SRT + polygon_segmentations.json → geographic polygons."
            : "point_detections.json + the trajectory SRT → geographic point observations.", MessageType.Info);
        using (new EditorGUI.DisabledScope(projector.IsRunning))
        {
            DroneInspectorSections.Draw(serializedObject, "Inputs", true, "player", "observationsJsonPath", "inputPathRoot");
            if (polygons)
            {
                DroneInspectorSections.Draw(serializedObject, "Photo Capture", true,
                    "photoPath", "photoSrtPath", "captureFrameCount");
                DroneInspectorSections.Draw(serializedObject, "Polygon Sampling", false,
                    "maximumSampleSpacingPixels", "maximumVerticesPerObservation");
            }
            DroneInspectorSections.Draw(serializedObject, "Output", true, "outputGeoJsonPath", "outputPathRoot");
            DroneInspectorSections.Draw(serializedObject, "Debug", false,
                "showDebug", "showRays", "showLabels", "maximumDebugSamples", "markerSize", "logSummary");
            DroneInspectorSections.Draw(serializedObject, "Performance", false, "raysPerUpdate");
            serializedObject.ApplyModifiedProperties();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Validate Inputs")) projector.ValidateInputs();
                if (GUILayout.Button("Project & Export")) projector.BeginProjection();
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!projector.IsRunning))
                if (GUILayout.Button("Cancel")) projector.CancelProjection();
            if (GUILayout.Button("Clear Debug")) { projector.ClearDebug(); SceneView.RepaintAll(); }
        }
        EditorGUILayout.HelpBox(projector.Status, MessageType.None);
        EditorGUILayout.LabelField("Select this component's object and enable Scene Gizmos for debugging.", EditorStyles.wordWrappedMiniLabel);
    }
}
