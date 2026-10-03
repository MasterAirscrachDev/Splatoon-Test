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

// ClimbTestRunner scenarios: combat, death and respawn.
public partial class ClimbTestRunner
{

    IEnumerator Death()
    {
        NetGameManager gm = NetGameManager.Instance;
        DeathPopup popup = FindFirstObjectByType<DeathPopup>(FindObjectsInactive.Include);
        Camera view = player.CameraRig != null ? player.CameraRig.GetComponentInChildren<Camera>(true) : null;
        if (popup == null || view == null) { Fail("no DeathPopup in the HUD (or no player camera)"); yield break; }
        Transform cam = view.transform;
        const ulong FoeId = 3070;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(8f, 0.05f, 8f)), "Splatterer");
        player.Hitbox.ResetHealth();
        yield return Hold(Still, false, 0.3f, "stand");

        // Splatted by them: the camera circles where it happened, the popup says who and with what.
        Vector3 spot = player.transform.position;
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = FoeId, amount = 250f, fromTeam = enemyTeam, source = "Inkshot" }, FoeId);
        yield return HoldUntil(Still, false, 0.5f, "dead", f => f.dead);
        float diedAt = Time.time;
        yield return null;
        Note($"popup: \"{popup.Message}\"");
        if (!player.IsDead || player.KilledBy != FoeId || player.KilledWith != "Inkshot") Fail("the hit didn't splat us (or lost who and what did it)");
        if (!popup.Shown || !popup.Message.Contains("Splatterer") || !popup.Message.Contains("Inkshot")) Fail("the popup doesn't say who splatted us and with what");
        float Bearing() { Vector3 d = cam.position - player.DeathSpot; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
        float startBearing = Bearing();
        yield return Hold(Still, false, 1f, "dead");
        Vector3 offset = cam.position - player.DeathSpot;
        float turned = Mathf.Abs(Mathf.DeltaAngle(startBearing, Bearing()));
        Note($"death camera {new Vector2(offset.x, offset.z).magnitude:F1}m out and {offset.y:F1}m up, turned {turned:F0}° in 1s");
        if (Vector3.Distance(player.DeathSpot, spot) > 0.5f) Fail("the death camera isn't over where we were splatted");
        if (!player.DeathCamera || new Vector2(offset.x, offset.z).magnitude < 3f || offset.y < 1f || turned < 20f) Fail("the camera isn't circling over the spot");

        // Back at a spawn after 4s, in swim form and held still (even pushing forward) for a second.
        yield return HoldUntil(Fwd, false, 4.5f, "dead", f => !f.dead);
        float respawnAfter = Time.time - diedAt;
        frames.Clear(); // the respawn is a teleport
        Vector3 at = player.transform.position;
        float toSpawn = GameObject.FindGameObjectsWithTag(player.Team == 1 ? "AlphaSpawn" : "BetaSpawn")
            .Select(g => Vector3.Distance(g.transform.position, at)).DefaultIfEmpty(Vector3.Distance(player.SpawnPosition, at)).Min();
        if (Mathf.Abs(respawnAfter - 4f) > 0.15f) Fail($"respawned {respawnAfter:F2}s after dying, not 4s");
        if (toSpawn > 1.5f) Fail($"respawned {toSpawn:F1}m from the nearest spawn");
        yield return Hold(Fwd, false, 0.6f, "held");
        float drift = Vector3.ProjectOnPlane(player.transform.position - at, Vector3.up).magnitude;
        bool heldSquid = player.IsSquid, holding = player.IsRespawning, popupGone = !popup.Shown;
        yield return Hold(Fwd, false, 0.8f, "free");
        float moved = Vector3.ProjectOnPlane(player.transform.position - at, Vector3.up).magnitude;
        Note($"respawned {respawnAfter:F2}s after dying; held {(heldSquid ? "in swim form" : "standing")}, drifted {drift:F2}m in 0.6s; then moved {moved:F1}m");
        if (!holding || !heldSquid || drift > 0.1f) Fail("not held still in swim form just after respawning");
        if (!popupGone) Fail("the popup is still up after respawning");
        if (player.IsRespawning || moved < 0.5f) Fail("didn't get control back after the hold");

        // Falling out of the world: the same, the camera over the last ground we stood on.
        player.PlaceAt(station.TransformPoint(new Vector3(-6f, 0.1f, -6f)), player.transform.eulerAngles.y);
        yield return Hold(Still, false, 0.3f, "ground");
        Vector3 ground = player.transform.position;
        player.transform.position = new Vector3(ground.x, PlayerController.KillHeight - 2f, ground.z);
        Physics.SyncTransforms();
        yield return HoldUntil(Still, false, 0.5f, "fall", f => f.dead);
        frames.Clear(); // the drop is a teleport
        yield return null;
        Note($"fell: \"{popup.Message}\", camera over a spot {Vector3.Distance(player.DeathSpot, ground):F2}m from the last ground");
        if (!player.IsDead || player.KilledBy != 0 || player.KilledWith != PlayerController.FellCause) Fail("falling out of bounds didn't splat us");
        if (!popup.Shown || !popup.Message.Contains(PlayerController.FellCause)) Fail("the popup doesn't say we fell");
        if (Vector3.Distance(player.DeathSpot, ground) > 0.5f) Fail("the death camera isn't over where we fell from");
        player.Respawn();
        yield return Hold(Still, false, 0.2f, "settle");
        frames.Clear();
        if (player.DeathCamera || player.IsDead) Fail("still splatted after respawning");

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "cleanup");
    }

    // Team of the ink directly below a world point (0 if none).
    static int TeamAt(Vector3 point)
    {
        if (Physics.Raycast(point + Vector3.up, Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
            && hit.collider.TryGetComponent(out SurfaceInkManager ink))
            return ink.getSurfaceTeam(ink.UVFromHit(hit));
        return 0;
    }

    // Plays the other client: a fake remote enemy joins, its shots and hits arrive as messages,
    // and our outgoing messages are captured instead of going to Steam.
    IEnumerator Combat()
    {
        NetGameManager gm = NetGameManager.Instance;
        const ulong RemoteId = 1001;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        var sent = new List<(NetMsg id, object data, ulong target)>();
        NetGameManager.SendOverride = (id, data, target) => sent.Add((id, data, target));

        Vector3 remotePos = station.TransformPoint(new Vector3(6f, 0.05f, -6f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = RemoteId, team = enemyTeam, position = remotePos, playerName = "TestRemote" }, RemoteId);
        yield return Hold(Still, false, 0.1f, "join");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = RemoteId, position = remotePos, moveDir = Vector2.zero, team = enemyTeam }, RemoteId);
        yield return Hold(Still, false, 0.2f, "join");

        PlayerController remote = gm.GetPlayer(RemoteId);
        if (remote == null || remote.Hitbox == null || player.Hitbox == null)
        {
            Fail("fake remote player (or a hitbox) didn't spawn");
            NetGameManager.SendOverride = null;
            yield break;
        }
        float fullHealth = player.Hitbox.Health;

        // 1. Replayed enemy shots (one onto us, one onto the floor) are visual only.
        Vector3 floorSpot = station.TransformPoint(new Vector3(-8f, 0f, -6f));
        int floorTeam = TeamAt(floorSpot);
        int activeBefore = ProjectileManager.LiveCount;
        gm.Receive(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = RemoteId, team = enemyTeam, origin = player.BodyCenter + Vector3.up * 3f,
            velocities = new float[] { 0f, -8f, 0f }, splashSizes = new[] { 10 }, visible = new[] { true }
        }, RemoteId);
        gm.Receive(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = RemoteId, team = enemyTeam, origin = floorSpot + Vector3.up * 3f,
            velocities = new float[] { 0f, -8f, 0f }, splashSizes = new[] { 10 }, visible = new[] { false }
        }, RemoteId);
        yield return Hold(Still, false, 1f / 30f, "replay");
        int replays = ProjectileManager.LiveCount - activeBefore;
        yield return Hold(Still, false, 0.8f, "replay");

        if (replays != 2) Fail($"expected 2 replayed projectiles, saw {replays}");
        if (player.Hitbox.Health < fullHealth) Fail($"a replayed shot damaged us ({player.Hitbox.Health}/{fullHealth})");
        if (TeamAt(floorSpot) != floorTeam) Fail("a replayed shot painted the floor (only the shooter's Splat message should)");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("replayed shots sent Damage/Splat messages");

        // 2. Our shots hitting the remote copy are forwarded to that player, not applied locally.
        sent.Clear();
        for (int i = 0; i < 3; i++)
        {
            ProjectileManager.Fire(projectilePrefab, remote.BodyCenter + Vector3.up * 3f, Vector3.down * 8f, 10, player.Team, true, authoritative: true, ownerId: player.OwnerId);
            yield return Hold(Still, false, 0.25f, "shoot");
        }
        yield return Hold(Still, false, 0.5f, "shoot");

        var hits = sent.Where(m => m.id == NetMsg.Damage).Select(m => (data: (DamageData)m.data, m.target)).ToList();
        Note($"{hits.Count} Damage messages for 3 hits");
        if (hits.Count != 3) Fail($"expected 3 Damage messages for 3 hits, got {hits.Count}");
        foreach (var (d, target) in hits)
        {
            if (target != RemoteId || d.targetSteamId != RemoteId) { Fail($"Damage sent to {target}/{d.targetSteamId}, not the victim {RemoteId}"); break; }
            if (d.attackerSteamId != player.OwnerId || d.fromTeam != player.Team || d.amount <= 0f) { Fail($"bad Damage message (attacker {d.attackerSteamId}, team {d.fromTeam}, amount {d.amount})"); break; }
        }
        if (remote.Hitbox.Health < fullHealth) Fail("the shooter changed the remote copy's health locally");

        // 3. Incoming Damage: friendly or misaddressed hits are ignored; enemy hits kill us.
        float amount = hits.Count > 0 ? hits[0].data.amount : 30f;
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = RemoteId, amount = amount, fromTeam = player.Team }, RemoteId);
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = 4242, attackerSteamId = RemoteId, amount = amount, fromTeam = enemyTeam }, RemoteId);
        yield return Hold(Still, false, 0.1f, "hit");
        if (player.Hitbox.Health < fullHealth) Fail("friendly or misaddressed Damage was applied");

        // Healing: none for 0.5s after a hit, then full in 6s (16.7/s), or 1.5s (66.7/s) submerged.
        for (int i = 0; i < 2; i++)
            gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = RemoteId, amount = amount, fromTeam = enemyTeam }, RemoteId);
        yield return Hold(Still, false, 0.05f, "heal");
        float hurt = player.Hitbox.Health;
        yield return Hold(Still, false, 0.4f, "heal");
        if (player.Hitbox.Health > hurt + 0.01f) Fail($"healed during the cooldown ({hurt:F1} -> {player.Hitbox.Health:F1})");

        yield return Hold(Still, false, 0.2f, "heal");
        float h0 = player.Hitbox.Health, t0 = Time.time;
        yield return Hold(Still, false, 0.5f, "heal");
        float standRate = (player.Hitbox.Health - h0) / (Time.time - t0);

        yield return Hold(Still, true, 0.3f, "heal"); // into squid form on own ink
        bool submerged = player.IsInInk;
        h0 = player.Hitbox.Health; t0 = Time.time;
        yield return Hold(Still, true, 0.25f, "heal");
        float submergedRate = (player.Hitbox.Health - h0) / (Time.time - t0);
        Note($"heal {standRate:F1}/s standing, {submergedRate:F1}/s submerged");
        if (Mathf.Abs(standRate - 100f / 6f) > 1.5f) Fail($"standing heal rate {standRate:F1}/s, expected ~16.7");
        if (!submerged) Fail("not submerged in own ink for the submerged heal check");
        else if (Mathf.Abs(submergedRate - 100f / 1.5f) > 4f) Fail($"submerged heal rate {submergedRate:F1}/s, expected ~66.7");

        yield return Hold(Still, true, 1.5f, "heal");
        if (Mathf.Abs(player.Hitbox.Health - fullHealth) > 0.01f) Fail($"didn't heal to exactly full ({player.Hitbox.Health:F2}/{fullHealth})");
        yield return Hold(Still, false, 0.3f, "heal"); // back to standing

        int hitsToKill = Mathf.CeilToInt(fullHealth / amount);
        for (int i = 0; i < hitsToKill; i++)
        {
            gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = RemoteId, amount = amount, fromTeam = enemyTeam }, RemoteId);
            yield return Hold(Still, false, 0.05f, "hit");
            float expected = fullHealth - amount * (i + 1);
            if (i < hitsToKill - 1 && Mathf.Abs(player.Hitbox.Health - expected) > 0.01f)
            {
                Fail($"health {player.Hitbox.Health} after {i + 1} hits, expected {expected}");
                break;
            }
        }
        if (!player.IsDead) Fail($"not dead after {hitsToKill} hits of {amount}");
        else Note($"died after {hitsToKill} hits");

        // Death respawns after 5s, at full health.
        yield return HoldUntil(Still, false, 7f, "dead", f => !f.dead);
        if (player.IsDead) Fail("didn't respawn within 7s");
        else if (player.Hitbox.Health < fullHealth) Fail($"respawned with {player.Hitbox.Health}/{fullHealth} health");

        // 4. The remote leaving removes its whole entity.
        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = RemoteId }, RemoteId);
        yield return Hold(Still, false, 0.1f, "leave");
        if (gm.GetPlayer(RemoteId) != null || GameObject.Find("PlayerEntity - TestRemote") != null)
            Fail("the remote's entity (or its root) is still in the scene after it left");

        NetGameManager.SendOverride = null;
    }
}
