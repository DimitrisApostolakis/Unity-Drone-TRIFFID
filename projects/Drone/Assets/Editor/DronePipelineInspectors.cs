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
        EditorGUILayout.LabelField("Input Path Loading", EditorStyles.boldLabel);
        SerializedProperty useShared = serializedObject.FindProperty("useSharedInputPathsJson");
        EditorGUILayout.PropertyField(useShared, new GUIContent("Use Shared Path JSON"));
        EditorGUI.indentLevel++;
        if (useShared.boolValue)
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("sharedInputPathsJsonFile"), new GUIContent("Shared Config File"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("srtPathJsonKey"), new GUIContent("SRT Path JSON Key"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("transformPathJsonKey"), new GUIContent("Transform Path JSON Key"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("inputPathRoot"), new GUIContent("Input Path Root"));
            EditorGUILayout.HelpBox(
                "The shared config is read from StreamingAssets; both path values use Input Path Root.",
                MessageType.None);
        }
        else
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("srtFilePath"), new GUIContent("Custom SRT Path"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("transformConfigFilePath"), new GUIContent("Custom Transform Path"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("inputPathRoot"), new GUIContent("Input Path Root"));
        }
        EditorGUI.indentLevel--;

        DrawPropertiesExcluding(
            serializedObject,
            "m_Script",
            "srtFilePath",
            "transformConfigFilePath",
            "inputPathRoot",
            "useSharedInputPathsJson",
            "sharedInputPathsJsonFile",
            "srtPathJsonKey",
            "transformPathJsonKey");
        serializedObject.ApplyModifiedProperties();

        if (targets.Length == 1)
        {
            var player = (SrtDroneRaycastPlayer)target;
            DronePipelineInspectorGui.DrawActionButtons(
                ("Validate Setup", player.ValidateSetup),
                ("Reload Inputs", player.ReloadInputs));
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
