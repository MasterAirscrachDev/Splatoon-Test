using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// Host-only menu (G): reset the map, edit teams, toggle coverage percentages, fill our special
// (for testing), start the game.
// During a match only "End match" is available.
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
    [SerializeField] Color slotColour = new Color(0.2f, 0.2f, 0.26f, 1f);
    [SerializeField] Color selectedColour = new Color(1f, 0.82f, 0.25f, 1f);

    static readonly PlayerRole[] ColumnRoles = { PlayerRole.Alpha, PlayerRole.Beta, PlayerRole.Spectator, PlayerRole.NotPlaying };
    SlotButton[][] columns;
    const ulong Empty = ulong.MaxValue; // not 0: that's the local player's id in dev mode
    ulong[][] layout;                   // player id per slot, mirrors `columns`
    int selectedColumn = -1, selectedSlot = -1;
    bool open;

    public bool IsOpen => open;
    public bool TeamEditorOpen => open && teamPanel.activeSelf;

    void Awake()
    {
        columns = new[] { alphaSlots, betaSlots, spectateSlots, benchSlots };
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
        teamsBackButton.onClick.AddListener(() => { teamPanel.SetActive(false); mainPanel.SetActive(true); });

        mainPanel.SetActive(false);
        teamPanel.SetActive(false);
        UpdatePercentLabel();
    }

    void OnEnable()  => NetGameManager.HostMenuToggled += Toggle;
    void OnDisable() => NetGameManager.HostMenuToggled -= Toggle;

    void Update()
    {
        if (!open) return;
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) SetOpen(false);
        RefreshMatchButtons();
        if (TeamEditorOpen) RefreshTeamEditor(); // reflect joins/leaves while open
    }

    public void Toggle() => SetOpen(!open);

    public void SetOpen(bool value)
    {
        NetGameManager gm = NetGameManager.Instance;
        if (value && (gm == null || !gm.IsHost)) return;
        if (value && !open && InputGate.Blocked) return; // the map is open
        open = value;
        mainPanel.SetActive(value);
        teamPanel.SetActive(false);
        selectedColumn = selectedSlot = -1;
        InputGate.Blocked = value;
        Cursor.lockState = value ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = value;
        if (value) EnsureEventSystem();
    }

    void RefreshMatchButtons()
    {
        NetGameManager gm = NetGameManager.Instance;
        bool inMatch = gm != null && gm.InMatch;
        resetButton.interactable = teamsButton.interactable = percentButton.interactable = fillSpecialButton.interactable = !inMatch;
        string start = inMatch ? "End match" : "Start game";
        if (startButtonLabel.text != start) startButtonLabel.text = start;
        if (inMatch && teamPanel.activeSelf) { teamPanel.SetActive(false); mainPanel.SetActive(true); }
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
        mainPanel.SetActive(false);
        teamPanel.SetActive(true);
        selectedColumn = selectedSlot = -1;
        BuildLayoutFromRoster();
        RefreshTeamEditor();
    }

    // Places every player in their role's column (in id order), overflowing into Not playing.
    void BuildLayoutFromRoster()
    {
        foreach (ulong[] col in layout) System.Array.Fill(col, Empty);
        var ordered = new List<PlayerController>();
        foreach (PlayerController p in NetGameManager.Instance.Players) if (p != null) ordered.Add(p);
        ordered.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId));
        foreach (PlayerController p in ordered)
            if (!TryPlace(System.Array.IndexOf(ColumnRoles, p.Role), p.OwnerId))
                TryPlace(3, p.OwnerId);
    }

    bool TryPlace(int column, ulong id)
    {
        if (column < 0) return false;
        for (int s = 0; s < layout[column].Length; s++)
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

    // For tests: the team editor's slot buttons, by column (Alpha, Beta, Spectate, Not playing).
    public SlotButton[][] Columns => columns;
    public Button TeamsButton => teamsButton;
    public Button PercentButton => percentButton;
    public Button FillSpecialButton => fillSpecialButton;
    public Button ResetButton => resetButton;
    public Button StartButton => startButton;
    public string StartButtonText => startButtonLabel.text;
    public Button CloseButton => closeButton;
}
