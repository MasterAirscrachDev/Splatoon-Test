using UnityEngine;

// A main weapon. Every weapon is a prefab with its own model and behaviour (WeaponShooter for the
// guns, Blaster); PlayerLoadout equips one under the player's weapon mount. On remote players'
// copies only the model matters, so the behaviour is switched off; their shots arrive as
// ProjectileSpawn messages, replayed (visual-only) by Replay.
public abstract class Weapon : MonoBehaviour
{
    [SerializeField] string displayName = "Weapon";
    [SerializeField, TextArea] string description;
    [SerializeField] Sprite icon;          // rendered in the icon studio (Tools/Icons/Render Icons)

    [Header("Feet ink")] // keeps the shooter standing in their own ink
    [SerializeField] float feetShotInterval = 0.4f;
    [SerializeField] int feetShotMinSize = 12, feetShotMaxSize = 18;
    [SerializeField] float feetShotDropSpeed = 6f; // starts falling at once, so it lands under us even on the move

    float nextFeetShot;

    public string DisplayName => displayName;
    public string Description => description;
    public Sprite Icon => icon;
    public PlayerController Owner { get; private set; }
    public int FeetShots { get; private set; } // tests

    // Another player's shots with this weapon, shown here (they paint and damage on their side).
    public virtual void Replay(ProjectileSpawnData d) { }

    // While shooting, every feetShotInterval: an unseen shot released just above the feet, heading
    // straight down. Not replayed on remotes: its splat reaches them like any other.
    protected void UpdateFeetInk(PlayerController player, bool shooting, GameObject projectile)
    {
        if (!shooting || projectile == null || Time.time < nextFeetShot) return;
        nextFeetShot = Time.time + feetShotInterval;
        FeetShots++;
        Vector3 feet = player.transform.position + Vector3.up * 0.5f;
        ProjectileManager.Fire(projectile, feet, Vector3.down * feetShotDropSpeed, Random.Range(feetShotMinSize, feetShotMaxSize + 1), player.Team,
                               visible: false, ownerId: player.OwnerId, damage: 0, impactParticles: false, source: DisplayName);
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
