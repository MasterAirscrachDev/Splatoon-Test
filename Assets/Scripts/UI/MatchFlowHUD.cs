using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Match announcements and the results screen, driven by NetGameManager's phase:
// 3-2-1 / GO! / ONE MINUTE LEFT! / TIME'S UP!, then an overhead view with a big turf bar that
// fills both sides to a suspense point, pauses, snaps to the real split, and names the winner.
// Spectators (and benched players) watch from the same overhead view throughout.
public class MatchFlowHUD : MonoBehaviour
{
    [Header("Announcements")]
    [SerializeField] TMP_Text banner, bannerSub;
    [SerializeField] float goTime = 1.2f, finalStretchBannerTime = 2.5f;
    [SerializeField] TMP_Text spectatingLabel;

    [Header("Results")]
    [SerializeField] CanvasGroup results;
    [SerializeField] RectTransform resultAlphaFill, resultBetaFill;
    [SerializeField] Graphic resultAlphaWave, resultBetaWave;
    [SerializeField] TMP_Text resultAlphaPercent, resultBetaPercent, resultNeutral;
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
    float bannerSince, finalStretchSince = -1f;
    float shownAlpha, shownBeta;
    bool revealed;

    public string BannerText => banner.text;
    public bool OverheadActive => overhead != null && overhead.enabled;
    public float ResultAlphaShown => shownAlpha;
    public float ResultBetaShown => shownBeta;
    public bool ResultsRevealed => revealed;
    public bool SpectatingShown => spectatingLabel.gameObject.activeSelf;

    void OnEnable()  => NetGameManager.FinalStretchStarted += OnFinalStretch;
    void OnDisable() => NetGameManager.FinalStretchStarted -= OnFinalStretch;
    void OnFinalStretch() => finalStretchSince = Time.time;

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
                if (t < goTime) text = "GO!";
                else if (gm.InFinalStretch && finalStretchSince >= 0f && Time.time - finalStretchSince < finalStretchBannerTime)
                {
                    text = "ONE MINUTE LEFT!";
                    sub = "Who's winning?";
                }
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
        SetOverhead(showResults || spectating);
    }

    void UpdateResults(NetGameManager gm, float t, out string text, out string sub, out Color colour)
    {
        Vector3Int r = gm.MatchResult;
        float total = Mathf.Max(1, r.x + r.y + r.z);
        float sum = r.x + r.y;
        // The bar ignores neutral turf: the two teams split it between them.
        float alphaShare = sum > 0 ? r.x / sum : 0.5f, betaShare = 1f - alphaShare;
        float suspense = Mathf.Max(minSuspense, suspenseShareOfLoser * Mathf.Min(alphaShare, betaShare));

        if (t < barAppear) shownAlpha = shownBeta = 0f;
        else if (t < suspenseFillEnd)
        {
            float k = 1f - Mathf.Pow(1f - (t - barAppear) / (suspenseFillEnd - barAppear), 3f); // ease out
            shownAlpha = shownBeta = suspense * k;
        }
        else if (t < quickFillStart) shownAlpha = shownBeta = suspense; // the pause
        else
        {
            float k = Mathf.Clamp01((t - quickFillStart) / quickFillTime);
            shownAlpha = Mathf.Lerp(suspense, alphaShare, k);
            shownBeta = Mathf.Lerp(suspense, betaShare, k);
        }
        revealed = t >= quickFillStart + quickFillTime;

        resultAlphaFill.anchorMax = new Vector2(shownAlpha, 1f);
        resultBetaFill.anchorMin = new Vector2(1f - shownBeta, 0f);
        resultAlphaFill.gameObject.SetActive(resultAlphaFill.rect.width > 0f);
        resultBetaFill.gameObject.SetActive(resultBetaFill.rect.width > 0f);
        resultAlphaFill.GetComponent<Graphic>().color = resultAlphaWave.color = gm.AlphaTeam;
        resultBetaFill.GetComponent<Graphic>().color = resultBetaWave.color = gm.BetaTeam;

        // Real coverage (of all turf, neutral included) once the bar has settled.
        resultAlphaPercent.gameObject.SetActive(revealed);
        resultBetaPercent.gameObject.SetActive(revealed);
        resultNeutral.gameObject.SetActive(revealed);
        resultAlphaPercent.text = $"{r.x / total * 100f:0.0}%";
        resultBetaPercent.text = $"{r.y / total * 100f:0.0}%";
        resultNeutral.text = $"Unclaimed {r.z / total * 100f:0.0}%";

        text = sub = "";
        colour = Color.white;
        if (!revealed) return;
        int winner = r.x > r.y ? 1 : r.y > r.x ? 2 : 0;
        PlayerController local = NetGameManager.LocalPlayer;
        if (winner == 0) { text = "DRAW!"; sub = "Evenly matched"; return; }
        text = winner == 1 ? "ALPHA TEAM WINS!" : "BETA TEAM WINS!";
        colour = winner == 1 ? gm.AlphaTeam : gm.BetaTeam;
        sub = local == null || local.Team == 0 ? "Good game!" : local.Team == winner ? "GOOD JOB!" : "So close!";
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
