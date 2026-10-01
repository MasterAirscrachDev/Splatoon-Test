using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

// SurfaceInkManager inspector buttons to preview texel density as a grid on the surface.
[CustomEditor(typeof(SurfaceInkManager))]
public class InkDensityCalibratorEditor : Editor
{
    static readonly Dictionary<int, Material> origMaterials = new Dictionary<int, Material>();

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.Space(6);
        if (GUILayout.Button("Bake Calibration Grid"))  Bake((SurfaceInkManager)target);
        if (GUILayout.Button("Clear Calibration Grid")) Clear((SurfaceInkManager)target);
    }

    static void Bake(SurfaceInkManager manager)
    {
        Renderer rend = manager.GetComponent<Renderer>();
        if (rend == null) { Debug.LogError($"[InkCalibrator] {manager.name}: no Renderer"); return; }

        int cellSize = Mathf.Max(1, Mathf.RoundToInt(manager.PixelsPerUnit / 8f));
        const int size = 2048;

        Color[] pixels = new Color[size * size];
        for (int py = 0; py < size; py++)
        for (int px = 0; px < size; px++)
            pixels[py * size + px] = (px % cellSize == 0 || py % cellSize == 0) ? Color.black : Color.white;

        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.SetPixels(pixels);
        tex.Apply();

        int rid = rend.GetInstanceID();
        if (!origMaterials.ContainsKey(rid))
            origMaterials[rid] = rend.sharedMaterial;

        Material calMat = new Material(rend.sharedMaterial) { name = "CalibrationPreview", mainTexture = tex };
        rend.sharedMaterial = calMat;

        SceneView.RepaintAll();
        Debug.Log($"[InkCalibrator] {manager.name}: grid cell = {cellSize}px in a {size}×{size} texture");
    }

    static void Clear(SurfaceInkManager manager)
    {
        Renderer rend = manager.GetComponent<Renderer>();
        if (rend == null) return;

        int rid = rend.GetInstanceID();
        if (origMaterials.TryGetValue(rid, out Material orig))
        {
            rend.sharedMaterial = orig;
            origMaterials.Remove(rid);
        }
        else if (rend.sharedMaterial != null && rend.sharedMaterial.name == "CalibrationPreview")
        {
            rend.sharedMaterial.mainTexture = null;
        }

        SceneView.RepaintAll();
        Debug.Log($"[InkCalibrator] {manager.name}: cleared");
    }
}
