using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(PointDuplicateMerger))]
public sealed class PointDuplicateMergerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var merger=(PointDuplicateMerger)target;
        serializedObject.Update();
        EditorGUILayout.HelpBox("The grid stores multiple objects per cell. Merge only compatible cross-view instances; ambiguous observations remain separate. Defaults need validation on your footage.",MessageType.Info);
        DroneInspectorSections.Draw(serializedObject,"Input",true,"projectedPointsPath","inputPathRoot");
        DroneInspectorSections.Draw(serializedObject,"Matching",true,"matching");
        DroneInspectorSections.Draw(serializedObject,"Output",true,"objectsPath","reportPath","outputPathRoot");
        DroneInspectorSections.Draw(serializedObject,"Debug",false,"preview","showGrid","showMemberLinks","showRejectedLinks","showLabels","maximumDebugSamples","markerRadiusMetres");
        serializedObject.ApplyModifiedProperties();
        var projector=merger.GetComponent<PointProjector>();
        using(new EditorGUI.DisabledScope(projector==null || string.IsNullOrWhiteSpace(projector.LastExportPath)))
            if(GUILayout.Button("Use Last PointProjector Export"))
            {
                Undo.RecordObject(merger,"Set projection input");merger.SetInput(projector.LastExportPath);EditorUtility.SetDirty(merger);
            }
        using(new EditorGUILayout.HorizontalScope())
        {
            if(GUILayout.Button("Merge & Export")) merger.MergeAndExport();
            if(GUILayout.Button("Clear Debug")) { merger.ClearDebug();SceneView.RepaintAll(); }
        }
        EditorGUILayout.HelpBox(merger.Status,MessageType.None);
    }
}
