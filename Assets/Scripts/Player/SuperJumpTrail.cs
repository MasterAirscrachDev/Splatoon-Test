using UnityEngine;

// A Super Jump's trail: blobs of ink left along the flight in the jumper's team colour, shrinking
// away behind them, with a few drifting off. Shown for remote jumpers too (their state says they're
// in flight). Emitted by distance flown, so a long jump leaves as even a trail as a short one.
public class SuperJumpTrail : MonoBehaviour
{
    [SerializeField] ParticleSystem blobs;            // world space; size and fade over life set on it
    [SerializeField] float perMetre = 6f;
    [SerializeField] float spread = 0.25f;            // around the path
    [SerializeField] Vector2 size = new Vector2(0.35f, 0.7f);
    [SerializeField] float drift = 1f;                // their scatter speed

    PlayerController player;
    Vector3 last;
    bool flying;
    float owed;

    public int Emitted { get; private set; } // tests

    void Awake() => player = GetComponentInParent<PlayerController>();

    void LateUpdate()
    {
        bool now = player != null && player.SuperJumpFlying && blobs != null;
        Vector3 at = transform.position;
        if (now && flying)
        {
            owed += Vector3.Distance(last, at) * perMetre;
            int n = Mathf.FloorToInt(owed);
            owed -= n;
            Color c = player.TeamColour;
            for (int i = 0; i < n; i++)
            {
                var p = new ParticleSystem.EmitParams
                {
                    position = Vector3.Lerp(last, at, (i + Random.value) / n) + Random.insideUnitSphere * spread,
                    velocity = Random.insideUnitSphere * drift,
                    startColor = c,
                    startSize = Random.Range(size.x, size.y),
                    applyShapeToPosition = false
                };
                blobs.Emit(p, 1);
            }
            Emitted += n;
        }
        if (!now) owed = 0f;
        flying = now;
        last = at;
    }
}
