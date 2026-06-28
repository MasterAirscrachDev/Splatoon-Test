using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class SurfaceInkManager : MonoBehaviour
{
    [SerializeField] float pixelsPerUnit = 32f;
    [SerializeField] float splatScale = 1.0f; // scale of the splat texture in world units
    public float PixelsPerUnit => pixelsPerUnit;

    // Assigned by NetGameManager at scene load. Identical on every client (all surfaces are scene-placed).
    public int SurfaceId;
    // Subscribers (NetGameManager) send the splat over the network. Set broadcast:false on remote replays.
    public static System.Action<SplatData> OnSplatApplied;

    int size;
    int coveredPixelCount; // pixels inside the mesh's UV triangles — used for accurate scoring
    NetGameManager gameManager;
    ComputeShader splatCompute;
    RenderTexture splatMapRenderTexture;

    const int KernelSplat = 0;
    const int KernelGetScores = 1;
    const int KernelGetTeamAtPixel = 2;

    ComputeBuffer scoreBuffer;
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

        coveredPixelCount = CalculateUVCoverage();
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

    // Rasterises the mesh's UV triangles into a boolean mask to count how many texture
    // pixels actually map to a surface. Pixels outside every triangle can never be painted
    // and must not inflate the neutral (unpainted) score.
    int CalculateUVCoverage()
    {
        Mesh mesh = GetSharedMesh();
        if (mesh == null) return size * size;
        Vector2[] uvs = mesh.uv;
        if (uvs == null || uvs.Length == 0) return size * size;

        int[] tris = mesh.triangles;
        bool[] covered = new bool[size * size];

        for (int ti = 0; ti < tris.Length; ti += 3)
        {
            Vector2 a = uvs[tris[ti]], b = uvs[tris[ti + 1]], c = uvs[tris[ti + 2]];

            int px0 = Mathf.Max(0,      Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.x, b.x), c.x) * size));
            int px1 = Mathf.Min(size-1, Mathf.CeilToInt (Mathf.Max(Mathf.Max(a.x, b.x), c.x) * size));
            int py0 = Mathf.Max(0,      Mathf.FloorToInt(Mathf.Min(Mathf.Min(a.y, b.y), c.y) * size));
            int py1 = Mathf.Min(size-1, Mathf.CeilToInt (Mathf.Max(Mathf.Max(a.y, b.y), c.y) * size));

            for (int py = py0; py <= py1; py++)
            for (int px = px0; px <= px1; px++)
            {
                int idx = py * size + px;
                if (covered[idx]) continue;

                Vector2 p = new Vector2((px + 0.5f) / size, (py + 0.5f) / size);
                if (PointInTriangle(p, a, b, c))
                    covered[idx] = true;
            }
        }

        int count = 0;
        for (int i = 0; i < covered.Length; i++) if (covered[i]) count++;
        return count;
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
        pixelTeamBuffer = new ComputeBuffer(1, sizeof(int));

        splatCompute.SetTexture(KernelSplat,          "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetScores,       "InkTexture", splatMapRenderTexture);
        splatCompute.SetTexture(KernelGetTeamAtPixel,  "InkTexture", splatMapRenderTexture);

        splatCompute.SetBuffer(KernelSplat,           "TeamScores",  scoreBuffer);
        splatCompute.SetBuffer(KernelSplat,           "PixelTeam",   pixelTeamBuffer);
        splatCompute.SetBuffer(KernelGetScores,        "TeamScores",  scoreBuffer);
        splatCompute.SetBuffer(KernelGetScores,        "PixelTeam",   pixelTeamBuffer);
        splatCompute.SetBuffer(KernelGetTeamAtPixel,   "TeamScores",  scoreBuffer);
        splatCompute.SetBuffer(KernelGetTeamAtPixel,   "PixelTeam",   pixelTeamBuffer);

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
        pixelTeamBuffer?.Release();
    }
}
