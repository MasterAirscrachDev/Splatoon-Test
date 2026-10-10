using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Spectating (no team: a spectator, or past the map's team sizes): the level from above (MatchFlowHUD's
// overhead view) to start with. Click any player at the top of the screen to watch them from behind;
// a small map of the level then shows in the bottom right, and clicking it goes back to the full view.
// On the match HUD (its player slots are the buttons); the cursor is free while spectating.
public class SpectatorView : MonoBehaviour
{
    [SerializeField] float followDistance = 5f;
    [SerializeField] float followHeight = 1.6f;      // the pivot above their feet
    [SerializeField] float followSmoothing = 10f;
    [SerializeField] Vector2 minimapSize = new Vector2(360f, 230f);
    [SerializeField] float minimapRefresh = 0.5f;    // seconds between renders (ink changes slowly)

    MatchHUD hud;
    MatchFlowHUD flow;
    Camera follow, minimapCamera;
    RenderTexture minimapTexture;
    RectTransform minimap;
    TMP_Text hint;
    float nextMinimap;
    bool spectating, cursorOpen;

    public PlayerController Following { get; private set; }
    public bool Spectating => spectating;
    public bool MinimapShown => minimap != null && minimap.gameObject.activeSelf;   // tests
    public void Watch(PlayerController p) { Following = p; nextMinimap = 0f; }   // tests, and the slot buttons
    public void WatchWholeMap() => Following = null;                                // tests, and the minimap

    void Awake()
    {
        hud = GetComponent<MatchHUD>();
        flow = GetComponent<MatchFlowHUD>();
        if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>(); // the HUD's slots are clickable
        BuildUI();
    }

    void Start()
    {
        // Every player slot becomes a button: click to watch whoever's in it (spectators only).
        for (int team = 1; team <= Teams.Max; team++)
        {
            MatchHUD.PlayerSlot[] slots = hud != null ? hud.SlotsOf(team) : null;
            if (slots == null) continue;
            foreach (MatchHUD.PlayerSlot slot in slots)
            {
                if (slot?.icon == null) continue;
                if (!slot.icon.TryGetComponent(out Button b)) b = slot.icon.gameObject.AddComponent<Button>();
                b.transition = Selectable.Transition.None;
                MatchHUD.PlayerSlot s = slot;
                b.onClick.AddListener(() => { if (spectating && hud.PlayerIn(s) is PlayerController p && p != null) Watch(p); });
            }
        }
    }

    void OnDestroy()
    {
        if (follow != null) Destroy(follow.gameObject);
        if (minimapCamera != null) Destroy(minimapCamera.gameObject);
        if (minimapTexture != null) { minimapTexture.Release(); Destroy(minimapTexture); }
        GameCursor.Close(this);
    }

    void BuildUI()
    {
        // Bottom right: the whole level, small. A button: back to the full view.
        var go = new GameObject("SpectatorMinimap", typeof(RectTransform), typeof(RawImage), typeof(Button), typeof(Outline));
        minimap = (RectTransform)go.transform;
        minimap.SetParent(transform, false);
        minimap.anchorMin = minimap.anchorMax = minimap.pivot = new Vector2(1f, 0f);
        minimap.anchoredPosition = new Vector2(-28f, 28f);
        minimap.sizeDelta = minimapSize;
        go.GetComponent<Outline>().effectColor = new Color(1f, 1f, 1f, 0.8f);
        go.GetComponent<Outline>().effectDistance = new Vector2(3f, -3f);
        go.GetComponent<Button>().onClick.AddListener(WatchWholeMap);
        go.SetActive(false);

        var h = new GameObject("SpectatorHint", typeof(RectTransform), typeof(TextMeshProUGUI));
        var hr = (RectTransform)h.transform;
        hr.SetParent(transform, false);
        hr.anchorMin = hr.anchorMax = hr.pivot = new Vector2(0.5f, 0f);
        hr.anchoredPosition = new Vector2(0f, 150f);
        hr.sizeDelta = new Vector2(1000f, 40f);
        hint = h.GetComponent<TextMeshProUGUI>();
        hint.alignment = TextAlignmentOptions.Center;
        hint.fontSize = 26f;
        hint.raycastTarget = false;
        h.SetActive(false);
    }

    void LateUpdate()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerController local = NetGameManager.LocalPlayer;
        bool results = gm != null && gm.Phase == MatchPhase.Results;
        spectating = local != null && !local.gameObject.activeInHierarchy && !results;
        if (!spectating || Following == null || !Following.gameObject.activeInHierarchy) Following = null; // gone, or now spectating too

        // The cursor is ours while spectating (unless a menu has it).
        if (spectating != cursorOpen)
        {
            cursorOpen = spectating;
            if (spectating) GameCursor.Open(this, GameCursor.Use.Menu); else GameCursor.Close(this);
        }

        bool watching = Following != null;
        if (flow != null) flow.SpectatorFollowing = watching;
        UpdateFollowCamera(watching);
        UpdateMinimap(watching);

        bool showHint = spectating;
        if (hint.gameObject.activeSelf != showHint) hint.gameObject.SetActive(showHint);
        if (showHint)
        {
            string text = watching ? $"Watching {Following.DisplayName}  ·  click the map to see everything" : "Click a player at the top to watch them";
            if (hint.text != text) hint.text = text;
        }
    }

    void UpdateFollowCamera(bool on)
    {
        if (on && follow == null)
        {
            follow = new GameObject("SpectatorCamera").AddComponent<Camera>();
            follow.depth = 45; // over the player camera, under the results view
            follow.cullingMask = ~LayerMask.GetMask("UI");
            follow.enabled = false;
        }
        if (follow == null) return;
        if (follow.enabled != on)
        {
            follow.enabled = on;
            if (on) SnapFollow(1f);
        }
        if (on) SnapFollow(1f - Mathf.Exp(-followSmoothing * Time.deltaTime));
    }

    // Behind the watched player, looking where they look.
    void SnapFollow(float k)
    {
        PlayerController p = Following;
        Quaternion view = Quaternion.Euler(Mathf.Clamp(p.ViewPitch, -60f, 70f), p.transform.eulerAngles.y, 0f);
        Vector3 pivot = p.transform.position + Vector3.up * followHeight;
        Vector3 at = pivot - view * Vector3.forward * followDistance;
        if (Physics.Linecast(pivot, at, out RaycastHit hit, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
            at = hit.point + hit.normal * 0.2f; // not through walls
        Transform t = follow.transform;
        t.position = Vector3.Lerp(t.position, at, k);
        t.rotation = Quaternion.Slerp(t.rotation, view, k);
    }

    void UpdateMinimap(bool on)
    {
        if (minimap.gameObject.activeSelf != on) minimap.gameObject.SetActive(on);
        if (!on) return;
        if (minimapCamera == null)
        {
            minimapTexture = new RenderTexture(Mathf.RoundToInt(minimapSize.x * 2f), Mathf.RoundToInt(minimapSize.y * 2f), 24);
            minimapCamera = new GameObject("SpectatorMinimapCamera").AddComponent<Camera>();
            minimapCamera.targetTexture = minimapTexture;
            minimapCamera.cullingMask = LayerMask.GetMask("Map", "InkSurface", "MapGrate", "Default", "Player", "Hitbox") & ~LayerMask.GetMask("UI");
            minimapCamera.clearFlags = CameraClearFlags.SolidColor;
            minimapCamera.backgroundColor = new Color(0.08f, 0.09f, 0.12f, 1f);
            minimapCamera.enabled = false;  // rendered by hand, now and then
            minimap.GetComponent<RawImage>().texture = minimapTexture;
            LevelView.Frame(minimapCamera, LayerMask.GetMask("Map", "InkSurface"), 70f, 45f, minimapSize.x / minimapSize.y, 1.05f);
        }
        if (Time.unscaledTime >= nextMinimap)
        {
            nextMinimap = Time.unscaledTime + minimapRefresh;
            minimapCamera.Render();
        }
    }
}
