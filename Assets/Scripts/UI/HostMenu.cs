using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// The pause menu's Host column (Esc, or G): reset the map, edit teams, toggle coverage percentages,
// fill our special (for testing), start the game, and change map (everyone in the lobby goes, see
// NetGameManager.SwitchMap). Everyone sees it; only the host can use it. During a match only "End
// match" is available. The team editor and map list open in place of both columns (PauseMenu
// owns the open/closed state, the cursor and input blocking).
public class HostMenu : MonoBehaviour
{
    [System.Serializable]
    public class SlotButton
    {
        public Button button;
        public Image background;
        public TMP_Text label;
    }

    [SerializeField] MatchHUD hud;
    [SerializeField] GameObject mainPanel, teamPanel;
    [SerializeField] Button resetButton, teamsButton, percentButton, fillSpecialButton, startButton, closeButton, teamsBackButton;
    [SerializeField] TMP_Text percentButtonLabel, startButtonLabel;

    [Header("Team editor columns")]
    [SerializeField] SlotButton[] alphaSlots = new SlotButton[4];
    [SerializeField] SlotButton[] betaSlots = new SlotButton[4];
    [SerializeField] SlotButton[] spectateSlots = new SlotButton[2];
    [SerializeField] SlotButton[] benchSlots = new SlotButton[2];
    [SerializeField] SlotButton[] gammaSlots = new SlotButton[4];
    [SerializeField] SlotButton[] deltaSlots = new SlotButton[4];
    [SerializeField] RectTransform[] columnHeaders;    // per column, as `columns`: laid out with them
    [SerializeField] float columnSpacing = 290f;
    [Header("Change map")]
    [SerializeField] Button mapsButton;
    [SerializeField] GameObject mapPanel;
    [SerializeField] Button mapButtonTemplate;   // cloned per scene in the build settings
    [SerializeField] Button mapsBackButton;

    [SerializeField] Color slotColour = new Color(0.2f, 0.2f, 0.26f, 1f);
    [SerializeField] Color selectedColour = new Color(1f, 0.82f, 0.25f, 1f);

    // Columns by index: 0 Alpha, 1 Beta, 2 Spectate, 3 (the old Not playing: unused, hidden), 4 Gamma,
    // 5 Delta; shown in DisplayOrder, teams the map hasn't got left out. Team columns offer the map's
    // team size; Spectate grows to fit everyone watching (plus a free slot), wrapping into more
    // columns past SpectateRows.
    static readonly PlayerRole[] ColumnRoles = { PlayerRole.Alpha, PlayerRole.Beta, PlayerRole.Spectator, PlayerRole.NotPlaying, PlayerRole.Gamma, PlayerRole.Delta };
    static readonly int[] DisplayOrder = { 0, 1, 4, 5, 2 };
    const int SpectateColumn = 2, UnusedColumn = 3, SpectateRows = 6;
    const float RowStep = 66f;
    SlotButton[][] columns;
    const ulong Empty = ulong.MaxValue; // not 0: that's the local player's id in dev mode
    ulong[][] layout;                   // player id per slot, mirrors `columns`
    int selectedColumn = -1, selectedSlot = -1;
    bool open;

    public bool IsOpen => open;
    public bool TeamEditorOpen => open && teamPanel.activeSelf;
    public bool MapPanelOpen => open && mapPanel != null && mapPanel.activeSelf;
    public Button MapsButton => mapsButton;
    readonly List<Button> mapButtons = new List<Button>();
    public IReadOnlyList<Button> MapButtons => mapButtons; // tests

    void Awake()
    {
        columns = new[] { alphaSlots, betaSlots, spectateSlots, benchSlots, gammaSlots ?? new SlotButton[0], deltaSlots ?? new SlotButton[0] };
        layout = new ulong[columns.Length][];
        for (int c = 0; c < columns.Length; c++)
        {
            layout[c] = new ulong[columns[c].Length];
            System.Array.Fill(layout[c], Empty);
            for (int s = 0; s < columns[c].Length; s++)
            {
                int col = c, slot = s; // captured per button
                columns[c][s].button.onClick.AddListener(() => OnSlotClicked(col, slot));
            }
        }

        resetButton.onClick.AddListener(() => { NetGameManager.Instance?.ResetMap(); SetOpen(false); });
        teamsButton.onClick.AddListener(OpenTeamEditor);
        percentButton.onClick.AddListener(TogglePercentages);
        fillSpecialButton.onClick.AddListener(() =>
        {
            if (PlayerLoadout.Local != null) PlayerLoadout.Local.SetSpecialPoints(float.MaxValue); // clamped to full
            SetOpen(false);
        });
        startButton.onClick.AddListener(() =>
        {
            NetGameManager gm = NetGameManager.Instance;
            if (gm == null) return;
            if (gm.InMatch) gm.EndMatch(); else gm.StartGame();
            SetOpen(false);
        });
        closeButton.onClick.AddListener(() => SetOpen(false));
        closeButton.gameObject.SetActive(false); // the pause menu's Resume closes it
        teamsBackButton.onClick.AddListener(BackToMenu);

        if (mapsButton != null) mapsButton.onClick.AddListener(OpenMaps);
        if (mapsBackButton != null) mapsBackButton.onClick.AddListener(BackToMenu);
        pause = transform.parent != null ? transform.parent.GetComponentInChildren<PauseMenu>(true) : null;
        if (mapButtonTemplate != null) mapButtonTemplate.gameObject.SetActive(false);
        if (mapPanel != null) mapPanel.SetActive(false);

        mainPanel.SetActive(false);
        teamPanel.SetActive(false);
        UpdatePercentLabel();
    }

    PauseMenu pause;

    void Update()
    {
        if (!open) return;
        RefreshMatchButtons();
        if (TeamEditorOpen) RefreshTeamEditor(); // reflect joins/leaves while open
    }

    // Opening and closing are the pause menu's (this is its Host column).
    public void Toggle() => SetOpen(!IsOpen);
    public void SetOpen(bool value)
    {
        if (pause != null) pause.SetOpen(value);
        else ShowColumn(value);
    }

    // PauseMenu: the column alongside its own (or none).
    public void ShowColumn(bool on)
    {
        open = on;
        mainPanel.SetActive(on);
        teamPanel.SetActive(false);
        if (mapPanel != null) mapPanel.SetActive(false);
        selectedColumn = selectedSlot = -1;
        if (on) RefreshMatchButtons();
    }

    // The team editor or map list is up (instead of the columns).
    public bool SubPanelOpen => open && (teamPanel.activeSelf || mapPanel != null && mapPanel.activeSelf);

    public void BackToMenu()
    {
        teamPanel.SetActive(false);
        if (mapPanel != null) mapPanel.SetActive(false);
        if (pause != null) pause.ShowMain(); else mainPanel.SetActive(true);
    }

    void ShowSubPanel(GameObject panel)
    {
        if (pause != null) pause.HideColumns();
        mainPanel.SetActive(false);
        panel.SetActive(true);
    }

    void RefreshMatchButtons()
    {
        NetGameManager gm = NetGameManager.Instance;
        bool host = gm != null && gm.IsHost;
        bool inMatch = gm != null && gm.InMatch;
        resetButton.interactable = teamsButton.interactable = percentButton.interactable = fillSpecialButton.interactable = host && !inMatch;
        bool canStart = gm != null && gm.CanStartMatch;
        string start = !host ? "Start game (host)" : inMatch ? "End match" : canStart ? "Start game" : "Change map to play";
        if (startButtonLabel.text != start) startButtonLabel.text = start;
        startButton.interactable = host && (inMatch || canStart);
        if ((inMatch || !host) && teamPanel.activeSelf) BackToMenu();
        bool canSwitch = host && !inMatch && gm.CanSwitchMap;
        if (mapsButton != null) mapsButton.interactable = canSwitch;
        if (!canSwitch && MapPanelOpen) BackToMenu();
    }

    // ── Change map ─────────────────────────────────────────────────────────
    // A button per scene in the build (the one we're in can't be picked): everyone goes there.
    public void OpenMaps()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (mapPanel == null || mapButtonTemplate == null || gm == null || !gm.CanSwitchMap || gm.InMatch) return;
        foreach (Button b in mapButtons) if (b != null) Destroy(b.gameObject);
        mapButtons.Clear();
        int current = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        var template = (RectTransform)mapButtonTemplate.transform;
        for (int i = 0; i < NetGameManager.SceneCount; i++)
        {
            Button b = Instantiate(mapButtonTemplate, template.parent);
            b.gameObject.SetActive(true);
            ((RectTransform)b.transform).anchoredPosition = template.anchoredPosition - new Vector2(0f, 66f * i);
            TMP_Text label = b.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = NetGameManager.SceneName(i) + (i == current ? "  (here)" : "");
            b.interactable = i != current;
            int index = i;
            b.onClick.AddListener(() => { NetGameManager.Instance?.SwitchMap(index); SetOpen(false); });
            mapButtons.Add(b);
        }
        ShowSubPanel(mapPanel);
    }

    void TogglePercentages()
    {
        if (hud != null) hud.ShowPercentages = !hud.ShowPercentages;
        UpdatePercentLabel();
    }

    void UpdatePercentLabel()
    {
        bool on = hud != null && hud.ShowPercentages;
        percentButtonLabel.text = on ? "Hide percentages" : "Show percentages";
    }

    // ── Team editor ────────────────────────────────────────────────────────
    void OpenTeamEditor()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm == null || !gm.IsHost) return;
        ShowSubPanel(teamPanel);
        selectedColumn = selectedSlot = -1;
        BuildLayoutFromRoster();
        RefreshTeamEditor();
    }

    bool ColumnUsed(int column) => column != UnusedColumn && (column == SpectateColumn || Teams.Of(ColumnRoles[column]) <= Teams.Count);
    int SlotsIn(int column) => column == SpectateColumn ? columns[column].Length : column == UnusedColumn ? 0 : Mathf.Min(columns[column].Length, Teams.SlotsPerTeam);
    float firstRowY;

    // Spectate's slots: at least `count`, cloned from its first (listeners and layout included).
    void EnsureSpectateSlots(int count)
    {
        SlotButton[] col = columns[SpectateColumn];
        if (col.Length >= count || col.Length == 0) return;
        var grown = new SlotButton[count];
        var ids = new ulong[count];
        System.Array.Fill(ids, Empty);
        for (int s = 0; s < count; s++)
        {
            if (s < col.Length) { grown[s] = col[s]; ids[s] = layout[SpectateColumn][s]; continue; }
            Button b = Instantiate(col[0].button, col[0].button.transform.parent);
            b.name = "SPECTATE_" + s;
            b.onClick.RemoveAllListeners();
            int slot = s;
            b.onClick.AddListener(() => OnSlotClicked(SpectateColumn, slot));
            grown[s] = new SlotButton { button = b, background = b.GetComponent<Image>(), label = b.GetComponentInChildren<TMP_Text>(true) };
        }
        columns[SpectateColumn] = grown;
        layout[SpectateColumn] = ids;
    }

    // The map's teams, then Spectate (as many columns as it needs), evenly spaced across the panel.
    void LayOutColumns()
    {
        if (firstRowY == 0f && columns[0].Length > 0) firstRowY = ((RectTransform)columns[0][0].button.transform).anchoredPosition.y;
        int spectateCols = Mathf.Max(1, Mathf.CeilToInt(columns[SpectateColumn].Length / (float)SpectateRows));
        var shown = new List<int>();
        foreach (int c in DisplayOrder) if (c < columns.Length && columns[c].Length > 0 && ColumnUsed(c)) shown.Add(c);
        int total = shown.Count - 1 + spectateCols;
        float X(int display) => (display - (total - 1) * 0.5f) * columnSpacing;
        for (int c = 0; c < columns.Length; c++)
        {
            int at = shown.IndexOf(c);
            int slots = at >= 0 ? SlotsIn(c) : 0;
            for (int s = 0; s < columns[c].Length; s++)
            {
                SlotButton b = columns[c][s];
                if (b?.button == null) continue;
                bool on = s < slots;
                b.button.gameObject.SetActive(on);
                if (!on) continue;
                int sub = c == SpectateColumn ? s / SpectateRows : 0, row = c == SpectateColumn ? s % SpectateRows : s;
                ((RectTransform)b.button.transform).anchoredPosition = new Vector2(X(at + sub), firstRowY - RowStep * row);
            }
            if (columnHeaders != null && c < columnHeaders.Length && columnHeaders[c] != null)
            {
                columnHeaders[c].gameObject.SetActive(at >= 0);
                if (at >= 0) columnHeaders[c].anchoredPosition = new Vector2(c == SpectateColumn ? (X(at) + X(at + spectateCols - 1)) * 0.5f : X(at), columnHeaders[c].anchoredPosition.y);
            }
        }
    }

    // Places every player in their role's column (in id order); anyone without a team slot here
    // (spectating, on a team this map hasn't got, or past its size) spectates.
    void BuildLayoutFromRoster()
    {
        var ordered = new List<PlayerController>();
        foreach (PlayerController p in NetGameManager.Instance.Players) if (p != null) ordered.Add(p);
        ordered.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId));
        EnsureSpectateSlots(ordered.Count + 1);
        foreach (ulong[] col in layout) System.Array.Fill(col, Empty);
        foreach (PlayerController p in ordered)
        {
            int column = System.Array.IndexOf(ColumnRoles, p.Role);
            if (column == UnusedColumn || column >= 0 && !ColumnUsed(column)) column = SpectateColumn;
            if (!TryPlace(column, p.OwnerId))
                TryPlace(SpectateColumn, p.OwnerId);
        }
        LayOutColumns();
    }

    bool TryPlace(int column, ulong id)
    {
        if (column < 0) return false;
        int slots = column == SpectateColumn ? layout[column].Length : SlotsIn(column);
        for (int s = 0; s < slots; s++)
            if (layout[column][s] == Empty && !IsPlaced(id)) { layout[column][s] = id; return true; }
        return false;
    }

    bool IsPlaced(ulong id)
    {
        foreach (ulong[] col in layout) foreach (ulong v in col) if (v == id) return true;
        return false;
    }

    // Click a player, then any slot: moves them there, swapping with whoever was in it.
    void OnSlotClicked(int column, int slot)
    {
        if (selectedColumn < 0)
        {
            if (NetGameManager.Instance.GetPlayer(layout[column][slot]) == null) return; // nothing to pick up
            selectedColumn = column;
            selectedSlot = slot;
        }
        else
        {
            (layout[column][slot], layout[selectedColumn][selectedSlot]) = (layout[selectedColumn][selectedSlot], layout[column][slot]);
            selectedColumn = selectedSlot = -1;
            ApplyLayout();
            int watching = 0;
            foreach (ulong v in layout[SpectateColumn]) if (v != Empty) watching++;
            if (watching + 1 > layout[SpectateColumn].Length) { EnsureSpectateSlots(watching + 1); LayOutColumns(); } // always a free slot
        }
        RefreshTeamEditor();
    }

    void ApplyLayout()
    {
        var ids = new List<ulong>();
        var roles = new List<PlayerRole>();
        for (int c = 0; c < layout.Length; c++)
            foreach (ulong id in layout[c])
                if (NetGameManager.Instance.GetPlayer(id) != null) { ids.Add(id); roles.Add(ColumnRoles[c]); }
        NetGameManager.Instance.SetRoster(ids.ToArray(), roles.ToArray());
    }

    void RefreshTeamEditor()
    {
        NetGameManager gm = NetGameManager.Instance;
        for (int c = 0; c < columns.Length; c++)
            for (int s = 0; s < columns[c].Length; s++)
            {
                PlayerController p = gm.GetPlayer(layout[c][s]);
                if (p == null) layout[c][s] = Empty; // left the lobby
                SlotButton b = columns[c][s];
                string text = p == null ? "—" : (p.IsLocalPlayer ? $"{p.DisplayName} (you)" : p.DisplayName);
                if (b.label.text != text) b.label.text = text;
                b.background.color = c == selectedColumn && s == selectedSlot ? selectedColour : slotColour;
            }
    }

    public static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        DontDestroyOnLoad(es);
    }

    // For tests: the team editor's slot buttons, by column (Alpha, Beta, Spectate, unused, Gamma, Delta).
    public SlotButton[][] Columns => columns;
    public Button TeamsButton => teamsButton;
    public Button PercentButton => percentButton;
    public Button FillSpecialButton => fillSpecialButton;
    public Button ResetButton => resetButton;
    public Button StartButton => startButton;
    public string StartButtonText => startButtonLabel.text;
    public Button CloseButton => closeButton;
}
