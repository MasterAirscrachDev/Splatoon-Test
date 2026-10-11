using UnityEngine;

// A lobby kiosk: walk up to it to open its menu. The online kiosk opens the online menu (host,
// join, or leave the lobby we're in); the loadout kiosk opens the loadout menu. Walking away, or
// Cancel, closes it. Step away and back to open it again.
public class LobbyKiosk : MonoBehaviour
{
    public enum Opens { Online, Loadout }

    [SerializeField] Opens opens = Opens.Online;
    [SerializeField] float radius = 2.2f;   // how close counts (metres, along the floor)
    [SerializeField] LobbyMenu menu;        // found if not set (the online kiosk's)
    LoadoutMenu loadout;                    // the loadout kiosk's

    bool inside;

    public bool PlayerInside => inside; // tests
    public float Radius => radius;

    void Update()
    {
        if (opens == Opens.Online && menu == null) menu = FindFirstObjectByType<LobbyMenu>(FindObjectsInactive.Include);
        if (opens == Opens.Loadout && loadout == null) loadout = FindFirstObjectByType<LoadoutMenu>(FindObjectsInactive.Include);
        PlayerController player = NetGameManager.LocalPlayer;
        bool now = false;
        if (player != null && !player.IsDead)
        {
            Vector3 d = player.transform.position - transform.position;
            d.y = 0f;
            now = d.magnitude <= radius;
        }
        if (now != inside)
        {
            if (opens == Opens.Online && menu != null) { if (now) menu.Open(); else menu.Close(); }
            if (opens == Opens.Loadout && loadout != null) loadout.SetOpen(now);
        }
        inside = now;
    }
}
