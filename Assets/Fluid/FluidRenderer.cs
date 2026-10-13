using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Screen-space fluid rendering: ink drops drawn as one merged, opaque, glossy liquid. Each frame:
//   1. every drop (FluidRenderer.Drop) is drawn as a sphere impostor into a depth image (nearest
//      eye depth, hidden behind the scene) and, added up, into a thickness + colour image;
//   2. both are smoothed (sized in world space) so nearby drops melt into one surface;
//   3. a full-screen pass shades the ink over the scene. Its shape (normals) comes mostly from the
//      smoothed thickness, read as a height field: a lone drop's thickness gives exactly a sphere's
//      normals, overlapping drops blend like metaballs, and edges curve away properly (depth alone
//      flattens them once smoothed). Lit by the sun and sky, with a highlight and a bright rim sheen.
// Runs after the opaque scene (before transparents), for the camera it's on and, if showInSceneView,
// the editor's Scene view too. Its look is InkFluidSettings' (one for every camera). InkFluid puts one
// on each main camera. Built-in render pipeline. Drops are submitted fresh every frame
// (immediate mode): Drop()/Streak() during the frame; every camera that renders it draws them.
[RequireComponent(typeof(Camera))]
public class FluidRenderer : MonoBehaviour
{
    public struct Particle { public Vector4 posRadius; public Vector4 colour; } // 32 bytes, as in the shaders

    // How ink is drawn: shared by every FluidRenderer (the game's cameras and the lab's), from
    // InkFluidSettings.renderer.
    [System.Serializable]
    public class Look
    {
        [Header("Merging")]
        [Tooltip("World-space blur radius (metres): how far apart drops can be and still melt together")]
        public float smoothRadius = 0.16f;
        [Tooltip("How sharply the blur stops at depth jumps (per metre²): higher keeps separate blobs apart")]
        public float depthFalloff = 6f;
        [Tooltip("Ink only melts together within this depth of each other (metres): drops further behind stay separate shapes, with their own colour")]
        public float blendDepth = 0.35f;
        [Range(1, 4)] public int smoothIterations = 2;
        [Tooltip("Gaps narrower than this share of the blur radius are filled (first pass only), so near drops join")]
        [Range(0f, 1f)] public float bridging = 0.5f;
        [Range(4, 40)] public int maxBlurPixels = 24;
        [Range(0.5f, 1f)] public float resolutionScale = 1f;
        [Tooltip("Drops are drawn at least this many pixels across their radius (tinier ones break up into crosses)")]
        [Range(0f, 6f)] public float minPixels = 2.5f;

        [Header("Shape")]
        [Tooltip("Share of the normal taken from the smoothed depth (the rest from the thickness): 0 is the roundest")]
        [Range(0f, 1f)] public float depthNormals = 0.1f;
        [Tooltip("How round the edges are: the thickness height field's steepness")]
        [Range(0.25f, 3f)] public float roundness = 1f;
        [Tooltip("How crisp the outline is (per metre of thickness)")]
        public float edgeSharpness = 400f;

        [Header("Look")]
        [Range(0f, 1f)] public float gloss = 0.8f;
        [Range(0f, 3f)] public float specular = 1.4f;
        [Range(0f, 1f)] public float reflection = 0.3f;
        [Tooltip("The body's smoothness: keep it the ink surface material's (InkSurface _Smoothness), so flying ink matches painted ink")]
        [Range(0f, 1f)] public float bodySmoothness = 0.4f;
        [Tooltip("How much the body is shaded by its shape (1: fully, light and dark sides; 0: lit flat, as the painted ink is). Low keeps just a hint of contour")]
        [Range(0f, 1f)] public float shading = 1f;
        [Tooltip("Blending strength (metres of ink): where drops overlap, thicknesses below about this add up and merge into one body; above it the thicker wins (so drops stacked along the view don't peak into a point). Higher merges more")]
        [Range(0.01f, 1f)] public float blendStrength = 0.15f;
        [Range(0f, 2f)] public float ambient = 1f;          // x the scene's ambient probe
        [Range(0.5f, 2f)] public float brightness = 1f;     // x the ink colour
        [Tooltip("The bright sheen round each blob's edge")]
        [Range(0f, 2f)] public float rimStrength = 0.9f;
        [Range(0.5f, 8f)] public float rimPower = 1.5f;
        [Tooltip("How far in from the outline the sheen reaches (metres of ink thickness)")]
        public float rimThickness = 0.05f;
        [Tooltip("How far the rim's colour goes towards white")]
        [Range(0f, 1f)] public float rimWhiten = 0.55f;

        [Header("Ripples")]
        public float rippleFrequency = 5f;   // per metre
        [Range(0f, 1f)] public float rippleStrength = 0f;
        public float rippleFlow = 1.2f;
    }

    static Look L => InkFluidSettings.Instance.renderer;

    [Tooltip("Debug: light the ink as if it faced straight up (compare with painted floor)")]
    public bool debugFlatUp;

    [Header("Cameras")]
    [SerializeField] bool showInSceneView = true;

    public static readonly List<FluidRenderer> Active = new List<FluidRenderer>();

    static readonly List<Particle> drops = new List<Particle>();
    static int dropsFrame = -1;
    public static int DropCount => dropsFrame == Time.frameCount ? drops.Count : 0; // this frame's

    readonly Dictionary<Camera, CommandBuffer> buffers = new Dictionary<Camera, CommandBuffer>();
    Camera cam;
    ComputeBuffer buffer;
    int uploadedFrame = -1, uploadedCount;
    Material particles, smooth, composite;
    Mesh quad, screenQuad; // the drops' quad; a 0-1 square (with UVs) covering the screen

    public int Drawn => uploadedCount; // tests: drops in the last frame drawn

    static readonly int ParticlesId = Shader.PropertyToID("_Particles");
    static readonly int DepthId = Shader.PropertyToID("_FluidDepth");
    static readonly int DepthTmpId = Shader.PropertyToID("_FluidDepthTmp");
    static readonly int RawDepthId = Shader.PropertyToID("_FluidRawDepth"); // the depth before smoothing: crisp, for telling layers apart
    static readonly int ThickId = Shader.PropertyToID("_FluidThick");
    static readonly int ThickTmpId = Shader.PropertyToID("_FluidThickTmp");
    static readonly int SceneId = Shader.PropertyToID("_FluidScene");
    static readonly int ZId = Shader.PropertyToID("_FluidZ");

    // One drop for this frame: a sphere at `position`, `radius` metres, in `colour`.
    public static void Drop(Vector3 position, float radius, Color colour)
    {
        if (dropsFrame != Time.frameCount) { drops.Clear(); dropsFrame = Time.frameCount; } // last frame's are done
        drops.Add(new Particle { posRadius = new Vector4(position.x, position.y, position.z, radius), colour = colour });
    }

    // A moving drop: a teardrop along its velocity (a round head, tapering behind), as a short line
    // of overlapping spheres that the smoothing melts into one shape.
    public static void Streak(Vector3 position, Vector3 velocity, float radius, Color colour, float stretch = 0.25f, float taper = 0.4f)
    {
        float speed = velocity.magnitude;
        float length = radius * speed * stretch;
        if (length < radius * 0.25f || speed < 1e-3f) { Drop(position, radius, colour); return; }
        Vector3 back = -velocity / speed;
        int n = Mathf.Clamp(Mathf.CeilToInt(length / (radius * 0.3f)), 2, 16); // close enough that the steps between them smooth out
        for (int i = 0; i < n; i++)
        {
            float t = i / (n - 1f);
            Drop(position + back * (length * t), radius * Mathf.Lerp(1f, taper, t * t), colour); // full near the head, thinning towards the tail
        }
    }

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        InkFluidSettings settings = InkFluidSettings.Instance; // (its shaders: referenced, so they're in builds)
        particles = new Material(settings.particleShader != null ? settings.particleShader : Shader.Find("Fluid/Particles")) { hideFlags = HideFlags.HideAndDontSave };
        smooth = new Material(settings.smoothShader != null ? settings.smoothShader : Shader.Find("Fluid/Smooth")) { hideFlags = HideFlags.HideAndDontSave };
        composite = new Material(settings.compositeShader != null ? settings.compositeShader : Shader.Find("Fluid/Composite")) { hideFlags = HideFlags.HideAndDontSave };
        quad = MakeQuad();
        screenQuad = new Mesh
        {
            vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(1f, 1f, 0f), new Vector3(0f, 1f, 0f) },
            uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) },
            triangles = new[] { 0, 2, 1, 0, 3, 2 },
            hideFlags = HideFlags.HideAndDontSave,
        };
        Camera.onPreRender += BeforeRender;
        Active.Add(this);
    }

    void OnDisable()
    {
        Active.Remove(this);
        Camera.onPreRender -= BeforeRender;
        foreach (var kv in buffers)
        {
            if (kv.Key != null) kv.Key.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, kv.Value);
            kv.Value.Release();
        }
        buffers.Clear();
        buffer?.Release();
        buffer = null;
        DestroyImmediate(particles);
        DestroyImmediate(smooth);
        DestroyImmediate(composite);
        DestroyImmediate(quad);
        DestroyImmediate(screenQuad);
    }

    // Any camera about to render: ours, or (in the editor) a Scene view. The frame's drops go to the
    // GPU once, then each camera records its own passes.
    void BeforeRender(Camera c)
    {
        bool ours = c == cam || showInSceneView && c.cameraType == CameraType.SceneView;
        if (!ours) return;
        if (!buffers.TryGetValue(c, out CommandBuffer cmd))
        {
            cmd = new CommandBuffer { name = "Fluid" };
            c.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, cmd);
            buffers[c] = cmd;
        }
        c.depthTextureMode |= DepthTextureMode.Depth; // the scene's depth, to hide drops behind it
        bool current = dropsFrame == Time.frameCount;
#if UNITY_EDITOR
        current |= UnityEditor.EditorApplication.isPaused && dropsFrame >= 0; // the last frame's, while the editor's paused
#endif
        int count = current ? drops.Count : 0;
        if (uploadedFrame != dropsFrame || uploadedCount != count)
        {
            EnsureBuffer(count);
            if (count > 0) buffer.SetData(drops);
            uploadedFrame = dropsFrame;
            uploadedCount = count;
        }
        Record(c, cmd, count);
    }

    void EnsureBuffer(int count)
    {
        if (buffer != null && buffer.count >= count) return;
        buffer?.Release();
        buffer = new ComputeBuffer(Mathf.NextPowerOfTwo(Mathf.Max(count, 256)), 32);
    }

    void Record(Camera c, CommandBuffer cmd, int count)
    {
        cmd.Clear();
        if (count == 0 || buffer == null) return;
        int w = Mathf.Max(1, Mathf.RoundToInt(c.pixelWidth * L.resolutionScale));
        int h = Mathf.Max(1, Mathf.RoundToInt(c.pixelHeight * L.resolutionScale));
        Matrix4x4 proj = c.projectionMatrix;

        // Shared by every pass.
        cmd.SetGlobalBuffer(ParticlesId, buffer);
        cmd.SetGlobalVector("_FluidProj", new Vector4(proj[0, 0], proj[1, 1], proj[0, 2], proj[1, 2]));
        cmd.SetGlobalVector("_FluidSize", new Vector4(w, h, 1f / w, 1f / h));
        cmd.SetGlobalMatrix("_FluidCamToWorld", c.cameraToWorldMatrix);
        cmd.SetGlobalFloat("_FluidMinPixels", L.minPixels * L.resolutionScale);
        cmd.SetGlobalVector("_FluidLayer", new Vector4(Mathf.Max(0.01f, L.blendDepth), Mathf.Max(0.01f, L.blendStrength), 0f, 0f));

        // 1. Depth (eye depth, the nearest drop's surface), with a depth buffer for the nearest.
        cmd.GetTemporaryRT(DepthId, w, h, 0, FilterMode.Point, RenderTextureFormat.RFloat);
        cmd.GetTemporaryRT(DepthTmpId, w, h, 0, FilterMode.Point, RenderTextureFormat.RFloat);
        cmd.GetTemporaryRT(ZId, w, h, 24, FilterMode.Point, RenderTextureFormat.Depth);
        cmd.SetRenderTarget(new RenderTargetIdentifier(DepthId), new RenderTargetIdentifier(ZId));
        cmd.ClearRenderTarget(true, true, Color.clear);
        cmd.DrawMeshInstancedProcedural(quad, 0, particles, 0, count);

        // ...and thickness + colour, blended (rgb: colour x w, a: w, w = exp(t/k) - 1: see FluidSmooth's
        // ThickOf; full floats, for the exponentials), from
        // the drops near the front surface only (the depth just drawn, as _FluidDepth).
        cmd.GetTemporaryRT(ThickId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBFloat);
        cmd.SetRenderTarget(ThickId);
        cmd.ClearRenderTarget(false, true, Color.clear);
        cmd.DrawMeshInstancedProcedural(quad, 0, particles, 1, count);

        // 2. Smoothed depth (ping-pong, then ironed, ending in DepthId), keeping the raw depth...
        cmd.GetTemporaryRT(RawDepthId, w, h, 0, FilterMode.Point, RenderTextureFormat.RFloat);
        cmd.CopyTexture(DepthId, RawDepthId);
        float pixelsPerMetre = proj[1, 1] * h * 0.5f; // at an eye depth of 1m
        for (int i = 0; i < L.smoothIterations; i++)
        {
            cmd.SetGlobalVector("_FluidBlur", new Vector4(L.smoothRadius * pixelsPerMetre, L.depthFalloff, L.maxBlurPixels, i == 0 ? L.bridging : 0f));
            bool even = i % 2 == 0;
            cmd.Blit(even ? DepthId : DepthTmpId, even ? DepthTmpId : DepthId, smooth, 0);
        }
        bool inTmp = L.smoothIterations % 2 == 1;
        cmd.Blit(inTmp ? DepthTmpId : DepthId, inTmp ? DepthId : DepthTmpId, smooth, 2);
        if (!inTmp) cmd.Blit(DepthTmpId, DepthId);
        // ...and thickness and colour, at the same world size (filling the bridged gaps, rounding the edges).
        cmd.SetGlobalVector("_FluidBlur", new Vector4(L.smoothRadius * pixelsPerMetre, L.depthFalloff, L.maxBlurPixels, 0f));
        cmd.GetTemporaryRT(ThickTmpId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBFloat);
        cmd.SetGlobalVector("_FluidStep", new Vector4(1f / w, 0f, 0f, 0f));
        cmd.Blit(ThickId, ThickTmpId, smooth, 1);
        cmd.SetGlobalVector("_FluidStep", new Vector4(0f, 1f / h, 0f, 0f));
        cmd.Blit(ThickTmpId, ThickId, smooth, 1);

        // 3. Shaded over the scene.
        cmd.GetTemporaryRT(SceneId, c.pixelWidth, c.pixelHeight, 0, FilterMode.Bilinear, c.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
        cmd.Blit(BuiltinRenderTextureType.CameraTarget, SceneId);
        SetLook();
        cmd.Blit(SceneId, BuiltinRenderTextureType.CameraTarget, composite, 0);
        // ...and its depth into the camera's, so transparent things behind it stay behind it.
        cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -1f, 1f));
        cmd.DrawMesh(screenQuad, Matrix4x4.identity, composite, 0, 1);
        cmd.SetViewProjectionMatrices(c.worldToCameraMatrix, c.projectionMatrix);

        cmd.ReleaseTemporaryRT(DepthId);
        cmd.ReleaseTemporaryRT(DepthTmpId);
        cmd.ReleaseTemporaryRT(RawDepthId);
        cmd.ReleaseTemporaryRT(ZId);
        cmd.ReleaseTemporaryRT(ThickId);
        cmd.ReleaseTemporaryRT(ThickTmpId);
        cmd.ReleaseTemporaryRT(SceneId);
    }

    // The sun, sky and the ink's look (a full-screen pass gets none of the forward lighting).
    void SetLook()
    {
        Light sun = RenderSettings.sun;
        if (sun == null)
            foreach (Light l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.isActiveAndEnabled) { sun = l; break; }
        Vector3 toSun = sun != null ? -sun.transform.forward : Vector3.up;
        Color sunColour = sun != null ? sun.color * sun.intensity : Color.white;
        // The L.ambient probe as Unity's shader constants (unity_SHAr..SHC), for ShadeSH9 by hand.
        SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
        string[] names = { "_SHAr", "_SHAg", "_SHAb" };
        string[] quadratic = { "_SHBr", "_SHBg", "_SHBb" };
        for (int i = 0; i < 3; i++)
        {
            composite.SetVector(names[i], new Vector4(sh[i, 3], sh[i, 1], sh[i, 2], sh[i, 0] - sh[i, 6]));
            composite.SetVector(quadratic[i], new Vector4(sh[i, 4], sh[i, 5], sh[i, 6] * 3f, sh[i, 7]));
        }
        composite.SetVector("_SHC", new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f));
        composite.SetVector("_SunDir", toSun);
        composite.SetColor("_SunColour", sunColour);
        composite.SetVector("_Body", new Vector4(L.bodySmoothness, L.shading, 0f, 0f));
        Texture env = ReflectionProbe.defaultTexture;
        if (env != null) composite.SetTexture("_Env", env);
        composite.SetVector("_EnvHDR", ReflectionProbe.defaultTextureHDRDecodeValues);
        composite.SetVector("_Shape", new Vector4(L.depthNormals, L.roundness, L.edgeSharpness, debugFlatUp ? 1f : 0f));
        composite.SetVector("_Look", new Vector4(L.gloss, L.specular, L.reflection, L.ambient));
        composite.SetVector("_Look2", new Vector4(L.brightness, L.rimStrength, L.rimPower, L.rimWhiten));
        composite.SetVector("_Noise", new Vector4(L.rippleFrequency, L.rippleStrength, L.rippleFlow, 0f));
        composite.SetVector("_Rim", new Vector4(L.rimThickness, 0f, 0f, 0f));
    }

    static Mesh MakeQuad()
    {
        var m = new Mesh { name = "FluidQuad", hideFlags = HideFlags.HideAndDontSave };
        m.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
        m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
        return m;
    }
}
