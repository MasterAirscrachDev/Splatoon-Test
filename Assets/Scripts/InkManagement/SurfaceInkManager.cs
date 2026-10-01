using UnityEngine;
using UnityEngine.Rendering;

// Owns one inkable surface: its ink texture (GPU), a CPU copy of team ownership, the dynamic
// ink normal map, and scoring.
public class SurfaceInkManager : MonoBehaviour
{
    [SerializeField] float pixelsPerUnit = 32f;
    [SerializeField] float splatScale = 1.0f;   // splat radius multiplier for this surface
    [SerializeField] float maxFloorAngle = 50f; // steeper faces count as walls (excluded from top-down score)

    float inkNormalStrength = 0.5f;             // 0 disables the dynamic normal map
    const float ReferencePixelsPerUnit = 32f;   // inkNormalStrength was tuned at this density
    float inkNoiseScale;                        // derived from splatScale, see CalculateNoiseScale
    const float InkSeamHeight = 0.7f;           // ridge where two teams' ink meets, relative to full coverage
    public float PixelsPerUnit => pixelsPerUnit;

    public int SurfaceId; // assigned by NetGameManager; identical on every client
    public static System.Action<SplatData> OnSplatApplied; // NetGameManager broadcasts these
    // Locally originated splats: (team, world m² newly turned to that team). Charges specials.
    public static System.Action<int, float> OnTurfInked;

    int size;
    int coveredPixelCount;    // texels inside the mesh's UV triangles (scoring denominator)
    int topCoveredPixelCount; // subset on floor/slope faces
    bool[] coveredMask;       // texels on the mesh (null = all); turf counts only these
    float texelArea;          // world m² per covered texel
    NetGameManager gameManager;
    ComputeShader splatCompute;
    RenderTexture splatMapRenderTexture;
    RenderTexture normalMapRenderTexture;
    Texture2D topMaskTexture; // 1 = floor/slope texel, 0 = wall

    int kernelSplat, kernelGetScores, kernelGetTopScores, kernelComputeNormals, kernelPaintTrailNormal, kernelFillRegion;

    ComputeBuffer scoreBuffer;
    ComputeBuffer topScoreBuffer;

    // The ink texture stores coverage per team (r = alpha, g = beta), not colours; the surface
    // shader colours it. teamMap is a CPU copy of ownership per texel (0 none, 1 alpha, 2 beta),
    // synced by reading back each splat's area. getSurfaceTeam reads this.
    byte[] teamMap;
    int teamMapGeneration; // bumped by ClearInk so older readbacks are discarded

    void Start()
    {
        splatCompute = Instantiate(Resources.Load<ComputeShader>("SplatCompute"));
        gameManager = NetGameManager.Instance;

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
            rend.material.SetColor("_AlphaColor", gameManager.AlphaTeam);
            rend.material.SetColor("_BetaColor", gameManager.BetaTeam);
        }
    }

    // Linear fit through splatScale 5 -> 0.025 and 1.5 -> 0.05, keeping bump grain proportional to splat size.
    static float CalculateNoiseScale(float splatScale)
    {
        const float x1 = 5f,   y1 = 0.025f;
        const float x2 = 1.5f, y2 = 0.05f;
        const float slope = (y2 - y1) / (x2 - x1);
        const float intercept = y1 - slope * x1;
        return intercept + slope * splatScale;
    }

    // Clamped texel box (and thread-group counts) around a point, so dispatches touch only that region.
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
        float worldScale = pixelsPerUnit / ReferencePixelsPerUnit;
        splatCompute.SetFloat("NormalStrength", inkNormalStrength * worldScale);
        splatCompute.SetFloat("NoiseStrength", 1);
        splatCompute.SetFloat("NoiseScale", inkNoiseScale);
        splatCompute.SetFloat("SeamHeight", InkSeamHeight);
        splatCompute.SetTexture(kernelComputeNormals, "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(kernelComputeNormals, "NormalTexture", normalMapRenderTexture);
    }

    void RegenerateNormalsFull()
    {
        if (inkNormalStrength <= 0f) return;
        SetNormalUniforms();
        splatCompute.SetInts("BoundsMin", new int[2] { 0, 0 });
        splatCompute.SetInt("MaskToSplat", 0);
        splatCompute.Dispatch(kernelComputeNormals, size / 8, size / 8, 1);
    }

    // Rebuilds normals in a box, only where the current splat's shape reached (keeps nearby trail indents).
    void RegenerateNormalsRegion(int minX, int minY, int groupsX, int groupsY)
    {
        if (inkNormalStrength <= 0f) return;
        SetNormalUniforms();
        splatCompute.SetInts("BoundsMin", new int[2] { minX, minY });
        splatCompute.SetInt("MaskToSplat", 1);
        splatCompute.Dispatch(kernelComputeNormals, groupsX, groupsY, 1);
    }

    // Original mesh in local space. Static batching swaps the MeshFilter's mesh for an unreadable
    // combined one at play time; the MeshCollider keeps the original.
    Mesh GetSharedMesh()
    {
        Renderer rend = GetComponent<Renderer>();
        bool batched = rend != null && rend.isPartOfStaticBatch;

        MeshFilter mf = GetComponent<MeshFilter>();
        if (!batched && mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable) return mf.sharedMesh;

        MeshCollider mc = GetComponent<MeshCollider>();
        if (mc != null && mc.sharedMesh != null && mc.sharedMesh.isReadable) return mc.sharedMesh;

        SkinnedMeshRenderer smr = GetComponent<SkinnedMeshRenderer>();
        if (smr != null && smr.sharedMesh != null && smr.sharedMesh.isReadable) return smr.sharedMesh;

        Debug.LogWarning($"[SurfaceInkManager] {name}: no readable source mesh (static-batched with no " +
                         "MeshCollider, or Read/Write disabled); scoring falls back to whole-surface coverage.", this);
        return null;
    }

    // Rasterises the mesh's UV triangles into coverage masks (all faces, and floor/slope faces only).
    void BuildCoverageMasks()
    {
        Mesh mesh = GetSharedMesh();
        Vector2[] uvs = mesh != null ? mesh.uv : null;

        if (mesh == null || uvs == null || uvs.Length == 0)
        {
            // No UVs: classify the whole surface by its up axis.
            coveredPixelCount = size * size;
            Renderer r = GetComponent<Renderer>();
            float side = r != null ? Mathf.Max(r.bounds.size.x, r.bounds.size.y, r.bounds.size.z) : 1f;
            texelArea = side * side / coveredPixelCount;
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
        float worldArea = 0f;

        for (int ti = 0; ti < tris.Length; ti += 3)
        {
            int i0 = tris[ti], i1 = tris[ti + 1], i2 = tris[ti + 2];
            Vector2 a = uvs[i0], b = uvs[i1], c = uvs[i2];
            Vector3 w0 = transform.TransformPoint(verts[i0]);
            worldArea += Vector3.Cross(transform.TransformPoint(verts[i1]) - w0, transform.TransformPoint(verts[i2]) - w0).magnitude * 0.5f;

            // World face normal: vertex normals if present, else geometric.
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
                if (covered[idx] && (topCov[idx] || !topFacing)) continue; // nothing new to learn

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
        coveredMask = covered;
        texelArea = count > 0 ? worldArea / count : 0f;
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
        kernelSplat            = splatCompute.FindKernel("Splat");
        kernelGetScores        = splatCompute.FindKernel("GetScores");
        kernelGetTopScores     = splatCompute.FindKernel("GetTopScores");
        kernelComputeNormals   = splatCompute.FindKernel("ComputeNormals");
        kernelPaintTrailNormal = splatCompute.FindKernel("PaintTrailNormal");
        kernelFillRegion       = splatCompute.FindKernel("FillRegion");

        teamMap = new byte[size * size];

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

        splatCompute.SetTexture(kernelSplat,            "InkTexture",    splatMapRenderTexture);
        splatCompute.SetTexture(kernelGetScores,        "InkTexture",    splatMapRenderTexture);
        splatCompute.SetTexture(kernelGetTopScores,     "InkTexture",    splatMapRenderTexture);
        splatCompute.SetTexture(kernelGetTopScores,     "TopMask",       topMaskTexture);
        splatCompute.SetTexture(kernelComputeNormals,   "InkTexture",    splatMapRenderTexture);
        splatCompute.SetTexture(kernelComputeNormals,   "NormalTexture", normalMapRenderTexture);
        splatCompute.SetTexture(kernelPaintTrailNormal, "NormalTexture", normalMapRenderTexture);

        splatCompute.SetBuffer(kernelSplat,        "TeamScores", scoreBuffer);
        splatCompute.SetBuffer(kernelGetScores,    "TeamScores", scoreBuffer);
        splatCompute.SetBuffer(kernelGetTopScores, "TeamScores", topScoreBuffer);

        splatCompute.SetInt("Size", size);
        splatCompute.SetInts("PixelCoords", new int[2] { 0, 0 });
        splatCompute.SetInt("SplashSize", 0);
        splatCompute.SetInt("Team", 1);
        splatCompute.SetFloat("NormalStrength", inkNormalStrength);
        splatCompute.Dispatch(kernelSplat, size / 8, size / 8, 1);
        RegenerateNormalsFull(); // prime to flat
    }

    public void Splat(Vector2 texCoords, int splashSize, int team, bool broadcast = true)
    {
        int x = (int)(texCoords.x * size);
        int y = (int)(texCoords.y * size);
        int radius = Mathf.RoundToInt(splashSize * splatScale);

        int boundRadius = Mathf.CeilToInt(radius * 1.4f) + 4; // domain warp pushes the edge out up to r*0.4, plus feather
        ComputeBounds(x, y, boundRadius, out int minX, out int minY, out int groupsX, out int groupsY);

        splatCompute.SetTexture(kernelSplat, "InkTexture", splatMapRenderTexture);
        splatCompute.SetInts("PixelCoords", new int[2] { x, y });
        splatCompute.SetInts("BoundsMin", new int[2] { minX, minY });
        splatCompute.SetInt("SplashSize", radius);
        splatCompute.SetInt("Team", team);
        splatCompute.Dispatch(kernelSplat, groupsX, groupsY, 1);

        RegenerateNormalsRegion(minX, minY, groupsX, groupsY);
        SyncTeamMapRegion(minX, minY, Mathf.Min(groupsX * 8, size - minX), Mathf.Min(groupsY * 8, size - minY),
                          broadcast ? team : 0); // our own splats charge our special

        if (broadcast)
            OnSplatApplied?.Invoke(new SplatData { surfaceId = SurfaceId, uv = texCoords, splashSize = splashSize, team = team });
    }

    // Stamps a swim-trail indent into the normal map only (ink, colour and score are untouched).
    // Blends toward the target depth, so repeated stamps converge.
    public void PaintTrailNormal(Vector2 texCoords, int radius, float depth = 1f, float blend = 0.25f)
    {
        if (inkNormalStrength <= 0f) return;
        int x = (int)(texCoords.x * size);
        int y = (int)(texCoords.y * size);
        int texRadius = Mathf.Max(1, Mathf.RoundToInt(radius * splatScale));

        ComputeBounds(x, y, texRadius + 2, out int minX, out int minY, out int groupsX, out int groupsY);

        splatCompute.SetTexture(kernelPaintTrailNormal, "NormalTexture", normalMapRenderTexture);
        splatCompute.SetVector("TrailCenter", new Vector4(x, y, 0f, 0f));
        splatCompute.SetFloat("TrailRadius", texRadius);
        splatCompute.SetFloat("TrailDepth", depth);
        splatCompute.SetFloat("TrailBlend", blend);
        splatCompute.SetInts("BoundsMin", new int[2] { minX, minY });
        splatCompute.Dispatch(kernelPaintTrailNormal, groupsX, groupsY, 1);
    }

    // Solidly fills a UV rect with one team's ink (0 erases). No broadcast; for tests and tools.
    public void FillRegion(Rect uvRect, int team)
    {
        if (splatMapRenderTexture == null) return; // Start hasn't run yet
        int minX = Mathf.Clamp(Mathf.FloorToInt(uvRect.xMin * size), 0, size);
        int minY = Mathf.Clamp(Mathf.FloorToInt(uvRect.yMin * size), 0, size);
        int maxX = Mathf.Clamp(Mathf.CeilToInt(uvRect.xMax * size), 0, size);
        int maxY = Mathf.Clamp(Mathf.CeilToInt(uvRect.yMax * size), 0, size);
        if (maxX <= minX || maxY <= minY) return;

        Vector4 coverage = team == 1 ? new Vector4(1, 0, 0, 0) : team == 2 ? new Vector4(0, 1, 0, 0) : Vector4.zero;
        splatCompute.SetTexture(kernelFillRegion, "InkTexture", splatMapRenderTexture);
        splatCompute.SetVector("FillColor", coverage);
        splatCompute.SetInts("FillRect", minX, minY, maxX, maxY);
        splatCompute.SetInts("BoundsMin", minX, minY);
        splatCompute.Dispatch(kernelFillRegion, Mathf.CeilToInt((maxX - minX) / 8f), Mathf.CeilToInt((maxY - minY) / 8f), 1);

        RegenerateNormalsFull();
        SyncTeamMapRegion(minX, minY, maxX - minX, maxY - minY);
    }

    public void ClearInk()
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = splatMapRenderTexture;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;
        RegenerateNormalsFull(); // also wipes trail indents

        teamMapGeneration++; // drop readbacks still in flight
        if (teamMap != null) System.Array.Clear(teamMap, 0, teamMap.Length);
    }

    public void CheckScoresAsync(System.Action<Vector3Int> callback)
    {
        scoreBuffer.SetData(new int[] { 0, 0, 0 });
        splatCompute.SetTexture(kernelGetScores, "InkTexture", splatMapRenderTexture);
        splatCompute.SetBuffer(kernelGetScores, "TeamScores", scoreBuffer);
        splatCompute.Dispatch(kernelGetScores, size / 8, size / 8, 1);

        AsyncGPUReadback.Request(scoreBuffer, request =>
        {
            if (request.hasError) return;
            var data = request.GetData<int>();
            int neutral = Mathf.Max(0, coveredPixelCount - data[0] - data[1]); // unpainted texels that map to the mesh
            callback(new Vector3Int(data[0], data[1], neutral));
        });
    }

    // Floor/slope texels only (walls excluded via the top mask).
    public void CheckTopScoresAsync(System.Action<Vector3Int> callback)
    {
        topScoreBuffer.SetData(new int[] { 0, 0, 0 });
        splatCompute.SetTexture(kernelGetTopScores, "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(kernelGetTopScores, "TopMask", topMaskTexture);
        splatCompute.SetBuffer(kernelGetTopScores, "TeamScores", topScoreBuffer);
        splatCompute.Dispatch(kernelGetTopScores, size / 8, size / 8, 1);

        AsyncGPUReadback.Request(topScoreBuffer, request =>
        {
            if (request.hasError) return;
            var data = request.GetData<int>();
            int neutral = Mathf.Max(0, topCoveredPixelCount - data[0] - data[1]);
            callback(new Vector3Int(data[0], data[1], neutral));
        });
    }

    // Team owning the ink at a UV (0 none, 1 alpha, 2 beta). Lags the GPU by 1â€“3 frames.
    public int getSurfaceTeam(Vector2 texCoords)
    {
        if (teamMap == null) return 0;
        if (texCoords.x < 0f || texCoords.y < 0f || texCoords.x > 1f || texCoords.y > 1f) return 0;
        int x = Mathf.Min((int)(texCoords.x * size), size - 1);
        int y = Mathf.Min((int)(texCoords.y * size), size - 1);
        return teamMap[y * size + x];
    }

    // UV of a raycast hit on this surface. textureCoord is only set by non-convex MeshColliders;
    // otherwise estimate it from the hit's position within the renderer's local bounds.
    public Vector2 UVFromHit(RaycastHit hit)
    {
        if (hit.textureCoord.sqrMagnitude > 0.0001f) return hit.textureCoord;

        Renderer rend = GetComponent<Renderer>();
        if (rend == null) return Vector2.zero;
        Bounds b = rend.localBounds;
        if (b.size.sqrMagnitude < 0.0001f) return Vector2.zero;

        Vector3 p = transform.InverseTransformPoint(hit.point);
        Vector3 n = transform.InverseTransformDirection(hit.normal);
        float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);

        // The normal's dominant axis is depth; the other two become U and V.
        if (ay >= ax && ay >= az)
            return new Vector2(Mathf.InverseLerp(b.min.x, b.max.x, p.x), Mathf.InverseLerp(b.min.z, b.max.z, p.z));
        if (az >= ax)
            return new Vector2(Mathf.InverseLerp(b.min.x, b.max.x, p.x), Mathf.InverseLerp(b.min.y, b.max.y, p.y));
        return new Vector2(Mathf.InverseLerp(b.min.z, b.max.z, p.z), Mathf.InverseLerp(b.min.y, b.max.y, p.y));
    }

    // Reads a texel box of the ink texture back and re-classifies it into teamMap. With
    // turfTeam set, reports the area that changed to that team (OnTurfInked).
    void SyncTeamMapRegion(int minX, int minY, int width, int height, int turfTeam = 0)
    {
        if (width <= 0 || height <= 0) return;
        int generation = teamMapGeneration;
        AsyncGPUReadback.Request(splatMapRenderTexture, 0, minX, width, minY, height, 0, 1, TextureFormat.RGBA32, request =>
        {
            if (request.hasError || teamMap == null || generation != teamMapGeneration) return;
            var pixels = request.GetData<Color32>();
            int converted = 0;
            for (int row = 0; row < height; row++)
            {
                int dst = (minY + row) * size + minX;
                int src = row * width;
                for (int col = 0; col < width; col++)
                {
                    byte now = ClassifyTeam(pixels[src + col]);
                    if (turfTeam != 0 && now == turfTeam && teamMap[dst + col] != turfTeam && (coveredMask == null || coveredMask[dst + col]))
                        converted++;
                    teamMap[dst + col] = now;
                }
            }
            if (converted > 0) OnTurfInked?.Invoke(turfTeam, converted * texelArea);
        });
    }

    // CPU twin of the compute shader's TeamOf: the higher coverage, if at least half covered.
    static byte ClassifyTeam(Color32 c)
    {
        if (Mathf.Max(c.r, c.g) < 128) return 0;
        return c.r >= c.g ? (byte)1 : (byte)2;
    }

    void OnDestroy()
    {
        teamMap = null; // makes in-flight readbacks no-ops
        scoreBuffer?.Release();
        topScoreBuffer?.Release();
        if (topMaskTexture != null) Destroy(topMaskTexture);
        if (splatMapRenderTexture != null) splatMapRenderTexture.Release();
        if (normalMapRenderTexture != null) normalMapRenderTexture.Release();
    }
}
