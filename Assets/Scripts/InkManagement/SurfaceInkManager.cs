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
    Texture2D topMaskTexture; // R8: 1 where a pixel maps to a floor/slope face, 0 for walls

    const int KernelSplat = 0;
    const int KernelGetScores = 1;
    const int KernelGetTeamAtPixel = 2;
    const int KernelGetTopScores = 3;

    ComputeBuffer scoreBuffer;
    ComputeBuffer topScoreBuffer;
    ComputeBuffer pixelTeamBuffer;

    int cachedSurfaceTeam = 0;
    bool teamReadbackPending = false;

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

        BuildCoverageMasks();
        InitializeSplatCompute();
        if (rend != null)
        {
            rend.material.mainTexture = splatMapRenderTexture;
            // Tile detail normal at ink-pixel density: pixelsPerUnit tiles per world unit
            rend.material.SetTextureScale("_DetailNormalMap", new Vector2(pixelsPerUnit / 16, pixelsPerUnit / 16));
        }
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
        splatMapRenderTexture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
        splatMapRenderTexture.enableRandomWrite = true;
        splatMapRenderTexture.filterMode = FilterMode.Bilinear;
        splatMapRenderTexture.Create();

        scoreBuffer = new ComputeBuffer(3, sizeof(int));
        topScoreBuffer = new ComputeBuffer(3, sizeof(int));
        pixelTeamBuffer = new ComputeBuffer(1, sizeof(int));

        splatCompute.SetTexture(KernelSplat,          "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetScores,       "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetTeamAtPixel,  "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetTopScores,    "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetTopScores,    "TopMask",    topMaskTexture);

        splatCompute.SetBuffer(KernelSplat,           "TeamScores",  scoreBuffer);
        splatCompute.SetBuffer(KernelSplat,           "PixelTeam",   pixelTeamBuffer);
        splatCompute.SetBuffer(KernelGetScores,        "TeamScores",  scoreBuffer);
        splatCompute.SetBuffer(KernelGetScores,        "PixelTeam",   pixelTeamBuffer);
        splatCompute.SetBuffer(KernelGetTeamAtPixel,   "TeamScores",  scoreBuffer);
        splatCompute.SetBuffer(KernelGetTeamAtPixel,   "PixelTeam",   pixelTeamBuffer);
        splatCompute.SetBuffer(KernelGetTopScores,     "TeamScores",  topScoreBuffer);

        Vector4 alphaTeam = new Vector4(gameManager.AlphaTeam.r, gameManager.AlphaTeam.g, gameManager.AlphaTeam.b, gameManager.AlphaTeam.a);
        Vector4 betaTeam  = new Vector4(gameManager.BetaTeam.r,  gameManager.BetaTeam.g,  gameManager.BetaTeam.b,  gameManager.BetaTeam.a);
        splatCompute.SetVector("AlphaColor", alphaTeam);
        splatCompute.SetVector("BetaColor",  betaTeam);
        splatCompute.SetVector("NoColor",    new Vector4(0, 0, 0, 0));
        splatCompute.SetInt("Size", size);
        splatCompute.SetInts("PixelCoords",     new int[2] { 0, 0 });
        splatCompute.SetInts("TeamQueryCoords", new int[2] { 0, 0 });
        splatCompute.SetInt("SplashSize", 0);
        splatCompute.SetInt("Team", 1);
        splatCompute.Dispatch(KernelSplat, size / 8, size / 8, 1);
    }

    public void Splat(Vector2 texCoords, int splashSize, int team, bool broadcast = true)
    {
        //Debug.Log($"[Splat] surface={SurfaceId} team={team} broadcast={broadcast}\n{new System.Diagnostics.StackTrace(true)}");
        int x = (int)(texCoords.x * size);
        int y = (int)(texCoords.y * size);
        splatCompute.SetTexture(KernelSplat, "InkTexture", splatMapRenderTexture);
        splatCompute.SetInts("PixelCoords", new int[2] { x, y });
        splatCompute.SetInt("SplashSize", Mathf.RoundToInt(splashSize * splatScale));
        splatCompute.SetInt("Team", team);
        splatCompute.Dispatch(KernelSplat, size / 8, size / 8, 1);
        if (broadcast)
            OnSplatApplied?.Invoke(new SplatData { surfaceId = SurfaceId, uv = texCoords, splashSize = splashSize, team = team });
    }

    public void ClearInk()
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = splatMapRenderTexture;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;
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

    public int getSurfaceTeam(Vector2 texCoords)
    {
        if (!teamReadbackPending)
        {
            int x = (int)(texCoords.x * size);
            int y = (int)(texCoords.y * size);
            splatCompute.SetTexture(KernelGetTeamAtPixel, "InkTexture", splatMapRenderTexture);
            splatCompute.SetBuffer(KernelGetTeamAtPixel, "PixelTeam", pixelTeamBuffer);
            splatCompute.SetInts("TeamQueryCoords", new int[2] { x, y });
            splatCompute.Dispatch(KernelGetTeamAtPixel, 1, 1, 1);

            teamReadbackPending = true;
            AsyncGPUReadback.Request(pixelTeamBuffer, request =>
            {
                if (!request.hasError)
                    cachedSurfaceTeam = request.GetData<int>()[0];
                teamReadbackPending = false;
            });
        }
        return cachedSurfaceTeam;
    }

    void OnDestroy()
    {
        scoreBuffer?.Release();
        topScoreBuffer?.Release();
        pixelTeamBuffer?.Release();
        if (topMaskTexture != null) Destroy(topMaskTexture);
    }
}
