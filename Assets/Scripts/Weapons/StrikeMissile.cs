using UnityEngine;

// The Inkstrike's missile (its model, nose up its local +Z as exported; turned nose-up on the back).
// Carried on a player's back from using the special until it launches (PlayerLoadout shows it,
// ours and other players'), then a copy flies straight up, faster and faster, its propeller
// spinning, and is gone: the strike itself (InkStrike) is separate. Look only: no paint or damage.
public class StrikeMissile : MonoBehaviour
{
    [SerializeField, Tooltip("Its size on a player's back, x the model (the model is full-size)")] float backScale = 0.5f;
    [SerializeField, Tooltip("Where it sits on the back, from the ink tank (metres, the kid model's space)")] Vector3 backOffset = new Vector3(0f, 0.15f, -0.2f);
    [SerializeField, Tooltip("Launch: starting speed up (metres per second)")] float launchSpeed = 4f;
    [SerializeField, Tooltip("Launch: acceleration up (metres per second²)")] float launchAcceleration = 60f;
    [SerializeField, Tooltip("Launch: seconds until it's gone")] float flightTime = 1.2f;
    [SerializeField, Tooltip("Launch: it grows to full size over the first share of its flight")] float growTime = 0.3f;
    [SerializeField, Tooltip("Spin about its length while flying (degrees per second)")] float spin = 540f;

    float launchedAt = -1f, speed, startScale;

    public bool Flying => launchedAt >= 0f;
    public float BackScale => backScale;

    // Mounted on a player's back, by their ink tank, under their kid model (so it hides with it).
    public void Mount(Transform tank, Color colour)
    {
        transform.SetParent(tank.parent, false);
        transform.localPosition = tank.localPosition + backOffset;
        transform.localRotation = Quaternion.Euler(-90f, 0f, 0f); // nose (its +Z) up
        transform.localScale = Vector3.one * backScale;
        Weapon.TintInkParts(gameObject, colour);
    }

    // A copy of this one where it is, flying off straight up.
    public StrikeMissile Launch(Color colour)
    {
        StrikeMissile flying = Instantiate(this, transform.position, Quaternion.LookRotation(Vector3.up, -transform.up));
        flying.transform.localScale = transform.lossyScale;
        Weapon.TintInkParts(flying.gameObject, colour);
        flying.launchedAt = Time.time;
        flying.speed = launchSpeed;
        flying.startScale = transform.lossyScale.x;
        return flying;
    }

    void Update()
    {
        if (!Flying) return;
        float t = Time.time - launchedAt;
        speed += launchAcceleration * Time.deltaTime;
        transform.position += Vector3.up * speed * Time.deltaTime;
        transform.Rotate(0f, 0f, spin * Time.deltaTime, Space.Self);
        transform.localScale = Vector3.one * Mathf.Lerp(startScale, 1f, Mathf.Clamp01(t / Mathf.Max(growTime, 0.01f)));
        if (t >= flightTime) Destroy(gameObject);
    }
}
