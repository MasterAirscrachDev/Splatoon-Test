using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Top left: every splat as "{Killer} {how} {Target}", the names in their team colours, newest at
// the bottom, each fading out after a while (ours on a brighter plate). And for whoever did the
// splatting, a small marker where it happened: the victim's name crossed out, rising and fading.
public class KillFeed : MonoBehaviour
{
    [SerializeField] RectTransform feed;          // rows stack down from its top left
    [SerializeField] RectTransform rowTemplate;   // hidden: a dark plate with a text child
    [SerializeField] RectTransform markers;       // full-screen layer the markers sit in
    [SerializeField] TMP_Text markerTemplate;     // hidden
    [SerializeField] int maxRows = 5;
    [SerializeField] float rowLife = 6f, rowFade = 0.6f, rowGap = 4f, rowPadding = 14f;
    [SerializeField] float markerLife = 1.8f, markerRise = 0.6f, markerHeight = 1.6f;

    static readonly Color OurPlate = new Color(0.22f, 0.22f, 0.3f, 0.85f);
    static readonly Color Nobody = new Color(0.75f, 0.75f, 0.8f);

    class Row { public RectTransform rt; public CanvasGroup group; public string plain; public float bornAt; }
    class Marker { public TMP_Text text; public string name; public Vector3 at; public float bornAt; }

    readonly List<Row> rows = new List<Row>();
    readonly List<Marker> live = new List<Marker>();
    Canvas canvas;

    public IEnumerable<string> Rows => rows.Select(r => r.plain);       // tests
    public IEnumerable<string> Markers => live.Select(m => m.name);
    public bool MarkerOnScreen => live.Any(m => m.text.gameObject.activeSelf);

    void Awake()
    {
        rowTemplate.gameObject.SetActive(false);
        markerTemplate.gameObject.SetActive(false);
        canvas = GetComponentInParent<Canvas>();
    }

    void OnEnable() => NetGameManager.KillReported += OnKill;
    void OnDisable() => NetGameManager.KillReported -= OnKill;

    void OnKill(KillData d)
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm == null) return;
        bool nobody = d.NoKiller;
        PlayerController victim = gm.GetPlayer(d.victimId), killer = nobody ? null : gm.GetPlayer(d.killerId);
        string victimName = NameOf(victim), killerName = NameOf(killer);
        string how = d.Fell ? "fell out of bounds" : nobody ? "was splatted" : Describe(d.cause, killer);
        string plain = nobody ? $"{victimName} {how}" : $"{killerName} {how} {victimName}";
        string rich = nobody ? $"{Named(victimName, victim, gm)} {how}" : $"{Named(killerName, killer, gm)} {how} {Named(victimName, victim, gm)}";
        PlayerController me = NetGameManager.LocalPlayer;
        bool ours = me != null && (d.victimId == me.OwnerId || !nobody && d.killerId == me.OwnerId);
        AddRow(rich, plain, ours);
        if (!nobody && me != null && d.killerId == me.OwnerId && d.victimId != d.killerId)
            AddMarker(victimName, victim != null ? gm.TeamColour(victim.Team) : Nobody, d.position);
    }

    // How they did it, from what did it: subs and specials by name, a main weapon by its kind.
    public static string Describe(string cause, PlayerController killer)
    {
        switch (cause)
        {
            case CurlingBomb.Cause: return "bombed";
            case "Sprinkler":       return "sprinkled";
            case "Inkstrike":       return "inkstruck";
        }
        PlayerLoadout loadout = killer != null ? killer.GetComponent<PlayerLoadout>() : null;
        if (loadout != null)
            foreach (Weapon w in loadout.Weapons)
                if (w != null && w.DisplayName == cause) return w is Roller ? "rolled" : w is Blaster ? "blasted" : "splatted";
        return "splatted";
    }

    static string NameOf(PlayerController p) => p != null && !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : "Someone";

    static string Named(string name, PlayerController p, NetGameManager gm)
    {
        Color c = p != null ? Color.Lerp(gm.TeamColour(p.Team), Color.white, 0.3f) : Nobody; // lifted a little: dark team colours on the dark plate
        return $"<color=#{ColorUtility.ToHtmlStringRGB(c)}><b><noparse>{name}</noparse></b></color>";
    }

    void AddRow(string rich, string plain, bool ours)
    {
        RectTransform rt = Instantiate(rowTemplate, feed, false);
        rt.gameObject.SetActive(true);
        TMP_Text text = rt.GetComponentInChildren<TMP_Text>(true);
        text.text = rich;
        rt.sizeDelta = new Vector2(text.GetPreferredValues(rich).x + rowPadding * 2f, rt.sizeDelta.y);
        if (ours && rt.TryGetComponent(out Image plate)) plate.color = OurPlate;
        var row = new Row { rt = rt, group = rt.GetComponent<CanvasGroup>(), plain = plain, bornAt = Time.time };
        rows.Add(row);
        rt.anchoredPosition = new Vector2(0f, SlotY(rows.Count - 1));
        while (rows.Count > maxRows) Drop(0);
    }

    float SlotY(int index) => -index * (rowTemplate.sizeDelta.y + rowGap);

    void Drop(int index)
    {
        Destroy(rows[index].rt.gameObject);
        rows.RemoveAt(index);
    }

    void AddMarker(string name, Color colour, Vector3 at)
    {
        TMP_Text t = Instantiate(markerTemplate, markers, false);
        t.text = $"<s><noparse>{name}</noparse></s>";
        t.color = colour;
        live.Add(new Marker { text = t, name = name, at = at, bornAt = Time.time });
    }

    void Update()
    {
        float k = 1f - Mathf.Exp(-14f * Time.deltaTime);
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            Row r = rows[i];
            float age = Time.time - r.bornAt;
            if (age >= rowLife) { Drop(i); continue; }
            if (r.group != null) r.group.alpha = Mathf.Min(Mathf.Clamp01(age / 0.15f), Mathf.Clamp01((rowLife - age) / rowFade));
        }
        for (int i = 0; i < rows.Count; i++) // closing up as old ones go
        {
            Vector2 p = rows[i].rt.anchoredPosition;
            rows[i].rt.anchoredPosition = new Vector2(0f, Mathf.Lerp(p.y, SlotY(i), k));
        }
        UpdateMarkers();
    }

    void UpdateMarkers()
    {
        Camera cam = Camera.main;
        if (cam == null && NetGameManager.LocalPlayer != null && NetGameManager.LocalPlayer.CameraRig != null)
            cam = NetGameManager.LocalPlayer.CameraRig.GetComponentInChildren<Camera>();
        for (int i = live.Count - 1; i >= 0; i--)
        {
            Marker m = live[i];
            float age = Time.time - m.bornAt;
            if (age >= markerLife) { Destroy(m.text.gameObject); live.RemoveAt(i); continue; }
            Vector3 world = m.at + Vector3.up * (markerHeight + markerRise * (age / markerLife));
            Vector3 screen = cam != null ? cam.WorldToScreenPoint(world) : Vector3.back;
            bool show = screen.z > 0f;
            if (m.text.gameObject.activeSelf != show) m.text.gameObject.SetActive(show);
            if (!show) continue;
            Camera ui = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(markers, screen, ui, out Vector2 local);
            m.text.rectTransform.anchoredPosition = local;
            m.text.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.4f, 1f, Mathf.Clamp01(age / 0.15f)); // pops in
            m.text.alpha = Mathf.Clamp01((markerLife - age) / 0.5f);
        }
    }
}
