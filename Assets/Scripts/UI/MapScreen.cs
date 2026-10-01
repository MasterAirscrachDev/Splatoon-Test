using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Hold Tab: live view of the level from a raised, rotated angle, with your team and your spawn
// listed on the left and a line from each entry to its marker, plus your team's beacons. Clicking
// a teammate, the spawn (entry or marker) or a beacon Super Jumps there.
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
        SetOpen(true);
    }

    public void SetOpen(bool value)
    {
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
        PlayerController local = NetGameManager.LocalPlayer;
        if (local == null || !local.StartSuperJump(target)) return;
        SuperJumpRequested?.Invoke(target);
        SetOpen(false);
        waitForRelease = true; // don't reopen while the button is still held
    }

    // ── Map rendering ──────────────────────────────────────────────────────

    // Orthographic camera looking down at viewPitch, turned by viewYaw, framed on every map
    // renderer as seen from that angle. Only map layers are drawn, over a transparent background.
    void EnsureMapCamera()
    {
        if (mapCamera != null) return;
        int mask = LayerMask.GetMask(mapLayers);
        var go = new GameObject("MapCamera");
        go.transform.rotation = Quaternion.Euler(viewPitch, viewYaw, 0f);

        Vector3 min = Vector3.one * float.MaxValue, max = -min;
        foreach (MeshRenderer r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (!r.enabled || (mask & (1 << r.gameObject.layer)) == 0) continue;
            Bounds b = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3(i % 2 == 0 ? b.min.x : b.max.x, i / 2 % 2 == 0 ? b.min.y : b.max.y, i / 4 == 0 ? b.min.z : b.max.z);
                Vector3 local = go.transform.InverseTransformPoint(corner); // camera space
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
        }
        if (min.x > max.x) { min = -Vector3.one * 25f; max = Vector3.one * 25f; } // no map geometry

        Rect area = markerArea.rect;
        float aspect = area.width / area.height;
        mapTexture = new RenderTexture(mapResolution, Mathf.RoundToInt(mapResolution / aspect), 16, RenderTextureFormat.ARGB32) { name = "MapView" };
        mapImage.texture = mapTexture;

        mapCamera = go.AddComponent<Camera>();
        mapCamera.enabled = false; // rendered on demand
        mapCamera.orthographic = true;
        mapCamera.aspect = aspect;
        mapCamera.orthographicSize = Mathf.Max((max.y - min.y) / 2f, (max.x - min.x) / 2f / aspect) * viewPadding;
        mapCamera.clearFlags = CameraClearFlags.SolidColor;
        mapCamera.backgroundColor = Color.clear;
        mapCamera.cullingMask = mask;
        // Centred on the level, backed off in front of its nearest point.
        go.transform.position = go.transform.TransformPoint(new Vector3((min.x + max.x) / 2f, (min.y + max.y) / 2f, min.z - 10f));
        mapCamera.nearClipPlane = 0.1f;
        mapCamera.farClipPlane = max.z - min.z + 20f;
        mapCamera.targetTexture = mapTexture;
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
        foreach (Beacon b in Beacon.All)
            if (b != null && b.Team == ourTeam) shownBeacons.Add(b);

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
