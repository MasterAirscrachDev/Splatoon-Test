using System.Collections.Generic;
using UnityEngine;

// What an explosion looks like: one ink sphere with a finely boiling surface that swells to most of
// the blast's reach and collapses, small droplets flung out of it (arcing down as they shrink), and
// a splash of particles, in the team colour. Spawned per explosion; gone after.
public class InkBlastEffect : MonoBehaviour
{
    [SerializeField] Transform sphere;          // a unit sphere: the core, also cloned for the droplets
    [SerializeField] GameObject splash;         // splash particles
    [SerializeField] float duration = 0.4f;     // the core swelling, then collapsing
    [SerializeField] float reach = 0.8f;        // the core's widest, as a share of the blast's radius
    [SerializeField] float coreLumpiness = 0.1f;// the core's surface noise (in its own size: it's 1 across)
    [SerializeField] float coreLumpScale = 16f; // how many lumps across it: fine, so it reads as a burst
    [SerializeField] float churn = 4f;          // how fast its surface boils
    [SerializeField] int droplets = 10;         // flung out of the core
    [SerializeField] Vector2 dropletSize = new Vector2(0.1f, 0.2f);  // across, as a share of the radius
    [SerializeField] Vector2 dropletSpeed = new Vector2(2f, 3.5f);     // radii per second
    [SerializeField] float dropletLife = 0.45f;

    const float LatestDroplet = 0.06f;          // droplets leave up to this long after the core starts

    static readonly int ColourId = Shader.PropertyToID("_Color");
    static readonly int SeedId = Shader.PropertyToID("_Seed");
    static readonly int ShapeAmpId = Shader.PropertyToID("_ShapeAmp");
    static readonly int NoiseFreqId = Shader.PropertyToID("_NoiseFreq");
    static readonly int FlowSpeedId = Shader.PropertyToID("_FlowSpeed");

    struct Droplet
    {
        public Transform t;
        public Vector3 velocity; // world, metres per second, at launch
        public float size;       // metres across
        public float delay;
    }

    Transform core;
    readonly List<Droplet> drops = new List<Droplet>();
    float radius, born;

    public int BlobCount => (core != null ? 1 : 0) + drops.Count; // tests

    public static InkBlastEffect Spawn(GameObject prefab, Vector3 at, float radius, Color colour)
    {
        GameObject go = Instantiate(prefab, at, Quaternion.identity);
        InkBlastEffect effect = go.GetComponent<InkBlastEffect>();
        if (effect != null) effect.Play(radius, colour);
        else Destroy(go, 1f);
        return effect;
    }

    void Play(float radius, Color colour)
    {
        this.radius = radius;
        born = Time.time;
        if (sphere != null)
        {
            sphere.gameObject.SetActive(false);
            core = Clone(colour, coreLumpiness, coreLumpScale);
            for (int i = 0; i < droplets; i++)
            {
                Vector3 dir = Random.onUnitSphere;
                dir.y = Mathf.Abs(dir.y) * 0.8f + 0.1f; // mostly outwards and up, so they arc
                drops.Add(new Droplet
                {
                    t = Clone(colour, 0.15f, 6f),
                    velocity = dir.normalized * (radius * Random.Range(dropletSpeed.x, dropletSpeed.y)),
                    size = radius * Random.Range(dropletSize.x, dropletSize.y),
                    delay = Random.Range(0f, LatestDroplet),
                });
            }
        }
        if (splash != null) InkParticles.Spawn(splash, transform.position, Quaternion.identity, colour);
        Update();
        Destroy(gameObject, Mathf.Max(duration, LatestDroplet + dropletLife) + 0.05f);
    }

    Transform Clone(Color colour, float lumpiness, float lumpScale)
    {
        Transform t = Instantiate(sphere, transform);
        t.gameObject.SetActive(true);
        t.localRotation = Random.rotation;
        if (t.TryGetComponent(out Renderer r))
        {
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            block.SetColor(ColourId, colour);
            block.SetFloat(SeedId, Random.Range(0f, 100f));
            block.SetFloat(ShapeAmpId, lumpiness);
            block.SetFloat(NoiseFreqId, lumpScale);
            block.SetFloat(FlowSpeedId, churn);
            r.SetPropertyBlock(block);
        }
        return t;
    }

    void Update()
    {
        float age = Time.time - born;
        if (core != null)
        {
            float k = age / duration;
            float swell = k < 0.35f ? Mathf.SmoothStep(0.2f, 1f, k / 0.35f) : Mathf.SmoothStep(1f, 0f, (k - 0.35f) / 0.65f);
            core.localScale = Vector3.one * (2f * radius * reach * swell);
            core.gameObject.SetActive(swell > 0.01f);
        }
        foreach (Droplet d in drops)
        {
            float t = age - d.delay;
            float k = t / dropletLife;
            bool live = t > 0f && k < 1f;
            d.t.gameObject.SetActive(live);
            if (!live) continue;
            // From inside the core's surface, flung out and falling, shrinking away.
            Vector3 start = d.velocity.normalized * (radius * reach * 0.6f);
            d.t.localPosition = start + d.velocity * t + 0.5f * Physics.gravity * t * t;
            d.t.localScale = Vector3.one * (d.size * (1f - k * k));
        }
    }
}
