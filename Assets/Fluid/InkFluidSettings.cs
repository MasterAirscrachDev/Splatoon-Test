using UnityEngine;

// The ink's look, in one place for the game and the fluid lab (Resources/InkFluidSettings): how the
// FluidRenderer draws ink (renderer), and how big and how many drops InkFluid draws for each shot,
// splash and blast.
[CreateAssetMenu(menuName = "Ink/Fluid Settings", fileName = "InkFluidSettings")]
public class InkFluidSettings : ScriptableObject
{
    [System.Serializable]
    public class Companions
    {
        public int count = 2;                                   // blobs travelling with each seen shot...
        public Vector2 size = new Vector2(0.5f, 0.85f);         // ...this big (share of the shot)...
        public float spread = 2f;                               // ...off its direction, up and down (degrees)...
        public float across = 2f;                               // ...and side to side...
        public float speed = 0.05f;                             // ...and its speed by up to this share
    }

    [Header("Shaders")]
    public Shader particleShader;   // Fluid/Particles
    public Shader smoothShader;     // Fluid/Smooth
    public Shader compositeShader;  // Fluid/Composite

    [Header("Rendering")]
    public FluidRenderer.Look renderer = new FluidRenderer.Look();

    [Header("Shots")]
    [Tooltip("A drawn shot's radius, as a share of the radius of the splat it paints (at the map's ink scale)")]
    public float blobFill = 0.3f;
    public float stretch = 0.1f;                                // teardrop length per m/s (x radius)
    public float wobble = 0.12f;                                // radius wobble as it flies (share)
    [Range(0f, 1f)] public float fillersSeen = 0.33f;           // shooters' filler shots drawn (the rest only paint)
    public Companions shotCompanions = new Companions();
    public Companions rollerCompanions = new Companions { count = 4 };
    public Companions bucketCompanions = new Companions { count = 3, spread = 1f, across = 3f, speed = 0.1f }; // along its line: one long body of ink

    [Header("Splatter (where a shot lands)")]
    public Vector2Int crownBlobs = new Vector2Int(2, 4);        // thrown up and out (more for bigger shots)
    public Vector2 crownSize = new Vector2(0.35f, 0.55f);       // share of the shot
    public float crownKick = 2.5f;                              // m/s off the surface
    public int puddleDrops = 6;                                 // the puddle spreading along the surface...
    public float puddleTime = 0.22f;                            // ...and sinking into the paint

    [Header("Blasts")]
    public float coreReach = 0.45f;                             // the core's widest radius (share of the blast's)...
    public float coreTime = 0.35f;                              // ...swelling and collapsing
    public int coreBlobs = 6;                                   // the core is several blobs...
    public Vector2 coreSize = new Vector2(0.55f, 0.8f);         // ...this big (share of coreReach)...
    public float coreScatter = 0.2f;                            // ...set this far off the centre (share of the blast's radius)
    public int shellBlobs = 18;                                 // a shell bursting out...
    public Vector2 shellSize = new Vector2(0.1f, 0.16f);        // ...radius, share of the blast's...
    public Vector2 shellSpeed = new Vector2(4f, 6f);            // ...blast radii per second, slowing...
    public float shellLife = 0.2f;                              // ...gone quickly (no gravity: the burst, not falling ink)...
    [Range(0f, 1f)] public float shellShrink = 0.5f;            // ...shrinking away over this last share of it

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
