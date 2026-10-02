using System.Collections.Generic;
using UnityEngine;

// Special: a column of ink called in from the map. The column runs from the lowest ground under the
// area (even when aimed at something tall) to well above the target. It's marked for everyone, then
// after a moment an ink column (a spinning mesh, with particles spiralling around it) grows out of
// nothing into a thin column up its middle, then widens to nearly the whole area for the explosion
// (the ink and the damage spreading with it), holds, and shrinks back to nothing. The owner's client
// paints every surface in the column
// (each floor stacked in it, and walls at every height) and hits whoever is inside: lethal in the
// core, falling off to non-fatal at the edge. Other clients show the marker and tornado; the paint
// and damage reach them through the usual Splat/Damage messages.
public class InkStrike : MonoBehaviour
{
    [Header("Area")]
    [SerializeField] float radius = 6f;
    [SerializeField] float height = 18f;          // the column reaches this far above the target
    [SerializeField] float groundSearch = 200f;   // how far below the target to look for the column's bottom
    [SerializeField] float delay = 1f;            // marked for this long before it strikes
    [SerializeField] float spreadTime = 1f;       // then the tornado widens, ink and damage spreading with it, from the middle to the edge

    [Header("Damage")]
    [SerializeField] float damage = 300f;         // in the core: well past lethal
    [SerializeField] float coreFraction = 0.6f;   // share of the radius that's lethal
    [SerializeField] float edgeDamage = 30f;      // at the very edge (from a full health bar at the core's edge)
    const float FullHealth = 100f;

    [Header("Paint")]
    [SerializeField] int splashSize = 18;
    [SerializeField] float floorRaySpacing = 1.2f;  // metres between the rays down through the column
    [SerializeField] float wallRayHeightStep = 1.5f; // and between the rings of rays out to its walls
    [SerializeField] float wallRayAngleStep = 15f;

    [Header("Look")]
    [SerializeField] Transform marker;            // primitive cylinder, scaled to the column
    [SerializeField] Transform column;            // the central column mesh (sized from its bounds), in the team colour
    [SerializeField] float columnSpin = 240f;     // degrees per second
    [SerializeField] ParticleSystem tornado;      // ink spiralling up around the column
    [SerializeField] ParticleSystem burst;        // spray off the ground along the spreading edge (its burst count, spread over the spread)
    [SerializeField] float tightRadius = 0.6f;    // the thin column it first grows into
    [SerializeField] float explosionWidth = 0.9f; // the column's radius at full size, as a share of the strike's radius
    [SerializeField] float riseTime = 1f;         // growing from nothing into the thin column, before the explosion widens it
    [SerializeField] float holdTime = 0.5f;       // at full size
    [SerializeField] float shrinkTime = 0.6f;     // shrinking back to nothing
    const float ParticleClimbTime = 2.2f;         // the spiralling particles climb the column in about this long,
    const float SpiralTurns = 1.5f;               // circling it this many times a second,
    const int SpiralArms = 3;                     // in this many bands (they leave the ground from fixed points)
    const float SpiralOut = 1.15f;                // just outside its surface

    static readonly int CoreAlpha = Shader.PropertyToID("_CoreAlpha");
    static readonly int RimAlpha = Shader.PropertyToID("_RimAlpha");
    static readonly int ColourId = Shader.PropertyToID("_Color");
    static readonly int SeedId = Shader.PropertyToID("_Seed");

    ulong ownerId;
    int team;
    Vector3 target;
    float bottom, top; // the column's vertical extent
    float tornadoRate = -1f; // the prefab's emission rate, for a column `height` tall
    float burstCount = -1f;  // the prefab's burst size, sprayed along the spreading edge
    float splashedTo, splashOwed;
    float width;             // the column's current radius
    float columnRadius = 1f, columnHeight = 6f, columnCentre; // the mesh's own size (bounds)
    Renderer columnRenderer;
    ParticleSystem.Particle[] swirling;
    readonly List<(float distance, RaycastHit hit)> unpainted = new List<(float, RaycastHit)>(); // nearest the middle first
    int painted;
    readonly HashSet<Object> hit = new HashSet<Object>(); // players and subs already struck
    Color colour = Color.white;
    bool authoritative, struck;
    float startTime;
    MaterialPropertyBlock block;

    public float Radius => radius;
    public float Height => height;
    public float Delay => delay;
    public bool Struck => struck;
    public Vector3 Target => target;
    public float Bottom => bottom;
    public float SpreadTime => spreadTime;
    public float AttackTime => riseTime + spreadTime; // from the strike until the front reaches the edge
    public float Front { get; private set; }      // how far out from the middle it has reached
    public float Width => width;                  // the column's radius now
    public float TightRadius => tightRadius;
    public float FullWidth => radius * explosionWidth;
    public bool IsOwnedLocally => authoritative;

    public void Begin(ulong ownerId, int team, Vector3 target, bool ownedLocally)
    {
        this.ownerId = ownerId;
        this.team = team;
        this.target = target;
        authoritative = ownedLocally;
        startTime = Time.time;
        transform.SetPositionAndRotation(target, Quaternion.identity);
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null) colour = team == 2 ? gm.BetaTeam : gm.AlphaTeam;
        block = new MaterialPropertyBlock();
        bottom = FindBottom();
        top = target.y + height;

        // Unity's cylinder is 1 wide and 2 tall: span the column, from just below its bottom.
        float length = top - (bottom - 1f);
        marker.localScale = new Vector3(radius * 2f, length / 2f, radius * 2f);
        marker.localPosition = new Vector3(0f, (top + bottom - 1f) / 2f - target.y, 0f);
        Tint(0.15f, 0.8f);

        // Fit the effects to the column, in the team colour.
        foreach (ParticleSystem ps in new[] { tornado, burst })
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.startColor = colour;
        }
        // The column mesh, in the team colour; nothing to see until it strikes.
        if (column != null)
        {
            MeshFilter filter = column.GetComponent<MeshFilter>();
            Bounds b = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds : new Bounds(Vector3.zero, new Vector3(2f, 6f, 2f));
            columnRadius = Mathf.Max(0.01f, b.extents.x);
            columnHeight = Mathf.Max(0.01f, b.size.y);
            columnCentre = b.center.y;
            columnRenderer = column.GetComponent<Renderer>();
            if (columnRenderer != null)
            {
                var look = new MaterialPropertyBlock();
                columnRenderer.GetPropertyBlock(look);
                Color c = colour;
                if (columnRenderer.sharedMaterial != null && columnRenderer.sharedMaterial.HasProperty(ColourId)) c.a = columnRenderer.sharedMaterial.color.a;
                look.SetColor(ColourId, c);
                look.SetFloat(SeedId, Random.Range(0f, 100f));
                columnRenderer.SetPropertyBlock(look);
            }
        }

        // Particles leave a ring around the column's foot and spiral up it; its width is set from Update.
        var spin = tornado.main;
        spin.loop = true; // runs until Update stops it
        spin.startLifetime = new ParticleSystem.MinMaxCurve(ParticleClimbTime * 0.8f, ParticleClimbTime);
        var swirl = tornado.shape;
        swirl.shapeType = ParticleSystemShapeType.Cone;
        swirl.angle = 0f;
        swirl.radiusThickness = 0f; // from the edge: around the column, not inside it
        swirl.arc = 360f;
        swirl.arcMode = ParticleSystemShapeMultiModeValue.Random;
        swirl.arcSpread = 1f / SpiralArms; // only from the arms' feet, so each traces a helix
        tornado.transform.localPosition = new Vector3(0f, bottom - target.y, 0f);
        var climb = tornado.velocityOverLifetime;
        climb.x = climb.z = 0f;
        climb.y = (top - bottom) / ParticleClimbTime;
        climb.radial = 0f; // no drifting out: SetWidth keeps them on the column
        climb.orbitalX = climb.orbitalZ = 0f;
        climb.orbitalY = SpiralTurns * 2f * Mathf.PI; // radians per second
        var emission = tornado.emission; // as dense in a tall column as in the default one (see SetWidth)
        if (tornadoRate < 0f) tornadoRate = emission.rateOverTimeMultiplier;
        width = 1f;
        SetWidth(0f);
        var spray = burst.shape; // a ring at the front, emitted from Update
        spray.radiusThickness = 0f;
        var splash = burst.emission;
        if (burstCount < 0f) burstCount = splash.burstCount > 0 ? splash.GetBurst(0).count.constantMax : 220f;
        splash.enabled = false;
    }

    // The lowest surface under the area: straight down from the target and around it.
    float FindBottom()
    {
        float lowest = target.y;
        for (int i = 0; i <= 8; i++)
        {
            Vector3 from = target + Vector3.up * 0.5f;
            if (i > 0) from += Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * radius * 0.7f;
            foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, groundSearch, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
                lowest = Mathf.Min(lowest, hit.point.y);
        }
        return lowest;
    }

    void Update()
    {
        float t = Time.time - startTime;
        if (!struck)
        {
            Tint(0.1f + 0.1f * Mathf.Sin(t * 18f), 0.8f); // flickers while it's coming
            if (t >= delay) Strike();
            return;
        }

        // Grows out of nothing into a thin column, widens to nearly the whole area (the ink and
        // damage spreading with it), holds, and shrinks back to nothing.
        float s = t - delay;
        float form = 1f - Mathf.Pow(1f - Mathf.Clamp01(s / riseTime), 3f); // eases out: soon a thin column
        float spread = Mathf.SmoothStep(0f, 1f, (s - riseTime) / spreadTime);
        float close = Mathf.SmoothStep(0f, 1f, (s - AttackTime - holdTime) / shrinkTime);
        float thin = tightRadius * form;
        Front = Mathf.Lerp(thin, radius, spread);
        SetWidth(Mathf.Lerp(thin, FullWidth, spread) * (1f - close));
        if (column != null) column.localRotation = Quaternion.Euler(0f, columnSpin * s, 0f);
        if (authoritative) Advance();
        Splash(spread);

        float k = Mathf.Clamp01(s / AttackTime);
        Tint(0.15f * (1f - k), 0.8f * (1f - k));
        bool done = s >= AttackTime + holdTime + shrinkTime;
        if (done && tornado.isEmitting) tornado.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        if (done && !tornado.IsAlive(true) && !burst.IsAlive(true)) Destroy(gameObject);
    }

    // Sizes the whole column at once: the mesh spans the column from bottom to top at this radius,
    // new particles leave a ring just outside it, and those already climbing are moved out (or in)
    // to match, so they keep spiralling around it.
    void SetWidth(float w)
    {
        if (column != null)
        {
            float length = top - bottom, tall = length / columnHeight;
            column.localScale = new Vector3(w / columnRadius, tall, w / columnRadius);
            column.localPosition = new Vector3(0f, bottom - target.y + length / 2f - columnCentre * tall, 0f);
            bool shown = w > 0.01f;
            if (column.gameObject.activeSelf != shown) column.gameObject.SetActive(shown);
        }
        var swirl = tornado.shape;
        swirl.radius = Mathf.Max(0.05f, w * SpiralOut);
        var emission = tornado.emission; // more as it widens, none until there's a column
        emission.rateOverTimeMultiplier = w < 0.05f ? 0f : tornadoRate * Mathf.Max(1f, (top - bottom) / height) * Mathf.Lerp(0.5f, 1.5f, w / FullWidth);
        if (Mathf.Abs(w - width) < 1e-4f) return;
        float scale = w / width;
        bool rescale = width > 0.05f && w > 0.05f; // nothing sensible to scale from (or to) at nothing
        width = w;
        if (!rescale) return;
        int max = tornado.main.maxParticles;
        if (swirling == null || swirling.Length < max) swirling = new ParticleSystem.Particle[max];
        int n = tornado.GetParticles(swirling);
        for (int i = 0; i < n; i++)
        {
            Vector3 p = swirling[i].position; // local to the tornado, its axis through the origin
            p.x *= scale;
            p.z *= scale;
            swirling[i].position = p;
        }
        tornado.SetParticles(swirling, n);
    }

    // Sprays the share of the burst for how far the front moved since last frame, off its edge.
    void Splash(float spread)
    {
        splashOwed += burstCount * (spread - splashedTo);
        splashedTo = spread;
        int n = Mathf.FloorToInt(splashOwed);
        if (n <= 0) return;
        splashOwed -= n;
        var ring = burst.shape;
        ring.radius = Mathf.Max(tightRadius, Front);
        burst.Emit(n);
    }

    void Tint(float core, float rim)
    {
        Renderer r = marker.GetComponent<Renderer>();
        if (r == null) return;
        r.GetPropertyBlock(block);
        block.SetColor(ColourId, colour);
        block.SetFloat(CoreAlpha, core);
        block.SetFloat(RimAlpha, rim);
        r.SetPropertyBlock(block);
    }

    void Strike()
    {
        struck = true;
        tornado.Play(true);
        burst.Play(true);
        if (authoritative) CollectSurfaces();
    }

    // Paints and hits everything the spreading front has reached so far.
    void Advance()
    {
        while (painted < unpainted.Count && unpainted[painted].distance <= Front)
        {
            RaycastHit h = unpainted[painted++].hit;
            SurfaceInkManager ink = h.collider != null ? h.collider.GetComponent<SurfaceInkManager>() : null;
            if (ink != null) ink.Splat(ink.UVFromHit(h), splashSize, team);
        }
        Damage();
    }

    // Every surface in the column, nearest the middle first: down through it over a grid (all
    // stacked floors), and in rings at every height both out from its axis and in from its edge
    // (walls facing either way, including the sides of whatever the target is standing on).
    void CollectSurfaces()
    {
        var hits = new List<RaycastHit>();
        float floor = bottom - 2f;
        for (float x = -radius; x <= radius; x += floorRaySpacing)
        for (float z = -radius; z <= radius; z += floorRaySpacing)
        {
            if (x * x + z * z > radius * radius) continue;
            hits.AddRange(Physics.RaycastAll(new Vector3(target.x + x, top, target.z + z), Vector3.down, top - floor,
                                             PhysicsLayers.Environment, QueryTriggerInteraction.Ignore));
        }
        for (float y = bottom + 0.5f; y < top; y += wallRayHeightStep)
        for (float a = 0f; a < 360f; a += wallRayAngleStep)
        {
            Vector3 axis = new Vector3(target.x, y, target.z);
            Vector3 outward = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
            hits.AddRange(Physics.RaycastAll(axis, outward, radius, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore));
            hits.AddRange(Physics.RaycastAll(axis + outward * radius, -outward, radius, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore));
        }

        unpainted.Clear();
        painted = 0;
        foreach (RaycastHit h in hits)
            if (h.collider.GetComponent<SurfaceInkManager>() != null)
                unpainted.Add((new Vector2(h.point.x - target.x, h.point.z - target.z).magnitude, h));
        unpainted.Sort((a, b) => a.distance.CompareTo(b.distance));
    }

    public bool Contains(Vector3 point) =>
        new Vector2(point.x - target.x, point.z - target.z).magnitude <= radius &&
        point.y >= bottom - 1f && point.y <= top;

    // Lethal across the core, then falling from a full health bar to edgeDamage at the edge.
    public float DamageAt(Vector3 point)
    {
        if (!Contains(point)) return 0f;
        float d = new Vector2(point.x - target.x, point.z - target.z).magnitude;
        float core = radius * coreFraction;
        if (d <= core) return damage;
        return Mathf.Lerp(FullHealth, edgeDamage, (d - core) / (radius - core));
    }

    bool Reached(Vector3 point) => Contains(point) && new Vector2(point.x - target.x, point.z - target.z).magnitude <= Front;

    // Each enemy (and enemy sub) once, as the front reaches them. Hits route like a shot's
    // (forwarded to remote players, blocked by the bubble shield).
    void Damage()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null)
            foreach (PlayerController p in gm.Players)
                if (p != null && p.isActiveAndEnabled && p.Team != 0 && p.Team != team && p.Hitbox != null && !hit.Contains(p) && Reached(p.BodyCenter))
                {
                    hit.Add(p);
                    p.Hitbox.TakeDamage(DamageAt(p.BodyCenter), team, ownerId, "Inkstrike");
                }
        var subs = new List<SubDevice>(SubDevice.All);
        foreach (SubDevice d in subs)
            if (d != null && d.Team != team && !hit.Contains(d) && Reached(d.transform.position))
            {
                hit.Add(d);
                d.TakeDamage(DamageAt(d.transform.position), team, ownerId);
            }
    }
}
