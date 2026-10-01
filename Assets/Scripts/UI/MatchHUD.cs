using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Match HUD: up to 4 players per team either side of the match timer, and a turf bar showing
// each team's coverage with the unclaimed share in the middle. Drop the prefab into any map.
public class MatchHUD : MonoBehaviour
{
    [System.Serializable]
    public class PlayerSlot
    {
        public Image icon;
        public TMP_Text initial;
        public TMP_Text playerName;
        public GameObject deadMark;
        public Outline localMark;      // highlights the local player
        public RectTransform specialMark; // ring shown while their special is charged
    }

    [SerializeField] TMP_Text timerText;
    [SerializeField] PlayerSlot[] alphaSlots = new PlayerSlot[4];
    [SerializeField] PlayerSlot[] betaSlots = new PlayerSlot[4];

    [Header("Turf bar")]
    [SerializeField] RectTransform alphaFill, betaFill;
    [SerializeField] RawImage alphaWave, betaWave;        // wavy leading edges; complementary so they interlock
    [SerializeField] float waveScroll = 0.3f;             // wave periods per second, drifting along the edge
    [SerializeField] GameObject percentLabels;           // hidden unless toggled from the host menu
    [SerializeField] TMP_Text alphaPercent, betaPercent, neutralPercent;
    [SerializeField] bool topDownScoring = true; // floors only, like the real game's turf count
    [SerializeField] float scoreInterval = 1f;   // seconds between coverage readbacks
    [SerializeField] float barSmoothing = 6f;

    [Header("Look")]
    [SerializeField] Color emptySlotColor = new Color(0f, 0f, 0f, 0.35f);
    [SerializeField] Color timerWarningColor = new Color(1f, 0.35f, 0.3f);
    [SerializeField] float deadDim = 0.35f;

    readonly List<PlayerController> alphaPlayers = new List<PlayerController>();
    readonly List<PlayerController> betaPlayers = new List<PlayerController>();
    float alphaShare, betaShare;   // latest coverage, 0..1
    float shownAlpha, shownBeta;   // eased toward the latest values
    float nextScoreTime, scoreRequestedAt = -1f;
    int shownSeconds = -1;
    int shownAlphaTenths = -1, shownBetaTenths = -1, shownNeutralTenths = -1;
    bool coloursApplied;
    Rect alphaWaveUV, betaWaveUV;

    void Awake()
    {
        alphaWaveUV = alphaWave.uvRect;
        betaWaveUV = betaWave.uvRect;
    }

    void Update()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm == null) return;
        if (!coloursApplied) ApplyTeamColours(gm);
        UpdateTimer(gm);
        UpdateRosters(gm);
        UpdateCoverage(gm);
    }

    void ApplyTeamColours(NetGameManager gm)
    {
        alphaFill.GetComponent<Image>().color = gm.AlphaTeam;
        betaFill.GetComponent<Image>().color = gm.BetaTeam;
        alphaWave.color = gm.AlphaTeam;
        betaWave.color = gm.BetaTeam;
        coloursApplied = true;
    }

    void UpdateTimer(NetGameManager gm)
    {
        int seconds = Mathf.CeilToInt(gm.MatchTimeRemaining);
        if (seconds == shownSeconds) return;
        shownSeconds = seconds;
        timerText.text = $"{seconds / 60}:{seconds % 60:00}";
        timerText.color = gm.Phase == MatchPhase.Playing && seconds <= 10 ? timerWarningColor : Color.white;
    }

    void UpdateRosters(NetGameManager gm)
    {
        alphaPlayers.Clear();
        betaPlayers.Clear();
        foreach (PlayerController p in gm.Players)
        {
            if (p == null) continue;
            if (p.Team == 1) alphaPlayers.Add(p);
            else if (p.Team == 2) betaPlayers.Add(p);
        }
        alphaPlayers.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId)); // stable order on every client
        betaPlayers.Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId));
        FillSlots(alphaSlots, alphaPlayers, gm.AlphaTeam);
        FillSlots(betaSlots, betaPlayers, gm.BetaTeam);
    }

    void FillSlots(PlayerSlot[] slots, List<PlayerController> team, Color colour)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            PlayerSlot slot = slots[i];
            PlayerController p = i < team.Count ? team[i] : null;
            if (p == null)
            {
                slot.icon.color = emptySlotColor;
                SetText(slot.initial, "");
                SetText(slot.playerName, "");
                slot.deadMark.SetActive(false);
                slot.localMark.enabled = false;
                slot.specialMark.gameObject.SetActive(false);
                continue;
            }

            Color c = p.IsDead ? new Color(colour.r * deadDim, colour.g * deadDim, colour.b * deadDim, 1f) : colour;
            slot.icon.color = c;
            string name = string.IsNullOrEmpty(p.DisplayName) ? "?" : p.DisplayName;
            SetText(slot.initial, name.Substring(0, 1).ToUpperInvariant());
            SetText(slot.playerName, name);
            slot.deadMark.SetActive(p.IsDead);
            slot.localMark.enabled = p.IsLocalPlayer;
            bool special = p.SpecialCharged && !p.IsDead;
            if (slot.specialMark.gameObject.activeSelf != special) slot.specialMark.gameObject.SetActive(special);
            if (special) slot.specialMark.localScale = Vector3.one * (1f + 0.07f * Mathf.Sin(Time.time * 7f)); // same pulse as the gauge
        }
    }

    void UpdateCoverage(NetGameManager gm)
    {
        bool waiting = scoreRequestedAt >= 0f && Time.time - scoreRequestedAt < 3f; // give up on a lost readback
        if (!waiting && Time.time >= nextScoreTime)
        {
            scoreRequestedAt = Time.time;
            nextScoreTime = Time.time + scoreInterval;
            gm.RequestScores(topDownScoring, total =>
            {
                float sum = total.x + total.y + total.z;
                if (sum > 0f) { alphaShare = total.x / sum; betaShare = total.y / sum; }
                scoreRequestedAt = -1f;
            });
        }

        float k = 1f - Mathf.Exp(-barSmoothing * Time.deltaTime);
        shownAlpha = Mathf.Lerp(shownAlpha, alphaShare, k);
        shownBeta  = Mathf.Lerp(shownBeta, betaShare, k);
        alphaFill.anchorMax = new Vector2(shownAlpha, 1f);
        betaFill.anchorMin  = new Vector2(1f - shownBeta, 0f);
        alphaFill.gameObject.SetActive(alphaFill.rect.width > 0f); // fills are inset by half the wave, so this goes negative near 0%
        betaFill.gameObject.SetActive(betaFill.rect.width > 0f);
        float drift = Time.time * waveScroll % 1f; // same drift on both keeps them interlocked
        alphaWave.uvRect = new Rect(alphaWaveUV.x, alphaWaveUV.y + drift, alphaWaveUV.width, alphaWaveUV.height);
        betaWave.uvRect = new Rect(betaWaveUV.x, betaWaveUV.y + drift, betaWaveUV.width, betaWaveUV.height);

        // Labels show the latest values (not the eased bar) and only rebuild when they change.
        SetPercent(alphaPercent, alphaShare, ref shownAlphaTenths);
        SetPercent(betaPercent, betaShare, ref shownBetaTenths);
        SetPercent(neutralPercent, Mathf.Max(0f, 1f - alphaShare - betaShare), ref shownNeutralTenths);
    }

    static void SetPercent(TMP_Text label, float share, ref int shownTenths)
    {
        int tenths = Mathf.RoundToInt(share * 1000f);
        if (tenths == shownTenths) return;
        shownTenths = tenths;
        label.text = $"{tenths / 10}.{tenths % 10}%";
    }

    static void SetText(TMP_Text label, string text)
    {
        if (label.text != text) label.text = text;
    }

    public bool ShowPercentages
    {
        get => percentLabels.activeSelf;
        set => percentLabels.SetActive(value);
    }

    // Read-only views for tests.
    public string TimerText => timerText.text;
    public PlayerSlot[] AlphaSlots => alphaSlots;
    public PlayerSlot[] BetaSlots => betaSlots;
    public float AlphaShare => alphaShare;
    public float BetaShare => betaShare;
    public float ShownAlphaFill => alphaFill.anchorMax.x;
}
