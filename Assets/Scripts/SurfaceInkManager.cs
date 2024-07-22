using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SurfaceInkManager : MonoBehaviour
{
    [SerializeField] int size = 256, alphaUVClear = 0;
    [SerializeField] Vector3Int scoresRaw;
    RenderTexture splatMapRenderTexture;
    [SerializeField] Texture2D scoresReadTexture;
    GameManager gameManager;
    ComputeShader splatCompute;
    ComputeBuffer splatBuffer;


    // Start is called before the first frame update
    void Start()
    {
        splatCompute = Resources.Load<ComputeShader>("SplatCompute");
        gameManager = FindObjectOfType<GameManager>();
        InitializeSplatCompute();
        GetComponent<Renderer>().material.mainTexture = splatMapRenderTexture;
    }
    void InitializeSplatCompute(){
        splatMapRenderTexture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
        splatMapRenderTexture.enableRandomWrite = true;
        scoresReadTexture = toTexture2D(splatMapRenderTexture);
        
        splatMapRenderTexture.Create();
        //splatCompute
        splatCompute.SetTexture(0, "InkTexture", splatMapRenderTexture);
        //set the alpha and beta team colors
        Vector4 alphaTeam, betaTeam;
        alphaTeam.x = gameManager.AlphaTeam.r; alphaTeam.y = gameManager.AlphaTeam.g; alphaTeam.z = gameManager.AlphaTeam.b; alphaTeam.w = gameManager.AlphaTeam.a;
        betaTeam.x = gameManager.BetaTeam.r; betaTeam.y = gameManager.BetaTeam.g; betaTeam.z = gameManager.BetaTeam.b; betaTeam.w = gameManager.BetaTeam.a;
        //Debug.Log(alphaTeam); Debug.Log(betaTeam);
        splatCompute.SetVector("AlphaColor", alphaTeam);
        splatCompute.SetVector("BetaColor", betaTeam);
        splatCompute.SetVector("NoColor", new Vector4(0, 0, 0, 0));
        splatCompute.SetInt("Size", size);
        splatCompute.SetInts("PixelCoords", new int[2] { 0, 0 });
        splatCompute.SetInt("SplashSize", 0);
        splatCompute.SetInt("Team", 1);
        splatCompute.Dispatch(0, size / 8, size / 8,1);
    }
    public void Splat(Vector2 texCoords, int splashSize, int team){
        int x = (int)(texCoords.x * size); int y = (int)(texCoords.y * size);
        splatCompute.SetTexture(0, "InkTexture", splatMapRenderTexture);
        splatCompute.SetInts("PixelCoords", new int[2] { x, y });
        splatCompute.SetInt("SplashSize", splashSize);
        splatCompute.SetInt("Team", team);
        splatCompute.Dispatch(0, size / 8, size / 8,1); 
    }
    public Vector3Int CheckScores(){
        scoresRaw = new Vector3Int(0, 0, 0);
        System.DateTime startTime = System.DateTime.Now;
        scoresReadTexture = toTexture2D(splatMapRenderTexture);
        System.DateTime endTime = System.DateTime.Now;
        //Debug.Log("Time to read texture: " + (endTime - startTime).TotalMilliseconds);
        startTime = System.DateTime.Now;
        Color[] colors = scoresReadTexture.GetPixels(0, 0, size, size, 0);
        for(int i = 0; i < colors.Length; i++){
            if(IsColorEqual(colors[i], gameManager.AlphaTeam, 0.01f)){ scoresRaw.x++; }
            else if(IsColorEqual(colors[i], gameManager.BetaTeam, 0.01f)){ scoresRaw.y++; }   
            else{ scoresRaw.z++; }
        }
        float count = colors.Length - alphaUVClear;
        scoresRaw.z -= alphaUVClear;
        if(scoresRaw.z < 0){ scoresRaw.z = 0; }
        endTime = System.DateTime.Now;
        //Debug.Log("Time to calculate pixels: " + (endTime - startTime).TotalMilliseconds);
        return scoresRaw;
    }
    Texture2D toTexture2D(RenderTexture rTex)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
        RenderTexture.active = splatMapRenderTexture; //capture a smaller area (faster ?)
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
        tex.Apply();
        return tex;
    }
    public int getSurfaceTeam(Vector2 texCoords){
        int x = (int)(texCoords.x * size); int y = (int)(texCoords.y * size);
        Color color = scoresReadTexture.GetPixel(x, y);
        if(IsColorEqual(color, gameManager.AlphaTeam, 0.01f)){ return 1; }
        else if(IsColorEqual(color, gameManager.BetaTeam, 0.01f)){ return 2; }
        else{ return 0; }
    }
    bool IsColorEqual(Color a, Color b, float tolerance = 0.01f){
        if(Mathf.Abs(a.a - b.a) > tolerance){ return false; }
        if(Mathf.Abs(a.r - b.r) > tolerance){ return false; }
        if(Mathf.Abs(a.g - b.g) > tolerance){ return false; }
        if(Mathf.Abs(a.b - b.b) > tolerance){ return false; }
        return true;
    }

}
