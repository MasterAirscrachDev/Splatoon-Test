using UnityEngine;

// Runs a ChargeWeapon's charge every frame from the player (ChargeWeapon.Equip adds it), so it keeps
// going in swim form, where the weapon itself is switched off: the swim hold time runs out, and
// letting go while swimming is seen (it calls the charge off).
public class ChargeDriver : MonoBehaviour
{
    public ChargeWeapon weapon;

    void Update()
    {
        if (weapon == null) { Destroy(this); return; }
        weapon.Drive();
    }
}
