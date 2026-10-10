using UnityEngine;

// The GameCore prefab's root (Assets/Prefabs/GameCore.prefab): the core (NetGameManager +
// SteamNetwork) is a disabled child, so nothing in it wakes before we've checked. The Lobby (the
// first scene) has one and it lives for the whole game; drop the prefab into any other scene to
// play it directly. If a GameCore is already running (carried from the Lobby), this copy goes.
public class GameCoreBootstrap : MonoBehaviour
{
    [SerializeField] GameObject gameCore; // the core, disabled in the prefab

    void Awake()
    {
        if (gameCore == null) return;
        if (NetGameManager.Instance != null)
        {
            Destroy(gameCore);
            Destroy(gameObject);
        }
        else gameCore.SetActive(true);
    }
}
