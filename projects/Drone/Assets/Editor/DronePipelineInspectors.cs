#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;


// Shared implementation lives in this file; no Unity-AR Editor scripts are required.
public abstract class DroneGroupedInspector : Editor
{
    private sealed class Section
    {
        public string Title;
        public readonly List<string> Paths = new List<string>();
    }

    private string search = string.Empty;

    protected void DrawToolbar()
    {
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
        search = EditorGUILayout.TextField("Find setting", search);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Expand all")) SetSections(true);
            if (GUILayout.Button("Collapse all")) SetSections(false);
        }
        EditorGUILayout.Space();
    }

    protected bool DrawSection(string title)
    {
        string key = SectionKey(title);
        bool open = SessionState.GetBool(key, true);
        bool next = EditorGUILayout.Foldout(open || search.Length > 0, title, true);
        if (search.Length == 0 && next != open) SessionState.SetBool(key, next);
        return next;
    }

    protected void DrawGroupedProperties(params string[] excludedPaths)
    {
        var excluded = new HashSet<string>(excludedPaths);
        foreach (Section section in Sections())
        {
            var visible = new List<SerializedProperty>();
            foreach (string path in section.Paths)
            {
                if (excluded.Contains(path)) continue;
                SerializedProperty property = serializedObject.FindProperty(path);
                if (property != null && (Matches(section.Title) || Matches(property.displayName) ||
                    Matches(property.name) || Matches(property.tooltip)))
                    visible.Add(property);
            }
            if (visible.Count == 0) continue;
            if (DrawSection(section.Title))
                using (new EditorGUI.IndentLevelScope())
                    foreach (SerializedProperty property in visible)
                        EditorGUILayout.PropertyField(property, true);
            EditorGUILayout.Space();
        }
    }

    private bool Matches(string value)
    {
        return search.Length == 0 || (!string.IsNullOrEmpty(value) &&
            value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private List<Section> Sections()
    {
        var sections = new List<Section>();
        Section current = null;
        SerializedProperty iterator = serializedObject.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (iterator.propertyPath == "m_Script") continue;
            FieldInfo field = null;
            for (Type type = target.GetType(); type != null && field == null; type = type.BaseType)
                field = type.GetField(iterator.name, BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var header = field == null ? null :
                Attribute.GetCustomAttribute(field, typeof(HeaderAttribute)) as HeaderAttribute;
            if (current == null || header != null)
            {
                current = new Section { Title = header == null ? "Settings" : header.header };
                sections.Add(current);
            }
            current.Paths.Add(iterator.propertyPath);
        }
        return sections;
    }

    private string SectionKey(string title)
    {
        return "TRIFFID.DroneInspector." + target.GetType().FullName + "." + title;
    }

    private void SetSections(bool expanded)
    {
        foreach (Section section in Sections()) SessionState.SetBool(SectionKey(section.Title), expanded);
        SessionState.SetBool(SectionKey("Path source"), expanded);
    }
}

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
        if (useSharedJson.boolValue || useSharedJson.hasMultipleDifferentValues)
        {
            EditorGUILayout.PropertyField(sharedJsonFile, new GUIContent("Shared Config File"));
            EditorGUILayout.PropertyField(jsonKey, new GUIContent("Path JSON Key"));
            EditorGUILayout.PropertyField(inputRoot, new GUIContent("Input Path Root"));
            EditorGUILayout.HelpBox(
                "The shared config is read from StreamingAssets; its path value uses Input Path Root.",
                MessageType.None);
        }
        if (!useSharedJson.boolValue || useSharedJson.hasMultipleDifferentValues)
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
public sealed class SrtDroneRaycastPlayerEditor : DroneGroupedInspector
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawToolbar();
        if (DrawSection("Path source"))
        {
            SerializedProperty useShared = serializedObject.FindProperty("useSharedInputPathsJson");
            EditorGUILayout.PropertyField(useShared, new GUIContent("Use Shared Path JSON"));
            EditorGUI.indentLevel++;
            if (useShared.boolValue || useShared.hasMultipleDifferentValues)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("sharedInputPathsJsonFile"), new GUIContent("Shared Config File"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("srtPathJsonKey"), new GUIContent("SRT Path JSON Key"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("transformPathJsonKey"), new GUIContent("Transform Path JSON Key"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("inputPathRoot"), new GUIContent("Input Path Root"));
                EditorGUILayout.HelpBox(
                    "The shared config is read from StreamingAssets; both path values use Input Path Root.",
                    MessageType.None);
            }
            if (!useShared.boolValue || useShared.hasMultipleDifferentValues)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("srtFilePath"), new GUIContent("Custom SRT Path"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("transformConfigFilePath"), new GUIContent("Custom Transform Path"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("inputPathRoot"), new GUIContent("Input Path Root"));
            }
            EditorGUI.indentLevel--;
        }

        DrawGroupedProperties(
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
public sealed class MeshPathJsonLoaderEditor : DroneGroupedInspector
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawToolbar();
        if (DrawSection("Path source"))
        {
            DronePipelineInspectorGui.DrawSinglePathLoading(
                serializedObject.FindProperty("meshPath"),
                serializedObject.FindProperty("inputPathRoot"),
                serializedObject.FindProperty("useSharedMeshPathJson"),
                serializedObject.FindProperty("sharedMeshPathsJsonFile"),
                serializedObject.FindProperty("meshPathJsonKey"),
                "Custom Mesh Path");
        }

        DrawGroupedProperties(
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
public sealed class ColmapGaussianRotationAlignerEditor : DroneGroupedInspector
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawToolbar();
        if (DrawSection("Path source"))
        {
            DronePipelineInspectorGui.DrawSinglePathLoading(
                serializedObject.FindProperty("transformJsonPath"),
                serializedObject.FindProperty("inputPathRoot"),
                serializedObject.FindProperty("useSharedTransformPathJson"),
                serializedObject.FindProperty("sharedTransformPathsJsonFile"),
                serializedObject.FindProperty("transformPathJsonKey"),
                "Custom Transform Path");
        }

        DrawGroupedProperties(
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
public sealed class MapColliderSyncEditor : DroneGroupedInspector
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawToolbar();
        DrawGroupedProperties("m_Script");
        serializedObject.ApplyModifiedProperties();
        if (targets.Length == 1 && GUILayout.Button("Sync Collider Now"))
        {
            var sync = (MapColliderSync)target;
            if (sync.MeshColliderObject != null)
                Undo.RecordObject(sync.MeshColliderObject, "Sync Collider");
            sync.SyncToMap();
            if (sync.MeshColliderObject != null)
                PrefabUtility.RecordPrefabInstancePropertyModifications(sync.MeshColliderObject);
        }
    }
}

#endif
