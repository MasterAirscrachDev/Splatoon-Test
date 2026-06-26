using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    public Color AlphaTeam, BetaTeam;

    void Start() { }
    void Update() { }

    public void GetScores()
    {
        SurfaceInkManager[] managers = FindObjectsByType<SurfaceInkManager>(FindObjectsSortMode.None);
        if (managers.Length == 0) return;

        int pending = managers.Length;
        Vector3Int totalScore = Vector3Int.zero;

        foreach (var manager in managers)
        {
            manager.CheckScoresAsync(scores =>
            {
                totalScore += scores;
                pending--;
                if (pending == 0)
                {
                    float total = totalScore.x + totalScore.y + totalScore.z;
                    if (total <= 0) return;
                    Debug.Log($"Alpha: {totalScore.x / total * 100:f2}%, Beta: {totalScore.y / total * 100:f2}%, Neutral: {totalScore.z / total * 100:f2}%");
                }
            });
        }
    }
}
