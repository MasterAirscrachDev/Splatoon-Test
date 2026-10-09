using System.Collections.Generic;
using UnityEngine;

// A team's spawn point: a small pad its players appear on (its child markers, tagged AlphaSpawn /
// BetaSpawn) and the barrier over it: a sphere raised so it curves out of the pad's rim. It keeps
// the other team out:
//  - an enemy player can't get inside: each client pushes its own player back out of it;
//  - enemy shots stop on it (ProjectileManager), so they can't ink or hit inside it;
//  - enemy subs that touch it break (their owner's client breaks them);
//  - the team's players inside can't be hurt by the other team at all (blasts, strikes...).
// It's invisible until it matters: it fades in as an enemy nears it, and lights up while it's
// under attack (stopping shots, subs or players), brightest where. That shows to both teams, so
// the team inside sees their spawn being attacked. Always on, matches or not. Everything is
// decided locally from positions, so every client agrees.
public class SpawnPad : MonoBehaviour
{
    [SerializeField] int team = 1;                    // 1 alpha, 2 beta
    [SerializeField] float radius = 3.2f;             // the barrier sphere's
    [SerializeField] float padRadius = 1.5f;          // where the sphere meets the pad's top
    [SerializeField] float fadeDistance = 4f;         // an enemy this far from the barrier starts it showing
    [SerializeField] float attackHold = 1.5f;         // seconds it stays lit after stopping something
    [SerializeField] Renderer dome;                   // a unit sphere, scaled and raised to fit
    [SerializeField] Renderer[] teamTinted;           // the glowing rim, emblem and marker dots

    static readonly List<SpawnPad> pads = new List<SpawnPad>();
    public static IReadOnlyList<SpawnPad> All => pads;

    public int Team => team;
    public float Radius => radius;
    public float PadRadius => padRadius;
    public Vector3 Top => transform.position;                                   // the pad's top, at its centre
    public float Lift => Mathf.Sqrt(Mathf.Max(0f, radius * radius - padRadius * padRadius));
    public Vector3 Centre => transform.position + Vector3.up * Lift;            // the sphere's
    public float Visibility { get; private set; }     // 0 hidden .. 1 an enemy against it, or under attack
    public float Flash { get; private set; }          // a pulse when it stops something
    public float UnderAttack { get; private set; }    // 1 just after stopping something, fading
    public int Blocked { get; private set; }          // shots, subs and pushes stopped (tests)

    MaterialPropertyBlock block;
    Vector3 lastHit;
    float attackUntil = float.NegativeInfinity; // never attacked yet
    readonly HashSet<SubDevice> subsHit = new HashSet<SubDevice>();
    static readonly int ColorId = Shader.PropertyToID("_Color"), EmissionId = Shader.PropertyToID("_EmissionColor");
    static readonly int FadeId = Shader.PropertyToID("_Fade"), FocusId = Shader.PropertyToID("_FocusPos"), FlashId = Shader.PropertyToID("_Flash"), CutId = Shader.PropertyToID("_CutY");

    public void Configure(int team, float radius, float padRadius = 1.5f) // tests, builders
    {
        this.team = team; this.radius = radius; this.padRadius = padRadius;
        attackUntil = float.NegativeInfinity; Visibility = Flash = UnderAttack = 0f; // a fresh barrier
        ApplyRadius(); ApplyColours();
    }

    void OnEnable()
    {
        pads.Add(this);
        ApplyRadius();
        ApplyColours();
        NetGameManager.TeamColoursChanged += ApplyColours;
    }

    void Start() => ApplyColours(); // the game's colours are set by now

    void OnDisable()
    {
        pads.Remove(this);
        NetGameManager.TeamColoursChanged -= ApplyColours;
    }

    void ApplyRadius()
    {
        if (dome == null) return;
        float s = Mathf.Max(1e-3f, transform.lossyScale.x);
        dome.transform.localScale = Vector3.one * radius / s;
        dome.transform.localPosition = Vector3.up * Lift / s;
    }

    Color Colour => NetGameManager.Instance != null ? NetGameManager.Instance.TeamColour(team) : team == 2 ? Color.magenta : Color.cyan;

    void ApplyColours()
    {
        block ??= new MaterialPropertyBlock();
        Color c = Colour;
        block.Clear();
        block.SetColor(ColorId, c);
        block.SetColor(EmissionId, c * 1.5f);
        if (teamTinted != null) foreach (Renderer r in teamTinted) if (r != null) r.SetPropertyBlock(block);
    }

    void Update()
    {
        BreakEnemySubs();

        // The nearest enemy: how close to the barrier, and where.
        float nearest = float.MaxValue;
        Vector3 focus = Centre + Vector3.up * radius;
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null)
            foreach (PlayerController p in gm.Players)
            {
                if (p == null || p.IsDead || p.Team == 0 || p.Team == team) continue;
                Vector3 at = p.BodyCenter;
                float d = Mathf.Max(0f, Vector3.Distance(at, Centre) - radius);
                if (d < nearest) { nearest = d; focus = at; }
            }
        float near = nearest < float.MaxValue ? Mathf.Clamp01(1f - nearest / fadeDistance) : 0f;

        // Under attack: full for attackHold, then fading over a second; lit where it was hit.
        UnderAttack = Mathf.Clamp01(attackUntil - Time.time + 1f);
        if (UnderAttack > near) focus = lastHit;

        Visibility = Mathf.MoveTowards(Visibility, Mathf.Max(near, UnderAttack), Time.deltaTime * 3f);
        Flash = Mathf.MoveTowards(Flash, 0f, Time.deltaTime * 2.5f);

        if (dome == null) return;
        float shown = Mathf.Max(Visibility, Flash);
        dome.enabled = shown > 0.005f;
        if (!dome.enabled) return;
        block ??= new MaterialPropertyBlock();
        block.Clear();
        block.SetColor(ColorId, Colour);
        block.SetFloat(FadeId, shown);
        block.SetVector(FocusId, focus);
        block.SetFloat(FlashId, Flash);
        block.SetFloat(CutId, -Lift / Mathf.Max(1e-3f, radius)); // its unit-sphere height at the pad's top
        dome.SetPropertyBlock(block);
    }

    // The other team's subs that touch it break (their owner breaks them; everyone sees it hit).
    void BreakEnemySubs()
    {
        IReadOnlyList<SubDevice> subs = SubDevice.All;
        for (int i = subs.Count - 1; i >= 0; i--)
        {
            SubDevice sub = subs[i];
            if (sub == null || sub.Team == team || sub.Team == 0 || !Contains(sub.transform.position, 0.3f)) continue;
            if (subsHit.Add(sub)) Pulse(sub.transform.position);
            if (sub.IsOwnedLocally) sub.Break();
        }
        subsHit.RemoveWhere(s => s == null);
    }

    // It stopped something at `at`.
    public void Pulse(Vector3 at)
    {
        Flash = 1f;
        Blocked++;
        lastHit = at;
        attackUntil = Time.time + attackHold;
    }

    // ── Queries ────────────────────────────────────────────────────────────

    // Under the pad's top it's the pad (and the floor), not the barrier.
    bool BelowPad(float y) => y < Top.y - 0.05f;

    // How far out (horizontally) a point at this height must be to clear the sphere by `margin`.
    float ClearRadius(float y, float margin)
    {
        float r = radius + margin, dy = y - Centre.y;
        return Mathf.Abs(dy) >= r ? 0f : Mathf.Sqrt(r * r - dy * dy);
    }

    // Inside the barrier (as a point, with `margin` added to its radius).
    public bool Contains(Vector3 point, float margin = 0f)
    {
        if (BelowPad(point.y)) return false;
        return (point - Centre).sqrMagnitude < (radius + margin) * (radius + margin);
    }

    // A player of `team` at `point` is in their own team's spawn: the other team can't hurt them.
    public static bool Shields(Vector3 point, int team)
    {
        if (team == 0) return false;
        foreach (SpawnPad p in pads) if (p.team == team && p.Contains(point)) return true;
        return false;
    }

    // Our own player: if they're inside the other team's barrier, back out of it (horizontally,
    // so they're not lifted). Returns the move needed (zero if none).
    public static Vector3 PushOut(Vector3 centre, float capsuleRadius, int team)
    {
        Vector3 total = Vector3.zero;
        if (team == 0) return total;
        foreach (SpawnPad p in pads)
        {
            if (p.team == team) continue;
            Vector3 at = centre + total;
            if (p.BelowPad(at.y)) continue;
            float clear = p.ClearRadius(at.y, capsuleRadius);
            Vector3 d = at - p.Centre;
            Vector2 flat = new Vector2(d.x, d.z);
            float dist = flat.magnitude;
            if (dist >= clear) continue;
            Vector2 dir = dist > 1e-4f ? flat / dist : Vector2.up;
            Vector2 move = dir * (clear - dist + 0.01f);
            total += new Vector3(move.x, 0f, move.y);
            p.Pulse(p.Centre + new Vector3(dir.x, 0f, dir.y) * p.radius + Vector3.up * (at.y - p.Centre.y));
        }
        return total;
    }

    // A shot of `team` moving from→along dir for `length`: where it first meets another team's
    // barrier from outside, if it does (the caller calls Pulse if that's what stopped it).
    public static bool Stops(int team, Vector3 from, Vector3 dir, float length, out float distance, out SpawnPad pad)
    {
        distance = float.MaxValue;
        pad = null;
        foreach (SpawnPad p in pads)
        {
            if (p.team == team) continue;
            Vector3 oc = from - p.Centre;
            float b = Vector3.Dot(oc, dir), c = oc.sqrMagnitude - p.radius * p.radius;
            if (c <= 0f && !p.BelowPad(from.y)) continue; // starting inside: it was already in (or fired from in there)
            float disc = b * b - c;
            if (disc < 0f) continue;
            float t = -b - Mathf.Sqrt(disc);
            if (t < 0f || t > length || t >= distance) continue;
            if (p.BelowPad(from.y + dir.y * t)) continue;
            distance = t;
            pad = p;
        }
        return pad != null;
    }
}
