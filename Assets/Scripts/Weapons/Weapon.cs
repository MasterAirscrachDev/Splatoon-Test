using UnityEngine;

// A main weapon. Every weapon is a prefab with its own model and behaviour (WeaponShooter for the
// guns, Blaster); PlayerLoadout equips one under the player's weapon mount. On remote players'
// copies only the model matters, so the behaviour is switched off; their shots arrive as
// ProjectileSpawn messages, replayed (visual-only) by Replay.
public abstract class Weapon : MonoBehaviour
{
    [SerializeField] string displayName = "Weapon";
    [SerializeField, TextArea] string description;
    [SerializeField, Tooltip("Rendered in the icon studio (Tools/Icons/Render Icons)")] Sprite icon;

    [Header("Loadout card")] // rough 0-1 ratings, the bars in the loadout menu (hand-set: they don't follow the tuning)
    [SerializeField, Range(0f, 1f), Tooltip("(not \"range\": the Blaster has its own)")] float powerRating = 0.5f;
    [SerializeField, Range(0f, 1f)] float rangeRating = 0.5f;
    [SerializeField, Range(0f, 1f)] float speedRating = 0.5f;

    [Header("Feet ink")] // keeps the shooter standing in their own ink
    [SerializeField, Tooltip("While shooting, an unseen shot drops at our feet this often (seconds), so we stand in our own ink")] float feetShotInterval = 0.4f;
    [SerializeField, Tooltip("Its splat size, random between these (min, max)")] int feetShotMinSize = 12, feetShotMaxSize = 18;
    [SerializeField, Tooltip("How fast it drops (metres per second): it lands under us even on the move")] float feetShotDropSpeed = 6f;

    float nextFeetShot;

    public string DisplayName => displayName;
    public string Description => description;
    public Sprite Icon => icon;
    public float PowerRating => powerRating;
    public float RangeRating => rangeRating;
    public float SpeedRating => speedRating;
    public PlayerController Owner { get; private set; }
    public int FeetShots { get; private set; } // tests
    public virtual Transform ShotOrigin => null;  // where aimed shots leave from (the reticle checks the way from it); none: thrown or swung
    // Where its shot would end right now, when the weapon works that out itself (the Sniper): the
    // true reticle shows it. False: the reticle checks the way from ShotOrigin to the aim point.
    public virtual bool AimEnd(out Vector3 point) { point = default; return false; }
    // How charged it is (0-1) for the true reticle's fill; below 0: it doesn't charge.
    public virtual float ChargeShown => -1f;

    // Another player's shots with this weapon, shown here (they paint and damage on their side).
    public virtual void Replay(ProjectileSpawnData d) { }

    // While shooting, every feetShotInterval: an unseen shot released just above the feet, heading
    // straight down. Not replayed on remotes: its splat reaches them like any other.
    protected void UpdateFeetInk(PlayerController player, bool shooting)
    {
        if (shooting && Time.time >= nextFeetShot) FeetInkNow(player);
    }

    // One at once (a shot's own, like the Sniper's), the next one feetShotInterval after.
    protected void FeetInkNow(PlayerController player)
    {
        nextFeetShot = Time.time + feetShotInterval;
        FeetShots++;
        Vector3 feet = player.transform.position + Vector3.up * 0.5f;
        ProjectileManager.Fire(feet, Vector3.down * feetShotDropSpeed, Random.Range(feetShotMinSize, feetShotMaxSize + 1), player.Team,
                               visible: false, ownerId: player.OwnerId, damage: 0, splashes: false, source: DisplayName);
    }

    // Called right after it's created under its owner (on every copy: ours and other players').
    public virtual void Equip(PlayerController owner)
    {
        Owner = owner;
        enabled = owner.IsLocalPlayer;
        WeaponCameraAim aim = GetComponent<WeaponCameraAim>();
        if (aim != null) aim.enabled = true; // remote copies tilt with their synced aim
        Tint();
        NetGameManager.TeamColoursChanged += Tint;
        owner.TeamChanged += Tint; // reassigned to another team: its colour
    }

    protected virtual void OnDestroy()
    {
        NetGameManager.TeamColoursChanged -= Tint;
        if (Owner != null) Owner.TeamChanged -= Tint;
    }

    // The ink-coloured parts (material slots named Ink...) in the owner's team colour.
    MaterialPropertyBlock tintBlock;
    void Tint()
    {
        if (Owner == null) return;
        tintBlock ??= new MaterialPropertyBlock();
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || !mats[i].name.StartsWith("Ink")) continue;
                r.GetPropertyBlock(tintBlock, i);
                tintBlock.SetColor("_Color", Owner.TeamColour);
                r.SetPropertyBlock(tintBlock, i);
            }
        }
    }
}
