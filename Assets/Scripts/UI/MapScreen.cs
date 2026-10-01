using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Hold Tab: live view of the level from a raised, rotated angle, with your team and your spawn
// listed on the left and a line from each entry to its marker, plus your team's beacons. Clicking
// a teammate, the spawn (entry or marker) or a beacon Super Jumps there. Specials can also open it
// to pick a spot (BeginTargeting): a reticle sized to the area follows the pointer.
public class MapScreen : MonoBehaviour
{
    [System.Serializable]
    public class Entry
    {
        public Button button;
        public Image icon;
        public TMP_Text initial, playerName, status;
        public RectTransform lineStart; // where the line leaves the entry
        public RectTransform line;      // stretched/rotated between lineStart and the marker
        public Button marker;
        public Image markerIcon;
        public TMP_Text markerInitial;
        public RectTransform facing;    // only shown for the local player
        public HoverFlag entryHover, markerHover;
    }

    [SerializeField] GameObject screen;        // everything shown while the map is open
    [SerializeField] RawImage mapImage;
    [SerializeField] RectTransform markerArea; // same rect as the map image
    [SerializeField] RectTransform lineLayer;
    [SerializeField] Entry[] entries = new Entry[4];
    [SerializeField] Entry spawnEntry;            // always available
    [SerializeField] Button beaconMarkerTemplate; // cloned per beacon; its first child is tinted

    [Header("Targeting")]
    [SerializeField] ClickRelay targetCatcher;    // invisible, over the map and its markers
    [SerializeField] RectTransform reticle;
    [SerializeField] TMP_Text hint;

    [Header("Map render")]
    [SerializeField] string[] mapLayers = { "Map", "InkSurface" };
    [SerializeField] float viewPitch = 65f;        // degrees below horizontal; 90 is straight down
    [SerializeField] float viewYaw = 45f;          // turns the level on screen
    [SerializeField] int mapResolution = 1400;     // width; height follows the map's aspect
    [SerializeField] float refreshInterval = 0.5f; // re-render while open so ink stays current
    [SerializeField] float viewPadding = 1.04f;

    [Header("Look")]
    [SerializeField] float lineThickness = 3f;
    [SerializeField] float hoverLineThickness = 6f;
    [SerializeField] float hoverMarkerScale = 1.3f;
    [SerializeField] float deadDim = 0.35f;

    // Raised when a teammate or beacon is picked, after the local player starts the Super Jump.
    public static event System.Action<ISuperJumpTarget> SuperJumpRequested;

    ControlLayer input;
    Camera mapCamera;
    RenderTexture mapTexture;
    bool open, waitForRelease;
    float nextRender;
    readonly List<PlayerController> team = new List<PlayerController>();
    readonly PlayerController[] shown = new PlayerController[4];
    readonly List<Button> beaconMarkers = new List<Button>();
    readonly List<Beacon> shownBeacons = new List<Beacon>();

    public bool IsOpen => open;
    public bool ScriptedHold { get; set; } // tests: acts as holding the map button
    public bool IsTargeting => onTargetPicked != null;

    System.Action<Vector3> onTargetPicked;
    System.Action onTargetCancelled;
    float targetRadius;
    string normalHint;

    void Awake()
    {
        input = new ControlLayer();
        input.GameControl.Enable();
        for (int i = 0; i < entries.Length; i++)
        {
            int index = i;
            entries[i].button.onClick.AddListener(() => RequestJump(index));
            entries[i].marker.onClick.AddListener(() => RequestJump(index));
        }
        spawnEntry.button.onClick.AddListener(RequestSpawnJump);
        spawnEntry.marker.onClick.AddListener(RequestSpawnJump);
        targetCatcher.Clicked += OnMapClicked;
        normalHint = hint.text;
        screen.SetActive(false);
    }

    void OnDestroy()
    {
        input?.Dispose();
        if (mapTexture != null) { mapTexture.Release(); Destroy(mapTexture); }
        if (mapCamera != null) Destroy(mapCamera.gameObject);
    }

    void Update()
    {
        if (IsTargeting) { UpdateTargeting(); return; }
        bool held = ScriptedHold || input.GameControl.Map.IsPressed();
        if (!held) waitForRelease = false;
        if (held && !open && !waitForRelease) TryOpen();
        else if (!held && open) SetOpen(false);
        if (!open) return;

        if (Time.unscaledTime >= nextRender) RenderMap();
        RefreshTeam();
    }

    void TryOpen()
    {
        PlayerController local = NetGameManager.LocalPlayer;
        if (InputGate.Blocked || local == null || !local.gameObject.activeInHierarchy || local.Team == 0) return; // another menu, or benched
        if (local.IsSuperJumping) return;
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null && (gm.Phase == MatchPhase.TimesUp || gm.Phase == MatchPhase.Results)) return; // the match is over
        SetOpen(true);
    }

    public void SetOpen(bool value)
    {
        if (!value && IsTargeting) { CancelTargeting(); return; }
        open = value;
        screen.SetActive(value);
        InputGate.Blocked = value;
        Cursor.lockState = value ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = value;
        if (!value) return;
        HostMenu.EnsureEventSystem();
        EnsureMapCamera();
        RenderMap();
        RefreshTeam();
    }

    void RequestJump(int index)
    {
        PlayerController target = shown[index];
        if (target != null && !target.IsLocalPlayer && !target.IsDead) TryJump(target);
    }

    void RequestSpawnJump()
    {
        PlayerController local = NetGameManager.LocalPlayer;
        if (local != null) TryJump(new SpawnJumpTarget(local.Team, local.SpawnPosition));
    }

    void RequestBeaconJump(int index)
    {
        if (index < shownBeacons.Count && shownBeacons[index] != null) TryJump(shownBeacons[index]);
    }

    void TryJump(ISuperJumpTarget target)
    {
        if (IsTargeting) return;
        PlayerController local = NetGameManager.LocalPlayer;
        if (local == null || !local.StartSuperJump(target)) return;
        SuperJumpRequested?.Invoke(target);
        SetOpen(false);
        waitForRelease = true; // don't reopen while the button is still held
    }

    // ── Targeting (specials) ───────────────────────────────────────────────

    // Opens the map to pick a spot on the level: picked gets the world point, cancelled is
    // called if it closes without one (Esc or right-click). False if the map can't open now.
    public bool BeginTargeting(float radius, System.Action<Vector3> picked, System.Action cancelled)
    {
        if (open || InputGate.Blocked || IsTargeting) return false;
        onTargetPicked = picked;
        onTargetCancelled = cancelled;
        targetRadius = radius;
        SetOpen(true);
        targetCatcher.gameObject.SetActive(true);
        reticle.gameObject.SetActive(true);
        hint.text = "Click where to strike   ·   Esc or right-click to cancel";
        return true;
    }

    public void CancelTargeting() => EndTargeting(false, Vector3.zero);

    // Tests: as if the map were clicked at this map position (MapPosition's space).
    public bool PickTargetAt(Vector2 mapLocal)
    {
        if (!IsTargeting || !TryWorldFromMap(mapLocal, out Vector3 world)) return false;
        EndTargeting(true, world);
        return true;
    }

    void OnMapClicked(PointerEventData e)
    {
        if (!IsTargeting || e.button != PointerEventData.InputButton.Left) return;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(markerArea, e.position, e.pressEventCamera, out Vector2 local))
            PickTargetAt(local);
    }

    void EndTargeting(bool picked, Vector3 world)
    {
        System.Action<Vector3> onPicked = onTargetPicked;
        System.Action onCancelled = onTargetCancelled;
        onTargetPicked = null;
        onTargetCancelled = null;
        targetCatcher.gameObject.SetActive(false);
        reticle.gameObject.SetActive(false);
        hint.text = normalHint;
        SetOpen(false);
        if (picked) onPicked?.Invoke(world); else onCancelled?.Invoke();
    }

    // The reticle follows the pointer, as big as the area would look there.
    void UpdateTargeting()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame || Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
        {
            CancelTargeting();
            return;
        }
        if (Time.unscaledTime >= nextRender) RenderMap();
        RefreshTeam();
        if (Mouse.current != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(markerArea, Mouse.current.position.ReadValue(), null, out Vector2 local))
            reticle.anchoredPosition = local;
        float pixelsPerMetre = markerArea.rect.height / (2f * mapCamera.orthographicSize);
        float across = targetRadius * 2f * pixelsPerMetre;
        reticle.sizeDelta = new Vector2(across, across * Mathf.Sin(viewPitch * Mathf.Deg2Rad)); // a flat circle, seen from the map's angle
    }

    // The level surface under a map position, if any.
    public bool TryWorldFromMap(Vector2 mapLocal, out Vector3 world)
    {
        EnsureMapCamera();
        Rect r = markerArea.rect;
        Ray ray = mapCamera.ViewportPointToRay(new Vector3(mapLocal.x / r.width + 0.5f, mapLocal.y / r.height + 0.5f, 0f));
        bool hit = Physics.Raycast(ray, out RaycastHit h, mapCamera.farClipPlane, LayerMask.GetMask(mapLayers), QueryTriggerInteraction.Ignore);
        world = hit ? h.point : Vector3.zero;
        return hit;
    }

    // ── Map rendering ──────────────────────────────────────────────────────

    // Orthographic camera looking down at viewPitch, turned by viewYaw, framed on the map layers.
    // Only those layers are drawn, over a transparent background.
    void EnsureMapCamera()
    {
        if (mapCamera != null) return;
        int mask = LayerMask.GetMask(mapLayers);
        Rect area = markerArea.rect;
        float aspect = area.width / area.height;
        mapTexture = new RenderTexture(mapResolution, Mathf.RoundToInt(mapResolution / aspect), 16, RenderTextureFormat.ARGB32) { name = "MapView" };
        mapImage.texture = mapTexture;

        mapCamera = new GameObject("MapCamera").AddComponent<Camera>();
        mapCamera.enabled = false; // rendered on demand
        mapCamera.clearFlags = CameraClearFlags.SolidColor;
        mapCamera.backgroundColor = Color.clear;
        mapCamera.cullingMask = mask;
        mapCamera.targetTexture = mapTexture;
        LevelView.Frame(mapCamera, mask, viewPitch, viewYaw, aspect, viewPadding);
    }

    void RenderMap()
    {
        nextRender = Time.unscaledTime + refreshInterval;
        if (mapCamera != null) mapCamera.Render();
    }

    // World position -> local position in the marker area (anchored at its centre).
    public Vector2 MapPosition(Vector3 world, bool clamp = true)
    {
        EnsureMapCamera();
        Vector3 v = mapCamera.WorldToViewportPoint(world);
        if (clamp) { v.x = Mathf.Clamp(v.x, 0.02f, 0.98f); v.y = Mathf.Clamp(v.y, 0.02f, 0.98f); } // off-map players sit on the edge
        Rect r = markerArea.rect;
        return new Vector2((v.x - 0.5f) * r.width, (v.y - 0.5f) * r.height);
    }

    // ── Team list, markers and lines ───────────────────────────────────────

    void RefreshTeam()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerController local = NetGameManager.LocalPlayer;
        if (gm == null || local == null) return;
        Color colour = local.Team == 2 ? gm.BetaTeam : gm.AlphaTeam;

        team.Clear();
        foreach (PlayerController p in gm.Players)
            if (p != null && p.Team == local.Team) team.Add(p);
        team.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId));
        RefreshBeacons(local.Team, colour);
        RefreshSpawn(local, colour);

        for (int i = 0; i < entries.Length; i++)
        {
            Entry e = entries[i];
            PlayerController p = i < team.Count ? team[i] : null;
            shown[i] = p;
            bool present = p != null;

            e.marker.gameObject.SetActive(present);
            e.line.gameObject.SetActive(present);
            if (!present)
            {
                SetText(e.initial, "");
                SetText(e.playerName, "—");
                SetText(e.status, "");
                e.icon.color = new Color(0f, 0f, 0f, 0.35f);
                e.button.interactable = false;
                continue;
            }

            bool jumpable = !p.IsLocalPlayer && !p.IsDead;
            Color c = p.IsDead ? new Color(colour.r * deadDim, colour.g * deadDim, colour.b * deadDim, 1f) : colour;
            string name = string.IsNullOrEmpty(p.DisplayName) ? "?" : p.DisplayName;
            SetText(e.initial, name.Substring(0, 1).ToUpperInvariant());
            SetText(e.playerName, name);
            SetText(e.status, p.IsLocalPlayer ? "You" : p.IsDead ? "Splatted" : "Super Jump");
            e.status.color = jumpable ? colour : new Color(1f, 1f, 1f, 0.5f);
            e.icon.color = c;
            e.button.interactable = jumpable;

            // Marker at the player's position; the local one also shows facing, as seen on the map.
            RectTransform m = (RectTransform)e.marker.transform;
            m.anchoredPosition = MapPosition(p.transform.position);
            e.markerIcon.color = c;
            SetText(e.markerInitial, name.Substring(0, 1).ToUpperInvariant());
            e.marker.interactable = jumpable;
            bool hovered = jumpable && (e.entryHover.Hovered || e.markerHover.Hovered); // either end lights up both
            m.localScale = Vector3.one * (hovered ? hoverMarkerScale : 1f);
            if (hovered) m.SetAsLastSibling();
            e.facing.gameObject.SetActive(p.IsLocalPlayer);
            if (p.IsLocalPlayer)
            {
                Vector2 ahead = MapPosition(p.transform.position + p.transform.forward, false) - MapPosition(p.transform.position, false);
                e.facing.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(ahead.y, ahead.x) * Mathf.Rad2Deg - 90f); // the arrow points up at 0
            }

            UpdateLine(e, m, c, hovered, jumpable);
        }
    }

    void RefreshSpawn(PlayerController local, Color colour)
    {
        Entry e = spawnEntry;
        RectTransform m = (RectTransform)e.marker.transform;
        m.anchoredPosition = MapPosition(local.SpawnPosition);
        e.icon.color = colour;
        e.markerIcon.color = colour;
        e.status.color = colour;
        bool hovered = e.entryHover.Hovered || e.markerHover.Hovered;
        m.localScale = Vector3.one * (hovered ? hoverMarkerScale : 1f);
        UpdateLine(e, m, colour, hovered, true);
    }

    // Line from the entry to its marker, in the line layer's space.
    void UpdateLine(Entry e, RectTransform marker, Color c, bool hovered, bool jumpable)
    {
        Vector2 a = lineLayer.InverseTransformPoint(e.lineStart.position);
        Vector2 b = lineLayer.InverseTransformPoint(marker.position);
        Vector2 d = b - a;
        e.line.anchoredPosition = (a + b) * 0.5f;
        e.line.sizeDelta = new Vector2(d.magnitude, hovered ? hoverLineThickness : lineThickness);
        e.line.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        e.line.GetComponent<Graphic>().color = new Color(c.r, c.g, c.b, hovered ? 1f : jumpable ? 0.75f : 0.35f);
    }

    // A marker per beacon of our team, pooled from the template.
    void RefreshBeacons(int ourTeam, Color colour)
    {
        shownBeacons.Clear();
        foreach (SubDevice d in SubDevice.All)
            if (d is Beacon b && b != null && b.Team == ourTeam) shownBeacons.Add(b);

        while (beaconMarkers.Count < shownBeacons.Count)
        {
            Button marker = Instantiate(beaconMarkerTemplate, beaconMarkerTemplate.transform.parent);
            marker.transform.SetSiblingIndex(beaconMarkerTemplate.transform.GetSiblingIndex() + 1); // under player markers
            int index = beaconMarkers.Count;
            marker.onClick.AddListener(() => RequestBeaconJump(index));
            beaconMarkers.Add(marker);
        }
        for (int i = 0; i < beaconMarkers.Count; i++)
        {
            Button marker = beaconMarkers[i];
            bool used = i < shownBeacons.Count;
            if (marker.gameObject.activeSelf != used) marker.gameObject.SetActive(used);
            if (!used) continue;
            ((RectTransform)marker.transform).anchoredPosition = MapPosition(shownBeacons[i].JumpPosition);
            marker.transform.GetChild(0).GetComponent<Graphic>().color = colour;
        }
    }

    static void SetText(TMP_Text label, string text)
    {
        if (label.text != text) label.text = text;
    }

    // For tests.
    public Entry[] Entries => entries;
    public Entry SpawnEntry => spawnEntry;
    public IReadOnlyList<Button> BeaconMarkers => beaconMarkers;
    public IReadOnlyList<Beacon> ShownBeacons => shownBeacons;
    public Camera MapCamera => mapCamera;
    public RenderTexture MapTexture => mapTexture;
}
