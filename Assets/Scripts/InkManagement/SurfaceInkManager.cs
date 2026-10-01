using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class SurfaceInkManager : MonoBehaviour
{
    [SerializeField] float pixelsPerUnit = 32f;
    [SerializeField] float splatScale = 1.0f; // scale of the splat texture in world units
    // Faces whose normal tilts more than this from world-up are treated as walls and
    // excluded from the top-down score (floors and gentler slopes still count).
    [SerializeField] float maxFloorAngle = 50f;
    // Intensity of the raised ink rim derived from splat coverage. 0 disables the dynamic normal map.
    // Tuned/tested at the default pixelsPerUnit (see ReferencePixelsPerUnit below).
    float inkNormalStrength = 0.5f;
    // The gradient in ComputeNormals is computed per-texel, but a texel covers a different
    // world distance on every surface (1 / pixelsPerUnit). Left uncorrected, the same
    // inkNormalStrength produces a visibly different bump slope on a fine-texel surface than
    // a coarse one. RegenerateNormals compensates by this ratio; at pixelsPerUnit == this
    // reference value the correction is 1 (matches the already-tuned default look).
    const float ReferencePixelsPerUnit = 32f;
    // Surface noise mixed into the ink height so splat tops aren't perfectly flat.
    // Derived from splatScale (see CalculateNoiseScale) so bump grain stays proportional
    // to splat size across surfaces without per-object tuning. Calibrated against
    // splatScale 5 -> 0.025 and splatScale 1.5 -> 0.05; not valid far outside that range.
    float inkNoiseScale;
    public float PixelsPerUnit => pixelsPerUnit;

    // Assigned by NetGameManager at scene load. Identical on every client (all surfaces are scene-placed).
    public int SurfaceId;
    // Subscribers (NetGameManager) send the splat over the network. Set broadcast:false on remote replays.
    public static System.Action<SplatData> OnSplatApplied;

    int size;
    int coveredPixelCount;    // pixels inside the mesh's UV triangles — used for accurate scoring
    int topCoveredPixelCount; // subset of the above that lies on floor/slope faces
    NetGameManager gameManager;
    ComputeShader splatCompute;
    RenderTexture splatMapRenderTexture;
    RenderTexture normalMapRenderTexture; // dynamic ink normal map, regenerated when splats change
    Texture2D topMaskTexture; // R8: 1 where a pixel maps to a floor/slope face, 0 for walls

    // Resolved by name in InitializeSplatCompute, so kernel order in SplatCompute.compute
    // doesn't matter.
    int KernelSplat;
    int KernelGetScores;
    int KernelGetTopScores;
    int KernelComputeNormals;
    int KernelPaintTrailNormal;
    int KernelFillRegion;

    ComputeBuffer scoreBuffer;
    ComputeBuffer topScoreBuffer;

    // CPU copy of InkTexture's team ownership, one byte per texel (0 none, 1 alpha, 2 beta),
    // kept in sync by reading back just each splat's area after it lands (SyncTeamMapRegion).
    // getSurfaceTeam is a plain lookup into this. It replaced a single GPU readback slot
    // per surface that every caller shared (ground check, 12 wall rays, remote players), so
    // each caller got whichever result came back last, for whatever texel someone else asked
    // about. That caused wrong-team reads on surfaces with both floors and walls.
    byte[] teamMap;
    // Bumped by ClearInk so a readback issued before a clear can't land afterwards and
    // restore stale ink.
    int teamMapGeneration;
    Color32 alphaColor32, betaColor32;

    void Start()
    {
        splatCompute = Instantiate(Resources.Load<ComputeShader>("SplatCompute"));
        gameManager = FindFirstObjectByType<NetGameManager>();

        Renderer rend = GetComponent<Renderer>();
        if (rend != null)
        {
            Bounds b = rend.bounds;
            float maxSide = Mathf.Max(b.size.x, b.size.y, b.size.z);
            size = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(maxSide * pixelsPerUnit)), 64, 2048);
        }
        else
        {
            size = 256;
        }

        inkNoiseScale = CalculateNoiseScale(splatScale);
        BuildCoverageMasks();
        InitializeSplatCompute();
        if (rend != null)
        {
            rend.material.mainTexture = splatMapRenderTexture;
            rend.material.SetTexture("_BumpMap", normalMapRenderTexture);
        }
    }

    // Linear fit through (splatScale 5 -> noiseScale 0.025) and (splatScale 1.5 -> noiseScale 0.05),
    // so bump grain size stays visually consistent across surfaces of differing splat scale.
    static float CalculateNoiseScale(float splatScale)
    {
        const float x1 = 5f,   y1 = 0.025f;
        const float x2 = 1.5f, y2 = 0.05f;
        const float slope = (y2 - y1) / (x2 - x1);
        const float intercept = y1 - slope * x1;
        return intercept + slope * splatScale;
    }

    // Computes a clamped texel-space bounding box (and matching thread-group counts) around a
    // point, so compute dispatches touch only the region they affect instead of the whole texture.
    void ComputeBounds(int x, int y, int boundRadius, out int minX, out int minY, out int groupsX, out int groupsY)
    {
        minX = Mathf.Clamp(x - boundRadius, 0, size - 1);
        minY = Mathf.Clamp(y - boundRadius, 0, size - 1);
        int maxX = Mathf.Clamp(x + boundRadius, 0, size - 1);
        int maxY = Mathf.Clamp(y + boundRadius, 0, size - 1);
        groupsX = Mathf.CeilToInt((maxX - minX + 1) / 8f);
        groupsY = Mathf.CeilToInt((maxY - minY + 1) / 8f);
    }

    void SetNormalUniforms()
    {
        // Re-set each pass so inspector tweaks take effect live during play.
        float worldScale = pixelsPerUnit / ReferencePixelsPerUnit;
        splatCompute.SetFloat("NormalStrength", inkNormalStrength * worldScale);
        splatCompute.SetFloat("NoiseStrength", 1); //tested as good
        splatCompute.SetFloat("NoiseScale", inkNoiseScale);
        splatCompute.SetTexture(KernelComputeNormals, "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelComputeNormals, "NormalTexture", normalMapRenderTexture);
    }

    void RegenerateNormalsFull()
    {
        if (inkNormalStrength <= 0f) return;
        SetNormalUniforms();
        splatCompute.SetInts("BoundsMin", new int[2] { 0, 0 });
        splatCompute.SetInt("MaskToSplat", 0); // unconditional full rebuild
        splatCompute.Dispatch(KernelComputeNormals, size / 8, size / 8, 1);
    }

    // Rebuilds normals only within the given box (e.g. right after a Splat call, scoped to
    // that same splat's bounds), and only for texels the splat's own organic/warped shape
    // actually reached (MaskToSplat) — not just texels inside the padded box. Everything
    // else in the box, including any trail indent that happened to sit in a corner the ink
    // never touched, is left exactly as it was.
    void RegenerateNormalsRegion(int minX, int minY, int groupsX, int groupsY)
    {
        if (inkNormalStrength <= 0f) return;
        SetNormalUniforms();
        splatCompute.SetInts("BoundsMin", new int[2] { minX, minY });
        splatCompute.SetInt("MaskToSplat", 1);
        splatCompute.Dispatch(KernelComputeNormals, groupsX, groupsY, 1);
    }

    Mesh GetSharedMesh()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) return mf.sharedMesh;
        SkinnedMeshRenderer smr = GetComponent<SkinnedMeshRenderer>();
        return smr != null ? smr.sharedMesh : null;
    }

    // Rasterises the mesh's UV triangles into boolean masks. `covered` counts every pixel
    // that maps to a surface (pixels outside all triangles can never be painted and must
    // not inflate the neutral score). `topCov` is the subset lying on floor/slope faces —
    // each triangle's world-space normal is classified against maxFloorAngle so the
    // top-down score can exclude walls. The top mask is uploaded to the GPU as topMaskTexture.
    void BuildCoverageMasks()
    {
        Mesh mesh = GetSharedMesh();
        Vector2[] uvs = mesh != null ? mesh.uv : null;

        // No usable UV data: classify the whole surface by its transform up-axis (all or nothing).
        if (mesh == null || uvs == null || uvs.Length == 0)
        {
            coveredPixelCount = size * size;
            bool topFacing = Vector3.Angle(transform.up, Vector3.up) <= maxFloorAngle;
            topCoveredPixelCount = topFacing ? coveredPixelCount : 0;
            CreateTopMask(topFacing ? (byte)255 : (byte)0);
            return;
        }

        int[] tris = mesh.triangles;
        Vector3[] verts = mesh.vertices;
        Vector3[] norms = (mesh.normals != null && mesh.normals.Length == verts.Length) ? mesh.normals : null;

        bool[] covered = new bool[size * size];
        bool[] topCov  = new bool[size * size];

        for (int ti = 0; ti < tris.Length; ti += 3)
        {
            int i0 = tris[ti], i1 = tris[ti + 1], i2 = tris[ti + 2];
            Vector2 a = uvs[i0], b = uvs[i1], c = uvs[i2];

            // World-space face normal — prefer authored vertex normals (respect winding/intent),
            // fall back to the geometric normal from world-space vertex positions.
            Vector3 wn = norms != null
                ? transform.TransformDirection(norms[i0] + norms[i1] + norms[i2]).normalized
                : Vector3.Cross(
                    transform.TransformPoint(verts[i1]) - transform.TransformPoint(verts[i0]),
                    transform.TransformPoint(verts[i2]) - transform.TransformPoint(verts[i0])).normalized;
            bool topFacing = Vector3.Angle(wn, Vector3.up) <= maxFloorAngle;

            int px0 = Mathf.Max(0,      Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), c.x) * size));
            int px1 = Mathf.Min(size-1, Mathf.CeilToInt (Mathf.Max(Mathf.Max(a.x, b.x), c.x) * size));
            int py0 = Mathf.Max(0,      Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.y, b.y), c.y) * size));
            int py1 = Mathf.Min(size-1, Mathf.CeilToInt (Mathf.Max(Mathf.Max(a.y, b.y), c.y) * size));

            for (int py = py0; py <= py1; py++)
            for (int px = px0; px <= px1; px++)
            {
                int idx = py * size + px;
                // Nothing left to learn about this pixel if it's covered and (already top, or this face isn't top).
                if (covered[idx] && (topCov[idx] || !topFacing)) continue;

                Vector2 p = new Vector2((px + 0.5f) / size, (py + 0.5f) / size);
                if (PointInTriangle(p, a, b, c))
                {
                    covered[idx] = true;
                    if (topFacing) topCov[idx] = true;
                }
            }
        }

        int count = 0, topCount = 0;
        byte[] mask = new byte[size * size];
        for (int i = 0; i < covered.Length; i++)
        {
            if (covered[i]) count++;
            if (topCov[i]) { topCount++; mask[i] = 255; }
        }
        coveredPixelCount = count;
        topCoveredPixelCount = topCount;
        CreateTopMask(mask);
    }

    void CreateTopMask(byte fill)
    {
        byte[] data = new byte[size * size];
        if (fill != 0) for (int i = 0; i < data.Length; i++) data[i] = fill;
        CreateTopMask(data);
    }

    void CreateTopMask(byte[] data)
    {
        topMaskTexture = new Texture2D(size, size, TextureFormat.R8, false) { filterMode = FilterMode.Point };
        topMaskTexture.SetPixelData(data, 0);
        topMaskTexture.Apply(false, true); // upload and free the CPU copy
    }

    static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross2D(p - b, a - b);
        float d2 = Cross2D(p - c, b - c);
        float d3 = Cross2D(p - a, c - a);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    static float Cross2D(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    void InitializeSplatCompute()
    {
        KernelSplat            = splatCompute.FindKernel("Splat");
        KernelGetScores        = splatCompute.FindKernel("GetScores");
        KernelGetTopScores     = splatCompute.FindKernel("GetTopScores");
        KernelComputeNormals   = splatCompute.FindKernel("ComputeNormals");
        KernelPaintTrailNormal = splatCompute.FindKernel("PaintTrailNormal");
        KernelFillRegion       = splatCompute.FindKernel("FillRegion");

        teamMap = new byte[size * size];
        alphaColor32 = gameManager.AlphaTeam;
        betaColor32  = gameManager.BetaTeam;

        splatMapRenderTexture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
        splatMapRenderTexture.enableRandomWrite = true;
        splatMapRenderTexture.filterMode = FilterMode.Bilinear;
        splatMapRenderTexture.Create();

        normalMapRenderTexture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
        normalMapRenderTexture.enableRandomWrite = true;
        normalMapRenderTexture.filterMode = FilterMode.Bilinear;
        normalMapRenderTexture.Create();

        scoreBuffer = new ComputeBuffer(3, sizeof(int));
        topScoreBuffer = new ComputeBuffer(3, sizeof(int));

        splatCompute.SetTexture(KernelSplat,          "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetScores,       "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetTopScores,    "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetTopScores,    "TopMask",    topMaskTexture);
        splatCompute.SetTexture(KernelComputeNormals,  "InkTexture",    splatMapRenderTexture);
        splatCompute.SetTexture(KernelComputeNormals,  "NormalTexture", normalMapRenderTexture);
        splatCompute.SetTexture(KernelPaintTrailNormal, "NormalTexture", normalMapRenderTexture);

        splatCompute.SetBuffer(KernelSplat,           "TeamScores",  scoreBuffer);
        splatCompute.SetBuffer(KernelGetScores,        "TeamScores",  scoreBuffer);
        splatCompute.SetBuffer(KernelGetTopScores,     "TeamScores",  topScoreBuffer);

        Vector4 alphaTeam = new Vector4(gameManager.AlphaTeam.r, gameManager.AlphaTeam.g, gameManager.AlphaTeam.b, gameManager.AlphaTeam.a);
        Vector4 betaTeam  = new Vector4(gameManager.BetaTeam.r,  gameManager.BetaTeam.g,  gameManager.BetaTeam.b,  gameManager.BetaTeam.a);
        splatCompute.SetVector("AlphaColor", alphaTeam);
        splatCompute.SetVector("BetaColor",  betaTeam);
        splatCompute.SetVector("NoColor",    new Vector4(0, 0, 0, 0));
        splatCompute.SetInt("Size", size);
        splatCompute.SetInts("PixelCoords",     new int[2] { 0, 0 });
        splatCompute.SetInt("SplashSize", 0);
        splatCompute.SetInt("Team", 1);
        splatCompute.SetFloat("NormalStrength", inkNormalStrength);
        splatCompute.Dispatch(KernelSplat, size / 8, size / 8, 1);
        RegenerateNormalsFull(); // prime the normal map to flat (matches the cleared ink)
    }

    public void Splat(Vector2 texCoords, int splashSize, int team, bool broadcast = true)
    {
        //Debug.Log($"[Splat] surface={SurfaceId} team={team} broadcast={broadcast}\n{new System.Diagnostics.StackTrace(true)}");
        int x = (int)(texCoords.x * size);
        int y = (int)(texCoords.y * size);
        int radius = Mathf.RoundToInt(splashSize * splatScale);

        // Dispatch only a bounding box around the splat instead of the whole texture — the
        // domain warp in GetColor can push the visible edge out by up to r*0.4, plus the
        // feather margin, so pad generously to avoid clipping the blob.
        int boundRadius = Mathf.CeilToInt(radius * 1.4f) + 4;
        ComputeBounds(x, y, boundRadius, out int minX, out int minY, out int groupsX, out int groupsY);

        splatCompute.SetTexture(KernelSplat, "InkTexture", splatMapRenderTexture);
        splatCompute.SetInts("PixelCoords", new int[2] { x, y });
        splatCompute.SetInts("BoundsMin", new int[2] { minX, minY });
        splatCompute.SetInt("SplashSize", radius);
        splatCompute.SetInt("Team", team);
        splatCompute.Dispatch(KernelSplat, groupsX, groupsY, 1);

        // Rebuild normals immediately, scoped to the same box — cheap now both kernels are
        // bounded, and it leaves trail indents anywhere else on the surface untouched.
        RegenerateNormalsRegion(minX, minY, groupsX, groupsY);

        SyncTeamMapRegion(minX, minY, Mathf.Min(groupsX * 8, size - minX), Mathf.Min(groupsY * 8, size - minY));

        if (broadcast)
            OnSplatApplied?.Invoke(new SplatData { surfaceId = SurfaceId, uv = texCoords, splashSize = splashSize, team = team });
    }

    // "Reverse splat" — stamps an indent directly onto the normal map so a swum-through path
    // shows a visible groove. Never touches InkTexture: coverage, colour and scoring are
    // completely unaffected, only the surface's apparent shape at that spot changes. Safe to
    // call every frame from a moving player — blend eases each stamp toward the target depth
    // rather than adding onto the last one, so lingering in one spot converges instead of
    // distorting further.
    public void PaintTrailNormal(Vector2 texCoords, int radius, float depth = 1f, float blend = 0.25f)
    {
        if (inkNormalStrength <= 0f) return; // no normal map to indent
        int x = (int)(texCoords.x * size);
        int y = (int)(texCoords.y * size);
        int texRadius = Mathf.Max(1, Mathf.RoundToInt(radius * splatScale));

        ComputeBounds(x, y, texRadius + 2, out int minX, out int minY, out int groupsX, out int groupsY);

        splatCompute.SetTexture(KernelPaintTrailNormal, "NormalTexture", normalMapRenderTexture);
        splatCompute.SetVector("TrailCenter", new Vector4(x, y, 0f, 0f));
        splatCompute.SetFloat("TrailRadius", texRadius);
        splatCompute.SetFloat("TrailDepth", depth);
        splatCompute.SetFloat("TrailBlend", blend);
        splatCompute.SetInts("BoundsMin", new int[2] { minX, minY });
        splatCompute.Dispatch(KernelPaintTrailNormal, groupsX, groupsY, 1);
    }

    // Paints a UV-space rectangle solidly with one team's ink (team 0 erases it): no splat
    // shape, no feathering, no network broadcast. For setting up exact, known ink states
    // (test scenes, tools); not used in normal gameplay.
    public void FillRegion(Rect uvRect, int team)
    {
        if (splatMapRenderTexture == null) return; // Start hasn't run yet
        int minX = Mathf.Clamp(Mathf.FloorToInt(uvRect.xMin * size), 0, size);
        int minY = Mathf.Clamp(Mathf.FloorToInt(uvRect.yMin * size), 0, size);
        int maxX = Mathf.Clamp(Mathf.CeilToInt(uvRect.xMax * size), 0, size);
        int maxY = Mathf.Clamp(Mathf.CeilToInt(uvRect.yMax * size), 0, size);
        if (maxX <= minX || maxY <= minY) return;

        Color color = team == 1 ? gameManager.AlphaTeam : team == 2 ? gameManager.BetaTeam : Color.clear;
        if (team != 0) color.a = 1f;
        splatCompute.SetTexture(KernelFillRegion, "InkTexture", splatMapRenderTexture);
        splatCompute.SetVector("FillColor", color);
        splatCompute.SetInts("FillRect", minX, minY, maxX, maxY);
        splatCompute.SetInts("BoundsMin", minX, minY);
        splatCompute.Dispatch(KernelFillRegion, Mathf.CeilToInt((maxX - minX) / 8f), Mathf.CeilToInt((maxY - minY) / 8f), 1);

        RegenerateNormalsFull(); // one-off setup call, so a full rebuild is fine
        SyncTeamMapRegion(minX, minY, maxX - minX, maxY - minY);
    }

    public void ClearInk()
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = splatMapRenderTexture;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;
        RegenerateNormalsFull(); // full rebuild — also wipes any trail indents, matching a full ink reset

        // The cleared state is known, so update the CPU copy directly instead of reading it
        // back. Bumping the generation drops any splat readbacks still in flight.
        teamMapGeneration++;
        if (teamMap != null) System.Array.Clear(teamMap, 0, teamMap.Length);
    }

    public void CheckScoresAsync(System.Action<Vector3Int> callback)
    {
        scoreBuffer.SetData(new int[] { 0, 0, 0 });
        splatCompute.SetTexture(KernelGetScores, "InkTexture", splatMapRenderTexture);
        splatCompute.SetBuffer(KernelGetScores, "TeamScores", scoreBuffer);
        splatCompute.Dispatch(KernelGetScores, size / 8, size / 8, 1);

        AsyncGPUReadback.Request(scoreBuffer, request =>
        {
            if (request.hasError) return;
            var data = request.GetData<int>();
            // Neutral = covered pixels not yet painted by either team.
            // coveredPixelCount excludes UV-space pixels that don't map to any mesh triangle.
            int neutral = Mathf.Max(0, coveredPixelCount - data[0] - data[1]);
            callback(new Vector3Int(data[0], data[1], neutral));
        });
    }

    // Like CheckScoresAsync but only tallies floor/slope pixels (walls excluded via the
    // top mask). Uses a dedicated score buffer so it can run alongside a full-score pass.
    public void CheckTopScoresAsync(System.Action<Vector3Int> callback)
    {
        topScoreBuffer.SetData(new int[] { 0, 0, 0 });
        splatCompute.SetTexture(KernelGetTopScores, "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetTopScores, "TopMask", topMaskTexture);
        splatCompute.SetBuffer(KernelGetTopScores, "TeamScores", topScoreBuffer);
        splatCompute.Dispatch(KernelGetTopScores, size / 8, size / 8, 1);

        AsyncGPUReadback.Request(topScoreBuffer, request =>
        {
            if (request.hasError) return;
            var data = request.GetData<int>();
            int neutral = Mathf.Max(0, topCoveredPixelCount - data[0] - data[1]);
            callback(new Vector3Int(data[0], data[1], neutral));
        });
    }

    // Team owning the ink at a UV: 0 none, 1 alpha, 2 beta. Synchronous and exact for any UV,
    // so any number of callers per frame is free. Lags the GPU by the readback latency
    // (typically 1–3 frames after a splat lands).
    public int getSurfaceTeam(Vector2 texCoords)
    {
        if (teamMap == null) return 0;
        if (texCoords.x < 0f || texCoords.y < 0f || texCoords.x > 1f || texCoords.y > 1f) return 0;
        int x = Mathf.Min((int)(texCoords.x * size), size - 1);
        int y = Mathf.Min((int)(texCoords.y * size), size - 1);
        return teamMap[y * size + x];
    }

    // Reads one texel-space box of InkTexture back to the CPU and re-classifies it into
    // teamMap. Splat calls this with its own dispatch bounds, so only what changed is copied.
    void SyncTeamMapRegion(int minX, int minY, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        int generation = teamMapGeneration;
        AsyncGPUReadback.Request(splatMapRenderTexture, 0, minX, width, minY, height, 0, 1, TextureFormat.RGBA32, request =>
        {
            if (request.hasError || teamMap == null || generation != teamMapGeneration) return;
            var pixels = request.GetData<Color32>();
            for (int row = 0; row < height; row++)
            {
                int dst = (minY + row) * size + minX;
                int src = row * width;
                for (int col = 0; col < width; col++)
                    teamMap[dst + col] = ClassifyTeam(pixels[src + col]);
            }
        });
    }

    // CPU twin of the compute shader's ColorNear: painted only when alpha > 0.5, and RGB
    // within 0.01 of a team colour (≤ 2 steps out of 255 once quantised to 8 bits).
    byte ClassifyTeam(Color32 c)
    {
        if (c.a < 128) return 0;
        if (ColorNear(c, alphaColor32)) return 1;
        if (ColorNear(c, betaColor32))  return 2;
        return 0;
    }

    static bool ColorNear(Color32 a, Color32 b) =>
        Mathf.Abs(a.r - b.r) <= 2 && Mathf.Abs(a.g - b.g) <= 2 && Mathf.Abs(a.b - b.b) <= 2;

    void OnDestroy()
    {
        teamMap = null; // makes any readback still in flight a no-op
        scoreBuffer?.Release();
        topScoreBuffer?.Release();
        if (topMaskTexture != null) Destroy(topMaskTexture);
        if (splatMapRenderTexture != null) splatMapRenderTexture.Release();
        if (normalMapRenderTexture != null) normalMapRenderTexture.Release();
    }
}
