using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

// Debugging shot lifespans: how long each weapon's shots really fly, to tune DamageFalloff against.
// Every damaging shot's end (ProjectileManager.Ended) is recorded: its age, how far it flew, what
// ended it, and the damage multiplier it carried by then. F10 shows (or hides) an overlay: per weapon,
// its recent shots' lifespans, distances and landing damage beside where its falloff curve ends, and
// a label where each shot ended (age, distance, damage %: green at full, red at 40% or less).
// Made at startup, kept across scenes; records even while hidden.
public class ShotDebug : MonoBehaviour
{
    public struct Record
    {
        public string source;
        public float age, distance, scale, damage, curveEnd, time;
        public Vector3 at;
        public ProjectileManager.EndReason reason;
    }

    const int PerSource = 40;          // recent shots kept per weapon
    const float MarkerLife = 3f;       // seconds a landing label stays up

    public static ShotDebug Instance { get; private set; }
    public static bool Shown { get; set; }

    readonly Dictionary<string, List<Record>> bySource = new Dictionary<string, List<Record>>();
    readonly List<Record> markers = new List<Record>();
    GUIStyle box, label;

    public IReadOnlyDictionary<string, List<Record>> BySource => bySource; // tests
    public IEnumerable<Record> Of(string source) => bySource.TryGetValue(source, out var list) ? list : Enumerable.Empty<Record>();
    public void Clear() { bySource.Clear(); markers.Clear(); }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null) return;
        var go = new GameObject("ShotDebug");
        DontDestroyOnLoad(go);
        go.AddComponent<ShotDebug>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        ProjectileManager.Ended += OnEnded;
    }

    void OnDestroy()
    {
        ProjectileManager.Ended -= OnEnded;
        if (Instance == this) Instance = null;
    }

    void OnEnded(ProjectileManager.Shot s, Vector3 at, ProjectileManager.EndReason reason)
    {
        if (!s.authoritative || s.damage <= 0f && s.falloff == null) return; // ours, and only shots that can hurt (no drips)
        string source = string.IsNullOrEmpty(s.source) ? "?" : s.source;
        var r = new Record
        {
            source = source, age = s.age, distance = s.travelled, at = at, reason = reason, time = Time.time,
            scale = s.falloff != null ? s.falloff.Scale(s.age) : 1f,
            damage = s.DamageNow,
            curveEnd = s.falloff != null && s.falloff.scaleOverTime != null && s.falloff.scaleOverTime.length > 0 ? s.falloff.scaleOverTime.keys[s.falloff.scaleOverTime.length - 1].time : 0f,
        };
        if (!bySource.TryGetValue(source, out var list)) bySource[source] = list = new List<Record>();
        list.Add(r);
        if (list.Count > PerSource) list.RemoveAt(0);
        markers.Add(r);
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f10Key.wasPressedThisFrame) Shown = !Shown;
        markers.RemoveAll(m => Time.time - m.time > MarkerLife);
    }

    // One weapon's line: "Inkroller (12): life 0.21-0.48s (avg 0.33) | 3.1-9.8m | landing damage 100-41% | curve ends 0.50s".
    public static string Summary(string source, IReadOnlyList<Record> list)
    {
        if (list == null || list.Count == 0) return $"{source}: no shots";
        float minA = list.Min(r => r.age), maxA = list.Max(r => r.age), avgA = list.Average(r => r.age);
        float minD = list.Min(r => r.distance), maxD = list.Max(r => r.distance);
        float maxS = list.Max(r => r.scale), minS = list.Min(r => r.scale);
        float curve = list[list.Count - 1].curveEnd;
        int hits = list.Count(r => r.reason == ProjectileManager.EndReason.Target);
        return $"{source} ({list.Count}, {hits} hit): life {minA:0.00}-{maxA:0.00}s (avg {avgA:0.00}) | {minD:0.0}-{maxD:0.0}m | landing damage {maxS * 100f:0}-{minS * 100f:0}%"
               + (curve > 0f ? $" | curve ends {curve:0.00}s" : " | flat damage");
    }

    static Camera View()
    {
        PlayerController p = NetGameManager.LocalPlayer;
        CameraAim aim = p != null ? p.GetComponentInChildren<CameraAim>() : null;
        Camera cam = aim != null ? aim.GetComponent<Camera>() : null;
        return cam != null ? cam : Camera.main;
    }

    void OnGUI()
    {
        if (!Shown) return;
        if (box == null)
        {
            box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = Mathf.RoundToInt(16f * Screen.height / 1080f), richText = true, padding = new RectOffset(10, 10, 8, 8) };
            box.normal.textColor = Color.white;
            var back = new Texture2D(1, 1);
            back.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.75f));
            back.Apply();
            box.normal.background = back;
        }
        label ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15f * Screen.height / 1080f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

        var lines = new List<string> { "<b>Shot lifespans</b> (F10 to hide; recent shots per weapon)" };
        foreach (var kv in bySource.OrderBy(k => k.Key)) lines.Add(Summary(kv.Key, kv.Value));
        if (bySource.Count == 0) lines.Add("fire something");
        string text = string.Join("\n", lines);
        Vector2 size = box.CalcSize(new GUIContent(text));
        GUI.Box(new Rect(10f, Screen.height - size.y - 10f, size.x, size.y), text, box);

        // Where each shot ended, while it's fresh (through our own camera).
        Camera cam = View();
        if (cam == null) return;
        foreach (Record m in markers)
        {
            Vector3 p = cam.WorldToScreenPoint(m.at);
            if (p.z <= 0f) continue;
            float fade = 1f - (Time.time - m.time) / MarkerLife;
            Color c = Color.Lerp(new Color(0.3f, 1f, 0.4f), new Color(1f, 0.3f, 0.25f), Mathf.InverseLerp(1f, 0.4f, m.scale)); // full damage green, 40% or less red
            c.a = fade;
            string t = $"{m.age:0.00}s {m.distance:0.0}m {m.scale * 100f:0}%";
            var r = new Rect(p.x - 110f, Screen.height - p.y - 26f, 220f, 24f);
            GUI.color = new Color(0f, 0f, 0f, fade * 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), t, label);
            GUI.color = c;
            GUI.Label(r, t, label);
        }
        GUI.color = Color.white;
    }
}
