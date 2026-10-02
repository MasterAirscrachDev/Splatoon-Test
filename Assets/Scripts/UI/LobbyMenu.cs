using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Placeholder front end until there's a hub: host a public lobby, or join one from the list.
// Shown while not in a lobby (never in dev mode). With autoHostInEditor, the editor hosts as soon
// as Steam is ready, skipping the menu.
public class LobbyMenu : MonoBehaviour
{
    [SerializeField] GameObject screen;
    [SerializeField] Button hostButton, refreshButton;
    [SerializeField] TMP_Text status;
    [SerializeField] LobbyRow rowTemplate;  // cloned per lobby, under the list content
    [SerializeField] GameObject emptyLabel; // shown when the list is empty
    [SerializeField] bool autoHostInEditor;
    [SerializeField] float autoRefreshInterval = 10f;

    readonly List<LobbyRow> rows = new List<LobbyRow>();
    bool busy, refreshing, wasReady, autoHosted;
    float nextRefresh;

    public bool ForceShow { get; set; } // tests: show regardless (dev mode always has a local player)
    public bool IsShown => screen.activeSelf;
    public bool EmptyShown => emptyLabel.activeSelf;
    public IReadOnlyList<LobbyRow> Rows => rows;
    public string Status => status.text;

    void Awake()
    {
        hostButton.onClick.AddListener(Host);
        refreshButton.onClick.AddListener(Refresh);
        rowTemplate.gameObject.SetActive(false);
        screen.SetActive(false);
    }

    void Update()
    {
        NetGameManager gm = NetGameManager.Instance;
        bool show = ForceShow || gm != null && !gm.InLobby && !gm.DevMode;
        if (screen.activeSelf != show)
        {
            screen.SetActive(show);
            if (show) GameCursor.Open(this, GameCursor.Use.Menu, hostButton.gameObject); else GameCursor.Close(this);
        }
        if (!show) return;

        if (ForceShow || gm == null) return; // tests drive the list directly

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
            if (autoHostInEditor && Application.isEditor && !autoHosted) { autoHosted = true; Host(); return; }
            Refresh();
        }
        else if (!busy && !refreshing && Time.unscaledTime >= nextRefresh) Refresh();
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
