using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public Color AlphaTeam, BetaTeam;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
    public void GetScores(){
        Vector3Int totalScore = new Vector3Int(0, 0, 0);
        //get scores
        for(int i = 0; i < FindObjectsOfType<SurfaceInkManager>().Length; i++){
           totalScore += FindObjectsOfType<SurfaceInkManager>()[i].CheckScores();
        }
        //convert to percentages
        float total = totalScore.x + totalScore.y + totalScore.z;
        float alphaPercent = totalScore.x / total;
        float betaPercent = totalScore.y / total;
        float neutralPercent = totalScore.z / total;
        //display scores
        Debug.Log($"Alpha: {Mathf.Round(alphaPercent * 100)}%, Beta: {Mathf.Round(betaPercent * 100)}%, Neutral: {Mathf.Round(neutralPercent * 100)}%");
    }
}
