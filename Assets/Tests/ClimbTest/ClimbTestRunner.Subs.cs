using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Controls;
using UnityEngine.EventSystems;
using static ClimbTestLayout;

// ClimbTestRunner scenarios: subs: beacon, sprinkler, curling bomb.
public partial class ClimbTestRunner
{

    IEnumerator SubBeacon()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (loadout == null || map == null) { Fail("player has no PlayerLoadout (or no MapScreen)"); yield break; }
        if (loadout.SubReadyLight == null) Fail("the ink tank's sub light isn't hooked up");
        loadout.SetSub(SubType.Beacon);
        if (Mathf.Abs(loadout.SubInkCost - loadout.BeaconPrefab.InkCost) > 0.001f) Fail("the sub's cost doesn't come from its prefab");
        CaptureSends();
        SubDevice.RemoveAll();
        const ulong FoeId = 3011;
        int enemyTeam = player.Team == 1 ? 2 : 1;

        // Costs the sub's share of ink; with less than that, nothing happens.
        player.RefillInk();
        yield return Hold(Still, false, 0.2f, "stand");
        PlayerStateData full = player.GetNetState(player.OwnerId, 0);
        bool litFull = loadout.SubReadyLight != null && loadout.SubReadyLight.enabled;
        if (!loadout.UseSub()) { Fail("couldn't place a beacon with a full tank"); NetGameManager.SendOverride = null; yield break; }
        Beacon beacon = SubDevice.All.OfType<Beacon>().First(b => b.OwnerId == player.OwnerId);
        Vector3 beaconPos = beacon.transform.position;
        float ahead = Vector3.Dot(beaconPos - player.transform.position, player.transform.forward);
        Note($"ink after placing {player.InkLevel:F2}, beacon {ahead:F1}m ahead");
        if (Mathf.Abs(player.InkLevel - 0.3f) > 0.01f) Fail($"ink went to {player.InkLevel:F2}, not 0.30");
        if (ahead < 0.5f) Fail("beacon isn't in front of the player");
        if (!sent.Any(m => m.id == NetMsg.SubSpawn)) Fail("beacon wasn't broadcast");
        if (loadout.UseSub() || SubDevice.All.Count != 1) Fail("placed a beacon with only 30% ink");
        yield return null;
        PlayerStateData low = player.GetNetState(player.OwnerId, 0);
        bool litLow = loadout.SubReadyLight != null && loadout.SubReadyLight.enabled;
        Note($"tank light {(litFull ? "on" : "off")} when full, {(litLow ? "on" : "off")} at {player.InkLevel:F2}; sent ink {full.ink:F2}/{low.ink:F2}, ready {full.subReady}/{low.subReady}");
        if (!litFull || litLow) Fail("the tank light should be on only with enough ink for the sub");
        if (!full.subReady || low.subReady || Mathf.Abs(low.ink - player.InkLevel) > 0.01f) Fail("ink level and sub readiness aren't in our state updates");

        // An enemy beacon arrives from its owner; only ours shows on our map.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "stand");

        // Their tank shows their ink, and its light whether they can use their sub.
        PlayerController foe = gm.GetPlayer(FoeId);
        PlayerLoadout foeLoadout = foe != null ? foe.GetComponent<PlayerLoadout>() : null;
        foreach (bool ready in new[] { true, false })
        {
            PlayerStateData state = foe.GetNetState(FoeId, 0);
            state.subReady = ready;
            state.ink = ready ? 0.8f : 0.2f;
            foe.ApplyNetState(state);
            yield return null;
            if (foeLoadout == null || foeLoadout.SubReadyLight == null || foeLoadout.SubReadyLight.enabled != ready) Fail($"their tank light didn't follow their state (ready {ready})");
            if (Mathf.Abs(foe.InkLevel - state.ink) > 0.001f) Fail("their ink level didn't follow their state");
        }

        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = FoeId, subId = 1, team = enemyTeam, subType = (int)SubType.Beacon, landed = true, normal = Vector3.up, position = station.TransformPoint(new Vector3(-8f, 0f, 8f)) }, FoeId);
        yield return Hold(Still, false, 0.1f, "stand");
        if (SubDevice.Find(FoeId, 1) == null) Fail("enemy beacon wasn't created from its message");

        // Walk away, open the map and pick our beacon.
        player.PlaceAt(player.transform.position + station.TransformDirection(new Vector3(9f, 0f, -7f)), player.transform.eulerAngles.y);
        frames.Clear(); // the teleport isn't a movement glitch
        map.ScriptedHold = true;
        yield return Hold(Still, false, 0.3f, "map");
        if (map.ShownBeacons.Count != 1 || map.ShownBeacons[0] != beacon) Fail($"map shows {map.ShownBeacons.Count} beacon(s), expected just ours");
        else if (!map.BeaconMarkers[0].gameObject.activeSelf) Fail("our beacon has no marker");
        else map.BeaconMarkers[0].onClick.Invoke();
        map.ScriptedHold = false;
        if (!player.IsSuperJumping) { Fail("picking the beacon didn't start a Super Jump"); }
        else
        {
            yield return HoldUntil(Still, false, 6f, "jump", f => !player.IsSuperJumping);
            yield return Hold(Still, false, 0.3f, "land");
            float miss = new Vector2(player.transform.position.x - beaconPos.x, player.transform.position.z - beaconPos.z).magnitude;
            Note($"landed {miss:F2}m from the beacon");
            if (miss > 1.5f) Fail($"landed {miss:F2}m from the beacon");
            if (beacon != null) Fail("landing on the beacon didn't break it");
            if (!sent.Any(m => m.id == NetMsg.SubDestroy)) Fail("the break wasn't broadcast");
        }

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "stand");
        if (SubDevice.Find(FoeId, 1) != null) Fail("enemy beacon outlived its owner leaving");
        NetGameManager.SendOverride = null;
    }

    IEnumerator BeaconHealth()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        loadout.SetSub(SubType.Beacon);
        CaptureSends();
        SubDevice.RemoveAll();
        const ulong FoeId = 3012;
        int enemyTeam = player.Team == 1 ? 2 : 1;

        player.RefillInk();
        loadout.UseSub();
        Beacon beacon = SubDevice.All.OfType<Beacon>().FirstOrDefault(b => b.OwnerId == player.OwnerId);
        if (beacon == null) { Fail("couldn't place a beacon"); NetGameManager.SendOverride = null; yield break; }

        // An enemy shot takes a chunk; friendly fire doesn't; the next enemy hit breaks it.
        Shoot(beacon.transform.position + Vector3.up * 3f, enemyTeam, 4242);
        yield return Hold(Still, false, 0.6f, "shot");
        float afterShot = beacon != null ? beacon.Health : 0f;
        Note($"health after an enemy shot {afterShot:F0}/35");
        if (beacon == null || afterShot <= 0f || afterShot >= 35f) Fail("an enemy shot didn't damage it (or broke it outright)");
        Shoot(beacon.transform.position + Vector3.up * 3f, player.Team, player.OwnerId);
        yield return Hold(Still, false, 0.6f, "shot");
        if (beacon == null || beacon.Health != afterShot) Fail("our own shot damaged it");
        sent.Clear();
        if (beacon != null) beacon.TakeDamage(afterShot, enemyTeam, 4242);
        yield return null;
        if (beacon != null) Fail("didn't break at 0 health");
        if (!sent.Any(m => m.id == NetMsg.SubDestroy)) Fail("breaking wasn't broadcast");

        // Lifetime: one left alone expires.
        player.RefillInk();
        loadout.UseSub();
        Beacon timed = SubDevice.All.OfType<Beacon>().FirstOrDefault(b => b.OwnerId == player.OwnerId);
        if (timed != null) timed.ExpireIn(0.3f);
        yield return Hold(Still, false, 0.6f, "expire");
        if (timed != null) Fail("didn't expire");

        // Someone else's: our hit goes to its owner (who decides), and charges our special.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "remote");
        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = FoeId, subId = 4, team = enemyTeam, subType = (int)SubType.Beacon, landed = true, normal = Vector3.up, position = station.TransformPoint(new Vector3(6f, 0f, 6f)) }, FoeId);
        yield return Hold(Still, false, 0.1f, "remote");
        SubDevice theirs = SubDevice.Find(FoeId, 4);
        if (theirs == null) { Fail("their beacon wasn't created"); }
        else
        {
            sent.Clear();
            float points = loadout.SpecialPoints;
            Shoot(theirs.transform.position + Vector3.up * 3f, player.Team, player.OwnerId);
            yield return Hold(Still, false, 0.6f, "remote");
            var hit = sent.FirstOrDefault(m => m.id == NetMsg.SubDamage);
            Note($"special +{loadout.SpecialPoints - points:F0}p for the hit");
            if (hit.data == null || hit.to != FoeId) Fail("hit on their beacon wasn't sent to its owner");
            if (theirs == null || theirs.Health != 35f) Fail("our copy applied the damage itself");
            if (loadout.SpecialPoints - points < 1f) Fail("hitting their beacon didn't charge the special");
            gm.Receive(NetMsg.SubDestroy, new SubData { ownerId = FoeId, subId = 4 }, FoeId);
            yield return Hold(Still, false, 0.1f, "remote");
            if (SubDevice.Find(FoeId, 4) != null) Fail("their break message didn't remove it");
        }

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "remote");
        NetGameManager.SendOverride = null;
    }

    IEnumerator CurlingBombSlopes()
    {
        CurlingBomb prefab = player.GetComponent<PlayerLoadout>()?.CurlingBombPrefab;
        if (prefab == null) { Fail("player has no curling bomb"); yield break; }
        Transform ramp = GameObject.Find(Name(Station.Ramp30)).transform;
        Quaternion tilt = ramp.rotation * RampRotation(30f);
        Vector3 normal = tilt * Vector3.up, upSlope = tilt * Vector3.forward;
        Vector3 foot = ramp.TransformPoint(RampSurfacePoint(30f, Ramp30Length, 0f));

        // Visual-only copies (not ours), so the station's ink stays as it is.
        IEnumerator Run(Vector3 from, Vector3 velocity, float seconds, List<(float along, float gap, float speed, float upDot, bool onRamp)> track)
        {
            CurlingBomb b = Instantiate(prefab, from, Quaternion.identity);
            b.Init(3099, 960 + track.GetHashCode() % 100, 2, ownedLocally: false);
            b.Launch(velocity, seconds + 1f);
            float end = Time.time + seconds;
            while (Time.time < end && b != null)
            {
                yield return null;
                Vector3 rel = b.transform.position - foot;
                float along = Vector3.Dot(rel, upSlope);
                bool onRamp = along > 0.5f && along < Ramp30Length - 0.5f && b.Grounded && Mathf.Abs(Vector3.Dot(rel, ramp.right)) < RampWidth / 2f - 0.5f;
                track.Add((along, Vector3.Dot(rel, normal), b.Velocity.magnitude, Vector3.Dot(b.transform.up, normal), onRamp));
            }
            if (b != null) b.Remove();
        }

        // Up: thrown at the ramp from the floor.
        var up = new List<(float along, float gap, float speed, float upDot, bool onRamp)>();
        yield return Run(ramp.TransformPoint(new Vector3(0f, 0.02f, -3f)), ramp.forward * prefab.SlideSpeed, 2.5f, up);
        var upOn = up.Where(t => t.onRamp).ToList();
        float highest = up.Count > 0 ? up.Max(t => t.along) : 0f;
        int peakAt = up.FindIndex(t => t.along >= highest - 1e-4f);
        float speedLow = upOn.Count > 0 ? upOn.First().speed : 0f, speedHigh = upOn.Count > 0 ? upOn.Last(t => up.IndexOf(t) <= peakAt).speed : 0f;
        float rolledBack = up.Count > 0 ? highest - up.Last().along : 0f;
        float upGap = upOn.Count > 0 ? upOn.Max(t => Mathf.Abs(t.gap)) : 99f;
        float upTilt = upOn.Count > 0 ? upOn.Min(t => t.upDot) : 0f;

        // Down: let go near the top, heading downhill.
        var down = new List<(float along, float gap, float speed, float upDot, bool onRamp)>();
        Vector3 top = ramp.TransformPoint(RampSurfacePoint(30f, Ramp30Length, Ramp30Length - 2f)) + normal * 0.02f;
        yield return Run(top, -ramp.forward * 3f, 2f, down);
        var downOn = down.Where(t => t.onRamp).ToList();
        float startSpeed = downOn.Count > 0 ? downOn.First().speed : 0f, bottomSpeed = downOn.Count > 0 ? downOn.Last().speed : 0f;
        float downGap = downOn.Count > 0 ? downOn.Max(t => Mathf.Abs(t.gap)) : 99f;
        float downTilt = downOn.Count > 0 ? downOn.Min(t => t.upDot) : 0f;
        float pastFoot = down.Count > 0 ? -down.Last().along : 0f;

        Note($"up: reached {highest:F1}m up the slope, {speedLow:F1} -> {speedHigh:F1} m/s on the way, rolled back {rolledBack:F1}m; furthest off the surface {upGap:F2}m, tilt match {upTilt:F2}");
        Note($"down: {startSpeed:F1} -> {bottomSpeed:F1} m/s down the slope; furthest off the surface {downGap:F2}m, tilt match {downTilt:F2}; ended {pastFoot:F1}m out across the floor");
        if (highest < 1.5f) Fail("didn't get up the ramp (bounced off its foot?)");
        if (speedHigh >= speedLow - 1f) Fail("didn't slow down climbing the ramp");
        if (rolledBack < 1f) Fail("didn't roll back down after running out of speed");
        if (bottomSpeed <= startSpeed + 2f) Fail("didn't speed up sliding down the ramp");
        if (upGap > 0.08f || downGap > 0.08f) Fail("left the surface while riding the ramp");
        if (upTilt < 0.97f || downTilt < 0.97f) Fail("didn't tilt with the ramp");
        if (pastFoot < 1f) Fail("didn't carry on across the floor after coming down");
        yield return Hold(Still, false, 0.1f, "settle");
    }

    IEnumerator CurlingBombSub()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        CurlingBomb prefab = loadout != null ? loadout.CurlingBombPrefab : null;
        if (prefab == null) { Fail("player has no curling bomb"); yield break; }
        CaptureSends();
        SubDevice.RemoveAll();
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        int enemyTeam = player.Team == 1 ? 2 : 1;
        const ulong FoePath = 3080, FoeBlast = 3081, FoeFar = 3082;
        loadout.SetSub(SubType.CurlingBomb);
        if (loadout.SubName != "CURLING") Fail("picking the curling bomb didn't select it");
        int OnFloor(Vector3 at)
        {
            if (!Physics.Raycast(at + Vector3.up, Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return -1;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            return ink != null ? ink.getSurfaceTeam(ink.UVFromHit(hit)) : -1;
        }
        float DamageTo(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Sum(m => ((DamageData)m.data).amount);

        // Thrown along the aim: costs its ink, slides off inking a stripe, clips the enemy in its way and carries on.
        Vector3 fwd = Vector3.ProjectOnPlane(player.CameraRig.forward, Vector3.up).normalized;
        Vector3 start = player.transform.position;
        SpawnRemote(FoePath, enemyTeam, start + fwd * 3.5f + Vector3.up * 0.05f, "FoePath");
        yield return Hold(Still, false, 0.3f, "stand");
        player.RefillInk();
        sent.Clear();
        if (!loadout.UseSub()) { Fail("couldn't throw a curling bomb with a full tank"); loadout.SetSub(SubType.Beacon); NetGameManager.SendOverride = null; yield break; }
        CurlingBomb bomb = loadout.LastBomb;
        float inkLeft = player.InkLevel;
        bool told = sent.Any(m => m.id == NetMsg.SubSpawn && m.data is SubData d && d.subType == (int)SubType.CurlingBomb && Mathf.Abs(d.fuse - prefab.Fuse) < 0.1f);
        yield return Hold(Still, false, 0.8f, "slide");
        float slid = Vector3.Dot(bomb.transform.position - start, fwd);
        int stripe = 0, probes = 0;
        for (float x = 1.5f; x < slid - 0.5f; x += 0.5f) { probes++; if (OnFloor(start + fwd * x) == player.Team) stripe++; }
        Note($"ink after throwing {inkLeft:F2}; slid {slid:F1}m in 0.8s ({(bomb.Grounded ? "on the ground" : "in the air")}); {stripe}/{probes} points along its path inked; FoePath took {DamageTo(FoePath):0}");
        if (Mathf.Abs(inkLeft - (1f - prefab.InkCost)) > 0.01f) Fail($"didn't cost {prefab.InkCost * 100f:0}% ink");
        if (!told) Fail("the throw (with its fuse) wasn't broadcast");
        if (slid < 4f || !bomb.Grounded) Fail("didn't slide off along the ground");
        if (probes == 0 || stripe < probes * 0.8f) Fail("didn't ink a stripe along its path");
        if (Mathf.Abs(DamageTo(FoePath) - prefab.PassDamage) > 0.01f) Fail($"the enemy it slid through should take {prefab.PassDamage:0}, once");
        if (slid < 4.5f) Fail("stopped at the enemy instead of carrying on");
        bomb.Break();

        // Off a wall: slid straight at the NeutralWall block (a visual-only copy, so that station stays clean), it comes back.
        Transform wallStation = GameObject.Find(Name(Station.NeutralWall)).transform;
        CurlingBomb bouncer = Instantiate(prefab, wallStation.TransformPoint(new Vector3(0f, 0.05f, -4f)), Quaternion.identity);
        bouncer.Init(FoeFar, 901, enemyTeam, ownedLocally: false);
        bouncer.Launch(wallStation.forward * prefab.SlideSpeed, 3f);
        yield return Hold(Still, false, 1f, "bounce");
        float back = wallStation.InverseTransformPoint(bouncer.transform.position).z;
        float heading = Vector3.Dot(bouncer.Velocity.normalized, wallStation.forward);
        Note($"1s after sliding at the wall from 4m: {-back:F1}m out from it, heading {(heading < 0f ? "away" : "into it")}");
        if (heading >= 0f || back > -1f) Fail("didn't bounce off the wall");
        bouncer.Remove();

        // The blast: one parked on a short fuse; enemies in range take 45, those outside nothing; a wide patch of ink.
        Vector3 spot = station.TransformPoint(new Vector3(-5f, 0f, 5f));
        SpawnRemote(FoeBlast, enemyTeam, spot + station.right * (prefab.BlastRadius * 0.5f) + Vector3.up * 0.05f, "FoeBlast");
        SpawnRemote(FoeFar, enemyTeam, spot + station.right * (prefab.BlastRadius + 2f) + Vector3.up * 0.05f, "FoeFar");
        yield return Hold(Still, false, 0.2f, "blast");
        sent.Clear();
        CurlingBomb parked = Instantiate(prefab, spot, Quaternion.identity);
        parked.Init(player.OwnerId, 900, player.Team, ownedLocally: true);
        parked.Launch(Vector3.zero, 0.4f);
        yield return Hold(Still, false, 0.8f, "blast");
        int patch = 0;
        for (int i = 0; i < 8; i++) if (OnFloor(spot + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * (prefab.BlastRadius * 0.6f)) == player.Team) patch++;
        Note($"blast: FoeBlast took {DamageTo(FoeBlast):0}, FoeFar {DamageTo(FoeFar):0}; {patch}/8 points {prefab.BlastRadius * 0.6f:F1}m out inked; centre {(OnFloor(spot) == player.Team ? "inked" : "not inked")}");
        if (SubDevice.Find(player.OwnerId, 900) != null || parked != null && !parked.Exploded) Fail("didn't explode on its fuse");
        if (Mathf.Abs(DamageTo(FoeBlast) - prefab.BlastDamage) > 0.01f) Fail($"an enemy in the blast should take {prefab.BlastDamage:0}");
        if (DamageTo(FoeFar) > 0f) Fail("hit an enemy outside the blast");
        if (OnFloor(spot) != player.Team || patch < 7) Fail("the blast didn't ink a wide patch");

        // Holding the button runs the fuse down; held to its end it goes by itself.
        player.RefillInk();
        if (!loadout.PressSub() || loadout.HeldBomb == null) Fail("pressing didn't take a bomb in hand");
        yield return Hold(Still, false, 1.5f, "cook");
        loadout.ReleaseSub();
        CurlingBomb cooked = loadout.LastBomb;
        float cookedFuse = cooked != null ? cooked.FuseLeft : 0f;
        if (cooked != null) cooked.Break();
        player.RefillInk();
        loadout.PressSub();
        float pressed = Time.time;
        yield return HoldUntil(Still, false, prefab.Fuse, "cook", f => loadout.HeldBomb == null);
        CurlingBomb overcooked = loadout.LastBomb;
        Note($"held 1.5s: thrown with {cookedFuse:F2}s left; held on: thrown by itself after {Time.time - pressed:F2}s with {(overcooked != null ? overcooked.FuseLeft : 0f):F2}s left");
        if (Mathf.Abs(cookedFuse - (prefab.Fuse - 1.5f)) > 0.15f) Fail("holding didn't shorten the fuse");
        if (overcooked == cooked || overcooked == null || Mathf.Abs(overcooked.FuseLeft - prefab.MinFuse) > 0.15f) Fail("holding on didn't throw it at its shortest fuse");
        if (overcooked != null) overcooked.Break();

        // Theirs: slides and goes off here too, but only the owner paints and damages.
        sent.Clear();
        gm.Receive(NetMsg.SubSpawn, new SubData
        {
            ownerId = FoeFar, subId = 7, team = enemyTeam, subType = (int)SubType.CurlingBomb,
            position = station.TransformPoint(new Vector3(6f, 0.05f, -6f)), velocity = station.right * 3f, fuse = 0.6f
        }, FoeFar);
        yield return Hold(Still, false, 0.2f, "remote");
        bool shown = SubDevice.Find(FoeFar, 7) is CurlingBomb;
        yield return Hold(Still, false, 0.8f, "remote");
        if (!shown) Fail("their curling bomb wasn't shown");
        if (SubDevice.Find(FoeFar, 7) != null) Fail("their curling bomb didn't go off");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("their bomb painted or damaged from here");

        foreach (ulong id in new[] { FoePath, FoeBlast, FoeFar }) Despawn(id);
        loadout.SetSub(SubType.Beacon);
        SubDevice.RemoveAll();
        yield return Hold(Still, false, 0.2f, "cleanup");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }

    IEnumerator SprinklerSub()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        CaptureSends();
        SubDevice.RemoveAll();
        const ulong FoeId = 3030;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        loadout.SetSub(SubType.Sprinkler);
        player.RefillInk();
        yield return Hold(Still, false, 0.3f, "ready");

        // Thrown: costs 60%, flies, then sticks to the floor.
        if (!loadout.UseSub()) { Fail("couldn't throw a sprinkler"); loadout.SetSub(SubType.Beacon); NetGameManager.SendOverride = null; yield break; }
        Sprinkler s = SubDevice.All.OfType<Sprinkler>().First(d => d.OwnerId == player.OwnerId);
        Note($"ink after throwing {player.InkLevel:F2}");
        if (Mathf.Abs(player.InkLevel - (1f - loadout.SprinklerPrefab.InkCost)) > 0.01f) Fail($"didn't cost {loadout.SprinklerPrefab.InkCost * 100f:0}% ink");
        if (s.Landed) Fail("landed the moment it was thrown");
        if (!sent.Any(m => m.id == NetMsg.SubSpawn && m.data is SubData d && !d.landed && d.subType == (int)SubType.Sprinkler)) Fail("throw wasn't broadcast");
        yield return HoldUntil(Still, false, 3f, "throw", f => s == null || s.Landed);
        if (s == null || !s.Landed) { Fail("never landed"); loadout.SetSub(SubType.Beacon); NetGameManager.SendOverride = null; yield break; }
        float thrownTo = Vector3.Distance(s.transform.position, player.transform.position);
        Note($"stuck {thrownTo:F1}m away, tilt {Vector3.Angle(s.transform.up, Vector3.up):F0}°, {s.TimeLeft:F1}s to live");
        if (Vector3.Angle(s.transform.up, Vector3.up) > 5f) Fail("not stuck upright on the floor");
        if (!sent.Any(m => m.id == NetMsg.SubSpawn && m.data is SubData d && d.landed)) Fail("landing wasn't broadcast");
        if (Mathf.Abs(s.TimeLeft - s.Lifetime) > 0.2f) Fail("doesn't last its lifetime from landing");

        // Spraying, winding down.
        float spin = s.SpinSpeed, shotSpeed = s.ShotSpeed, gap = s.FireInterval;
        int shots = s.ShotsFired;
        yield return Hold(Still, false, 3f, "spray");
        Note($"after 3s: spin {spin:F0}->{s.SpinSpeed:F0}°/s, shot speed {shotSpeed:F1}->{s.ShotSpeed:F1}, gap {gap:F2}->{s.FireInterval:F2}s, {s.ShotsFired - shots} volleys");
        if (s.ShotsFired - shots < 5) Fail("barely fired");
        if (!(s.SpinSpeed < spin && s.ShotSpeed < shotSpeed && s.FireInterval > gap)) Fail("didn't wind down");
        int inked = 0;
        for (int i = 0; i < 16 * 6; i++) // rings 1-6m out
        {
            Vector3 probe = s.transform.position + Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward * (1 + i / 16) + Vector3.up;
            if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponent<SurfaceInkManager>() is SurfaceInkManager ink && ink.getSurfaceTeam(ink.UVFromHit(hit)) == player.Team) inked++;
        }
        Note($"{inked}/96 floor points 1-6m around it are our ink");
        if (inked == 0) Fail("didn't ink around itself");

        // One at a time: another throw replaces it. 30 HP.
        player.RefillInk();
        loadout.UseSub();
        yield return null;
        if (s != null) Fail("the first sprinkler survived a second throw");
        Sprinkler second = SubDevice.All.OfType<Sprinkler>().FirstOrDefault(d => d.OwnerId == player.OwnerId);
        yield return HoldUntil(Still, false, 3f, "throw", f => second == null || second.Landed);
        if (second != null) second.TakeDamage(29f, enemyTeam, 4242);
        if (second == null) Fail("broke before 30 damage");
        else second.TakeDamage(1f, enemyTeam, 4242);
        yield return null;
        if (second != null) Fail("survived 30 damage");

        // Someone else's: flies from their throw, snaps to their landing, sprays (visual only).
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "remote");
        Vector3 from = station.TransformPoint(new Vector3(-6f, 2f, 6f)), at = station.TransformPoint(new Vector3(-3f, 0f, 6f));
        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = FoeId, subId = 9, team = enemyTeam, subType = (int)SubType.Sprinkler, position = from, velocity = new Vector3(4f, 3f, 0f), normal = Vector3.up }, FoeId);
        yield return Hold(Still, false, 0.15f, "remote");
        Sprinkler theirs = SubDevice.Find(FoeId, 9) as Sprinkler;
        if (theirs == null || theirs.Landed || theirs.IsOwnedLocally) Fail("their thrown sprinkler wasn't created in flight");
        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = FoeId, subId = 9, team = enemyTeam, subType = (int)SubType.Sprinkler, position = at, normal = Vector3.up, landed = true }, FoeId);
        yield return Hold(Still, false, 1f, "remote");
        if (theirs == null || !theirs.Landed || Vector3.Distance(theirs.transform.position, at) > 0.01f) Fail("their sprinkler didn't snap to the owner's landing");
        else if (theirs.ShotsFired == 0) Fail("their sprinkler isn't spraying");
        gm.Receive(NetMsg.SubDestroy, new SubData { ownerId = FoeId, subId = 9 }, FoeId);
        yield return Hold(Still, false, 0.1f, "remote");
        if (SubDevice.Find(FoeId, 9) != null) Fail("their destroy message didn't remove it");

        Despawn(FoeId);
        loadout.SetSub(SubType.Beacon);
        yield return Hold(Still, false, 0.5f, "settle");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }
}
