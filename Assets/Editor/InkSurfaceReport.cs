using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Tools > Ink > Report Surfaces: every inkable surface in the open scene with the ink texture it'll
// get, its texels per metre (sharpness) and how big a splat comes out in the world, against Map1's
// reference. Flags surfaces that would ink coarser than Map1, or whose splat scale differs.
public static class InkSurfaceReport
{
    const float ReferenceTexelsPerMetre = SurfaceInkManager.ReferenceTexelsPerMetre;
    const float ReferenceSplatScale = 2.5f;          // Map1's surfaces

    [MenuItem("Tools/Ink/Report Surfaces")]
    public static void Report() => Debug.Log(Build());

    public static string Build()
    {
        var sb = new StringBuilder("[Ink] Surfaces in " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "\n");
        float refSplat = ReferenceSplatScale / ReferenceTexelsPerMetre;
        foreach (SurfaceInkManager s in Object.FindObjectsByType<SurfaceInkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(s => s.name))
        {
            var so = new SerializedObject(s);
            float ppu = so.FindProperty("pixelsPerUnit").floatValue, scale = so.FindProperty("splatScale").floatValue;
            MeshCollider mc = s.GetComponent<MeshCollider>();
            MeshFilter mf = s.GetComponent<MeshFilter>();
            Mesh mesh = mc != null && mc.sharedMesh != null ? mc.sharedMesh : mf != null ? mf.sharedMesh : null;
            float span = SurfaceInkManager.UVSpanMetres(mesh, s.transform);
            int size = SurfaceInkManager.TextureSizeFor(mesh, s.transform, s.GetComponent<Renderer>(), ppu);
            float tpm = span > 0f ? size / span : 0f;
            float splatMetres = scale / ReferenceTexelsPerMetre;   // world radius per splash-size unit (converted through the surface's resolution)
            bool coarse = tpm < ReferenceTexelsPerMetre * 0.95f, sized = Mathf.Abs(splatMetres - refSplat) > refSplat * 0.05f;
            sb.AppendLine($"  {(coarse || sized ? "!!" : "ok")} {s.name}: {span:F0}m per UV unit -> {size} texture, {tpm:F1} texels/m{(coarse ? " (coarser than Map1)" : "")}, " +
                          $"splats {splatMetres * 100f:F1}cm per size unit{(sized ? $" (splat scale {scale}, Map1 {ReferenceSplatScale})" : "")}");
        }
        return sb.ToString();
    }

    // Tools > Ink > Set Up Inkable Surfaces: every I_* mesh in the open scene that isn't inkable yet
    // gets the ink setup (InkSurface layer, ink material, mesh collider, SurfaceInkManager at Map1's
    // resolution and splat size). Run it after (re)importing a map; surfaces already set up are left as
    // they are. Then check them with Report Surfaces.
    const string InkMaterialPath = "Assets/Materials/InkMaterial 1.mat";

    [MenuItem("Tools/Ink/Set Up Inkable Surfaces")]
    public static void SetUp() => Debug.Log(SetUpScene());

    public static string SetUpScene()
    {
        var sb = new StringBuilder("[Ink] Set up in " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + ":");
        Material inkMaterial = AssetDatabase.LoadAssetAtPath<Material>(InkMaterialPath);
        int layer = LayerMask.NameToLayer("InkSurface");
        int n = 0;
        foreach (MeshFilter mf in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            GameObject go = mf.gameObject;
            if (!go.name.StartsWith("I_") || go.GetComponent<SurfaceInkManager>() != null || mf.sharedMesh == null) continue;
            Undo.RecordObject(go, "Set Up Inkable Surfaces");
            go.layer = layer;
            Renderer rend = go.GetComponent<Renderer>();
            if (rend != null && inkMaterial != null) { Undo.RecordObject(rend, "Set Up Inkable Surfaces"); rend.sharedMaterials = Enumerable.Repeat(inkMaterial, rend.sharedMaterials.Length).ToArray(); }
            MeshCollider mc = go.GetComponent<MeshCollider>();
            if (mc == null) mc = Undo.AddComponent<MeshCollider>(go);
            Undo.RecordObject(mc, "Set Up Inkable Surfaces");
            mc.sharedMesh = mf.sharedMesh;
            var so = new SerializedObject(Undo.AddComponent<SurfaceInkManager>(go));
            so.FindProperty("pixelsPerUnit").floatValue = ReferenceTexelsPerMetre;   // texels per metre of the UV layout
            so.FindProperty("splatScale").floatValue = ReferenceSplatScale;
            so.ApplyModifiedProperties();
            if (!mf.sharedMesh.isReadable) sb.Append($" ({go.name}'s mesh isn't readable: turn on Read/Write in its import settings)");
            sb.Append(" " + go.name);
            n++;
        }
        if (n == 0) sb.Append(" nothing new");
        else UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        return sb.ToString();
    }
}
