using UnityEditor;
using UnityEngine;

internal static class DronePipelineInspectorGui
{
    public static void DrawSinglePathLoading(
        SerializedProperty customPath,
        SerializedProperty inputRoot,
        SerializedProperty useSharedJson,
        SerializedProperty sharedJsonFile,
        SerializedProperty jsonKey,
        string customPathLabel)
    {
        EditorGUILayout.PropertyField(useSharedJson, new GUIContent("Use Shared Path JSON"));
        EditorGUI.indentLevel++;
        if (useSharedJson.boolValue)
        {
            EditorGUILayout.PropertyField(sharedJsonFile, new GUIContent("Shared Config File"));
            EditorGUILayout.PropertyField(jsonKey, new GUIContent("Path JSON Key"));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent("Input Path Root"));
            EditorGUILayout.HelpBox(
                "The shared config is read from StreamingAssets; its path value uses Input Path Root.",
                MessageType.None);
        }
        else
        {
            EditorGUILayout.PropertyField(customPath, new GUIContent(customPathLabel));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent("Input Path Root"));
        }
        EditorGUI.indentLevel--;
    }

    public static void DrawActionButtons(params (string Label, System.Action Action)[] actions)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            for (int i = 0; i < actions.Length; i++)
            {
                if (GUILayout.Button(actions[i].Label))
                    actions[i].Action();
            }
        }
    }
}

[CustomEditor(typeof(SrtDroneRaycastPlayer))]
[CanEditMultipleObjects]
public sealed class SrtDroneRaycastPlayerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.HelpBox("Trajectory simulation and shared camera/map calibration. PointProjector and PolygonProjector handle perception results.", MessageType.Info);
        bool shared = serializedObject.FindProperty("useSharedInputPathsJson").boolValue;
        DroneInspectorSections.Draw(serializedObject, "Input Paths", true,
            shared ? new[] { "useSharedInputPathsJson", "sharedInputPathsJsonFile", "srtPathJsonKey", "transformPathJsonKey", "inputPathRoot" }
                : new[] { "useSharedInputPathsJson", "srtFilePath", "transformConfigFilePath", "inputPathRoot" });
        DroneInspectorSections.Draw(serializedObject, "Scene & Raycast Target", true,
            "mapRoot", "droneViewCamera", "targetCollider", "raycastTargetOnly", "raycastMask", "raycastDistance");
        DroneInspectorSections.Draw(serializedObject, "Trajectory Playback", true,
            "playTrajectory", "playbackSpeed", "loopPlayback", "interpolateFrames", "showInGameView");
        DroneInspectorSections.Draw(serializedObject, "Lens Calibration", false,
            "estimateFovFromFocalLength", "srtFocalLengthIs35mmEquivalent", "sensorHeightMm", "fixedVerticalFovDegrees");
        DroneInspectorSections.Draw(serializedObject, "Pose & Alignment", false,
            "useAbsAltitude", "flipPositionX", "flipPositionY", "flipDirectionX", "flipDirectionY",
            "yawOffsetDegrees", "gimbalBaseRotationEuler", "flipYaw", "flipPitch", "flipRoll");
        DroneInspectorSections.Draw(serializedObject, "Debug", false, "drawDebug");
        serializedObject.ApplyModifiedProperties();
        if (targets.Length == 1)
        {
            var player = (SrtDroneRaycastPlayer)target;
            DronePipelineInspectorGui.DrawActionButtons(
                ("Validate Setup", player.ValidateSetup), ("Reload Inputs", player.ReloadInputs));
        }
    }
}

[CustomEditor(typeof(MeshPathJsonLoader))]
[CanEditMultipleObjects]
public sealed class MeshPathJsonLoaderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.LabelField("Mesh Path Loading", EditorStyles.boldLabel);
        DronePipelineInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("meshPath"),
            serializedObject.FindProperty("inputPathRoot"),
            serializedObject.FindProperty("useSharedMeshPathJson"),
            serializedObject.FindProperty("sharedMeshPathsJsonFile"),
            serializedObject.FindProperty("meshPathJsonKey"),
            "Custom Mesh Path");

        DrawPropertiesExcluding(
            serializedObject,
            "m_Script",
            "meshPath",
            "inputPathRoot",
            "useSharedMeshPathJson",
            "sharedMeshPathsJsonFile",
            "meshPathJsonKey");
        serializedObject.ApplyModifiedProperties();

        if (targets.Length == 1)
        {
            var loader = (MeshPathJsonLoader)target;
            DronePipelineInspectorGui.DrawActionButtons(
                ("Validate Path", loader.ValidatePaths),
                ("Load Mesh", loader.LoadAndApplyMesh));
        }
    }
}

[CustomEditor(typeof(ColmapGaussianRotationAligner))]
[CanEditMultipleObjects]
public sealed class ColmapGaussianRotationAlignerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.LabelField("Transform Path Loading", EditorStyles.boldLabel);
        DronePipelineInspectorGui.DrawSinglePathLoading(
            serializedObject.FindProperty("transformJsonPath"),
            serializedObject.FindProperty("inputPathRoot"),
            serializedObject.FindProperty("useSharedTransformPathJson"),
            serializedObject.FindProperty("sharedTransformPathsJsonFile"),
            serializedObject.FindProperty("transformPathJsonKey"),
            "Custom Transform Path");

        DrawPropertiesExcluding(
            serializedObject,
            "m_Script",
            "transformJsonPath",
            "inputPathRoot",
            "useSharedTransformPathJson",
            "sharedTransformPathsJsonFile",
            "transformPathJsonKey");
        serializedObject.ApplyModifiedProperties();

        if (targets.Length == 1)
        {
            var aligner = (ColmapGaussianRotationAligner)target;
            DronePipelineInspectorGui.DrawActionButtons(
                ("Validate Path", aligner.ValidateTransformJson),
                ("Apply Alignment", aligner.ApplyJsonRotationAndSyncCollider));
        }
    }
}

[CustomEditor(typeof(MapColliderSync))]
[CanEditMultipleObjects]
public sealed class MapColliderSyncEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        if (targets.Length == 1 && GUILayout.Button("Sync Collider Now"))
            ((MapColliderSync)target).SyncToMap();
    }
}

