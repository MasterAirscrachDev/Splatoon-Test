using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Match HUD, top of the screen: the players grouped into their teams (in the bar's order: Alpha,
// Gamma, Delta, Beta; as many as the map has, of the map's team size), the turf bar under them, and
// the match timer under that (moving up into the bar's place while the bar's hidden). The bar shows
// each team's coverage: Alpha from the left, Beta from the right, Gamma and Delta between, with the
// unclaimed share as gaps between them (see BarLayout). Drop the prefab into any map.
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
    [SerializeField] PlayerSlot[] gammaSlots = new PlayerSlot[4];
    [SerializeField] PlayerSlot[] deltaSlots = new PlayerSlot[4];
    [SerializeField] GameObject gammaRow, deltaRow;       // hidden on maps without those teams

    [Header("Turf bar")]
    [SerializeField] RectTransform alphaFill, betaFill;
    [SerializeField] RectTransform gammaFill, deltaFill;  // after Alpha's, before Beta's
    [SerializeField] RawImage alphaWave, betaWave;        // wavy leading edges; complementary so they interlock
    [SerializeField] float waveScroll = 0.3f;             // wave periods per second, drifting along the edge
    [SerializeField] GameObject percentLabels;           // hidden unless toggled from the host menu, and never in a match
    [SerializeField] CanvasGroup turfBarGroup;           // fades out for the final stretch ("who's winning?")
    [SerializeField] float barFadeSpeed = 1.5f;          // alpha per second
    [SerializeField] TMP_Text alphaPercent, betaPercent, neutralPercent;
    [SerializeField] TMP_Text gammaPercent, deltaPercent;
    [SerializeField] bool topDownScoring = true; // floors only, like the real game's turf count
    [SerializeField] float scoreInterval = 1f;   // seconds between coverage readbacks
    [SerializeField] float barSmoothing = 6f;

    [Header("Look")]
    [SerializeField] Color emptySlotColor = new Color(0f, 0f, 0f, 0.35f);
    [SerializeField] Color timerWarningColor = new Color(1f, 0.35f, 0.3f);
    [SerializeField] float deadDim = 0.35f;

    readonly List<PlayerController>[] teamPlayers = { null, new List<PlayerController>(), new List<PlayerController>(), new List<PlayerController>(), new List<PlayerController>() };
    readonly float[] share = new float[Teams.Max + 1];   // latest coverage per team, 0..1
    readonly float[] shown = new float[Teams.Max + 1];   // eased toward the latest values
    float alphaShare => share[1];
    float betaShare => share[2];
    float nextScoreTime, scoreRequestedAt = -1f;
    int shownSeconds = -1;
    readonly int[] shownTenths = { -1, -1, -1, -1, -1 }; // [0] neutral
    bool coloursApplied;

    void OnEnable() => NetGameManager.TeamColoursChanged += RecolourLater;
    void OnDisable() => NetGameManager.TeamColoursChanged -= RecolourLater;
    void RecolourLater() => coloursApplied = false;
    public Color AlphaBarColour => alphaFill.GetComponent<Image>().color; // tests
    Rect alphaWaveUV, betaWaveUV;
    RawImage[][] middleWaves;   // Gamma's and Delta's: a wavy edge each side
    Rect[][] middleWaveUV;

    void Awake()
    {
        alphaWaveUV = alphaWave.uvRect;
        betaWaveUV = betaWave.uvRect;
        middleWaves = new RawImage[2][];
        middleWaveUV = new Rect[2][];
        for (int i = 0; i < 2; i++)
        {
            RectTransform fill = i == 0 ? gammaFill : deltaFill;
            middleWaves[i] = fill != null ? fill.GetComponentsInChildren<RawImage>(true) : new RawImage[0];
            middleWaveUV[i] = System.Array.ConvertAll(middleWaves[i], w => w.uvRect);
        }
    }

    void Update()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm == null) return;
        if (!coloursApplied) ApplyTeamColours(gm);
        UpdateTimer(gm);
        UpdateRosters(gm);
        UpdateCoverage(gm);
        LayOutTop();

        bool barHidden = gm.InFinalStretch || gm.Phase == MatchPhase.TimesUp || gm.Phase == MatchPhase.Results;
        turfBarGroup.alpha = Mathf.MoveTowards(turfBarGroup.alpha, barHidden ? 0f : 1f, barFadeSpeed * Time.deltaTime);
        bool pct = percentagesWanted && !gm.InMatch;
        if (percentLabels.activeSelf != pct) percentLabels.SetActive(pct);
    }

    void ApplyTeamColours(NetGameManager gm)
    {
        alphaFill.GetComponent<Image>().color = gm.TeamColour(1);
        betaFill.GetComponent<Image>().color = gm.TeamColour(2);
        if (gammaFill != null) gammaFill.GetComponent<Image>().color = gm.TeamColour(3);
        if (deltaFill != null) deltaFill.GetComponent<Image>().color = gm.TeamColour(4);
        if (middleWaves != null)
            for (int i = 0; i < 2; i++) foreach (RawImage w in middleWaves[i]) w.color = gm.TeamColour(3 + i);
        alphaWave.color = gm.TeamColour(1);
        betaWave.color = gm.TeamColour(2);
        coloursApplied = true;
    }

    PlayerSlot[] SlotsFor(int team) => team switch { 1 => alphaSlots, 2 => betaSlots, 3 => gammaSlots, _ => deltaSlots };

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
        for (int t = 1; t <= Teams.Max; t++) teamPlayers[t].Clear();
        foreach (PlayerController p in gm.Players)
            if (p != null && Teams.Valid(p.Team)) teamPlayers[p.Team].Add(p);
        int count = Teams.Count;
        if (gammaRow != null && gammaRow.activeSelf != count >= 3) gammaRow.SetActive(count >= 3);
        if (deltaRow != null && deltaRow.activeSelf != count >= 4) deltaRow.SetActive(count >= 4);
        for (int t = 1; t <= count; t++)
        {
            PlayerSlot[] slots = SlotsFor(t);
            if (slots == null || slots.Length == 0 || slots[0] == null || slots[0].icon == null) continue;
            teamPlayers[t].Sort((a, b) => a.OwnerId.CompareTo(b.OwnerId)); // stable order on every client
            FillSlots(slots, teamPlayers[t], gm.TeamColour(t));
        }
    }

    readonly Dictionary<PlayerSlot, PlayerController> inSlot = new Dictionary<PlayerSlot, PlayerController>();
    public PlayerController PlayerIn(PlayerSlot slot) => slot != null && inSlot.TryGetValue(slot, out var p) ? p : null; // spectators pick from these
    public PlayerSlot[] SlotsOf(int team) => SlotsFor(team);

    void FillSlots(PlayerSlot[] slots, List<PlayerController> team, Color colour)
    {
        int size = Teams.SlotsPerTeam; // the map's team size: the rest of the row is hidden
        for (int i = 0; i < slots.Length; i++)
        {
            PlayerSlot slot = slots[i];
            inSlot[slot] = i < team.Count && i < size ? team[i] : null;
            bool used = i < size;
            if (slot.icon.gameObject.activeSelf != used) slot.icon.gameObject.SetActive(used);
            if (!used) continue;
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
                float sum = 0f;
                foreach (int n in total) sum += n;
                if (sum > 0f) for (int t = 1; t <= Teams.Max; t++) share[t] = t <= Teams.Count ? total[t] / sum : 0f;
                scoreRequestedAt = -1f;
            });
        }

        float k = 1f - Mathf.Exp(-barSmoothing * Time.deltaTime);
        for (int t = 1; t <= Teams.Max; t++) shown[t] = Mathf.Lerp(shown[t], share[t], k);
        alphaFill.anchorMax = new Vector2(shown[1], 1f);
        betaFill.anchorMin  = new Vector2(1f - shown[2], 0f);
        alphaFill.gameObject.SetActive(alphaFill.rect.width > 0f); // fills are inset by half the wave, so this goes negative near 0%
        betaFill.gameObject.SetActive(betaFill.rect.width > 0f);
        BarLayout(shown, Teams.Count, segLeft);
        PlaceSegment(gammaFill, segLeft[3], segLeft[3] + shown[3], Teams.Count >= 3);
        PlaceSegment(deltaFill, segLeft[4], segLeft[4] + shown[4], Teams.Count >= 4);
        float drift = Time.time * waveScroll % 1f; // same drift on both keeps them interlocked
        alphaWave.uvRect = new Rect(alphaWaveUV.x, alphaWaveUV.y + drift, alphaWaveUV.width, alphaWaveUV.height);
        betaWave.uvRect = new Rect(betaWaveUV.x, betaWaveUV.y + drift, betaWaveUV.width, betaWaveUV.height);
        for (int i = 0; i < 2; i++)
            for (int w = 0; w < middleWaves[i].Length; w++)
            {
                Rect uv = middleWaveUV[i][w];
                middleWaves[i][w].uvRect = new Rect(uv.x, uv.y + drift, uv.width, uv.height);
            }

        // Labels show the latest values (not the eased bar) and only rebuild when they change.
        SetPercent(alphaPercent, share[1], ref shownTenths[1]);
        SetPercent(betaPercent, share[2], ref shownTenths[2]);
        // A middle team's label rides its segment, and hides while it has no turf (it'd sit on a neighbour's).
        if (gammaPercent != null) { gammaPercent.gameObject.SetActive(Teams.Count >= 3 && share[3] >= 0.0005f); SetPercent(gammaPercent, share[3], ref shownTenths[3]); Follow(gammaPercent, segLeft[3] + shown[3] * 0.5f); }
        if (deltaPercent != null) { deltaPercent.gameObject.SetActive(Teams.Count >= 4 && share[4] >= 0.0005f); SetPercent(deltaPercent, share[4], ref shownTenths[4]); Follow(deltaPercent, segLeft[4] + shown[4] * 0.5f); }
        // The unclaimed share's label sits in the bar's widest gap (it's split between the gaps).
        float neutral = Mathf.Max(0f, 1f - share[1] - share[2] - share[3] - share[4]);
        neutralPercent.gameObject.SetActive(neutral >= 0.0005f);
        SetPercent(neutralPercent, neutral, ref shownTenths[0]);
        Follow(neutralPercent, WidestGapCentre(shown, Teams.Count, segLeft));
    }

    readonly float[] segLeft = new float[Teams.Max + 1];

    // Where each team's segment starts (0..1 along the bar), given their shares: Alpha fills from
    // the left and Beta from the right. With three teams Gamma sits in the middle, growing outwards;
    // with four, Gamma and Delta sit at a third and two thirds. The unclaimed share is the gaps
    // between neighbours, and each gap keeps at least a fair part of it (half an equal split), so the
    // fills push the middle teams along but they only touch when the whole map is covered.
    public static void BarLayout(float[] share, int count, float[] left)
    {
        float a = share[1], b = share[2];
        left[1] = 0f;
        left[2] = 1f - b;
        left[3] = left[4] = 0f;
        if (count == 3)
        {
            float g = share[3];
            float gap = Mathf.Max(0f, 1f - a - b - g) / 4f;            // 2 gaps: each at least half of an equal split
            left[3] = Mathf.Clamp(0.5f - g * 0.5f, a + gap, Mathf.Max(a + gap, 1f - b - g - gap));
        }
        else if (count >= 4)
        {
            float g = share[3], d = share[4];
            float gap = Mathf.Max(0f, 1f - a - b - g - d) / 6f;        // 3 gaps
            left[3] = Mathf.Clamp(1f / 3f - g * 0.5f, a + gap, Mathf.Max(a + gap, 1f - b - d - g - 2f * gap));
            left[4] = Mathf.Clamp(2f / 3f - d * 0.5f, left[3] + g + gap, Mathf.Max(left[3] + g + gap, 1f - b - d - gap));
        }
    }

    // ── Layout: players, then the bar, then the timer ─────────────────────
    const float RowsTop = -6f, BarTop = -100f, TimerBelowBar = -146f, SlotStep = 76f, SlotSize = 64f, GroupGap = 48f;
    static readonly int[] BarOrder = { 1, 3, 4, 2 };
    RectTransform[] rows;
    RectTransform barRect, timerRect;

    void CacheLayout()
    {
        rows = new RectTransform[Teams.Max + 1];
        for (int t = 1; t <= Teams.Max; t++)
        {
            PlayerSlot[] s = SlotsFor(t);
            if (s != null && s.Length > 0 && s[0]?.icon != null) rows[t] = (RectTransform)s[0].icon.transform.parent;
        }
        barRect = (RectTransform)turfBarGroup.transform;
        timerRect = (RectTransform)timerText.transform.parent;
        foreach (RectTransform r in new[] { barRect, timerRect })
        {
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
        }
        barRect.anchoredPosition = new Vector2(0f, BarTop);
        var neutralRect = neutralPercent.rectTransform;   // was under the bar, where the timer goes now: into the bar
        neutralRect.anchorMin = neutralRect.anchorMax = new Vector2(0.5f, 0.5f);
        neutralRect.pivot = new Vector2(0.5f, 0.5f);
        neutralRect.anchoredPosition = Vector2.zero;
    }

    // The map's teams side by side across the top, centred; the timer under the bar, or up in its
    // place while it's hidden.
    void LayOutTop()
    {
        if (rows == null) CacheLayout();
        int count = Teams.Count, size = Teams.SlotsPerTeam;
        float rowWidth = size * SlotStep - (SlotStep - SlotSize);
        int shown = 0;
        foreach (int t in BarOrder) if (t <= count && rows[t] != null) shown++;
        float x = -(shown * rowWidth + (shown - 1) * GroupGap) * 0.5f;
        foreach (int t in BarOrder)
        {
            RectTransform row = rows[t];
            if (row == null || t > count) continue;
            row.anchorMin = row.anchorMax = new Vector2(0.5f, 1f);
            row.pivot = new Vector2(0f, 1f);
            row.anchoredPosition = new Vector2(x, RowsTop);
            x += rowWidth + GroupGap;
        }
        float y = Mathf.Lerp(BarTop, TimerBelowBar, turfBarGroup.alpha);
        timerRect.anchoredPosition = new Vector2(0f, y);
    }

    // The middle of the widest stretch of unclaimed bar between neighbouring segments, not counting
    // the ends, where Alpha's and Beta's labels sit.
    static float WidestGapCentre(float[] shown, int count, float[] left)
    {
        const float EndLabels = 0.19f;
        float from = shown[1], best = float.NegativeInfinity, centre = 0.5f;
        void Gap(float to)
        {
            float x0 = Mathf.Max(from, EndLabels), x1 = Mathf.Min(to, 1f - EndLabels);
            if (x1 - x0 > best) { best = x1 - x0; centre = (x0 + x1) * 0.5f; }
        }
        if (count >= 3) { Gap(left[3]); from = left[3] + shown[3]; }
        if (count >= 4) { Gap(left[4]); from = left[4] + shown[4]; }
        Gap(1f - shown[2]);
        return centre;
    }

    static void Follow(TMP_Text label, float x)
    {
        var rt = label.rectTransform;
        rt.anchorMin = new Vector2(x, rt.anchorMin.y);
        rt.anchorMax = new Vector2(x, rt.anchorMax.y);
    }

    // A middle segment of the bar, from x0 to x1 (0..1 along it).
    static void PlaceSegment(RectTransform fill, float x0, float x1, bool used)
    {
        if (fill == null) return;
        bool on = used && x1 - x0 > 0.001f;
        if (fill.gameObject.activeSelf != on) fill.gameObject.SetActive(on);
        if (!on) return;
        fill.anchorMin = new Vector2(x0, 0f);
        fill.anchorMax = new Vector2(x1, 1f);
        fill.offsetMin = new Vector2(SegmentInset, 0f);   // inset by half a wave, like Alpha's and Beta's
        fill.offsetMax = new Vector2(-SegmentInset, 0f);
    }
    const float SegmentInset = 2f;

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

    // The host's toggle; only shown outside matches.
    public bool ShowPercentages
    {
        get => percentagesWanted;
        set => percentagesWanted = value;
    }
    bool percentagesWanted;
    public float TurfBarAlpha => turfBarGroup.alpha;
    public bool PercentagesShown => percentLabels.activeSelf;

    // Read-only views for tests.
    public string TimerText => timerText.text;
    public PlayerSlot[] AlphaSlots => alphaSlots;
    public PlayerSlot[] BetaSlots => betaSlots;
    public float AlphaShare => alphaShare;
    public float BetaShare => betaShare;
    public float ShownAlphaFill => alphaFill.anchorMax.x;
    public PlayerSlot[] GammaSlots => gammaSlots;
    public PlayerSlot[] DeltaSlots => deltaSlots;
    public float Share(int team) => share[team];
    public bool RowShown(int team) => team <= 2 || (team == 3 ? gammaRow : deltaRow) != null && (team == 3 ? gammaRow : deltaRow).activeSelf;
    public RectTransform FillFor(int team) => team switch { 1 => alphaFill, 2 => betaFill, 3 => gammaFill, _ => deltaFill };
    public RectTransform RowOf(int team) { if (rows == null) CacheLayout(); return rows[team]; }
    public RectTransform TimerRect { get { if (rows == null) CacheLayout(); return timerRect; } }
    public RectTransform BarRect { get { if (rows == null) CacheLayout(); return barRect; } }
}
