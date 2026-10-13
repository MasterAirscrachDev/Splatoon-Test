using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ClimbTestRunner scenarios: diving into and out of the ink (splashes), and the charge weapons (the
// Sniper and the Minigun).
public partial class ClimbTestRunner
{
    // A splash diving into the ink and another coming out; coming out on the move, it carries on with us.
    IEnumerator SwimSplashes()
    {
        SwimWake wake = player.GetComponent<SwimWake>();
        if (wake == null) { Fail("player has no SwimWake"); yield break; }
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), player.Team);
        yield return Hold(Still, false, 0.3f, "stand");

        int before = wake.Splashes;
        yield return Hold(Still, true, 0.4f, "dive");
        int dove = wake.Splashes - before;
        Vector3 diveCarry = InkFluid.LastBurstCarry;

        yield return Hold(Fwd, true, 0.8f, "swim");
        Vector3 travel = player.TravelVelocity;
        before = wake.Splashes;
        yield return Hold(Fwd, false, 0.2f, "surface on the move");
        int surfaced = wake.Splashes - before;
        Vector3 carry = InkFluid.LastBurstCarry;
        float along = Vector3.Dot(carry, travel.normalized);

        yield return Hold(Back, false, 0.8f, "back"); // stay on the station
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        Note($"diving in: {dove} splash(es), carrying {diveCarry.magnitude:F1} m/s; coming out at {travel.magnitude:F1} m/s: {surfaced} splash(es), carrying {carry.magnitude:F1} m/s along our way ({along:F1})");
        if (dove != 1) Fail("diving into the ink didn't splash (once)");
        if (surfaced != 1) Fail("coming out of the ink didn't splash (once)");
        if (along < travel.magnitude * 0.25f) Fail("the splash coming out of the ink didn't carry on some of our speed");
    }

    // Charge, see the laser, let go: farther and harder with more charge, a full one an instakill;
    // ink falls all along its line. A charge can be held over a short swim.
    IEnumerator SniperWeapon()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int index = WeaponIndexOf(loadout, "Sniper");
        if (index < 0) { Fail("no Sniper in the player's weapons"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        int enemyTeam = player.Team == 1 ? 2 : 1;
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 forward = Flat(player.transform.forward).normalized;
        Vector3 start = player.transform.position;
        float DamageTo(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Sum(m => ((DamageData)m.data).amount);
        int OnFloor(Vector3 at)
        {
            if (!Physics.Raycast(new Vector3(at.x, station.position.y + 1f, at.z), Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return -1;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            return ink != null ? ink.getSurfaceTeam(ink.UVFromHit(hit)) : -1;
        }
        CaptureSends();
        player.ScriptedInput.recenter = true;
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;
        loadout.SetMainWeapon(index);
        yield return Hold(Still, false, 0.4f, "equip");
        var sniper = loadout.CurrentWeapon as Sniper;
        if (sniper == null) { Fail("the Sniper didn't equip"); NetGameManager.SendOverride = null; yield break; }
        Reticle reticle = FindFirstObjectByType<Reticle>();

        // An enemy 8m ahead.
        const ulong Foe = 3601;
        Vector3 foeAt = new Vector3(start.x, station.position.y + 0.05f, start.z) + forward * 8f;
        SpawnRemote(Foe, enemyTeam, foeAt, "SniperFoe");
        yield return Hold(Still, false, 0.3f, "foe");
        void AimAtFoe() => sniper.ScriptedAim = foeAt + Vector3.up * 0.9f - sniper.ShotOrigin.position;

        // A tap: a weak shot.
        player.RefillInk();
        sent.Clear();
        AimAtFoe();
        sniper.ScriptedFire = true; yield return null; sniper.ScriptedFire = false;
        yield return Hold(Still, false, 0.4f, "tap");
        float tapCharge = sniper.LastCharge, tapDamage = DamageTo(Foe);
        int tapSplat = sniper.LastLineSplat;

        // Charging halfway: the laser, at half charge, and the true ring half filled; ink at our feet.
        sent.Clear();
        AimAtFoe();
        int feetBefore = sniper.FeetShots;
        sniper.ScriptedFire = true;
        yield return Hold(Still, false, sniper.ChargeTime * 0.5f, "half charge");
        AimAtFoe();
        float half = sniper.Charge;
        float ringFill = reticle != null ? reticle.ChargeFill : -1f;
        bool laser = sniper.LaserShown, glowHalf = sniper.GlowShown;
        float halfWidth = sniper.LaserWidth;
        float slowed = player.WeaponSpeedMultiplier;
        // ...then full: an instakill.
        yield return Hold(Still, false, sniper.ChargeTime * 0.7f, "full charge");
        AimAtFoe();
        float full = sniper.Charge;
        bool glowFull = sniper.GlowShown;
        float fullWidth = sniper.LaserWidth;
        sniper.ScriptedFire = false;
        yield return Hold(Still, false, 0.5f, "fire");
        float fullDamage = DamageTo(Foe);
        int fullSplat = sniper.LastLineSplat, feetInk = sniper.FeetShots - feetBefore;
        float ringAfter = reticle != null ? reticle.ChargeFill : -1f;
        int spawnMsgs = sent.Count(m => m.id == NetMsg.ProjectileSpawn);
        bool laserGone = !sniper.LaserShown, glowGone = !sniper.GlowShown;
        Despawn(Foe);
        Note($"tap: charge {tapCharge:F2}, {tapDamage:0} damage, line blobs {tapSplat}; charging: {half:F2} at half time, ring {ringFill:F2} filled, laser {(laser ? "shown" : "NOT shown")} {halfWidth:F3}m wide, light {(glowHalf ? "ON" : "off")}, speed x{slowed:F2}; full {full:F2}: laser {fullWidth:F3}m wide, light {(glowFull ? "on" : "OFF")}, {fullDamage:0} damage (a full charge does {sniper.DamageAt(1f):0}), line blobs {fullSplat}, laser {(laserGone ? "gone" : "STILL SHOWN")}, light {(glowGone ? "off" : "STILL ON")} and ring {ringAfter:F2} after; {feetInk} feet ink drop(s); {spawnMsgs} broadcast");
        if (tapCharge > 0.2f || tapDamage <= 0f || tapDamage >= 100f) Fail("a tap didn't fire a weak shot");
        if (Mathf.Abs(half - 0.5f) > 0.15f || full < 1f) Fail($"it doesn't charge fully in {sniper.ChargeTime}s");
        if (!laser || !laserGone) Fail("no laser while charging (or it stayed after firing)");
        if (fullDamage < 100f) Fail("a full charge didn't splat in one direct hit");
        if (spawnMsgs != 1) Fail("the shot wasn't broadcast once");
        if (fullSplat <= tapSplat) Fail("a full charge's line blobs aren't bigger than a tap's");
        if (Mathf.Abs(halfWidth - fullWidth) > 1e-4f) Fail("the laser's width changes with the charge");
        if (glowHalf || !glowFull || !glowGone) Fail("the light on its front didn't show only at full charge");
        if (feetInk < 1) Fail("no ink dropped at our feet charging and firing");
        if (reticle != null && (Mathf.Abs(ringFill - half) > 0.05f || ringAfter > 0f)) Fail("the true reticle's ring didn't fill with the charge (and empty after)");

        // Level, at full charge into the open: ink falls all along its line.
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        player.RefillInk();
        sniper.ScriptedAim = forward;
        sniper.ScriptedFire = true;
        yield return Hold(Still, false, sniper.ChargeTime * 1.2f, "charge");
        sniper.ScriptedFire = false;
        yield return Hold(Still, false, 1.2f, "line falls");
        float length = sniper.LastLineLength;
        int inked = 0, probes = 0;
        for (float d = 2f; d < Mathf.Min(length - 1f, 12f); d += 1f) { probes++; if (OnFloor(start + forward * d) == player.Team) inked++; }
        Note($"full charge into the open: its line ran {length:F1}m (reach {sniper.RangeAt(1f):F0}m); {inked}/{probes} points under it inked");
        if (probes < 4 || inked < probes * 0.8f) Fail("ink didn't fall all along the shot's line");

        // Held over a short swim (attack kept down), carrying on after; too long a swim loses it.
        player.RefillInk();
        sniper.ScriptedFire = true;
        yield return Hold(Still, false, sniper.ChargeTime * 1.2f, "charge");
        int shotsBefore = sniper.Shots;
        yield return Hold(Still, true, sniper.SwimHoldTime * 0.5f, "short swim");
        bool kept = sniper.KeptInSwim && sniper.Charge >= 1f;
        yield return Hold(Still, false, 0.15f, "surface");
        float after = sniper.Charge;
        var samples = new List<string>();
        for (int k = 0; k < 6; k++)
        {
            yield return Hold(Still, true, (sniper.SwimHoldTime + 0.3f) / 6f, "long swim");
            samples.Add($"{(player.IsSquid ? "S" : "k")}{(sniper.KeptInSwim ? "+" : "")}{sniper.Charge:F1}{(sniper.isActiveAndEnabled ? "" : "(off)")}");
        }
        Note("long swim: " + string.Join(" ", samples));
        float lost = sniper.Charge;
        sniper.ScriptedFire = false;
        yield return Hold(Still, false, 0.3f, "let go");
        Note($"swim hold ({sniper.SwimHoldTime:F1}s): charge {(kept ? "kept" : "NOT kept")} over a short swim, {after:F2} on surfacing, {lost:F2} after a long one; {sniper.Shots - shotsBefore} shot(s) fired");
        if (sniper.SwimHoldTime > 0f && (!kept || after < 1f)) Fail("a charge wasn't held over a short swim");
        if (lost > 0f) Fail("a charge outlasted the swim hold time");

        // Down the cursor's line: the laser (and the true reticle) end on the ring looking level; aimed
        // more steeply down than it can, they end off it, at its limit.
        sniper.ScriptedAim = null;
        player.RefillInk();
        sniper.ScriptedFire = true;
        yield return Hold(Still, false, sniper.ChargeTime * 0.6f, "charge, level");
        bool onRing = reticle != null && !reticle.Blocked;
        sniper.ScriptedAim = forward * 0.2f + Vector3.down; // about 80° down
        yield return Hold(Still, false, 0.1f, "charge, aimed steeply down");
        bool offRing = reticle != null && reticle.Blocked;
        float limited = sniper.AimEnd(out Vector3 end) ? Mathf.Asin((end - sniper.ShotOrigin.position).normalized.y) * Mathf.Rad2Deg : 0f;
        sniper.ScriptedFire = false;
        sniper.ScriptedAim = null;
        yield return Hold(Still, false, 0.3f, "fire");
        Note($"aiming down the cursor's line: true reticle {(onRing ? "on the ring" : "OFF the ring")} looking level; aimed about 80° down, {(offRing ? "off it" : "STILL ON IT")}, aiming {limited:F0}° (its limit)");
        if (reticle != null && (!onRing || !offRing)) Fail("the laser and true reticle don't end on the cursor when it can reach it (and off it past its aim limits)");
        if (limited > -45f) Fail("aimed past its limit, it didn't stop at it");

        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        loadout.SetMainWeapon(weaponBefore);
        NetGameManager.SendOverride = null;
    }

    // Charge, let go: a stream for as long as it charged; slower while charging and firing; the
    // variants (full charge only, charging while firing).
    IEnumerator MinigunWeapon()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int index = WeaponIndexOf(loadout, "Minigun");
        if (index < 0) { Fail("no Minigun in the player's weapons"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        CaptureSends();
        player.ScriptedInput.recenter = true;
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;
        loadout.SetMainWeapon(index);
        yield return Hold(Still, false, 0.4f, "equip");
        var gun = loadout.CurrentWeapon as Minigun;
        if (gun == null) { Fail("the Minigun didn't equip"); NetGameManager.SendOverride = null; yield break; }

        // Half a charge: a stream half as long, slowed while charging and while firing.
        player.RefillInk();
        sent.Clear();
        gun.ScriptedFire = true;
        yield return Hold(Still, false, gun.ChargeTime * 0.5f, "half charge");
        float charge = gun.Charge, chargingSpeed = player.WeaponSpeedMultiplier;
        int shots0 = gun.Shots;
        gun.ScriptedFire = false;
        yield return Hold(Still, false, 0.2f, "firing");
        float firingSpeed = player.WeaponSpeedMultiplier;
        float ringFiring = gun.ChargeShown;
        float firedFor = 0f;
        while (gun.Firing && firedFor < 5f) { firedFor += Time.deltaTime; yield return null; }
        firedFor += 0.2f;
        float ringDone = gun.ChargeShown;
        yield return Hold(Still, false, 0.2f, "done");
        int shots = gun.Shots - shots0;
        float expected = charge * gun.BurstTime;
        int broadcast = sent.Count(m => m.id == NetMsg.ProjectileSpawn);
        Note($"charge {charge:F2}: fired for {firedFor:F2}s (expected {expected:F2}s), {shots} shots ({broadcast} broadcast); speed x{chargingSpeed:F2} charging, x{firingSpeed:F2} firing, x{player.WeaponSpeedMultiplier:F2} after; ring {charge:F2} charged, {ringFiring:F2} 0.2s into the stream, {ringDone:F2} at its end");
        if (Mathf.Abs(firedFor - expected) > 0.15f) Fail("the stream didn't last as long as it was charged");
        if (Mathf.Abs(shots - expected / gun.FireRate) > 3f) Fail("the stream didn't fire at its rate");
        if (broadcast != shots) Fail("its shots weren't broadcast");
        if (ringFiring >= charge - 0.02f || ringFiring <= 0f || ringDone > 0.05f) Fail("the ring didn't drain as it fired");
        if (Mathf.Abs(chargingSpeed - gun.ChargeMoveSpeed) > 0.01f || Mathf.Abs(firingSpeed - gun.FireMoveSpeed) > 0.01f || player.WeaponSpeedMultiplier != 1f)
            Fail("it doesn't slow us while charging and firing (and only then)");

        // Full charge only: letting go early loses it.
        gun.RequireFullCharge = true;
        player.RefillInk();
        int bursts = gun.Bursts;
        gun.ScriptedFire = true;
        yield return Hold(Still, false, gun.ChargeTime * 0.5f, "half charge");
        gun.ScriptedFire = false;
        yield return Hold(Still, false, 0.4f, "let go early");
        bool lostEarly = gun.Bursts == bursts && !gun.Firing;
        gun.ScriptedFire = true;
        yield return Hold(Still, false, gun.ChargeTime * 1.1f, "full charge");
        gun.ScriptedFire = false;
        yield return Hold(Still, false, 0.2f, "fire");
        bool firedFull = gun.Firing;
        yield return HoldUntil(Still, false, 3f, "stream", f => !gun.Firing);
        gun.RequireFullCharge = false;

        // Charging while firing: off, holding attack mid-stream waits; on, it charges.
        var whileFiring = new List<float>();
        foreach (bool variant in new[] { false, true })
        {
            gun.ChargeWhileFiring = variant;
            player.RefillInk();
            gun.ScriptedFire = true;
            yield return Hold(Still, false, gun.ChargeTime * 1.1f, "charge");
            gun.ScriptedFire = false;
            yield return Hold(Still, false, 0.15f, "fire");
            gun.ScriptedFire = true;
            yield return Hold(Still, false, 0.4f, "hold mid-stream");
            whileFiring.Add(gun.Firing ? gun.Charge : -1f);
            gun.ScriptedFire = false;
            yield return HoldUntil(Still, false, 4f, "stream", f => !gun.Firing);
            yield return Hold(Still, false, 0.3f, "settle");
        }
        gun.ChargeWhileFiring = false;
        Note($"full charge only: early release {(lostEarly ? "lost" : "FIRED")}, full {(firedFull ? "fired" : "DIDN'T fire")}; charge after 0.4s held mid-stream: {whileFiring[0]:F2} (off), {whileFiring[1]:F2} (on)");
        if (!lostEarly || !firedFull) Fail("the full-charge-only variant didn't need a full charge");
        if (whileFiring[0] != 0f) Fail("it charged mid-stream without the charge-while-firing variant");
        if (whileFiring[1] <= 0.2f) Fail("the charge-while-firing variant didn't charge mid-stream");

        loadout.SetMainWeapon(weaponBefore);
        NetGameManager.SendOverride = null;
    }
}
