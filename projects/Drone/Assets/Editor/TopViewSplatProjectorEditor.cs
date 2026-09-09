using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(TopViewSplatProjector))]
public sealed class TopViewSplatProjectorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var projector = (TopViewSplatProjector)target;
        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Calculated Height", EditorStyles.boldLabel);

        if (GUILayout.Button("Refresh Height Info"))
        {
            if (!projector.TryRefreshHeightInfo(out string error))
                Debug.LogError($"[TopViewSplatProjector] {error}", projector);
            Repaint();
        }

        if (!projector.HasCurrentHeightInfo)
        {
            EditorGUILayout.HelpBox(
                "Press Refresh Height Info after changing the reference frame, SRT, transform " +
                "JSON, map alignment, or collider.",
                MessageType.Info);
            return;
        }

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.FloatField(
                "SRT Camera Above Hit (m)", projector.ReferenceHeightInfoMeters);
            EditorGUILayout.FloatField(
                "Height Offset (m)", projector.ConfiguredHeightOffsetMeters);
            EditorGUILayout.FloatField(
                "Final Top-View Height (m)", projector.FinalHeightInfoMeters);
        }

        EditorGUILayout.HelpBox(
            "Final height = SRT camera above centre hit + Height Offset Meters",
            MessageType.None);
    }
}
