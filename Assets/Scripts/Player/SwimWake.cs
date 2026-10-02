using System.Collections.Generic;
using UnityEngine;

// Ink pushed up around a swimming squid (its path is already pressed into the ink surface): a low
// ring around it, which grows and leans forward into a bow wave with speed, and a short V of wake
// swelling out of the ring's flanks and trailing back, longer and higher the faster it goes, with
// ripples and a spray of droplets along it. Nothing while it sits still: a hidden squid stays
// hidden. Follows the surface it's swimming in (floor, slope or wall), fades in and out, and is
// rebuilt as one world-space mesh each frame, drawn with the ink volume material in the team
// colour. Runs on remote copies too, from their replicated state.
public class SwimWake : MonoBehaviour
{
    [SerializeField] PlayerController player;
    [SerializeField] Material material;           // Ink/InkVolumeLit (shadowed like the ink it sits on)
    [SerializeField] float fullSpeed = 10f;       // swim speed for the biggest ring and longest wakes

    [Header("Ring")]
    [SerializeField] float ringRadius = 0.45f;    // at rest
    [SerializeField] float ringGrowth = 0.25f;    // extra radius at full speed
    [SerializeField] float ringStretch = 0.3f;    // pushed out in front at full speed (a bow)
    [SerializeField] float ringWidth = 0.16f;     // half-width of its ridge
    [SerializeField] float ringHeight = 0.05f;    // at rest
    [SerializeField] float bowHeight = 0.12f;     // its front at full speed

    [Header("Side wakes")]
    [SerializeField] float wakeLength = 1.8f;     // each arm, at full speed
    [SerializeField] float wakeAngle = 25f;       // out from straight back
    [SerializeField] float wakeWidth = 0.14f;     // half-width where it leaves the ring
    [SerializeField] float wakeHeight = 0.11f;    // there, at full speed
    [SerializeField] float rippleLength = 0.5f;   // metres between crests running out along the arms
    [SerializeField] float rippleSpeed = 3f;      // metres per second

    [Header("Spray")]
    [SerializeField] ParticleSystem spray;        // droplets thrown off the side wakes (world space, emitted from here)
    [SerializeField] float sprayRate = 60f;       // per side wake per second, at full speed
    [SerializeField] float sprayLift = 2f;        // upward speed, metres per second (at full speed)
    [SerializeField] float sprayOut = 0.8f;       // and outward

    const int RingSegments = 28;
    const int ArmSections = 9;
    const int Across = 5;              // vertices across every ridge
    const float SurfaceProbe = 1.5f;   // how far to look for the surface under the squid
    const float Sink = 0.03f;          // ridge edges tucked just under the surface
    const float FadeRate = 5f;         // in and out, per second
    const float Smoothing = 8f;        // speed and heading follow the squid at this rate
    const float SurfaceNoise = 0.025f; // the material's wobble, in metres: kept well under the ridges' size
    const float RingStart = 0.03f;     // share of full speed below which there's no ring (at rest, settling)
    const float RingSpeed = 0.15f;     // and by which it's fully up
    const float ArmSwell = 0.3f;       // share of each side wake spent swelling out of the ring

    static readonly int ColourId = Shader.PropertyToID("_Color");
    static readonly int SeedId = Shader.PropertyToID("_Seed");
    static readonly int ShapeAmpId = Shader.PropertyToID("_ShapeAmp");

    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<int> tris = new List<int>();
    Mesh mesh;
    GameObject view;
    MeshRenderer viewRenderer;
    MaterialPropertyBlock block;
    Vector3 centre, up = Vector3.up, heading = Vector3.forward;
    float shown, speed, sprayOwed;

    public float Shown => shown;                                     // tests
    public float Speed => speed;                                     // share of fullSpeed
    public float WakeLength => wakeLength * speed * shown;           // each arm, now
    public float RingShown => shown * Mathf.SmoothStep(0f, 1f, (speed - RingStart) / (RingSpeed - RingStart));
    public int VertexCount => mesh != null ? mesh.vertexCount : 0;
    public Bounds Bounds => mesh != null ? mesh.bounds : default;    // world space
    public Renderer View => viewRenderer;

    void Awake()
    {
        if (player == null) player = GetComponent<PlayerController>();
        mesh = new Mesh { name = "SwimWake" };
        mesh.MarkDynamic();
        // Under the player entity, but built in world space (it follows the squid's surface, not its
        // transform), so it's pinned to the world origin every frame.
        view = new GameObject("SwimWake");
        view.transform.SetParent(player != null && player.transform.parent != null ? player.transform.parent : transform, false);
        view.AddComponent<MeshFilter>().sharedMesh = mesh;
        viewRenderer = view.AddComponent<MeshRenderer>();
        viewRenderer.sharedMaterial = material;
        viewRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        block = new MaterialPropertyBlock();
        block.SetFloat(SeedId, Random.Range(0f, 100f));
        block.SetFloat(ShapeAmpId, SurfaceNoise);
    }

    void OnDestroy()
    {
        if (view != null) Destroy(view);
        if (mesh != null) Destroy(mesh);
    }

    void OnDisable()
    {
        shown = speed = 0f;
        if (mesh != null) mesh.Clear();
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;
        view.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        bool submerged = player != null && !player.IsDead && player.IsInInk && FindSurface(out centre, out up);
        shown = Mathf.MoveTowards(shown, submerged ? 1f : 0f, dt * FadeRate); // fades out where it was
        if (submerged)
        {
            float k = 1f - Mathf.Exp(-Smoothing * dt);
            Vector3 travel = Vector3.ProjectOnPlane(player.TravelVelocity, up);
            speed = Mathf.Lerp(speed, Mathf.Clamp01(travel.magnitude / fullSpeed), k);
            if (travel.sqrMagnitude > 0.1f) heading = Vector3.Slerp(heading, travel.normalized, k);
        }
        heading = Vector3.ProjectOnPlane(heading, up);
        if (heading.sqrMagnitude < 1e-4f) heading = Vector3.ProjectOnPlane(player != null ? player.transform.forward : Vector3.forward, up);
        heading.Normalize();

        verts.Clear();
        tris.Clear();
        if (RingShown > 0.001f) Build();
        mesh.Clear();
        if (tris.Count == 0) return;
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        Color c = player != null ? player.TeamColour : Color.white;
        if (material != null) c.a = material.color.a;
        block.SetColor(ColourId, c);
        viewRenderer.SetPropertyBlock(block);
    }

    // Under the squid, along its swim pose's up (the floor's or the wall's normal).
    bool FindSurface(out Vector3 point, out Vector3 normal)
    {
        normal = player.SwimUp;
        if (Physics.Raycast(player.BodyCenter + normal * 0.3f, -normal, out RaycastHit hit, SurfaceProbe, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
        {
            point = hit.point;
            normal = hit.normal;
            return true;
        }
        point = Vector3.zero;
        return false;
    }

    void Build()
    {
        float t = Time.time;
        Vector3 side = Vector3.Cross(up, heading).normalized;
        float ring = RingShown;

        // The ring: egg-shaped forward and higher at the front as it speeds up, gently wobbling.
        float radius = ringRadius + ringGrowth * speed;
        int ringStart = verts.Count;
        for (int i = 0; i < RingSegments; i++)
        {
            float a = 2f * Mathf.PI * i / RingSegments;
            float front = Mathf.Cos(a);
            Vector3 outward = heading * front + side * Mathf.Sin(a);
            float r = radius * (1f + ringStretch * speed * Mathf.Max(0f, front));
            float h = Mathf.Lerp(ringHeight, bowHeight, speed * Mathf.Max(0f, front)) * (1f + 0.2f * Mathf.Sin(3f * a + t * 5f));
            Ridge(centre + outward * r, outward, ringWidth, h * ring);
            if (i > 0) Join(verts.Count - 2 * Across, verts.Count - Across);
        }
        Join(verts.Count - Across, ringStart); // closed

        // The side wakes: out of the ring's flanks (at its height and width there), swelling to
        // their own size, then angled back and out, tapering to nothing.
        float length = WakeLength;
        if (length < 0.05f) return;
        float angle = wakeAngle * Mathf.Deg2Rad;
        float flank = ringHeight * ring; // the ring's height at its sides
        float peak = wakeHeight * speed * shown;
        for (int s = -1; s <= 1; s += 2)
        {
            Vector3 along = (-heading * Mathf.Cos(angle) + side * (s * Mathf.Sin(angle))).normalized;
            Vector3 across = Vector3.Cross(up, along).normalized;
            Vector3 start = centre + side * (s * radius);
            for (int i = 0; i < ArmSections; i++)
            {
                float f = (float)i / (ArmSections - 1);
                ArmShape(f, length, flank, peak, t, out float h, out float half);
                Ridge(start + along * (length * f), across, half, h);
                if (i > 0) Join(verts.Count - 2 * Across, verts.Count - Across);
            }
            Spray(start, along, side * s, length, flank, peak, t);
        }
    }

    // Height and half-width a share f of the way along a side wake.
    void ArmShape(float f, float length, float flank, float peak, float t, out float height, out float halfWidth)
    {
        float swell = Mathf.SmoothStep(0f, 1f, f / ArmSwell);
        float taper = Mathf.Min(1f, Mathf.Pow((1f - f) / (1f - ArmSwell), 1.2f));
        float ripple = 1f + 0.3f * swell * Mathf.Sin(2f * Mathf.PI * (f * length - t * rippleSpeed) / rippleLength);
        height = Mathf.Lerp(flank, peak, swell) * taper * ripple;
        halfWidth = Mathf.Lerp(ringWidth, wakeWidth, swell) * (1f + 0.8f * f);
    }

    // Droplets thrown up and out off a side wake's crest, more the faster it goes.
    void Spray(Vector3 start, Vector3 along, Vector3 outward, float length, float flank, float peak, float t)
    {
        if (spray == null) return;
        sprayOwed += sprayRate * speed * shown * Time.deltaTime;
        int n = Mathf.FloorToInt(sprayOwed);
        if (n <= 0) return;
        sprayOwed -= n;
        Color c = player != null ? player.TeamColour : Color.white;
        Vector3 carried = heading * (speed * fullSpeed * 0.3f);
        for (int i = 0; i < n; i++)
        {
            float f = Random.Range(ArmSwell * 0.5f, 0.85f);
            ArmShape(f, length, flank, peak, t, out float h, out _);
            var p = new ParticleSystem.EmitParams
            {
                position = start + along * (length * f) + up * h,
                velocity = up * (sprayLift * Random.Range(0.6f, 1.3f) * (0.4f + 0.6f * speed))
                         + outward * (sprayOut * Random.Range(0.3f, 1.2f)) + carried,
                startColor = c,
                applyShapeToPosition = false
            };
            spray.Emit(p, 1);
        }
    }

    // One cross-section: a rounded ridge across `across`, its edges just under the surface.
    void Ridge(Vector3 middle, Vector3 across, float halfWidth, float height)
    {
        for (int j = 0; j < Across; j++)
        {
            float u = -1f + 2f * j / (Across - 1);
            float bump = 1f - u * u;
            verts.Add(middle + across * (u * halfWidth) + up * (height * bump * bump - Sink));
        }
    }

    // Quads between two cross-sections, wound to face up off the surface.
    void Join(int a, int b)
    {
        for (int j = 0; j < Across - 1; j++)
        {
            int a0 = a + j, a1 = a + j + 1, b0 = b + j, b1 = b + j + 1;
            bool facesUp = Vector3.Dot(Vector3.Cross(verts[b0] - verts[a0], verts[b1] - verts[a0]), up) > 0f;
            if (facesUp) { tris.Add(a0); tris.Add(b0); tris.Add(b1); tris.Add(a0); tris.Add(b1); tris.Add(a1); }
            else         { tris.Add(a0); tris.Add(b1); tris.Add(b0); tris.Add(a0); tris.Add(a1); tris.Add(b1); }
        }
    }
}
