using System.Collections.Generic;
using UnityEngine;

// Special: a column of ink called in from the map. The column is marked for everyone, then after a
// moment an ink tornado tears through it. The owner's client paints every surface in the column
// (each floor stacked in it, and walls at every height) and hits whoever is inside: lethal in the
// core, falling off to non-fatal at the edge. Other clients show the marker and tornado; the paint
// and damage reach them through the usual Splat/Damage messages.
public class InkStrike : MonoBehaviour
{
    [Header("Area")]
    [SerializeField] float radius = 6f;
    [SerializeField] float height = 18f;          // the column reaches this far above the target
    [SerializeField] float delay = 1f;            // marked for this long before it strikes

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
    [SerializeField] ParticleSystem tornado;      // ink swirling up through the column
    [SerializeField] ParticleSystem burst;        // spray off the ground as it hits
    [SerializeField] float tornadoTime = 1.4f;

    static readonly int CoreAlpha = Shader.PropertyToID("_CoreAlpha");
    static readonly int RimAlpha = Shader.PropertyToID("_RimAlpha");
    static readonly int ColourId = Shader.PropertyToID("_Color");

    ulong ownerId;
    int team;
    Vector3 target;
    Color colour = Color.white;
    bool authoritative, struck;
    float startTime;
    MaterialPropertyBlock block;

    public float Radius => radius;
    public float Height => height;
    public float Delay => delay;
    public bool Struck => struck;
    public Vector3 Target => target;
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

        // Unity's cylinder is 1 wide and 2 tall: span from just below the target to the top.
        marker.localScale = new Vector3(radius * 2f, (height + 1f) / 2f, radius * 2f);
        marker.localPosition = new Vector3(0f, (height - 1f) / 2f, 0f);
        Tint(0.15f, 0.8f);

        // Fit the effects to the column, in the team colour.
        foreach (ParticleSystem ps in new[] { tornado, burst })
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.startColor = colour;
        }
        var swirl = tornado.shape; // a funnel: narrow at the ground, out to the column's edge at the top
        swirl.radius = radius * 0.3f;
        swirl.length = height;
        swirl.angle = Mathf.Atan2(radius * 0.6f, height) * Mathf.Rad2Deg;
        var spray = burst.shape;
        spray.radius = radius * 0.9f;
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

        float k = Mathf.Clamp01((t - delay) / tornadoTime);
        Tint(0.15f * (1f - k), 0.8f * (1f - k));
        if (k >= 1f && tornado.isEmitting) tornado.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        if (k >= 1f && !tornado.IsAlive(true) && !burst.IsAlive(true)) Destroy(gameObject);
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
        if (!authoritative) return;
        Paint();
        Damage();
    }

    // Every surface in the column: down through it over a grid (all stacked floors), and out from
    // its axis in rings at every height (walls).
    void Paint()
    {
        var hits = new List<RaycastHit>();
        float top = target.y + height, bottom = target.y - 2f;
        for (float x = -radius; x <= radius; x += floorRaySpacing)
        for (float z = -radius; z <= radius; z += floorRaySpacing)
        {
            if (x * x + z * z > radius * radius) continue;
            hits.AddRange(Physics.RaycastAll(new Vector3(target.x + x, top, target.z + z), Vector3.down, top - bottom,
                                             PhysicsLayers.Environment, QueryTriggerInteraction.Ignore));
        }
        for (float y = target.y + 0.5f; y < top; y += wallRayHeightStep)
        for (float a = 0f; a < 360f; a += wallRayAngleStep)
            hits.AddRange(Physics.RaycastAll(new Vector3(target.x, y, target.z), Quaternion.Euler(0f, a, 0f) * Vector3.forward, radius,
                                             PhysicsLayers.Environment, QueryTriggerInteraction.Ignore));

        foreach (RaycastHit hit in hits)
        {
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink != null) ink.Splat(ink.UVFromHit(hit), splashSize, team);
        }
    }

    public bool Contains(Vector3 point) =>
        new Vector2(point.x - target.x, point.z - target.z).magnitude <= radius &&
        point.y >= target.y - 1f && point.y <= target.y + height;

    // Lethal across the core, then falling from a full health bar to edgeDamage at the edge.
    public float DamageAt(Vector3 point)
    {
        if (!Contains(point)) return 0f;
        float d = new Vector2(point.x - target.x, point.z - target.z).magnitude;
        float core = radius * coreFraction;
        if (d <= core) return damage;
        return Mathf.Lerp(FullHealth, edgeDamage, (d - core) / (radius - core));
    }

    // Hits route like a shot's (forwarded to remote players, blocked by the bubble shield).
    void Damage()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null)
            foreach (PlayerController p in gm.Players)
                if (p != null && p.isActiveAndEnabled && p.Team != 0 && p.Team != team && p.Hitbox != null && Contains(p.BodyCenter))
                    p.Hitbox.TakeDamage(DamageAt(p.BodyCenter), team, ownerId);
        var subs = new List<SubDevice>(SubDevice.All);
        foreach (SubDevice d in subs)
            if (d != null && d.Team != team && Contains(d.transform.position))
                d.TakeDamage(DamageAt(d.transform.position), team, ownerId);
    }
}
