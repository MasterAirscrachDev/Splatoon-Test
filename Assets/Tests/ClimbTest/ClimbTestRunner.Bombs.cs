using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The thrown bombs (ThrownBomb): suction (sticks, then its fuse), impact (goes off on contact), ink
// (bounces to a stop, its fuse from the first touch); and the sprinkler's throw reaching about as far.
public partial class ClimbTestRunner
{
    IEnumerator BombSubs()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        CaptureSends();
        SubDevice.RemoveAll();
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        int enemyTeam = player.Team == 1 ? 2 : 1;
        player.ScriptedInput.recenter = true;
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 start = player.transform.position;
        float DamageTo(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Sum(m => ((DamageData)m.data).amount);
        int OnFloor(Vector3 at)
        {
            if (!Physics.Raycast(new Vector3(at.x, station.position.y + 1f, at.z), Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return -1;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            return ink != null ? ink.getSurfaceTeam(ink.UVFromHit(hit)) : -1;
        }

        // The sprinkler's reach thrown level at full speed (past the station floor's end, so worked out).
        float sprinklerReach = loadout.SprinklerPrefab.Throwing.LevelRange(loadout.Hand.y - start.y);
        Note($"sprinkler thrown level: reaches {sprinklerReach:F1}m (throw {loadout.SprinklerPrefab.Throwing.speed:0} m/s, {loadout.SprinklerPrefab.Throwing.angle:0}° up)");
        if (sprinklerReach < 10f) Fail("the sprinkler still reaches short (under 10m thrown level)");

        // The bombs, looking down at the floor ahead (the camera's high and back: flatter, it aims past the
        // floor's end and there's nothing to land on).
        yield return LookAtPitch(35f);
        {
            CameraAim ca = player.GetComponentInChildren<CameraAim>(true);
            Vector3 eye = ca != null ? ca.transform.position : player.BodyCenter;
            bool hit = Physics.Raycast(eye, player.AimForward, out RaycastHit h, 100f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore);
            Note($"aim ray: CameraAim {(ca != null ? ca.name : "NONE")} at {eye - start} (from our feet), forward {player.AimForward}; hit {(hit ? $"{h.collider.name} (layer {LayerMask.LayerToName(h.collider.gameObject.layer)}) {h.distance:F2}m out" : "nothing")}");
        }
        ulong foeId = 3901;
        foreach (SubType type in new[] { SubType.SuctionBomb, SubType.ImpactBomb, SubType.InkBomb })
        {
            loadout.SetSub(type);
            ThrownBomb prefab = loadout.BombPrefab(type);
            if (prefab == null) { Fail($"no {type} prefab on the player"); continue; }
            Vector3? aimed = loadout.AimPoint();
            if (!aimed.HasValue) { Fail("the reticle isn't on anything to throw at"); continue; }
            Vector3 land = aimed.Value; // it should come down on what the reticle's on
            Vector3 foeAt = new Vector3(land.x, station.position.y + 0.05f, land.z) + Flat(land - start).normalized * 0.6f;
            SpawnRemote(foeId, enemyTeam, foeAt, $"{type}Foe");
            yield return Hold(Still, false, 0.3f, "foe");
            player.RefillInk();
            sent.Clear();
            if (!loadout.UseSub()) { Fail($"couldn't throw the {prefab.DisplayName}"); Despawn(foeId); continue; }
            float inkLeft = player.InkLevel;
            ThrownBomb bomb = loadout.LastThrownBomb;
            float thrownAt = Time.time, touched = -1f;
            bool stuckUp = false;
            int bouncesAtTouch = 0;
            bool foeFollowed = false;
            Vector3 touchAt = start;
            for (float t = 0f; t < 6f && bomb != null; t += Time.deltaTime)
            {
                if (touched < 0f && (bomb.Settled || bomb.Bounces > 0 || bomb.Exploded)) { touched = Time.time; touchAt = bomb.Exploded ? bomb.ExplodedAt : bomb.transform.position; stuckUp = bomb.Stuck && Vector3.Angle(bomb.SurfaceNormal, Vector3.up) < 5f; bouncesAtTouch = bomb.Bounces; }
                if (bomb.Exploded) break;
                // The ink bomb tumbles somewhere random: the enemy's moved beside it just before it goes off.
                if (type == SubType.InkBomb && bomb.Armed && bomb.FuseLeft < 0.25f && !foeFollowed)
                {
                    foeFollowed = true;
                    Despawn(foeId);
                    foeId += 50;
                    SpawnRemote(foeId, enemyTeam, new Vector3(bomb.transform.position.x, station.position.y + 0.05f, bomb.transform.position.z) + Flat(bomb.transform.position - start).normalized * 0.6f, $"{type}Foe");
                }
                yield return null;
            }
            bool exploded = bomb == null || bomb.Exploded;
            float wentOff = Time.time;
            Vector3 blastAt = bomb != null ? bomb.ExplodedAt : land;
            yield return Hold(Still, false, 0.4f, "after");
            float damage = DamageTo(foeId);
            int msgs = sent.Count(m => m.id == NetMsg.SubSpawn && m.data is SubData d && d.subType == (int)type);
            bool toldBlast = sent.Any(m => m.id == NetMsg.SubSpawn && m.data is SubData d && d.subType == (int)type && d.fuse < 0f);
            bool inked = OnFloor(blastAt) == player.Team;
            float fuseRan = touched >= 0f ? wentOff - touched : -1f;
            float offAim = Flat(touchAt - land).magnitude;
            int blobsFired = bomb != null ? bomb.BlobsFired : 0;
            Note($"{prefab.DisplayName}: cost {(1f - inkLeft) * 100f:0}% ink; touched down {(touched >= 0f ? touched - thrownAt : -1f):F2}s after the throw, {Flat(touchAt - start).magnitude:F1}m out, {offAim:F2}m from the reticle's point" +
                 $"{(type == SubType.SuctionBomb ? $", stuck {(stuckUp ? "upright" : "NOT upright")}" : "")}{(type == SubType.InkBomb ? $", {bomb?.Bounces ?? bouncesAtTouch} knock(s), {Flat(blastAt - touchAt).magnitude:F1}m tumbled" : "")}; " +
                 $"went off {(exploded ? $"{fuseRan:F2}s after touching (fuse {prefab.Fuse:F1}s)" : "NEVER")}; the enemy beside it took {damage:0}; {blobsFired} blob(s); blast spot {(inked ? "inked" : "NOT inked")}; {msgs} update(s) sent{(toldBlast ? ", the blast among them" : "")}");
            if (Mathf.Abs((1f - inkLeft) - prefab.InkCost) > 0.01f) Fail($"the {prefab.DisplayName} didn't cost {prefab.InkCost * 100f:0}% ink");
            if (!exploded) Fail($"the {prefab.DisplayName} never went off");
            if (damage <= 0f) Fail($"the {prefab.DisplayName} didn't hurt an enemy beside it");
            if (!inked) Fail($"the {prefab.DisplayName} didn't ink where it went off");
            if (!toldBlast || msgs < 2) Fail($"the {prefab.DisplayName}'s throw and blast weren't both broadcast");
            if (offAim > (type == SubType.InkBomb ? 1f : 0.75f)) Fail($"the {prefab.DisplayName} didn't come down on the reticle's point"); // (the ink bomb's hull knocks the ground a little early)
            if (type == SubType.SuctionBomb && (!stuckUp || Mathf.Abs(fuseRan - prefab.Fuse) > 0.15f)) Fail("the suction bomb didn't stick (upright, on the floor) and go off its fuse later");
            if (type == SubType.SuctionBomb && (blobsFired != prefab.Blobs || prefab.Blobs <= 0)) Fail("the suction bomb didn't throw out its blobs");
            if (type == SubType.SuctionBomb && (Mathf.Abs(prefab.Blast.coreDamage - 160f) > 0.01f || Mathf.Abs(prefab.Blast.edgeDamage - 60f) > 0.01f)) Fail("the suction bomb's damage isn't 160 falling to 60");
            if (type == SubType.ImpactBomb && fuseRan > 0.05f) Fail("the impact bomb didn't go off the moment it touched");
            if (type == SubType.ImpactBomb && Mathf.Abs(damage - 30f) > 0.01f) Fail("the impact bomb doesn't do a flat 30");
            if (type == SubType.InkBomb && ((bomb?.Bounces ?? bouncesAtTouch) < 1 || Mathf.Abs(fuseRan - prefab.Fuse) > 0.15f)) Fail("the ink bomb didn't knock about and go off its fuse after first touching down");
            Despawn(foeId);
            foeId++;
            yield return Hold(Still, false, 0.2f, "despawn");
        }

        // A suction bomb only sticks to the level: thrown onto a prop (a box on the Default layer, not
        // the map's), it glances off and sticks to the floor instead.
        loadout.SetSub(SubType.SuctionBomb);
        Vector3 propAt = loadout.AimPoint() ?? start;
        GameObject prop = GameObject.CreatePrimitive(PrimitiveType.Cube);
        prop.name = "BombTestProp";
        prop.transform.position = propAt + Vector3.up * 0.4f;
        prop.transform.localScale = new Vector3(1.2f, 0.8f, 1.2f);
        yield return Hold(Still, false, 0.2f, "prop");
        player.RefillInk();
        loadout.UseSub();
        ThrownBomb glancer = loadout.LastThrownBomb;
        yield return HoldUntil(Still, false, 3f, "glance", f => glancer == null || glancer.Stuck || glancer.Exploded);
        bool glanced = glancer != null && glancer.Bounces > 0, onFloor = glancer != null && glancer.Stuck && Vector3.Angle(glancer.SurfaceNormal, Vector3.up) < 5f;
        Note($"suction bomb onto a prop: {(glanced ? "glanced off it" : "DIDN'T glance")}, then {(onFloor ? "stuck to the floor" : "NOT stuck to the floor")}");
        if (!glanced || !onFloor) Fail("the suction bomb stuck to a prop, not the level");
        if (glancer != null) glancer.Remove();
        Destroy(prop);

        // Theirs: a suction bomb flies from their throw, sticks where they say, and goes off on its own
        // timer; only the owner paints and damages.
        const ulong Them = 3950;
        SpawnRemote(Them, enemyTeam, station.TransformPoint(new Vector3(-8f, 0.05f, 8f)), "BombThrower");
        yield return Hold(Still, false, 0.1f, "thrower");
        sent.Clear();
        Vector3 from = station.TransformPoint(new Vector3(-6f, 2f, 6f)), at = station.TransformPoint(new Vector3(-3f, 0f, 6f));
        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = Them, subId = 3, team = enemyTeam, subType = (int)SubType.SuctionBomb, position = from, velocity = new Vector3(4f, 3f, 0f), normal = Vector3.up }, Them);
        yield return Hold(Still, false, 0.1f, "remote");
        ThrownBomb theirs = SubDevice.Find(Them, 3) as ThrownBomb;
        bool flying = theirs != null && !theirs.Settled && !theirs.IsOwnedLocally;
        gm.Receive(NetMsg.SubSpawn, new SubData { ownerId = Them, subId = 3, team = enemyTeam, subType = (int)SubType.SuctionBomb, position = at, normal = Vector3.up, landed = true, fuse = 0.5f }, Them);
        yield return Hold(Still, false, 0.1f, "remote");
        bool snapped = theirs != null && theirs.Stuck && Vector3.Distance(theirs.transform.position, at) < 0.01f;
        yield return Hold(Still, false, 0.7f, "remote");
        bool gone = theirs == null || theirs.Exploded;
        Note($"theirs: {(flying ? "flying" : "NOT flying")} from the throw, {(snapped ? "stuck where they said" : "NOT snapped")}, {(gone ? "went off" : "DIDN'T go off")} on its timer; we sent {sent.Count(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)} damage/splat message(s)");
        if (!flying || !snapped || !gone) Fail("their suction bomb didn't fly, snap to their stick and go off");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("their bomb painted or damaged from here");

        Despawn(Them);
        loadout.SetSub(SubType.Beacon);
        SubDevice.RemoveAll();
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        player.ScriptedInput.recenter = true;
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;
        NetGameManager.SendOverride = null;
        yield return Hold(Still, false, 0.2f, "cleanup");
    }
}
