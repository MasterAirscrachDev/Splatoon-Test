using UnityEngine;

// A main weapon. Every weapon is a prefab with its own model and behaviour (WeaponShooter for the
// guns); PlayerLoadout equips one under the player's weapon mount. On remote players' copies only
// the model matters, so the behaviour is switched off.
public abstract class Weapon : MonoBehaviour
{
    [SerializeField] string displayName = "Weapon";
    [SerializeField, TextArea] string description;

    public string DisplayName => displayName;
    public string Description => description;
    public PlayerController Owner { get; private set; }

    // Called right after it's created under its owner.
    public virtual void Equip(PlayerController owner)
    {
        Owner = owner;
        enabled = owner.IsLocalPlayer;
        WeaponCameraAim aim = GetComponent<WeaponCameraAim>();
        if (aim != null) aim.enabled = owner.IsLocalPlayer;
    }
}
