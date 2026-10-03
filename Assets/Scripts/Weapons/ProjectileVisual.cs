using UnityEngine;

// The look of an ink projectile; ProjectileManager moves it. Team colour, a random wobble seed, and
// a stretch along its direction of travel. The prefab also says how big shots of its kind are when
// hitting things, and what splashes where they land.
public class ProjectileVisual : MonoBehaviour
{
    [SerializeField] float hitRadius = 0.12f;
    [SerializeField] GameObject splashParticlesPrefab;

    static readonly int ShaderDirection = Shader.PropertyToID("_Direction");
    static readonly int ShaderColor     = Shader.PropertyToID("_Color");
    static readonly int ShaderSeed      = Shader.PropertyToID("_Seed");

    Renderer visual;
    MaterialPropertyBlock block;

    public float HitRadius => hitRadius;
    public GameObject SplashParticles => splashParticlesPrefab;

    void Awake()
    {
        foreach (Transform child in transform) // the first child renderer
        {
            Renderer r = child.GetComponent<Renderer>();
            if (r != null) { visual = r; break; }
        }
        block = new MaterialPropertyBlock();
    }

    public void Show(Color colour, int splashSize, float scale = 1f)
    {
        if (visual == null) return;
        visual.GetPropertyBlock(block);
        block.SetColor(ShaderColor, colour);
        block.SetFloat(ShaderSeed, Random.Range(0f, 100f));
        visual.SetPropertyBlock(block);
        visual.transform.localScale = Vector3.one * (splashSize * 0.035f * scale);
    }

    public void Follow(Vector3 position, Vector3 velocity)
    {
        transform.position = position;
        if (visual == null) return;
        float speed = velocity.magnitude;
        Vector3 dir = speed > 0.1f ? velocity / speed : Vector3.down;
        visual.GetPropertyBlock(block);
        block.SetVector(ShaderDirection, new Vector4(dir.x, dir.y, dir.z, 1f + speed * 0.12f)); // w: stretch
        visual.SetPropertyBlock(block);
    }
}
