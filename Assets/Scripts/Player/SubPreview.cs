using System.Collections.Generic;
using UnityEngine;

// What a sub held ready will do, shown to its thrower while the button's down (PlayerLoadout): a
// dotted path for a throw (a sprinkler's arc, a curling bomb's slide) with a disc where it ends,
// or a see-through copy of a placed sub (a beacon) where it would stand. Pale, ringed in the team
// colour (Resources/SubPreview: reads on our own ink too). World space; local to the thrower.
public class SubPreview : MonoBehaviour
{
    const float DotSize = 0.2f, DotSpacing = 0.45f, EndSize = 0.7f;
    static readonly int ColourId = Shader.PropertyToID("_Color");
    static readonly int AlphaId = Shader.PropertyToID("_Alpha");
    const float PathAlpha = 0.95f, GhostAlpha = 0.7f;

    Material material;
    MaterialPropertyBlock block;
    readonly List<Renderer> dots = new List<Renderer>();
    Renderer end;
    Transform ghost;
    Object ghostOf;
    int dotsShown;

    public bool PathShown => dotsShown > 0;
    public bool GhostShown => ghost != null && ghost.gameObject.activeSelf;
    public Vector3 PathEnd { get; private set; }      // tests
    public Vector3 GhostPosition => ghost != null ? ghost.position : Vector3.zero;

    public static SubPreview Create()
    {
        var go = new GameObject("SubPreview");
        var preview = go.AddComponent<SubPreview>();
        Material material = Resources.Load<Material>("SubPreview");
        preview.material = material != null ? material : new Material(Shader.Find("Sprites/Default"));
        preview.block = new MaterialPropertyBlock();
        return preview;
    }

    // Dots every DotSpacing along the path, and a flat disc at its end.
    public void ShowPath(List<Vector3> path, Color colour)
    {
        if (ghost != null) ghost.gameObject.SetActive(false);
        Tint(colour, PathAlpha);
        int n = 0;
        float carried = DotSpacing * 0.5f;
        for (int i = 1; i < path.Count; i++)
        {
            Vector3 a = path[i - 1], b = path[i];
            float length = Vector3.Distance(a, b);
            while (carried <= length)
            {
                Renderer dot = Dot(n++);
                dot.transform.position = Vector3.Lerp(a, b, length > 0f ? carried / length : 0f);
                carried += DotSpacing;
            }
            carried -= length;
        }
        for (int i = n; i < dotsShown; i++) dots[i].gameObject.SetActive(false);
        dotsShown = n;
        PathEnd = path.Count > 0 ? path[path.Count - 1] : transform.position;
        if (end == null) end = Make("End", PrimitiveType.Cylinder, new Vector3(EndSize, 0.01f, EndSize));
        end.gameObject.SetActive(path.Count > 1);
        end.transform.position = PathEnd + Vector3.up * 0.03f;
        end.SetPropertyBlock(block);
    }

    // A copy of the sub's shape (its meshes) where it would stand.
    public void ShowGhost(Component prefab, Vector3 at, Quaternion rotation, Color colour)
    {
        HidePath();
        Tint(colour, GhostAlpha);
        if (ghost == null || ghostOf != prefab) BuildGhost(prefab);
        ghost.gameObject.SetActive(true);
        ghost.SetPositionAndRotation(at, rotation);
        foreach (Renderer r in ghost.GetComponentsInChildren<Renderer>()) r.SetPropertyBlock(block);
    }

    public void Hide()
    {
        HidePath();
        if (ghost != null) ghost.gameObject.SetActive(false);
    }

    void HidePath()
    {
        for (int i = 0; i < dotsShown; i++) dots[i].gameObject.SetActive(false);
        dotsShown = 0;
        if (end != null) end.gameObject.SetActive(false);
    }

    void Tint(Color colour, float alpha)
    {
        block.SetColor(ColourId, colour);
        block.SetFloat(AlphaId, alpha);
    }

    Renderer Dot(int i)
    {
        while (dots.Count <= i) dots.Add(Make("Dot", PrimitiveType.Sphere, Vector3.one * DotSize));
        Renderer dot = dots[i];
        if (!dot.gameObject.activeSelf) dot.gameObject.SetActive(true);
        dot.SetPropertyBlock(block);
        return dot;
    }

    Renderer Make(string name, PrimitiveType shape, Vector3 scale)
    {
        GameObject go = GameObject.CreatePrimitive(shape);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        go.transform.localScale = scale;
        Renderer r = go.GetComponent<Renderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return r;
    }

    void BuildGhost(Component prefab)
    {
        if (ghost != null) Destroy(ghost.gameObject);
        ghost = new GameObject("Ghost").transform;
        ghost.SetParent(transform, false);
        ghostOf = prefab;
        Transform root = prefab.transform;
        foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null || !mf.TryGetComponent(out MeshRenderer source) || !source.enabled) continue;
            var part = new GameObject(mf.name);
            part.transform.SetParent(ghost, false);
            Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix; // where it sits in the prefab
            part.transform.localPosition = m.GetColumn(3);
            part.transform.localRotation = m.rotation;
            part.transform.localScale = m.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var r = part.AddComponent<MeshRenderer>();
            Material[] mats = new Material[mf.sharedMesh.subMeshCount];
            for (int i = 0; i < mats.Length; i++) mats[i] = material;
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }
}
