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

// ClimbTestRunner scenarios: the map screen and Super Jumps.
public partial class ClimbTestRunner
{

    IEnumerator MapTeam()
    {
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (map == null) { Fail("no MapScreen in the scene"); yield break; }
        NetGameManager gm = NetGameManager.Instance;
        const ulong MateId = 3003, FoeId = 3004;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        NetGameManager.SendOverride = (id, data, target) => { };
        Vector3 matePos = station.TransformPoint(new Vector3(8f, 0.05f, 6f));
        Vector3 foePos = station.TransformPoint(new Vector3(-8f, 0.05f, 6f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = MateId, team = player.Team, position = matePos, playerName = "Mate" }, MateId);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = matePos, moveDir = Vector2.zero, team = player.Team }, MateId);
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = FoeId, team = enemyTeam, position = foePos, playerName = "Foe" }, FoeId);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = FoeId, position = foePos, moveDir = Vector2.zero, team = enemyTeam }, FoeId);
        ISuperJumpTarget picked = null;
        Action<ISuperJumpTarget> onPick = t => picked = t;
        MapScreen.SuperJumpRequested += onPick;

        map.ScriptedHold = true;
        yield return Hold(Still, false, 0.3f, "map");
        if (!map.IsOpen || !InputGate.Blocked) Fail("holding didn't open the map (or didn't block gameplay input)");
        if (map.MapTexture == null || !map.MapTexture.IsCreated()) Fail("map wasn't rendered");

        // Our team only, in a stable order, empty slots padded out.
        MapScreen.Entry[] entries = map.Entries;
        int self = Array.FindIndex(entries, e => e.playerName.text == player.DisplayName);
        int mate = Array.FindIndex(entries, e => e.playerName.text == "Mate");
        Note($"entries: {string.Join(", ", entries.Select(e => $"{e.playerName.text}/{e.status.text}"))}");
        if (self < 0 || mate < 0) { Fail("our team isn't listed"); map.ScriptedHold = false; MapScreen.SuperJumpRequested -= onPick; yield break; }
        if (entries.Any(e => e.playerName.text == "Foe")) Fail("an enemy is listed");
        if (entries.Count(e => !e.marker.gameObject.activeSelf && !e.line.gameObject.activeSelf) != 2) Fail("empty slots should have no marker or line");

        // Angled view of the map layers only; markers sit where the players appear on it.
        Camera cam = map.MapCamera;
        float tilt = Vector3.Angle(cam.transform.forward, Vector3.down);
        Note($"map camera tilt {tilt:F0}° from straight down, yaw {cam.transform.eulerAngles.y:F0}°");
        if (tilt < 5f || tilt > 45f) Fail("map view isn't a slight angle");
        if (cam.cullingMask != LayerMask.GetMask("Map", "InkSurface")) Fail("map draws more than the map layers");
        PlayerController mateCtrl = gm.Players.First(pc => pc != null && pc.OwnerId == MateId);
        Vector2 markerPos = ((RectTransform)entries[mate].marker.transform).anchoredPosition;
        Vector2 expected = map.MapPosition(mateCtrl.transform.position);
        if (Vector2.Distance(markerPos, expected) > 1f) Fail($"teammate's marker at {markerPos}, expected {expected}");

        // Each line runs from its entry to its marker.
        foreach (int i in new[] { self, mate })
        {
            MapScreen.Entry e = entries[i];
            Vector3 mid = (e.lineStart.position + e.marker.transform.position) * 0.5f;
            float length = Vector3.Distance(e.lineStart.position, e.marker.transform.position) / e.line.lossyScale.x;
            if (Vector3.Distance(e.line.position, mid) > 2f || Mathf.Abs(e.line.rect.width - length) > 2f)
                Fail($"line {i} doesn't connect its entry to its marker");
        }

        // We can't jump to ourselves; picking the teammate's marker requests it and closes the map.
        if (entries[self].button.interactable || entries[self].marker.interactable) Fail("own entry is clickable");
        if (!entries[mate].button.interactable) Fail("teammate's entry isn't clickable");
        entries[self].button.onClick.Invoke();
        if (picked != null || !map.IsOpen) Fail("picking ourselves did something");
        entries[mate].marker.onClick.Invoke();
        yield return Hold(Still, false, 0.2f, "map");
        if (!(picked is PlayerController pickedPlayer) || pickedPlayer.DisplayName != "Mate") Fail("picking the teammate didn't request a Super Jump to them");
        if (!player.IsSuperJumping) Fail("picking the teammate didn't start a Super Jump");
        if (map.IsOpen || InputGate.Blocked) Fail("map didn't close after picking (or reopened while still held)");
        player.CancelSuperJump(); // the jump itself is covered by the SuperJump scenario

        // Release and hold again: a splatted teammate can't be picked.
        map.ScriptedHold = false;
        yield return Hold(Still, false, 0.1f, "map");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = matePos, moveDir = Vector2.zero, team = player.Team, dead = true }, MateId);
        map.ScriptedHold = true;
        yield return Hold(Still, false, 0.2f, "map");
        if (!map.IsOpen) Fail("map didn't reopen after releasing");
        if (entries[mate].button.interactable || entries[mate].status.text != "Splatted") Fail("splatted teammate is still pickable");
        map.ScriptedHold = false;
        yield return Hold(Still, false, 0.1f, "map");
        if (map.IsOpen || InputGate.Blocked) Fail("releasing didn't close the map");

        MapScreen.SuperJumpRequested -= onPick;
        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = MateId }, MateId);
        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = FoeId }, FoeId);
        yield return Hold(Still, false, 0.1f, "map");
        NetGameManager.SendOverride = null;
    }

    IEnumerator SuperJump()
    {
        NetGameManager gm = NetGameManager.Instance;
        const ulong MateId = 3005;
        NetGameManager.SendOverride = (id, data, target) => { };
        Vector3 matePos = station.TransformPoint(new Vector3(9f, 0.05f, 7f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = MateId, team = player.Team, position = matePos, playerName = "Mate" }, MateId);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = matePos, moveDir = Vector2.zero, team = player.Team }, MateId);
        yield return Hold(Still, false, 0.3f, "settle");
        PlayerController mate = gm.Players.FirstOrDefault(pc => pc != null && pc.OwnerId == MateId);
        if (mate == null) { Fail("teammate didn't spawn"); NetGameManager.SendOverride = null; yield break; }
        float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        // First landing without swim held, then (after the teammate moves) with it held.
        for (int run = 0; run < 2; run++)
        {
            bool holdSwim = run == 1;
            if (!player.StartSuperJump(mate)) { Fail("couldn't start a Super Jump to a teammate"); break; }
            if (player.StartSuperJump(mate)) Fail("a second jump started during the first");

            // Charge: forced into swim form and held in place, even pushing forward with swim released.
            string charge = "charge" + run, fly = "fly" + run;
            yield return HoldUntil(Fwd, false, 3f, charge, f => player.SuperJumpState != SuperJumpPhase.Charging);
            List<Frame> charging = frames.Where(f => f.phase == charge).ToList();
            float drift = charging.Max(f => Flat(f.pos - charging[0].pos));
            Note($"run {run}: charged {charging.Count * Dt:F2}s, drifted {drift:F3}m");
            if (Mathf.Abs(charging.Count * Dt - player.SuperJumpChargeTime) > 0.1f) Fail($"charge took {charging.Count * Dt:F2}s, not {player.SuperJumpChargeTime:F2}s");
            if (charging.Skip(1).Any(f => !f.squid)) Fail("not forced into swim form while charging");
            if (drift > 0.05f) Fail("moved while charging");
            if (player.SuperJumpState != SuperJumpPhase.Flying) { Fail("didn't launch after charging"); break; }

            // Flight: a high arc, staying a squid with its nose along the path.
            yield return HoldUntil(Still, holdSwim, 5f, fly, f => !player.IsSuperJumping);
            List<Frame> flying = frames.Where(f => f.phase == fly).ToList();
            float peak = flying.Max(f => f.pos.y) - charging.Last().pos.y;
            float alignSum = 0f; int alignCount = 0;
            for (int i = flying.Count / 8; i < flying.Count - flying.Count / 8 - 1; i++)
            {
                Vector3 travel = station.TransformDirection(flying[i + 1].pos - flying[i - 1].pos).normalized;
                alignSum += Vector3.Dot(travel, flying[i].squidRot * Vector3.forward);
                alignCount++;
            }
            float align = alignCount > 0 ? alignSum / alignCount : 0f;
            Note($"run {run}: flew {flying.Count * Dt:F2}s, peak {peak:F1}m up, squid/path alignment {align:F3}");
            if (peak < 10f) Fail($"arc only peaked {peak:F1}m up");
            if (flying.Any(f => !f.squid)) Fail("left swim form mid-flight");
            if (align < 0.9f) Fail("squid doesn't point along its flight path"); // eased, so it lags where a tall arc turns sharply

            // Landed on the teammate and unlocked; swim form kept only if held.
            yield return Hold(Still, holdSwim, 0.4f, "land" + run);
            float miss = Flat(player.transform.position - mate.transform.position);
            Note($"run {run}: landed {miss:F2}m from the teammate");
            if (miss > 1.5f) Fail($"landed {miss:F2}m from the teammate");
            if (player.IsSuperJumping) Fail("still Super Jumping after landing");
            if (player.IsSquid != holdSwim) Fail(holdSwim ? "dropped out of swim form while it was held" : "stayed in swim form without swim held");
            Vector3 before = player.transform.position;
            yield return Hold(Fwd, holdSwim, 0.6f, "after" + run);
            if (Flat(player.transform.position - before) < 0.3f) Fail("can't move after landing");

            // Move the teammate for the second run.
            Vector3 next = station.TransformPoint(new Vector3(-9f, 0.05f, 7f));
            gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = next, moveDir = Vector2.zero, team = player.Team }, MateId);
            yield return Hold(Still, false, 0.6f, "settle");
        }

        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = MateId }, MateId);
        yield return Hold(Still, false, 0.1f, "settle");
        NetGameManager.SendOverride = null;
    }

    IEnumerator SpawnBarrier()
    {
        int enemyTeam = player.Team == 1 ? 2 : 1;
        float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
        Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
        Vector3 feet = player.transform.position;
        const float R = 3f, Pad = 1.5f;

        // The other team's pad 10m ahead: a sphere curving out of the pad's rim. Hidden from afar, it
        // keeps us out walking and swimming, showing as we press.
        var padGo = new GameObject("TestSpawnPad");
        padGo.transform.position = new Vector3(feet.x, station.position.y, feet.z) + forward * 10f;
        SpawnPad pad = padGo.AddComponent<SpawnPad>();
        pad.Configure(enemyTeam, R, Pad);
        yield return Hold(Still, false, 0.4f, "far");
        float farVisible = pad.Visibility;
        yield return Hold(Fwd, false, 3f, "walk in");
        bool walkedIn = pad.Contains(player.BodyCenter);
        float walkGap = Flat(player.transform.position - pad.Top), pressedVisible = pad.Visibility;
        yield return Hold(Fwd, true, 1.5f, "swim in");
        bool swamIn = pad.Contains(player.BodyCenter);
        float swimGap = Flat(player.transform.position - pad.Top);
        Note($"enemy barrier (sphere r {R} raised {pad.Lift:F2}m, meeting a {Pad}m pad): visibility {farVisible:F2} from 7m off, {pressedVisible:F2} against it; walking stopped {walkGap:F2}m from its centre, swimming {swimGap:F2}m");
        if (farVisible > 0.05f) Fail("the barrier showed with no enemy near");
        if (pressedVisible < 0.6f) Fail("the barrier didn't show with an enemy against it");
        if (walkedIn || swamIn || swimGap < Pad) Fail("walked or swam into the other team's spawn");

        // Our own team's: walk straight in.
        pad.Configure(player.Team, R, Pad);
        float ownGap = float.MaxValue;
        yield return HoldUntil(Fwd, false, 1.5f, "own", f => (ownGap = Mathf.Min(ownGap, Flat(player.transform.position - pad.Top))) < 0.5f);
        Note($"our own barrier: walked to {ownGap:F2}m from its centre");
        if (ownGap > 1f) Fail("our own spawn's barrier kept us out");

        // Inside it: hidden while nothing threatens it; an enemy shot stops on it and it lights up for
        // us (under attack), then fades; nothing of theirs hurts us.
        player.PlaceAt(new Vector3(pad.Top.x, station.position.y + 0.05f, pad.Top.z), player.transform.eulerAngles.y);
        player.Hitbox.ResetHealth();
        frames.Clear(); // the teleport isn't a movement glitch
        yield return Hold(Still, false, 1f, "inside");
        float quietVisible = pad.Visibility;
        Vector3? stoppedAt = null;
        Action<ProjectileManager.Shot, Vector3> onImpact = (shot, point) => { if (shot.team == enemyTeam) stoppedAt = point; };
        ProjectileManager.Impact += onImpact;
        Vector3 aim = player.BodyCenter;
        Vector3 from = aim - forward * 9f;
        var straight = new Ballistics { gravityOverTime = AnimationCurve.Constant(0f, 1f, 0f) };
        ProjectileManager.Fire(projectilePrefab, from, (aim - from).normalized * 30f, 10, enemyTeam, ownerId: 4242, damage: 40f, ballistics: straight);
        yield return HoldUntil(Still, false, 1f, "shot", f => stoppedAt.HasValue);
        ProjectileManager.Impact -= onImpact;
        yield return Hold(Still, false, 0.4f, "attacked");
        float attackedVisible = pad.Visibility;
        yield return Hold(Still, false, 3f, "calm");
        float calmVisible = pad.Visibility;
        float afterShot = player.Hitbox.Health;
        bool hurtInside = player.Hitbox.TakeDamage(500f, enemyTeam, 4242, "Test");
        float afterBlast = player.Hitbox.Health;
        float stopR = stoppedAt.HasValue ? Vector3.Distance(stoppedAt.Value, pad.Centre) : -1f;
        Note($"inside our spawn: shown {quietVisible:F2} while quiet; an enemy shot stopped {stopR:F2}m from the sphere's centre (r {R}), shown {attackedVisible:F2} under attack, {calmVisible:F2} 3s later; health {afterShot:0} after it and {afterBlast:0} after a 500 hit");
        if (!stoppedAt.HasValue || Mathf.Abs(stopR - R) > 0.3f) Fail("an enemy shot wasn't stopped on the barrier");
        if (quietVisible > 0.05f || attackedVisible < 0.6f || calmVisible > 0.05f) Fail("our own barrier didn't show while under attack (and only then)");
        if (afterShot < 99f || afterBlast < 99f || hurtInside) Fail("we were hurt inside our own spawn");

        // An enemy sub sliding at it breaks on the barrier.
        CurlingBomb bombPrefab = player.GetComponent<PlayerLoadout>()?.CurlingBombPrefab;
        if (bombPrefab != null)
        {
            Vector3 start = pad.Top - forward * 7f + Vector3.up * 0.05f;
            CurlingBomb bomb = Instantiate(bombPrefab, start, Quaternion.identity);
            bomb.Init(4242, 977, enemyTeam, ownedLocally: true);
            bomb.Launch(forward * bombPrefab.SlideSpeed, 5f);
            yield return HoldUntil(Still, false, 2f, "sub", f => SubDevice.Find(4242, 977) == null);
            bool broke = SubDevice.Find(4242, 977) == null;
            float reached = bomb != null ? Flat(bomb.transform.position - pad.Top) : -1f;
            Note("an enemy curling bomb slid at it: " + (broke ? "broke on the barrier" : $"STILL THERE {reached:F1}m from the centre"));
            if (!broke) { Fail("an enemy sub didn't break on the barrier"); if (bomb != null) bomb.Remove(); }
        }
        else Fail("no curling bomb prefab to test subs with");

        pad.Configure(enemyTeam, R, Pad); // now we're in theirs: pushed out, and no longer shielded once out
        yield return Hold(Still, false, 0.3f, "evicted");
        frames.Clear(); // nor is being pushed out
        bool stillIn = pad.Contains(player.BodyCenter);
        player.Hitbox.TakeDamage(30f, enemyTeam, 4242, "Test");
        float outsideHealth = player.Hitbox.Health;
        Note($"in the other team's: pushed {Flat(player.transform.position - pad.Top):F2}m out; then hurt to {outsideHealth:0}");
        if (stillIn) Fail("an enemy standing in a spawn wasn't pushed out");
        if (outsideHealth > 75f) Fail("damage outside a spawn was blocked");

        Destroy(padGo);
        player.Hitbox.ResetHealth();
        yield return Hold(Still, false, 0.1f, "done");
    }

    IEnumerator SuperJumpLocking()
    {
        NetGameManager gm = NetGameManager.Instance;
        MapScreen map = FindFirstObjectByType<MapScreen>();
        const ulong MateId = 3006;
        NetGameManager.SendOverride = (id, data, target) => { };
        float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
        Vector3 clickedAt = station.TransformPoint(new Vector3(8f, 0.05f, 6f));
        SpawnRemote(MateId, player.Team, clickedAt, "Mate");
        yield return Hold(Still, false, 0.3f, "settle");
        PlayerController mate = gm.GetPlayer(MateId);
        if (mate == null) { Fail("teammate didn't spawn"); NetGameManager.SendOverride = null; yield break; }

        // The teammate moves away and then leaves mid-charge: we still land where they were.
        player.StartSuperJump(mate);
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = station.TransformPoint(new Vector3(-8f, 0.05f, 6f)), moveDir = Vector2.zero, team = player.Team }, MateId);
        yield return Hold(Still, false, 0.5f, "charge");
        Despawn(MateId);
        yield return HoldUntil(Still, false, 6f, "jump", f => !player.IsSuperJumping);
        yield return Hold(Still, false, 0.3f, "land");
        float miss = Flat(player.transform.position - clickedAt);
        Note($"landed {miss:F2}m from where the teammate was when picked");
        if (miss > 1.5f) Fail($"landed {miss:F2}m from the locked spot (followed the teammate, or cancelled)");

        // Dying mid-charge cancels.
        player.StartSuperJump(new SpawnJumpTarget(player.Team, player.SpawnPosition));
        yield return Hold(Still, false, 0.3f, "charge");
        player.OnDeath();
        yield return Hold(Still, false, 0.1f, "dead");
        if (player.IsSuperJumping) Fail("still Super Jumping after dying");
        player.Respawn();
        yield return Hold(Still, false, 0.3f, "settle");

        // The spawn entry on the map is always there; picking it jumps home.
        player.PlaceAt(player.transform.position + station.TransformDirection(new Vector3(9f, 0f, 7f)), player.transform.eulerAngles.y);
        frames.Clear(); // the teleport isn't a movement glitch
        map.ScriptedHold = true;
        yield return Hold(Still, false, 0.3f, "map");
        if (!map.SpawnEntry.button.interactable || !map.SpawnEntry.marker.gameObject.activeInHierarchy) Fail("spawn entry isn't available");
        map.SpawnEntry.button.onClick.Invoke();
        map.ScriptedHold = false;
        if (!player.IsSuperJumping) Fail("picking the spawn didn't start a Super Jump");
        else
        {
            yield return HoldUntil(Still, false, 6f, "jump", f => !player.IsSuperJumping);
            yield return Hold(Still, false, 0.3f, "land");
            float home = Flat(player.transform.position - player.SpawnPosition);
            Note($"landed {home:F2}m from the spawn");
            if (home > 1.5f) Fail($"landed {home:F2}m from the spawn");
        }
        NetGameManager.SendOverride = null;
    }
}
