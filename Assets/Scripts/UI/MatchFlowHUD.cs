using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Match announcements and the results screen, driven by NetGameManager's phase:
// 3-2-1 / GO! / TIME'S UP!, then an overhead view with a big turf bar that
// fills both sides to a suspense point, pauses, snaps to the real split, and names the winner.
// Spectators (and benched players) watch from the same overhead view throughout.
public class MatchFlowHUD : MonoBehaviour
{
    [Header("Announcements")]
    [SerializeField] TMP_Text banner, bannerSub;
    [SerializeField] float goTime = 1.2f;
    [SerializeField] TMP_Text spectatingLabel;

    [Header("Results")]
    [SerializeField] CanvasGroup results;
    [SerializeField] RectTransform resultAlphaFill, resultBetaFill;
    [SerializeField] Graphic resultAlphaWave, resultBetaWave;
    [SerializeField] TMP_Text resultAlphaPercent, resultBetaPercent, resultNeutral;
    [SerializeField] RectTransform resultGammaFill, resultDeltaFill;   // after Alpha's, before Beta's
    [SerializeField] TMP_Text resultGammaPercent, resultDeltaPercent;
    [SerializeField] CanvasGroup[] hiddenDuringResults; // the match HUD and gauges

    [Header("Results timing (seconds into the results)")]
    [SerializeField] float barAppear = 0.4f;
    [SerializeField] float suspenseFillEnd = 2.4f;
    [SerializeField] float quickFillStart = 3.6f;
    [SerializeField] float quickFillTime = 0.2f;
    [SerializeField] float minSuspense = 0.2f;          // both sides fill to at least this...
    [SerializeField] float suspenseShareOfLoser = 0.8f; // ...or this much of the losing side's share

    [Header("Overhead view")]
    [SerializeField] float viewPitch = 55f, viewYaw = 45f, viewPadding = 1.05f;

    Camera overhead;
    string shownBanner = "";
    float bannerSince;
    readonly float[] shownShare = new float[Teams.Max + 1];
    readonly float[] segLeft = new float[Teams.Max + 1];
    float shownAlpha => shownShare[1];
    float shownBeta => shownShare[2];
    bool revealed;

    public string BannerText => banner.text;
    public bool OverheadActive => overhead != null && overhead.enabled;
    public float ResultAlphaShown => shownAlpha;
    public float ResultBetaShown => shownBeta;
    public bool ResultsRevealed => revealed;
    public bool SpectatingShown => spectatingLabel.gameObject.activeSelf;
    public bool SpectatorFollowing { get; set; } // SpectatorView: watching a player, not the whole level


    void OnDestroy()
    {
        if (overhead != null) Destroy(overhead.gameObject);
    }

    void Update()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm == null) return;
        MatchPhase phase = gm.Phase;
        float t = gm.PhaseElapsed;

        string text = "", sub = "";
        Color colour = Color.white;
        switch (phase)
        {
            case MatchPhase.Countdown:
                int n = Mathf.CeilToInt(gm.PhaseTimeRemaining);
                text = n > 0 ? n.ToString() : "";
                break;
            case MatchPhase.Playing:
                if (t < goTime) text = "GO!"; // the final minute is announced by music (FinalStretchStarted)
                break;
            case MatchPhase.TimesUp:
                text = "TIME'S UP!";
                break;
            case MatchPhase.Results:
                UpdateResults(gm, t, out text, out sub, out colour);
                break;
        }
        if (phase != MatchPhase.Results) revealed = false;
        SetBanner(text, sub, colour);

        bool showResults = phase == MatchPhase.Results;
        PlayerController local = NetGameManager.LocalPlayer;
        bool spectating = local != null && !local.gameObject.activeInHierarchy; // spectator or benched: no player to look through
        bool showSpectating = spectating && !showResults;
        if (spectatingLabel.gameObject.activeSelf != showSpectating) spectatingLabel.gameObject.SetActive(showSpectating);
        if (showSpectating)
        {
            string label = gm.InMatch && gm.IsLateJoiner(local.OwnerId) ? "SPECTATING\n<size=60%>You'll play from the next round</size>" : "SPECTATING";
            if (spectatingLabel.text != label) spectatingLabel.text = label;
        }
        results.alpha = showResults ? Mathf.Clamp01(t / barAppear) : 0f;
        if (results.gameObject.activeSelf != showResults) results.gameObject.SetActive(showResults);
        foreach (CanvasGroup g in hiddenDuringResults)
            g.alpha = Mathf.MoveTowards(g.alpha, showResults ? 0f : 1f, 4f * Time.deltaTime);
        SetOverhead(showResults || spectating && !SpectatorFollowing);
    }

    void UpdateResults(NetGameManager gm, float t, out string text, out string sub, out Color colour)
    {
        int[] r = gm.MatchResult;
        int count = Teams.Count;
        float total = 0f, sum = 0f;
        foreach (int n in r) total += n;
        for (int team = 1; team <= count; team++) sum += r[team];
        total = Mathf.Max(1f, total);
        // The bar ignores neutral turf: the teams split it between them.
        var share = new float[Teams.Max + 1];
        float least = 1f;
        for (int team = 1; team <= count; team++)
        {
            share[team] = sum > 0 ? r[team] / sum : 1f / count;
            least = Mathf.Min(least, share[team]);
        }
        float suspense = Mathf.Min(Mathf.Max(minSuspense, suspenseShareOfLoser * least), 0.9f / count);

        for (int team = 1; team <= Teams.Max; team++)
        {
            float target = team <= count ? share[team] : 0f, hold = team <= count ? suspense : 0f;
            if (t < barAppear) shownShare[team] = 0f;
            else if (t < suspenseFillEnd)
            {
                float k = 1f - Mathf.Pow(1f - (t - barAppear) / (suspenseFillEnd - barAppear), 3f); // ease out
                shownShare[team] = hold * k;
            }
            else if (t < quickFillStart) shownShare[team] = hold; // the pause
            else shownShare[team] = Mathf.Lerp(hold, target, Mathf.Clamp01((t - quickFillStart) / quickFillTime));
        }
        revealed = t >= quickFillStart + quickFillTime;

        resultAlphaFill.anchorMax = new Vector2(shownShare[1], 1f);
        resultBetaFill.anchorMin = new Vector2(1f - shownShare[2], 0f);
        resultAlphaFill.gameObject.SetActive(resultAlphaFill.rect.width > 0f);
        resultBetaFill.gameObject.SetActive(resultBetaFill.rect.width > 0f);
        MatchHUD.BarLayout(shownShare, count, segLeft);
        Segment(resultGammaFill, segLeft[3], segLeft[3] + shownShare[3], count >= 3, gm.TeamColour(3));
        Segment(resultDeltaFill, segLeft[4], segLeft[4] + shownShare[4], count >= 4, gm.TeamColour(4));
        resultAlphaFill.GetComponent<Graphic>().color = resultAlphaWave.color = gm.TeamColour(1);
        resultBetaFill.GetComponent<Graphic>().color = resultBetaWave.color = gm.TeamColour(2);

        // Real coverage (of all turf, neutral included) once the bar has settled.
        Percent(resultAlphaPercent, revealed, r[1] / total);
        Percent(resultBetaPercent, revealed, r[2] / total);
        Percent(resultGammaPercent, revealed && count >= 3, r[3] / total);
        Percent(resultDeltaPercent, revealed && count >= 4, r[4] / total);
        resultNeutral.gameObject.SetActive(revealed);
        resultNeutral.text = $"Unclaimed {r[0] / total * 100f:0.0}%";

        text = sub = "";
        colour = Color.white;
        if (!revealed) return;
        int winner = Winner(r);
        PlayerController local = NetGameManager.LocalPlayer;
        if (winner == 0) { text = "DRAW!"; sub = "Evenly matched"; return; }
        text = Teams.Name(winner).ToUpperInvariant() + " TEAM WINS!";
        colour = gm.TeamColour(winner);
        sub = local == null || local.Team == 0 ? "Good game!" : local.Team == winner ? "GOOD JOB!" : "So close!";
    }

    static void Segment(RectTransform fill, float x0, float x1, bool used, Color colour)
    {
        if (fill == null) return;
        bool on = used && x1 - x0 > 0.001f;
        if (fill.gameObject.activeSelf != on) fill.gameObject.SetActive(on);
        if (!on) return;
        fill.anchorMin = new Vector2(x0, 0f);
        fill.anchorMax = new Vector2(x1, 1f);
        fill.offsetMin = new Vector2(3f, 0f);  // inset by half a wave, like Alpha's and Beta's
        fill.offsetMax = new Vector2(-3f, 0f);
        foreach (Graphic g in fill.GetComponentsInChildren<Graphic>(true)) g.color = colour; // the fill and its waves
    }

    static void Percent(TMP_Text label, bool show, float share)
    {
        if (label == null) return;
        if (label.gameObject.activeSelf != show) label.gameObject.SetActive(show);
        if (show) label.text = $"{share * 100f:0.0}%";
    }

    public float ResultShown(int team) => shownShare[team]; // tests

    // The team with the most turf among the map's teams; 0 when the top teams tie.
    public static int Winner(int[] scores)
    {
        int best = 0, bestScore = -1;
        bool tie = false;
        for (int team = 1; team <= Teams.Count && team < scores.Length; team++)
        {
            if (scores[team] > bestScore) { best = team; bestScore = scores[team]; tie = false; }
            else if (scores[team] == bestScore) tie = true;
        }
        return tie ? 0 : best;
    }

    void SetBanner(string text, string sub, Color colour)
    {
        if (text != shownBanner)
        {
            shownBanner = text;
            bannerSince = Time.time;
            banner.text = text;
        }
        banner.color = colour;
        if (bannerSub.text != sub) bannerSub.text = sub;
        float pop = 1f + 0.5f * Mathf.Exp(-12f * (Time.time - bannerSince)); // punch in on each change
        banner.rectTransform.localScale = Vector3.one * pop;
    }

    // Everyone watches the level from above during the results.
    void SetOverhead(bool on)
    {
        if (on && overhead == null)
        {
            overhead = new GameObject("ResultsCamera").AddComponent<Camera>();
            overhead.depth = 50; // over the player camera
            overhead.cullingMask = ~LayerMask.GetMask("UI");
            overhead.enabled = false;
        }
        if (overhead == null || overhead.enabled == on) return;
        if (on) LevelView.Frame(overhead, LayerMask.GetMask("Map", "InkSurface"), viewPitch, viewYaw, (float)Screen.width / Screen.height, viewPadding);
        overhead.enabled = on;
    }
}
