using UnityEngine;

// GameCore (the game manager and the Steam session) lives for the whole game. Every scene carries
// one, disabled: the first scene to load enables its own, which then outlives the scene; any
// scene loaded after that destroys its copy, as the game already has one.
public class GameCoreBootstrap : MonoBehaviour
{
    [SerializeField] GameObject gameCore; // this scene's, disabled in the scene

    void Awake()
    {
        if (gameCore == null) return;
        if (NetGameManager.Instance != null) Destroy(gameCore); // already running from an earlier scene
        else gameCore.SetActive(true);
    }
}
