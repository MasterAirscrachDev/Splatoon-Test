using UnityEditor;
using UnityEngine;

// GameObject > Splatoon > Target Dummy: drops a TargetDummy on the ground at the Scene view's focus.
public static class TargetDummyMenu
{
    [MenuItem("GameObject/Splatoon/Target Dummy", false, 10)]
    static void Create(MenuCommand command)
    {
        Vector3 pos = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
        if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 200f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
            pos = hit.point;

        GameObject go = new GameObject("Target Dummy", typeof(TargetDummy));
        go.transform.position = pos;
        GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
        Undo.RegisterCreatedObjectUndo(go, "Create Target Dummy");
        Selection.activeObject = go;
    }
}
