using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The variants: the Light Roller (horizontal flicks only, the fastest roll), the Era Roller (its
// vertical swing, a slower roll) and the Triplesplash Bucket (three fanned streams, shorter range).
public partial class ClimbTestRunner
{
    // Flat yaw spread of some launches (degrees, edge to edge), against the way we face.
    float YawSpread(IEnumerable<Vector3> launches)
    {
        Vector3 forward = player.transform.forward; forward.y = 0f;
        var yaws = launches.Select(v => Vector3.SignedAngle(forward, new Vector3(v.x, 0f, v.z), Vector3.up)).ToList();
        return yaws.Count > 0 ? yaws.Max() - yaws.Min() : 0f;
    }

    // Ground and air flicks both wide (it never folds: the body doesn't turn); it rolls faster than any
    // other roller, the drum spinning.
    IEnumerator LightRollerWeapon()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int index = WeaponIndexOf(loadout, "Light Roller");
        if (index < 0) { Fail("no Light Roller in the player's weapons"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        Vector3 start = player.transform.position;
        float fastestOther = loadout.Weapons.OfType<Roller>().Where(r => r.DisplayName != "Light Roller").Max(r => r.RollSpeed);
        loadout.SetMainWeapon(index);
        yield return Hold(Still, false, 0.4f, "equip");
        var roller = loadout.CurrentWeapon as Roller;
        if (roller == null) { Fail("the Light Roller didn't equip"); yield break; }
        Transform body = roller.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "LightRoller_Body");
        Quaternion bodyHome = body != null ? body.localRotation : Quaternion.identity;
        IEnumerator Tap() { roller.ScriptedFire = true; yield return null; roller.ScriptedFire = false; }

        player.RefillInk();
        yield return Tap();
        yield return Hold(Still, false, 0.6f, "ground flick");
        bool groundVertical = roller.LastFlickInAir;
        float groundSpread = YawSpread(roller.LastFlick ?? new Vector3[0]);
        yield return Hold(Still, false, roller.FlickRate, "cooldown");
        player.PressJump();
        yield return Hold(Still, false, 0.15f, "jump");
        bool airborne = !player.IsGrounded;
        yield return Tap();
        yield return Hold(Still, false, roller.Windup, "air flick");
        float turnAtTop = roller.FrameTurn;
        float bodyTurned = body != null ? Quaternion.Angle(bodyHome, body.localRotation) : -1f;
        yield return Hold(Still, false, 1.2f, "air flick");
        bool airVertical = roller.LastFlickInAir;
        float airSpread = YawSpread(roller.LastFlick ?? new Vector3[0]);
        Note($"ground flick {(groundVertical ? "VERTICAL" : "horizontal")} across {groundSpread:F0}°; air flick ({(airborne ? "airborne" : "NOT airborne")}) {(airVertical ? "VERTICAL" : "horizontal")} across {airSpread:F0}°, frame turn {turnAtTop:F0}°, body turned {bodyTurned:F0}°");
        if (groundVertical || airVertical || airSpread < 30f) Fail("the Light Roller didn't flick horizontally both on the ground and in the air");
        if (Mathf.Abs(turnAtTop) > 1f || bodyTurned > 1f) Fail("the Light Roller folded (its frame or body turned)");

        // The roll: faster than any other roller, the drum spinning as it goes.
        player.PlaceAt(start, player.transform.eulerAngles.y);
        frames.Clear();
        player.RefillInk();
        roller.ScriptedFire = true;
        yield return HoldUntil(Still, false, 1.5f, "to roll", f => roller.Down);
        Quaternion drum0 = roller.Drum != null ? roller.Drum.localRotation : Quaternion.identity;
        yield return Hold(Fwd, false, 0.8f, "roll");
        float mul = player.WeaponSpeedMultiplier;
        float spun = roller.Drum != null ? Quaternion.Angle(drum0, roller.Drum.localRotation) : 0f;
        roller.ScriptedFire = false;
        yield return Hold(Still, false, 0.3f, "release");
        Note($"rolling x{mul:F2} (its top speed x{roller.RollSpeed:F2}; the fastest other roller x{fastestOther:F2}); drum turned {spun:F0}°");
        if (roller.RollSpeed <= fastestOther) Fail("the Light Roller isn't the fastest roller");
        if (mul < roller.RollSpeed - 0.05f) Fail("the Light Roller didn't reach its rolling speed in 0.8s");
        if (spun < 5f) Fail("the Light Roller's drum didn't spin as it rolled");

        loadout.SetMainWeapon(weaponBefore);
        player.PlaceAt(start, player.transform.eulerAngles.y);
        frames.Clear();
        yield return Hold(Still, false, 0.3f, "cleanup");
    }

    // Against the Inkroller: a weaker ground flick (still horizontal), an air flick (vertical) reaching
    // about half as far again, and a slower roll.
    IEnumerator EraRollerWeapon()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int era = WeaponIndexOf(loadout, "Era Roller"), ink = WeaponIndexOf(loadout, "Inkroller");
        if (era < 0 || ink < 0) { Fail("no Era Roller or Inkroller in the player's weapons"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 forward = Flat(player.transform.forward).normalized;
        Vector3 start = player.transform.position;
        CaptureSends();
        player.ScriptedInput.recenter = true;
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;
        var landed = new List<Vector3>();
        string source = null;
        Action<ProjectileManager.Shot, Vector3> onLand = (shot, point) => { if (shot.source == source) landed.Add(point); };
        ProjectileManager.Impact += onLand;
        const ulong Foe = 3801;
        var result = new Dictionary<int, (bool groundVertical, float groundDamage, bool airVertical, float airReach, float rollSpeed)>();
        foreach (int index in new[] { ink, era })
        {
            player.PlaceAt(start, player.transform.eulerAngles.y);
            frames.Clear();
            loadout.SetMainWeapon(index);
            yield return Hold(Still, false, 0.4f, "equip");
            var roller = (Roller)loadout.CurrentWeapon;
            source = roller.Source;
            SpawnRemote(Foe, enemyTeam, new Vector3(start.x, station.position.y + 0.05f, start.z) + forward * 3f, "EraFoe");
            yield return Hold(Still, false, 0.3f, "foe");
            player.RefillInk();
            sent.Clear();
            roller.ScriptedFire = true; yield return null; roller.ScriptedFire = false;
            yield return Hold(Still, false, 1.2f, "ground flick");
            bool groundVertical = roller.LastFlickInAir;
            float damage = sent.Where(m => m.id == NetMsg.Damage && m.to == Foe).Sum(m => ((DamageData)m.data).amount);
            Despawn(Foe);
            yield return Hold(Still, false, roller.FlickRate, "cooldown");
            landed.Clear();
            player.PressJump();
            yield return Hold(Still, false, 0.15f, "jump");
            roller.ScriptedFire = true; yield return null; roller.ScriptedFire = false;
            // Its reach: how far out its droplets get before they're back down to the floor's height
            // (the furthest fly off the end of the station's floor, so landings alone undercount it).
            float reach = 0f;
            for (float t = 0f; t < 1.8f; t += Time.deltaTime)
            {
                foreach (var shot in ProjectileManager.Live)
                    if (shot.source == source && shot.position.y >= start.y) reach = Mathf.Max(reach, Flat(shot.position - start).magnitude);
                yield return null;
            }
            result[index] = (groundVertical, damage, roller.LastFlickInAir, reach, roller.RollSpeed);
            Vector3[] air = roller.LastFlick ?? new Vector3[0];
            Note($"{roller.DisplayName} air flick: fastest launch {(air.Length > 0 ? air.Max(v => v.magnitude) : 0f):F1} m/s, elevation {(air.Length > 0 ? air.Average(v => Mathf.Atan2(v.y, Flat(v).magnitude) * Mathf.Rad2Deg) : 0f):F0}°, {landed.Count} landed, heights {(landed.Count > 0 ? landed.Min(p => p.y - start.y) : 0f):F1}..{(landed.Count > 0 ? landed.Max(p => p.y - start.y) : 0f):F1}m");
        }
        ProjectileManager.Impact -= onLand;
        var i = result[ink]; var e = result[era];
        float ratio = e.airReach / Mathf.Max(i.airReach, 0.1f);
        Note($"Inkroller: ground flick {(i.groundVertical ? "vertical" : "horizontal")} for {i.groundDamage:0}, air flick {(i.airVertical ? "vertical" : "horizontal")} to {i.airReach:F1}m, rolling x{i.rollSpeed:F2}; " +
             $"Era Roller: ground flick {(e.groundVertical ? "VERTICAL" : "horizontal")} for {e.groundDamage:0}, air flick {(e.airVertical ? "vertical" : "HORIZONTAL")} to {e.airReach:F1}m (x{ratio:F2}), rolling x{e.rollSpeed:F2}");
        if (e.groundVertical || !e.airVertical) Fail("the Era Roller doesn't flick horizontally on the ground and vertically in the air");
        if (e.groundDamage <= 0f || e.groundDamage >= i.groundDamage) Fail("the Era Roller's ground flick isn't weaker than the Inkroller's");
        if (ratio < 1.35f || ratio > 1.65f) Fail("the Era Roller's air flick doesn't reach about 1.5x the Inkroller's");
        if (e.rollSpeed >= i.rollSpeed) Fail("the Era Roller doesn't roll slower than the Inkroller");
        loadout.SetMainWeapon(weaponBefore);
        player.PlaceAt(start, player.transform.eulerAngles.y);
        frames.Clear();
        NetGameManager.SendOverride = null;
        yield return Hold(Still, false, 0.3f, "cleanup");
    }

    // Three streams per slosh, fanned out, each with its own blob; they reach less far than the
    // Bucket's; an enemy straight ahead takes a hit.
    IEnumerator TriBucketWeapon()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int index = WeaponIndexOf(loadout, "Triplesplash Bucket"), plain = WeaponIndexOf(loadout, "Bucket");
        if (index < 0 || plain < 0) { Fail("no Triplesplash Bucket or Bucket in the player's weapons"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 forward = Flat(player.transform.forward).normalized;
        Vector3 start = player.transform.position;
        CaptureSends();
        player.ScriptedInput.recenter = true;
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;
        var landed = new List<Vector3>();
        string source = null;
        Action<ProjectileManager.Shot, Vector3> onLand = (shot, point) => { if (shot.source == source) landed.Add(point); };
        ProjectileManager.Impact += onLand;

        var reach = new Dictionary<int, float>();
        Bucket tri = null;
        const ulong Foe = 3701;
        float damage = 0f;
        int spawns = 0;
        foreach (int i in new[] { plain, index })
        {
            loadout.SetMainWeapon(i);
            yield return Hold(Still, false, 0.4f, "equip");
            var bucket = (Bucket)loadout.CurrentWeapon;
            source = bucket.Source;
            if (i == index) { tri = bucket; SpawnRemote(Foe, enemyTeam, new Vector3(start.x, station.position.y + 0.05f, start.z) + forward * 2.5f, "TriFoe"); yield return Hold(Still, false, 0.3f, "foe"); }
            player.RefillInk();
            sent.Clear();
            landed.Clear();
            bucket.ScriptedFire = true; yield return null; bucket.ScriptedFire = false;
            yield return Hold(Still, false, 1.6f, "slosh");
            reach[i] = landed.Count > 0 ? landed.Max(p => Flat(p - start).magnitude) : 0f;
            if (i == index)
            {
                damage = sent.Where(m => m.id == NetMsg.Damage && m.to == Foe).Sum(m => ((DamageData)m.data).amount);
                spawns = sent.Count(m => m.id == NetMsg.ProjectileSpawn);
                Despawn(Foe);
            }
            yield return Hold(Still, false, 0.3f, "settle");
        }
        ProjectileManager.Impact -= onLand;
        Vector3[] slosh = tri.LastSlosh ?? new Vector3[0];
        int perStream = tri.Droplets + 1;
        var blobs = Enumerable.Range(0, tri.Streams).Where(s => (s + 1) * perStream <= slosh.Length).Select(s => slosh[(s + 1) * perStream - 1]).ToList();
        float fan = YawSpread(blobs);
        Note($"Triplesplash: {tri.Streams} streams, {slosh.Length} shots ({spawns} broadcast), blobs fanned {fan:F0}° (set {tri.StreamFan:F0}°), reaching {reach[index]:F1}m against the Bucket's {reach[plain]:F1}m; the enemy 2.5m ahead took {damage:0}");
        if (tri.Streams != 3 || slosh.Length != 3 * perStream) Fail("the Triplesplash didn't throw three whole streams");
        if (Mathf.Abs(fan - tri.StreamFan) > 3f) Fail("its streams' blobs aren't fanned out as set");
        if (spawns != 1) Fail("the slosh wasn't broadcast once");
        if (reach[index] >= reach[plain] - 0.5f) Fail("the Triplesplash doesn't reach less far than the Bucket");
        if (damage <= 0f) Fail("the enemy straight ahead took no hit");

        loadout.SetMainWeapon(weaponBefore);
        NetGameManager.SendOverride = null;
        yield return Hold(Still, false, 0.3f, "cleanup");
    }
}
