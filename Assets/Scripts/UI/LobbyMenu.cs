using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The online menu, opened at the lobby scene's kiosk (LobbyKiosk) and closed by walking away or
// Cancel (never in dev mode). On our own: host a public lobby, or join one from the list. In a
// lobby: who it belongs to, and leaving it (back to the lobby scene, alone).
public class LobbyMenu : MonoBehaviour
{
    [SerializeField] GameObject screen;
    [SerializeField] Button hostButton, refreshButton;
    [SerializeField] TMP_Text status;
    [SerializeField] LobbyRow rowTemplate;  // cloned per lobby, under the list content
    [SerializeField] GameObject emptyLabel; // shown when the list is empty
    [SerializeField] float autoRefreshInterval = 10f;

    readonly List<LobbyRow> rows = new List<LobbyRow>();
    bool busy, refreshing, wasReady, opened;
    float nextRefresh;
    ControlLayer input; // Cancel closes it
    TMP_Text hostLabel;

    public bool ForceShow { get; set; } // tests: show regardless (dev mode always has a local player)
    public bool IsShown => screen.activeSelf;
    public bool IsOpen => opened;
    public string HostButtonText => hostLabel != null ? hostLabel.text : "";

    public void Open() => opened = true;
    public void Close() => opened = false;
    public bool EmptyShown => emptyLabel.activeSelf;
    public IReadOnlyList<LobbyRow> Rows => rows;
    public string Status => status.text;

    void Awake()
    {
        hostButton.onClick.AddListener(HostOrLeave);
        refreshButton.onClick.AddListener(Refresh);
        hostLabel = hostButton.GetComponentInChildren<TMP_Text>(true);
        rowTemplate.gameObject.SetActive(false);
        screen.SetActive(false);
        input = new ControlLayer();
        input.GameControl.Enable();
    }

    void OnDestroy()
    {
        GameCursor.Close(this);
        input?.Dispose();
    }

    void Update()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (opened && input.GameControl.Cancel.WasPressedThisFrame()) opened = false;
        bool show = ForceShow || opened && gm != null && !gm.DevMode;
        if (screen.activeSelf != show)
        {
            screen.SetActive(show);
            if (show) GameCursor.Open(this, GameCursor.Use.Menu, hostButton.gameObject); else GameCursor.Close(this);
        }
        if (!show) return;

        if (ForceShow || gm == null) return; // tests drive the list directly

        if (gm.InLobby) { ShowOwnLobby(); return; }
        refreshButton.gameObject.SetActive(true);
        SetHostLabel("Host");

        bool ready = gm.Steam == NetGameManager.SteamStatus.Ready;
        hostButton.interactable = refreshButton.interactable = ready && !busy;
        if (!ready)
        {
            SetStatus(gm.Steam == NetGameManager.SteamStatus.Connecting ? "Connecting to Steam…" : "Steam isn't available: start Steam and restart to host or join.");
            return;
        }

        if (!wasReady)
        {
            wasReady = true;
            Refresh();
        }
        else if (!busy && !refreshing && Time.unscaledTime >= nextRefresh) Refresh();
    }

    // In a lobby: whose it is, and a way out (the list is for finding one).
    void ShowOwnLobby()
    {
        foreach (LobbyRow row in rows) row.gameObject.SetActive(false);
        emptyLabel.SetActive(false);
        refreshButton.gameObject.SetActive(false);
        hostButton.interactable = true;
        SetHostLabel("Leave lobby");
        string host = SteamGlobal.thisLobby.GetData("host");
        int count = NetGameManager.Instance.Players.Count;
        SetStatus($"In {(SteamGlobal.isHost ? "your" : string.IsNullOrEmpty(host) ? "a" : host + "'s")} lobby: {count} player{(count == 1 ? "" : "s")}.");
    }

    void SetHostLabel(string text)
    {
        if (hostLabel != null && hostLabel.text != text) hostLabel.text = text;
    }

    void HostOrLeave()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null && gm.InLobby) { opened = false; gm.LeaveLobby(); return; }
        Host();
    }

    async void Host()
    {
        if (busy) return;
        busy = true;
        SetStatus("Creating lobby…");
        bool ok = await NetGameManager.Instance.HostLobby();
        busy = false;
        if (!ok) SetStatus("Couldn't create a lobby.");
    }

    async void Join(NetGameManager.LobbyInfo lobby)
    {
        if (busy) return;
        busy = true;
        SetStatus($"Joining {lobby.host}…");
        bool ok = await NetGameManager.Instance.JoinLobby(lobby.id);
        busy = false;
        if (!ok) { SetStatus($"Couldn't join {lobby.host}."); Refresh(); }
    }

    async void Refresh()
    {
        if (refreshing || busy) return;
        refreshing = true;
        nextRefresh = Time.unscaledTime + autoRefreshInterval;
        SetStatus("Searching for lobbies…");
        List<NetGameManager.LobbyInfo> lobbies = await NetGameManager.Instance.FindLobbies();
        refreshing = false;
        if (this == null || busy) return; // destroyed, or a host/join started meanwhile
        ShowLobbies(lobbies);
        SetStatus(lobbies.Count == 0 ? "No lobbies found." : lobbies.Count == 1 ? "1 lobby found." : $"{lobbies.Count} lobbies found.");
    }

    public void ShowLobbies(IList<NetGameManager.LobbyInfo> lobbies)
    {
        while (rows.Count < lobbies.Count)
            rows.Add(Instantiate(rowTemplate, rowTemplate.transform.parent));
        for (int i = 0; i < rows.Count; i++)
        {
            LobbyRow row = rows[i];
            bool used = i < lobbies.Count;
            row.gameObject.SetActive(used);
            if (!used) continue;
            NetGameManager.LobbyInfo lobby = lobbies[i];
            row.hostName.text = lobby.host;
            row.players.text = $"{lobby.players}/{lobby.maxPlayers}";
            row.join.onClick.RemoveAllListeners();
            row.join.onClick.AddListener(() => Join(lobby));
        }
        emptyLabel.SetActive(lobbies.Count == 0);
    }

    public void SetStatus(string text)
    {
        if (status.text != text) status.text = text;
    }
}
