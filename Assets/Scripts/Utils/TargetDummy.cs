using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Practice target: a teamless hitbox that never dies, showing each hit as a floating number, the
// current combo (resets after comboTimeout without a hit; red once it would have splatted), damage
// per second over the last dpsWindow, and the total since the last reset. Builds its own body,
// hitbox and labels, so it only needs dropping in the scene (GameObject > Splatoon > Target Dummy).
// Hits only land where they're authoritative, so each client sees its own damage.
public class TargetDummy : MonoBehaviour
{
    [SerializeField] float comboTimeout = 2f;   // seconds without a hit before the combo clears
    [SerializeField] float dpsWindow = 1f;      // seconds of hits the DPS reading averages
    [SerializeField] float splatThreshold = 100f; // a player's health: combos past this turn red
    [SerializeField] Color bodyColour = new Color(0.75f, 0.72f, 0.62f);

    [Header("Floating numbers")]
    [SerializeField] float popupLife = 0.9f;
    [SerializeField] float popupRise = 1.2f;    // metres over its life
    [SerializeField] float popupSpread = 0.35f; // random sideways offset, metres

    const float Height = 1.92f, BodyRadius = 0.4f; // a standing player
    static readonly List<TargetDummy> all = new List<TargetDummy>();
    public static IReadOnlyList<TargetDummy> All => all;

    PlayerHitbox hitbox;
    Transform body, labels;
    Material bodyMat;
    TextMeshPro comboLabel, statsLabel;
    TMP_FontAsset outlineFont;
    Material outlineMat;

    readonly Queue<(float time, float amount)> recent = new Queue<(float, float)>();
    float combo, total, recentSum, lastHitTime = -999f, flash;

    struct Popup { public TextMeshPro text; public Vector3 start, drift; public float born; }
    readonly List<Popup> popups = new List<Popup>();
    readonly Stack<TextMeshPro> popupPool = new Stack<TextMeshPro>();

    public PlayerHitbox Hitbox => hitbox;
    public Vector3 BodyCenter => transform.position + Vector3.up * (Height * 0.5f);
    public float Combo => combo;  // tests
    public float Total => total;

    void Awake()
    {
        Build();
        hitbox.Damaged += OnDamaged;
    }

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    void OnDestroy()
    {
        if (hitbox != null) hitbox.Damaged -= OnDamaged;
        if (bodyMat != null) Destroy(bodyMat);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    void Build()
    {
        // Body: solid, so players bump into it. The hitbox around it is wider, so shots reach it first.
        body = GameObject.CreatePrimitive(PrimitiveType.Capsule).transform;
        body.name = "Body";
        body.SetParent(transform, false);
        body.localPosition = Vector3.up * (Height * 0.5f);
        body.localScale = new Vector3(BodyRadius * 2f, Height * 0.5f, BodyRadius * 2f);
        Renderer r = body.GetComponent<Renderer>();
        bodyMat = new Material(r.sharedMaterial) { color = bodyColour };
        r.sharedMaterial = bodyMat;

        GameObject hb = new GameObject("Hitbox");
        hb.layer = LayerMask.NameToLayer("Hitbox");
        hb.transform.SetParent(transform, false);
        hb.transform.localPosition = Vector3.up * (Height * 0.5f);
        hitbox = hb.AddComponent<PlayerHitbox>(); // no PlayerController: team 0, so everyone's shots land

        outlineMat = Resources.Load<Material>("Fonts & Materials/LiberationSans SDF - Outline");
        labels = new GameObject("Labels").transform;
        labels.SetParent(transform, false);
        labels.localPosition = Vector3.up * (Height + 0.55f);
        comboLabel = MakeText("Combo", labels, 6f);
        comboLabel.transform.localPosition = Vector3.up * 0.3f;
        statsLabel = MakeText("Stats", labels, 2.6f);
        statsLabel.color = new Color(1f, 1f, 1f, 0.85f);
        RefreshLabels();
    }

    TextMeshPro MakeText(string name, Transform parent, float size)
    {
        TextMeshPro t = new GameObject(name).AddComponent<TextMeshPro>();
        t.transform.SetParent(parent, false);
        t.rectTransform.sizeDelta = new Vector2(6f, 1f);
        t.alignment = TextAlignmentOptions.Center;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.fontSize = size;
        t.fontStyle = FontStyles.Bold;
        if (outlineMat != null) t.fontSharedMaterial = outlineMat;
        return t;
    }

    void OnDamaged(float amount, string source)
    {
        if (Time.time - lastHitTime > comboTimeout) combo = 0f;
        lastHitTime = Time.time;
        combo += amount;
        total += amount;
        recent.Enqueue((Time.time, amount));
        recentSum += amount;
        flash = 1f;
        SpawnPopup(amount);
        RefreshLabels();
    }

    [ContextMenu("Reset Totals")]
    public void ResetTotals()
    {
        combo = total = recentSum = 0f;
        recent.Clear();
        lastHitTime = -999f;
        RefreshLabels();
    }

    void Update()
    {
        bool changed = false;
        while (recent.Count > 0 && Time.time - recent.Peek().time > dpsWindow)
        {
            recentSum -= recent.Dequeue().amount;
            changed = true;
        }
        if (recent.Count == 0) recentSum = 0f; // no float drift left behind
        if (combo > 0f && Time.time - lastHitTime > comboTimeout) { combo = 0f; changed = true; }
        if (changed) RefreshLabels();

        // Hit reaction: flash white and squash, easing back.
        flash = Mathf.MoveTowards(flash, 0f, Time.deltaTime * 6f);
        bodyMat.color = Color.Lerp(bodyColour, Color.white, flash);
        float squash = flash * 0.08f;
        body.localScale = new Vector3(BodyRadius * 2f * (1f + squash), Height * 0.5f * (1f - squash), BodyRadius * 2f * (1f + squash));

        Camera cam = Camera.main;
        if (cam != null) labels.rotation = Facing(cam, labels.position);
        UpdatePopups(cam);
    }

    void RefreshLabels()
    {
        bool splat = combo >= splatThreshold;
        comboLabel.text = combo > 0f ? combo.ToString("0.#") : "-";
        comboLabel.color = splat ? new Color(1f, 0.3f, 0.25f) : Color.white;
        float dps = recentSum / dpsWindow;
        statsLabel.text = $"{dps:0} DPS   ·   Total {total:0}";
    }

    void SpawnPopup(float amount)
    {
        TextMeshPro t = popupPool.Count > 0 ? popupPool.Pop() : MakeText("Popup", transform, 4f);
        t.gameObject.SetActive(true);
        t.text = amount.ToString("0.#");
        Vector2 side = Random.insideUnitCircle * popupSpread;
        Vector3 start = BodyCenter + Vector3.up * 0.4f + new Vector3(side.x, 0f, side.y);
        t.transform.position = start;
        popups.Add(new Popup { text = t, start = start, drift = new Vector3(side.x, 0f, side.y), born = Time.time });
    }

    void UpdatePopups(Camera cam)
    {
        for (int i = popups.Count - 1; i >= 0; i--)
        {
            Popup p = popups[i];
            float k = (Time.time - p.born) / popupLife;
            if (k >= 1f)
            {
                p.text.gameObject.SetActive(false);
                popupPool.Push(p.text);
                popups.RemoveAt(i);
                continue;
            }
            float ease = 1f - (1f - k) * (1f - k);
            p.text.transform.position = p.start + Vector3.up * (popupRise * ease) + p.drift * ease;
            p.text.transform.localScale = Vector3.one * (k < 0.15f ? Mathf.Lerp(1.6f, 1f, k / 0.15f) : 1f); // pop in
            p.text.color = new Color(1f, 0.85f, 0.3f, k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f); // fade out late
            if (cam != null) p.text.transform.rotation = Facing(cam, p.text.transform.position);
        }
    }

    // The body is built at runtime; show where it'll stand.
    void OnDrawGizmos()
    {
        if (Application.isPlaying) return;
        Gizmos.color = bodyColour;
        Gizmos.DrawWireCube(transform.position + Vector3.up * (Height * 0.5f), new Vector3(BodyRadius * 2f, Height, BodyRadius * 2f));
    }

    // TMP text reads correctly when its forward points away from the viewer.
    static Quaternion Facing(Camera cam, Vector3 at) => Quaternion.LookRotation(at - cam.transform.position, Vector3.up);
}
