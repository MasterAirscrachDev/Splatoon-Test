using UnityEngine;

// The lobby scene's kiosk: walk up to it to go online. Standing in front of it opens the online
// menu (host, join, or leave the lobby we're in); walking away, or Cancel, closes it. Step away
// and back to open it again.
public class LobbyKiosk : MonoBehaviour
{
    [SerializeField] float radius = 2.2f;   // how close counts (metres, along the floor)
    [SerializeField] LobbyMenu menu;        // found if not set

    bool inside;

    public bool PlayerInside => inside; // tests
    public float Radius => radius;

    void Update()
    {
        if (menu == null) menu = FindFirstObjectByType<LobbyMenu>(FindObjectsInactive.Include);
        PlayerController player = NetGameManager.LocalPlayer;
        bool now = false;
        if (player != null && !player.IsDead)
        {
            Vector3 d = player.transform.position - transform.position;
            d.y = 0f;
            now = d.magnitude <= radius;
        }
        if (now != inside && menu != null)
        {
            if (now) menu.Open(); else menu.Close();
        }
        inside = now;
    }
}
