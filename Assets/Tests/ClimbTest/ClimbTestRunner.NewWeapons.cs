using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The Heavy Roller (against the Inkroller) and the Inkbrush.
public partial class ClimbTestRunner
{
    int WeaponIndexOf(PlayerLoadout loadout, string name)
    {
        for (int i = 0; i < loadout.Weapons.Count; i++) if (loadout.Weapons[i].DisplayName == name) return i;
        return -1;
    }

    // Slower to swing and roll than the Inkroller, but its ground flicks throw more droplets further
    // for more damage; its roll starts slow and builds to walking speed over 3s, inking past the drum.
    IEnumerator HeavyRollerWeapon()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int light = WeaponIndexOf(loadout, "Inkroller"), heavy = WeaponIndexOf(loadout, "Heavy Roller");
        if (light < 0 || heavy < 0) { Fail("no Inkroller or Heavy Roller in the player's weapons"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        int enemyTeam = player.Team == 1 ? 2 : 1;
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 forward = Flat(player.transform.forward).normalized;
        Vector3 start = player.transform.position;
        int OnFloor(Vector3 at)
        {
            if (!Physics.Raycast(new Vector3(at.x, station.position.y + 1f, at.z), Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return -1;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            return ink != null ? ink.getSurfaceTeam(ink.UVFromHit(hit)) : -1;
        }
        CaptureSends();
        var landed = new List<Vector3>();
        string source = null;
        Action<ProjectileManager.Shot, Vector3> onLand = (shot, point) => { if (shot.source == source) landed.Add(point); };
        ProjectileManager.Impact += onLand;

        // The same ground flick at an enemy 3m ahead, with each.
        const ulong Foe = 3301;
        var flick = new Dictionary<int, (int droplets, float reach, float damage, float rate, float rollSpeed, float width)>();
        foreach (int index in new[] { light, heavy })
        {
            player.PlaceAt(start, player.transform.eulerAngles.y);
            frames.Clear(); // a teleport
            loadout.SetMainWeapon(index);
            floor.FillRegion(new Rect(0, 0, 1, 1), 0);
            yield return Hold(Still, false, 0.3f, "equip");
            var roller = (Roller)loadout.CurrentWeapon;
            source = roller.Source;
            SpawnRemote(Foe, enemyTeam, new Vector3(start.x, station.position.y + 0.05f, start.z) + forward * 3f, "HeavyFoe");
            yield return Hold(Still, false, 0.3f, "foe");
            player.RefillInk();
            sent.Clear();
            landed.Clear();
            roller.ScriptedFire = true; yield return null; roller.ScriptedFire = false;
            yield return Hold(Still, false, 1.6f, "flick");
            float damage = sent.Where(m => m.id == NetMsg.Damage && m.to == Foe).Sum(m => ((DamageData)m.data).amount);
            float reach = landed.Count > 0 ? landed.Max(p => Flat(p - start).magnitude) : 0f;
            flick[index] = (roller.LastFlick?.Length ?? 0, reach, damage, roller.FlickRate, roller.RollSpeed, roller.RollerWidth);
            Despawn(Foe);
            yield return Hold(Still, false, 0.2f, "despawn");
        }
        var l = flick[light]; var h = flick[heavy];
        Note($"Inkroller: {l.droplets} droplets out to {l.reach:F1}m for {l.damage:0}, a flick every {l.rate:0.##}s, rolling x{l.rollSpeed:0.##} {l.width:F2}m wide; " +
             $"Heavy Roller: {h.droplets} droplets out to {h.reach:F1}m for {h.damage:0}, every {h.rate:0.##}s, rolling x{h.rollSpeed:0.##} {h.width:F2}m wide");
        if (h.rate <= l.rate) Fail("the Heavy Roller doesn't flick slower");
        if (h.droplets <= l.droplets || h.reach <= l.reach + 0.5f) Fail("the Heavy Roller's flick doesn't throw more, further");
        if (h.damage <= l.damage) Fail("the Heavy Roller's flick doesn't hit harder");
        if (h.rollSpeed >= l.rollSpeed || h.width <= l.width) Fail("the Heavy Roller doesn't roll slower and wider");

        // Walking speed, for comparison.
        player.PlaceAt(start - forward * 6f, player.transform.eulerAngles.y);
        frames.Clear();
        yield return Hold(Fwd, false, 0.8f, "walk");
        Vector3 w0 = player.transform.position;
        yield return Hold(Fwd, false, 0.4f, "walk");
        float walkSpeed = Flat(player.transform.position - w0).magnitude / 0.4f;

        // Its roll: down after the flick, slow at first and walking pace after 3s, inking past the drum's ends.
        player.PlaceAt(start - forward * 6f, player.transform.eulerAngles.y);
        frames.Clear();
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.4f, "placed");
        var heavyRoller = (Roller)loadout.CurrentWeapon;
        player.RefillInk();
        heavyRoller.ScriptedFire = true;
        yield return HoldUntil(Still, false, 1.5f, "to roll", f => heavyRoller.Down);
        Vector3 rollFrom = player.transform.position;
        yield return Hold(Fwd, false, 0.2f, "roll");
        float earlyMul = player.WeaponSpeedMultiplier;
        yield return Hold(Fwd, false, 1.3f, "roll");
        float midMul = player.WeaponSpeedMultiplier;
        yield return Hold(Fwd, false, 1.7f, "roll");
        float lateMul = player.WeaponSpeedMultiplier;
        Vector3 r0 = player.transform.position;
        yield return Hold(Fwd, false, 0.4f, "roll");
        float lateSpeed = Flat(player.transform.position - r0).magnitude / 0.4f;
        Vector3 rollTo = player.transform.position;
        heavyRoller.ScriptedFire = false;
        yield return Hold(Still, false, 0.3f, "release");
        Vector3 mid = Vector3.Lerp(rollFrom, rollTo, 0.5f), side = Vector3.Cross(Vector3.up, forward);
        float drumHalf = heavyRoller.RollerWidth * 0.5f, inkHalf = heavyRoller.RollInkWidth * 0.5f;
        bool pastDrum = OnFloor(mid + side * (drumHalf + 0.2f)) == player.Team && OnFloor(mid - side * (drumHalf + 0.2f)) == player.Team;
        Note($"Heavy Roller rolled {Flat(rollTo - rollFrom).magnitude:F1}m: x{earlyMul:0.##} at first, x{midMul:0.##} at 1.5s, x{lateMul:0.##} after 3s ({lateSpeed:F1} m/s against walking {walkSpeed:F1}); " +
             $"ink {(pastDrum ? "reaches" : "DOESN'T reach")} 0.2m past the {heavyRoller.RollerWidth:F2}m drum's ends (strip set to {heavyRoller.RollInkWidth:F2}m)");
        if (earlyMul > heavyRoller.RollStartSpeed + 0.1f || midMul <= earlyMul + 0.1f || midMul >= lateMul - 0.1f) Fail("the Heavy Roller's roll didn't start slow and build");
        if (Mathf.Abs(lateMul - heavyRoller.RollSpeed) > 0.01f || heavyRoller.RollRampTime < 2.9f) Fail("the Heavy Roller didn't reach its rolling speed over about 3s");
        if (Mathf.Abs(lateSpeed / Mathf.Max(walkSpeed, 0.1f) - 1f) > 0.12f) Fail("the Heavy Roller doesn't end up rolling at walking speed");
        if (!pastDrum || inkHalf <= drumHalf) Fail("the Heavy Roller's ink doesn't reach past its drum");

        ProjectileManager.Impact -= onLand;
        loadout.SetMainWeapon(weaponBefore);
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        player.PlaceAt(start, player.transform.eulerAngles.y);
        frames.Clear();
        yield return Hold(Still, false, 0.3f, "cleanup");
        NetGameManager.SendOverride = null;
    }

    // Swipes alternating sides, a light hit each; held, a fast, narrow brush along the floor.
    IEnumerator InkbrushWeapon()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int index = WeaponIndexOf(loadout, "Inkbrush");
        if (index < 0) { Fail("no Inkbrush in the player's weapons"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        int enemyTeam = player.Team == 1 ? 2 : 1;
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 forward = Flat(player.transform.forward).normalized;
        Vector3 start = player.transform.position;
        int OnFloor(Vector3 at)
        {
            if (!Physics.Raycast(new Vector3(at.x, station.position.y + 1f, at.z), Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return -1;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            return ink != null ? ink.getSurfaceTeam(ink.UVFromHit(hit)) : -1;
        }
        float DamageTo(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Sum(m => ((DamageData)m.data).amount);
        int HitsTo(ulong id) => sent.Count(m => m.id == NetMsg.Damage && m.to == id);
        float Lean(Vector3[] launches) => launches.Average(v => Vector3.SignedAngle(forward, Flat(v), Vector3.up));
        CaptureSends();
        loadout.SetMainWeapon(index);
        yield return Hold(Still, false, 0.3f, "equip");
        Brush brush = loadout.CurrentWeapon as Brush;
        if (brush == null) { Fail("the Inkbrush didn't equip"); loadout.SetMainWeapon(weaponBefore); NetGameManager.SendOverride = null; yield break; }

        // Two swipes: each the other way, the fan leaning the way the head swings; the swing sweeps across.
        const ulong Foe = 3401;
        SpawnRemote(Foe, enemyTeam, new Vector3(start.x, station.position.y + 0.05f, start.z) + forward * 2.5f, "BrushFoe");
        yield return Hold(Still, false, 0.3f, "foe");
        player.RefillInk();
        sent.Clear();
        var signs = new List<int>(); var leans = new List<float>(); var sweeps = new List<float>();
        for (int s = 0; s < 2; s++)
        {
            brush.ScriptedFire = true; yield return null; brush.ScriptedFire = false;
            float from = brush.YawOffset;
            yield return Hold(Still, false, brush.SwipeTime * 0.9f, "swipe");
            sweeps.Add(brush.YawOffset - from);
            yield return Hold(Still, false, brush.SwipeRate, "swipe");
            signs.Add(brush.LastSwipeSign);
            leans.Add(Lean(brush.LastSwipe ?? new Vector3[0]));
        }
        yield return Hold(Still, false, 0.6f, "land");
        Note($"swipes: sides {string.Join(", ", signs)}, fans leaning {string.Join("°, ", leans.Select(x => x.ToString("F0")))}°, swung {string.Join("°, ", sweeps.Select(x => x.ToString("F0")))}°; " +
             $"the enemy 2.5m ahead took {DamageTo(Foe):0} in {HitsTo(Foe)} hit(s) (a swipe does up to {brush.SwipeDamage.damage:0})");
        if (brush.Swipes != 2 || signs[0] != -signs[1]) Fail("swipes don't alternate sides");
        if (Mathf.Sign(leans[0]) != signs[0] || Mathf.Sign(leans[1]) != signs[1]) Fail("a swipe's droplets don't lean the way it swings");
        if (Mathf.Sign(sweeps[0]) != signs[0] || Mathf.Abs(sweeps[0]) < 20f) Fail("the brush doesn't swing across for a swipe");
        if (HitsTo(Foe) < 1 || HitsTo(Foe) > 2 || DamageTo(Foe) > brush.SwipeDamage.damage * 2f + 0.01f) Fail("a swipe didn't hit the enemy once for its light damage");
        Despawn(Foe);

        // Swung where we look, like a sword: looking up raises the swing and the droplets.
        float Elevation(Vector3[] launches) => launches.Average(v => Mathf.Atan2(v.y, Flat(v).magnitude) * Mathf.Rad2Deg);
        float levelElevation = Elevation(brush.LastSwipe);
        player.ScriptedInput.lookStick = new Vector2(0f, 1f);
        yield return Hold(Still, false, 0.2f, "look up");
        player.ScriptedInput.lookStick = Vector2.zero;
        float lookedUp = -player.CameraPitch;
        yield return Hold(Still, false, brush.SwipeRate, "cooldown");
        brush.ScriptedFire = true; yield return null; brush.ScriptedFire = false;
        yield return Hold(Still, false, brush.SwipeTime * 0.6f, "aimed swipe");
        float swingPitch = -brush.SwipePitchNow;
        yield return Hold(Still, false, brush.SwipeTime, "aimed swipe");
        float raisedElevation = Elevation(brush.LastSwipe);
        player.ScriptedInput.recenter = true;
        yield return Hold(Still, false, 0.6f, "recenter");
        player.ScriptedInput.recenter = false;
        Note($"looking {lookedUp:F0}° up: the swing pitched {swingPitch:F0}° up, the droplets rose from {levelElevation:F0}° to {raisedElevation:F0}°");
        if (Mathf.Abs(swingPitch - lookedUp) > 8f) Fail("the swipe didn't swing where we look");
        if (Mathf.Abs(raisedElevation - levelElevation - lookedUp) > 6f) Fail("the swipe's droplets didn't follow the view up");

        // Walking speed, then brushing: down, fast, a strip about 2m wide.
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        Vector3 back = start - forward * 5f;
        player.PlaceAt(back, player.transform.eulerAngles.y);
        frames.Clear();
        yield return Hold(Fwd, false, 0.8f, "walk");
        Vector3 w0 = player.transform.position;
        yield return Hold(Fwd, false, 0.4f, "walk");
        float walkSpeed = Flat(player.transform.position - w0).magnitude / 0.4f;
        yield return Hold(Still, false, 0.4f, "stop");
        player.PlaceAt(back, player.transform.eulerAngles.y);
        frames.Clear();
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.3f, "placed");
        player.RefillInk();
        brush.ScriptedFire = true;
        yield return HoldUntil(Still, false, 1f, "to brush", f => brush.Down);
        bool wentDown = brush.Down;
        yield return Hold(Still, false, 0.4f, "settle");
        float headUp = brush.Head.position.y - station.position.y;
        floor.FillRegion(new Rect(0, 0, 1, 1), 0); // not the swipe's ink
        Vector3 from0 = player.transform.position;
        SpawnRemote(Foe, enemyTeam, new Vector3(from0.x, station.position.y + 0.05f, from0.z) + forward * 14f, "BrushFoe"); // past where the speed is measured
        yield return Hold(Still, false, 0.3f, "foe");
        sent.Clear();
        var samples = new List<string>();
        for (int k = 0; k < 6; k++)
        {
            yield return Hold(Fwd, false, 0.1f, "brush");
            samples.Add($"{(brush.Down ? "D" : "u")}{player.WeaponSpeedMultiplier:0.##}");
        }
        Note($"brushing frames (down/up, speed): {string.Join(" ", samples)}");
        Vector3 b0 = player.transform.position;
        yield return Hold(Fwd, false, 0.4f, "brush");
        float brushSpeedSeen = Flat(player.transform.position - b0).magnitude / 0.4f;
        float mul = player.WeaponSpeedMultiplier;
        Vector3 to = player.transform.position;
        yield return Hold(Fwd, false, 0.8f, "run over");
        Vector3 mid = Vector3.Lerp(from0, to, 0.5f), side = Vector3.Cross(Vector3.up, forward);
        bool centre = OnFloor(mid) == player.Team;
        bool narrow = OnFloor(mid + side * 0.75f) == player.Team && OnFloor(mid - side * 0.75f) == player.Team
                   && OnFloor(mid + side * 1.6f) != player.Team && OnFloor(mid - side * 1.6f) != player.Team; // about 2m
        string profile = string.Join(" ", Enumerable.Range(-6, 13).Select(k => OnFloor(mid + side * k * 0.25f) == player.Team ? "#" : "."));
        Note($"across the strip every 0.25m: {profile} (head {brush.HeadWidth:F2}m wide)");
        float hit = DamageTo(Foe); int hits = HitsTo(Foe);
        Despawn(Foe);
        // A jump lifts it.
        player.PressJump();
        yield return Hold(Still, false, 0.15f, "jump");
        bool lifted = !player.IsGrounded && !brush.Down;
        brush.ScriptedFire = false;
        yield return Hold(Still, false, 0.5f, "release");
        Note($"brushing: down {(wentDown ? "yes" : "NO")} (head {headUp:F2}m up), x{mul:0.##} ({brushSpeedSeen:F1} m/s against walking {walkSpeed:F1}), strip {(centre ? "inked" : "NOT inked")} down the middle, {(narrow ? "about 2m wide" : "NOT about 2m wide")}; " +
             $"ran into the enemy: {hit:0} in {hits} hit(s); jump {(lifted ? "lifted it" : "DIDN'T lift it")}; released: speed x{player.WeaponSpeedMultiplier:0.##}");
        if (!wentDown || headUp > 0.45f) Fail("holding after a swipe didn't put the brush down on the floor");
        if (Mathf.Abs(mul - brush.BrushSpeed) > 0.01f || Mathf.Abs(brushSpeedSeen / Mathf.Max(walkSpeed, 0.1f) - brush.BrushSpeed) > 0.15f) Fail("brushing isn't its fast multiple of walking speed");
        if (!centre || !narrow) Fail("brushing didn't ink a strip about 2m wide");
        if (hits != 1 || Mathf.Abs(hit - brush.BrushDamage) > 0.01f) Fail("brushing into the enemy didn't hit them once for its light damage");
        if (!lifted || player.WeaponSpeedMultiplier != 1f) Fail("a jump didn't lift it, or letting go didn't give our speed back");

        // Theirs: a replayed swipe swings their brush.
        const ulong Them = 3402;
        Vector3 theirSpot = station.TransformPoint(new Vector3(6f, 0.05f, -6f));
        SpawnRemote(Them, enemyTeam, theirSpot, "TheirBrush");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = Them, position = theirSpot, moveDir = Vector2.zero, team = enemyTeam, weapon = index }, Them);
        yield return Hold(Still, false, 0.3f, "theirs");
        Brush theirs = gm.GetPlayer(Them)?.GetComponent<PlayerLoadout>().CurrentWeapon as Brush;
        sent.Clear();
        gm.Receive(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = Them, team = enemyTeam, origin = theirSpot + Vector3.up, inherit = Vector3.zero,
            velocities = new[] { 1f, 1f, 9f, 0f, 1f, 9f, -1f, 1f, 9f }, splashSizes = new[] { 9, 9, 9 }, visible = new[] { true, true, true }
        }, Them);
        yield return null;
        bool theirSwing = theirs != null && theirs.Swiping;
        yield return Hold(Still, false, 0.5f, "theirs");
        Note($"their swipe: {(theirSwing ? "swung" : "DIDN'T swing")}; we sent {sent.Count(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)} damage/splat messages");
        if (!theirSwing) Fail("their brush didn't swing for their swipe");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("their swipe painted or damaged from here");
        Despawn(Them);

        loadout.SetMainWeapon(weaponBefore);
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        player.PlaceAt(start, player.transform.eulerAngles.y);
        frames.Clear();
        yield return Hold(Still, false, 0.3f, "cleanup");
        NetGameManager.SendOverride = null;
    }
}
