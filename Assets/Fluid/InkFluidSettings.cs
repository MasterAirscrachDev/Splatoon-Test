using UnityEngine;

// The ink's look, in one place for the game and the fluid lab (Resources/InkFluidSettings): how the
// FluidRenderer draws ink (renderer), the size rule for drawn shots, and the splatter and blasts.
// Each weapon's own shots (their size, stretch and companions) are tuned on the weapon (ShotLook).
[CreateAssetMenu(menuName = "Ink/Fluid Settings", fileName = "InkFluidSettings")]
public class InkFluidSettings : ScriptableObject
{
    [Header("Shaders")]
    public Shader particleShader;   // Fluid/Particles
    public Shader smoothShader;     // Fluid/Smooth
    public Shader compositeShader;  // Fluid/Composite

    [Header("Rendering")]
    public FluidRenderer.Look renderer = new FluidRenderer.Look();

    [Header("Shots")]
    [Tooltip("The usual size of a drawn shot: its radius as a share of the radius of the splat it paints (at the map's ink scale). Scales every weapon's ink at once; each weapon's ShotLook Size multiplies it")]
    public float blobFill = 0.3f;
    [Tooltip("How much every blob's radius wobbles as it flies (share of its radius), so they don't look like rigid balls")]
    public float wobble = 0.12f;
    [Tooltip("The look of shots fired without one of their own (tests, and anything that doesn't say)")]
    public ShotLook defaultLook = new ShotLook();

    [Header("Splatter (where a seen shot lands)")]
    [Tooltip("Blobs thrown up and out where a shot lands (min, max: bigger shots throw more)")]
    public Vector2Int crownBlobs = new Vector2Int(2, 4);
    [Tooltip("Their size, a random share of the shot's (min, max)")]
    public Vector2 crownSize = new Vector2(0.35f, 0.55f);
    [Tooltip("How hard they're thrown up off the surface (metres per second)")]
    public float crownKick = 2.5f;
    [Tooltip("Blobs in the ring of the puddle that spreads out along the surface where a shot lands")]
    public int puddleDrops = 6;
    [Tooltip("Seconds the puddle takes to spread and sink into the paint")]
    public float puddleTime = 0.22f;
    [Tooltip("How much splatter and blast blobs stretch into teardrops as they fly (as ShotLook Stretch)")]
    public float stretch = 0.1f;

    [Header("Blasts")]
    [Tooltip("How wide the blast's core gets at its biggest: each core blob's radius, as a share of the blast's damage radius")]
    public float coreReach = 0.45f;
    [Tooltip("Seconds the core takes to swell and collapse")]
    public float coreTime = 0.35f;
    [Tooltip("Blobs making up the core (more: a lumpier surface)")]
    public int coreBlobs = 6;
    [Tooltip("Each core blob's size, a random share of Core Reach (min, max)")]
    public Vector2 coreSize = new Vector2(0.55f, 0.8f);
    [Tooltip("How far the core blobs sit off the centre (share of the blast's radius): higher is lumpier")]
    public float coreScatter = 0.2f;
    [Tooltip("Blobs in the shell bursting out of the blast")]
    public int shellBlobs = 18;
    [Tooltip("Each shell blob's radius, a random share of the blast's radius (min, max)")]
    public Vector2 shellSize = new Vector2(0.1f, 0.16f);
    [Tooltip("How fast the shell bursts out, in blast radii per second (min, max); it slows as it goes")]
    public Vector2 shellSpeed = new Vector2(4f, 6f);
    [Tooltip("Seconds the shell lasts (no gravity: it's the burst, not falling ink)")]
    public float shellLife = 0.2f;
    [Tooltip("The last share of its life over which each shell blob shrinks away (0: vanishes at once)")]
    [Range(0f, 1f)] public float shellShrink = 0.5f;

    static InkFluidSettings instance;
    public static InkFluidSettings Instance
    {
        get
        {
            if (instance == null) instance = Resources.Load<InkFluidSettings>("InkFluidSettings");
            if (instance == null) instance = CreateInstance<InkFluidSettings>(); // defaults
            return instance;
        }
    }
}
