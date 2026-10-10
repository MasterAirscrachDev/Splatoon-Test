using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Shown to the local player while splatted: who got them, with what (or that they fell out of
// bounds), and the time left until they're back at spawn. Fades in on death and out on respawn.
public class DeathPopup : MonoBehaviour
{
    [SerializeField] CanvasGroup group;
    [SerializeField] TextMeshProUGUI header, killer, cause, countdown;
    [SerializeField] Image accent;          // stripe in the killer's team colour

    const float FadeTime = 0.25f;
    static readonly Color NoKiller = new Color(0.75f, 0.75f, 0.8f);

    PlayerController victim;
    ulong killerId;
    bool killerFound;

    public bool Shown => victim != null;                                   // tests
    public string Message => $"{header.text} {killer.text} {cause.text}".Trim();

    void Awake() => group.alpha = 0f;
    void OnEnable() => PlayerController.Splatted += OnSplatted;
    void OnDisable() => PlayerController.Splatted -= OnSplatted;

    void OnSplatted(PlayerController who, ulong by, string with)
    {
        if (who != NetGameManager.LocalPlayer) return;
        victim = who;
        killerId = by;
        killerFound = false;
        header.text = by != 0 ? "SPLATTED BY" : "SPLATTED";
        cause.text = string.IsNullOrEmpty(with) ? "" : by != 0 ? $"with {with}" : with;
        killer.gameObject.SetActive(by != 0);
        ShowKiller();
    }

    // Their name in their team colour; retried while shown, in case they weren't known yet.
    void ShowKiller()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerController by = killerId != 0 && gm != null ? gm.GetPlayer(killerId) : null;
        killerFound = by != null;
        Color team = by == null || gm == null ? NoKiller : gm.TeamColour(by.Team);
        killer.text = killerId == 0 ? "" : by != null && !string.IsNullOrEmpty(by.DisplayName) ? by.DisplayName : "Someone";
        killer.color = team;
        accent.color = team;
    }

    void Update()
    {
        if (victim != null && !victim.IsDead) victim = null; // back at spawn
        bool show = victim != null;
        group.alpha = Mathf.MoveTowards(group.alpha, show ? 1f : 0f, Time.unscaledDeltaTime / FadeTime);
        if (!show) return;
        if (killerId != 0 && !killerFound) ShowKiller();
        countdown.text = $"Respawning in {Mathf.CeilToInt(victim.RespawnIn)}";
    }
}
