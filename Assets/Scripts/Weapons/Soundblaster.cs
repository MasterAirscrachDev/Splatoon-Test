using System.Collections.Generic;
using UnityEngine;

// Special: a big speaker set down in front of the player, facing the way they're looking (on the
// flat). Its targeting beam shows at once: a tube out of the horn's mouth, widening to `radius`
// over `flareLength`, then straight on across the whole map (`beamLength`), through walls. After
// `windup` it blasts for `blastTime`: everyone on the other team inside the beam (walls don't stop
// it) takes `tickDamage` every `tickInterval`. Then it's gone. The owner's copy deals the damage
// (routed like any hit); everyone else's shows the speaker and beam on the same timer.
public class Soundblaster : MonoBehaviour
{
    [Header("Beam")]
    [SerializeField, Tooltip("The beam's full radius (metres)")] float radius = 4f;
    [SerializeField, Tooltip("It widens from the horn's mouth to Radius over this far (metres)")] float flareLength = 3f;
    [SerializeField, Tooltip("How far it reaches (metres): across the whole map")] float beamLength = 200f;
    [SerializeField, Tooltip("The horn's mouth, in the model's space: where the beam starts")] Vector3 mouth = new Vector3(0f, 1.05f, 1.15f);
    [SerializeField, Tooltip("The horn's mouth's radius (metres): the beam's at its start")] float mouthRadius = 0.7f;
    [SerializeField, Tooltip("Extra reach for a player's body: they're hit when this close to the beam's edge (metres)")] float bodyPad = 0.4f;

    [Header("Timing and damage")]
    [SerializeField, Tooltip("Seconds of the targeting beam before it blasts")] float windup = 1f;
    [SerializeField, Tooltip("Seconds it blasts for")] float blastTime = 4f;
    [SerializeField, Tooltip("Seconds between hits while it blasts")] float tickInterval = 0.2f;
    [SerializeField, Tooltip("Each hit's damage, to everyone inside")] float tickDamage = 33f;
    [SerializeField, Tooltip("Seconds it takes to fade away after")] float fadeTime = 0.4f;

    [Header("Look")]
    [SerializeField, Tooltip("The beam's material (Ink/SoundBeam)")] Material beamMaterial;
    [SerializeField, Tooltip("Segments round the beam")] int beamSides = 24;
    [SerializeField, Tooltip("The model: hidden as it fades")] Transform model;

    public const string Cause = "Soundblaster";
    static readonly int ColourId = Shader.PropertyToID("_Color");
    static readonly int FadeId = Shader.PropertyToID("_Fade");
    static readonly int BlastId = Shader.PropertyToID("_Blast");
    static readonly int LengthId = Shader.PropertyToID("_Length");

    ulong ownerId;
    int team;
    bool authoritative;
    float startTime;
    int ticks;
    Color colour = Color.white;
    MeshRenderer beam;
    MaterialPropertyBlock block;

    public float Radius => radius;
    public float Windup => windup;
    public float BlastTime => blastTime;
    public float TickInterval => tickInterval;
    public float TickDamage => tickDamage;
    public float BeamLength => beamLength;
    public int Ticks => ticks;                    // tests: hits dealt so far (rounds of them)
    public bool Blasting => Age >= windup && Age < windup + blastTime;
    public bool BeamShown => beam != null && beam.enabled;
    public float Age => Time.time - startTime;
    public int Team => team;
    public Vector3 Mouth => transform.TransformPoint(mouth);

    public void Begin(ulong ownerId, int team, Vector3 position, float yaw, bool ownedLocally)
    {
        this.ownerId = ownerId;
        this.team = team;
        authoritative = ownedLocally;
        startTime = Time.time;
        transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        NetGameManager gm = NetGameManager.Instance;
        colour = gm != null && Teams.Valid(team) ? gm.TeamColour(team) : Color.white;
        Weapon.TintInkParts(gameObject, colour);
        block = new MaterialPropertyBlock();
        beam = MakeBeam();
        SetBeam(0f, 0f);
    }

    void Update()
    {
        float t = Age;
        float end = windup + blastTime;
        if (t < windup) SetBeam(Mathf.Clamp01(t / 0.2f), 0f); // the targeting beam fades in
        else if (t < end)
        {
            SetBeam(1f, 1f);
            // Every tickInterval from the start of the blast, the owner hits whoever's inside.
            while (ticks * tickInterval <= t - windup && ticks * tickInterval < blastTime - 1e-4f)
            {
                ticks++;
                if (authoritative) Hit();
            }
        }
        else
        {
            float k = 1f - Mathf.Clamp01((t - end) / Mathf.Max(fadeTime, 0.01f));
            SetBeam(k, 1f);
            if (model != null) model.localScale = Vector3.one * Mathf.Max(0.001f, k);
            if (k <= 0f) Destroy(gameObject);
        }
    }

    // Whether a point is inside the beam (walls don't matter), with `pad` of extra reach.
    public bool Contains(Vector3 point, float pad = 0f)
    {
        Vector3 local = Quaternion.Inverse(transform.rotation) * (point - Mouth);
        if (local.z < -pad || local.z > beamLength) return false;
        float r = Mathf.Lerp(mouthRadius, radius, Mathf.Clamp01(local.z / Mathf.Max(flareLength, 0.01f)));
        return new Vector2(local.x, local.y).magnitude <= r + pad;
    }

    // One round of hits: everyone on another team inside, and their subs.
    void Hit()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null)
            foreach (PlayerController p in gm.Players)
                if (p != null && p.isActiveAndEnabled && Teams.Valid(p.Team) && p.Team != team && !p.IsDead && p.Hitbox != null && Contains(p.BodyCenter, bodyPad))
                    p.Hitbox.TakeDamage(tickDamage, team, ownerId, Cause);
        foreach (TargetDummy dummy in TargetDummy.All)
            if (dummy.Hitbox != null && Contains(dummy.BodyCenter, bodyPad))
                dummy.Hitbox.TakeDamage(tickDamage, team, ownerId, Cause);
        foreach (SubDevice d in new List<SubDevice>(SubDevice.All))
            if (d != null && d.Team != team && Contains(d.transform.position, bodyPad))
                d.TakeDamage(tickDamage, team, ownerId);
    }

    void SetBeam(float fade, float blast)
    {
        if (beam == null) return;
        beam.enabled = fade > 0.001f;
        beam.GetPropertyBlock(block);
        block.SetColor(ColourId, colour);
        block.SetFloat(FadeId, fade);
        block.SetFloat(BlastId, blast);
        block.SetFloat(LengthId, beamLength);
        beam.SetPropertyBlock(block);
    }

    // The beam: a tube from the mouth along +Z, flaring from mouthRadius to radius over flareLength,
    // then straight to beamLength. UVs: u round it, v along it in metres (the shader's rings).
    MeshRenderer MakeBeam()
    {
        var go = new GameObject("Beam");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = mouth;
        var rings = new List<float>();
        const int FlareRings = 10;
        for (int i = 0; i <= FlareRings; i++) rings.Add(flareLength * i / FlareRings);
        for (float z = flareLength + 10f; z < beamLength; z += 10f) rings.Add(z); // (the shader fades its far end smoothly)
        rings.Add(beamLength);
        int sides = Mathf.Max(6, beamSides), around = sides + 1;
        var verts = new Vector3[rings.Count * around];
        var normals = new Vector3[verts.Length];
        var uvs = new Vector2[verts.Length];
        var tris = new int[(rings.Count - 1) * sides * 6];
        for (int r = 0; r < rings.Count; r++)
        {
            float z = rings[r];
            float rad = Mathf.Lerp(mouthRadius, radius, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(z / Mathf.Max(flareLength, 0.01f))));
            for (int s = 0; s < around; s++)
            {
                float a = s * Mathf.PI * 2f / sides;
                Vector3 n = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                verts[r * around + s] = n * rad + Vector3.forward * z;
                normals[r * around + s] = n;
                uvs[r * around + s] = new Vector2((float)s / sides, z);
            }
        }
        int k = 0;
        for (int r = 0; r < rings.Count - 1; r++)
            for (int s = 0; s < sides; s++)
            {
                int a = r * around + s, b = a + 1, c = a + around, d = c + 1;
                tris[k++] = a; tris[k++] = c; tris[k++] = b;
                tris[k++] = b; tris[k++] = c; tris[k++] = d;
            }
        var mesh = new Mesh { name = "SoundblasterBeam" };
        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = beamMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return mr;
    }
}
