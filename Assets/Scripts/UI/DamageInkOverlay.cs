using UnityEngine;
using UnityEngine.UI;

// Enemy ink over the edges of our screen while we're hurt (UI/DamageInk on a full-screen image):
// it reaches in with the health we're missing, plus a splash on each hit, in the colour of the
// team that hit us, and recedes as we heal. A lethal hit floods it, then it drains away.
[RequireComponent(typeof(RawImage))]
public class DamageInkOverlay : MonoBehaviour
{
    [SerializeField] float riseSpeed = 14f;      // how fast it reaches in after a hit (per second, eased)
    [SerializeField] float fallSpeed = 4f;       // and draws back as we heal
    [SerializeField] float hitSplash = 0.25f;    // extra reach for a moment per full-health's worth of damage
    [SerializeField] float splashFade = 3f;      // the splash dies away this fast (per second)
    [SerializeField] float deathFlood = 1f;      // a lethal hit covers this much...
    [SerializeField] float deathDrain = 0.8f;    // ...then drains over this long

    static readonly int AmountId = Shader.PropertyToID("_Amount");
    static readonly int AspectId = Shader.PropertyToID("_Aspect");
    static readonly int SeedId = Shader.PropertyToID("_Seed");

    RawImage image;
    Material material;
    PlayerHitbox hitbox;
    float shown, splash, flood;

    public float Amount => shown + splash + flood; // tests
    public Color InkColour => image != null ? image.color : Color.clear;

    void Awake()
    {
        image = GetComponent<RawImage>();
        image.raycastTarget = false;
        material = new Material(image.material); // our own: its values change every frame
        image.material = material;
        material.SetFloat(SeedId, Random.Range(0f, 100f));
    }

    void OnDestroy()
    {
        if (hitbox != null) hitbox.Damaged -= OnDamaged;
        if (material != null) Destroy(material);
    }

    void Update()
    {
        PlayerController player = NetGameManager.LocalPlayer;
        PlayerHitbox current = player != null ? player.Hitbox : null;
        if (current != hitbox)
        {
            if (hitbox != null) hitbox.Damaged -= OnDamaged;
            hitbox = current;
            if (hitbox != null) hitbox.Damaged += OnDamaged;
            shown = splash = flood = 0f;
        }

        float target = hitbox != null && !player.IsDead ? 1f - hitbox.HealthNormalized : 0f;
        float speed = target > shown ? riseSpeed : fallSpeed;
        shown = Mathf.Lerp(shown, target, 1f - Mathf.Exp(-speed * Time.deltaTime));
        if (Mathf.Abs(shown - target) < 0.005f) shown = target; // settled, not creeping forever
        splash = Mathf.MoveTowards(splash, 0f, splashFade * Time.deltaTime * Mathf.Max(splash, 0.05f) * 4f);
        flood = Mathf.MoveTowards(flood, 0f, deathFlood / Mathf.Max(deathDrain, 0.01f) * Time.deltaTime);

        float amount = Mathf.Clamp01(shown + splash + flood);
        material.SetFloat(AmountId, amount);
        material.SetFloat(AspectId, Screen.height > 0 ? (float)Screen.width / Screen.height : 1.777f);
        image.enabled = amount > 0.005f;
    }

    void OnDamaged(float amount, string source)
    {
        if (hitbox == null) return;
        NetGameManager gm = NetGameManager.Instance;
        int team = hitbox.LastHitTeam;
        if (gm != null && team != 0) image.color = gm.TeamColour(team);
        splash = Mathf.Min(0.5f, splash + hitSplash * amount / 100f);
        if (hitbox.Health - amount <= 0f) flood = deathFlood; // this one's lethal
    }
}
