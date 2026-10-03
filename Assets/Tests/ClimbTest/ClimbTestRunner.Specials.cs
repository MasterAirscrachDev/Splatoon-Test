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

// ClimbTestRunner scenarios: specials and the loadout gauges.
public partial class ClimbTestRunner
{

    IEnumerator SpecialDeathAndSync()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        if (loadout == null || hud == null) { Fail("no PlayerLoadout or MatchHUD"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        const ulong FoeId = 3014;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        MatchHUD.PlayerSlot[] own = player.Team == 1 ? hud.AlphaSlots : hud.BetaSlots;
        MatchHUD.PlayerSlot[] enemy = player.Team == 1 ? hud.BetaSlots : hud.AlphaSlots;

        // Ours: in the network state and on our slot when charged.
        loadout.SetSpecial(SpecialType.BubbleShield);
        loadout.SetSpecialPoints(1000f);
        yield return Hold(Still, false, 0.1f, "sync");
        if (!player.GetNetState(0, 0).specialReady) Fail("charged special isn't in the network state");
        if (!own[0].specialMark.gameObject.activeSelf) Fail("our slot doesn't show the charged special");

        // Splatted: half of it is lost.
        player.OnDeath();
        yield return Hold(Still, false, 0.1f, "dead");
        Note($"special {loadout.SpecialPoints:F0}p after dying at 1000p");
        if (Mathf.Abs(loadout.SpecialPoints - 500f) > 0.01f) Fail("dying didn't halve the special");
        if (own[0].specialMark.gameObject.activeSelf) Fail("slot still shows a special after losing it");
        player.Respawn();
        yield return Hold(Still, false, 0.2f, "settle");

        // Theirs: from their state updates.
        Vector3 foePos = station.TransformPoint(new Vector3(-9f, 0.05f, 8f));
        SpawnRemote(FoeId, enemyTeam, foePos, "Foe");
        yield return Hold(Still, false, 0.1f, "sync");
        if (enemy[0].specialMark.gameObject.activeSelf) Fail("enemy slot shows a special before they have one");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = FoeId, position = foePos, moveDir = Vector2.zero, team = enemyTeam, specialReady = true }, FoeId);
        yield return Hold(Still, false, 0.1f, "sync");
        if (!enemy[0].specialMark.gameObject.activeSelf) Fail("enemy's charged special isn't shown");

        loadout.SetSpecialPoints(0f);
        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "sync");
        NetGameManager.SendOverride = null;
    }

    IEnumerator SpecialShield()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        CaptureSends();
        const ulong FoeId = 3013;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        loadout.SetSpecial(SpecialType.BubbleShield);
        loadout.SetSpecialPoints(0f);

        // Turf: inking clean floor charges it; inking the same spots again barely does.
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.3f, "turf");
        Vector3[] spots = Enumerable.Range(0, 6).Select(i => station.TransformPoint(new Vector3(-9f + i * 3.5f, 3f, -8f))).ToArray();
        foreach (Vector3 spot in spots) Shoot(spot, player.Team, player.OwnerId);
        yield return Hold(Still, false, 1.2f, "turf");
        float fresh = loadout.SpecialPoints;
        foreach (Vector3 spot in spots) Shoot(spot, player.Team, player.OwnerId);
        yield return Hold(Still, false, 1.2f, "turf");
        float repaint = loadout.SpecialPoints - fresh;
        Note($"special +{fresh:F0}p for new turf, +{repaint:F0}p repainting it");
        if (fresh < 1f) Fail("inking neutral turf didn't charge the special");
        if (repaint > fresh * 0.35f) Fail("repainting our own turf charged nearly as much as new turf");

        // Damage dealt: our hits on an enemy count, other people's don't.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.1f, "damage");
        PlayerController foe = gm.GetPlayer(FoeId);
        if (foe == null) { Fail("enemy didn't spawn"); NetGameManager.SendOverride = null; yield break; }
        float before = loadout.SpecialPoints;
        foe.Hitbox.TakeDamage(30f, player.Team, player.OwnerId);
        float ours = loadout.SpecialPoints - before, afterOurs = loadout.SpecialPoints;
        foe.Hitbox.TakeDamage(30f, player.Team, 99999);
        float theirs = loadout.SpecialPoints - afterOurs;
        Note($"special +{ours:F2}p for our 30 damage, +{theirs:F2}p for someone else's");
        if (Mathf.Abs(ours - 30f) > 0.01f || Mathf.Abs(theirs) > 0.001f) Fail("damage dealt didn't charge 1p per damage (ours only)");

        // Full: ready. Using it refills ink, raises the shield (replicated) and spends the charge.
        loadout.SetSpecialPoints(1000f);
        if (!loadout.SpecialReady) Fail("not ready at 1000p");
        yield return Hold(Still, false, 0.1f, "special");
        player.ConsumeInk(0.5f);
        if (!loadout.UseSpecial()) { Fail("couldn't use a ready special"); Despawn(FoeId); NetGameManager.SendOverride = null; yield break; }
        float usedAt = Time.time;
        yield return Hold(Still, false, 0.2f, "shield");
        if (player.InkLevel < 0.99f) Fail($"ink is {player.InkLevel:F2} after the special, not full");
        if (!loadout.ShieldActive || !player.GetNetState(0, 0).shielded) Fail("shield isn't up (or isn't in the network state)");
        if (!loadout.ShieldVisible) Fail("bubble isn't showing");
        if (loadout.SpecialPoints != 0f || loadout.UseSpecial()) Fail("charge wasn't spent");

        // Shielded: hits do nothing, and nothing charges meanwhile.
        float hp = player.Hitbox.Health;
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = FoeId, amount = 50f, fromTeam = enemyTeam }, FoeId);
        yield return Hold(Still, false, 0.1f, "shield");
        if (player.Hitbox.Health < hp) Fail("took damage through the shield");
        loadout.AddSpecialPoints(100f);
        if (loadout.SpecialPoints > 0f) Fail("charged while the special was running");

        // It lasts 5s, then damage applies again.
        yield return HoldUntil(Still, false, 6f, "shield", f => !loadout.ShieldActive);
        float lasted = Time.time - usedAt;
        Note($"shield lasted {lasted:F2}s");
        if (Mathf.Abs(lasted - 5f) > 0.1f) Fail($"shield lasted {lasted:F2}s, not 5s");
        hp = player.Hitbox.Health;
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = FoeId, amount = 20f, fromTeam = enemyTeam }, FoeId);
        yield return Hold(Still, false, 0.1f, "after");
        if (player.Hitbox.Health >= hp) Fail("no damage after the shield ended");

        // A shielded enemy: the shooter's side sends nothing and gains nothing.
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = FoeId, position = foe.transform.position, moveDir = Vector2.zero, team = enemyTeam, shielded = true }, FoeId);
        yield return Hold(Still, false, 0.1f, "after");
        sent.Clear();
        before = loadout.SpecialPoints;
        foe.Hitbox.TakeDamage(30f, player.Team, player.OwnerId);
        if (sent.Any(m => m.id == NetMsg.Damage) || loadout.SpecialPoints != before) Fail("hit a shielded enemy");

        player.Hitbox.ResetHealth();
        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "after");
        NetGameManager.SendOverride = null;
    }

    IEnumerator LoadoutGauges()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        LoadoutHUD gauges = FindFirstObjectByType<LoadoutHUD>();
        if (loadout == null || gauges == null) { Fail("no PlayerLoadout or LoadoutHUD"); yield break; }

        player.RefillInk();
        loadout.SetSpecialPoints(500f);
        yield return Hold(Still, false, 1f, "gauges");
        Note($"sub gauge {gauges.SubFill:F2}, special gauge {gauges.SpecialFill:F2}");
        if (Mathf.Abs(gauges.SubFill - player.InkLevel) > 0.03f) Fail("sub gauge doesn't follow the ink");
        if (Mathf.Abs(gauges.SpecialFill - 0.5f) > 0.03f) Fail("special gauge doesn't follow the charge");
        if (gauges.ReadyShown) Fail("READY shown at half charge");
        loadout.SetSpecialPoints(1000f);
        yield return Hold(Still, false, 0.2f, "gauges");
        if (!gauges.ReadyShown) Fail("READY not shown when full");
        loadout.SetSpecialPoints(0f);
    }

    IEnumerator InkStrikeSpecial()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (loadout == null || map == null || loadout.InkStrikePrefab == null) { Fail("missing PlayerLoadout, MapScreen or the InkStrike prefab"); yield break; }
        CaptureSends();
        const ulong FoeIn = 3050, FoeOut = 3051, MateIn = 3052, FoeEdge = 3053, FoeHigh = 3054;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        Vector3 target = station.TransformPoint(new Vector3(6f, 0f, 6f));
        float radius = loadout.InkStrikePrefab.Radius;
        SpawnRemote(FoeIn, enemyTeam, target + new Vector3(1f, 0.05f, 1f), "FoeIn");
        SpawnRemote(FoeOut, enemyTeam, target + new Vector3(radius + 3f, 0.05f, 0f), "FoeOut");
        SpawnRemote(MateIn, player.Team, target + new Vector3(-1f, 0.05f, 0f), "MateIn");
        SpawnRemote(FoeEdge, enemyTeam, target + new Vector3(0f, 0.05f, -radius * 0.92f), "FoeEdge");
        SpawnRemote(FoeHigh, enemyTeam, target + new Vector3(0.5f, loadout.InkStrikePrefab.Height * 0.6f, 0f), "FoeHigh"); // up the column
        loadout.SetSpecial(SpecialType.InkStrike);
        loadout.SetSpecialPoints(1000f);
        yield return Hold(Still, false, 0.3f, "ready");
        player.ConsumeInk(0.5f);

        // Using it opens the map to aim; cancelling keeps the charge.
        if (!loadout.UseSpecial() || !map.IsOpen || !map.IsTargeting) Fail("using it didn't open the map to aim");
        map.CancelTargeting();
        yield return null;
        if (map.IsOpen || loadout.Targeting || loadout.SpecialPoints < 1000f) Fail("cancelling didn't close the map (or spent the charge)");

        // Pick the spot on the map: it's marked, then strikes after the delay.
        loadout.UseSpecial();
        yield return null;
        if (!map.PickTargetAt(map.MapPosition(target, false))) Fail("couldn't pick the spot on the map");
        yield return null;
        InkStrike strike = FindFirstObjectByType<InkStrike>();
        if (strike == null) { Fail("no strike after picking"); loadout.SetSpecial(SpecialType.BubbleShield); NetGameManager.SendOverride = null; yield break; }
        float aimError = new Vector2(strike.Target.x - target.x, strike.Target.z - target.z).magnitude;
        Note($"strike {aimError:F2}m from the spot picked on the map");
        if (aimError > 0.5f) Fail("the strike isn't where the map was clicked");
        if (loadout.SpecialPoints != 0f || player.InkLevel < 0.99f) Fail("didn't spend the charge and refill ink");
        if (!sent.Any(m => m.id == NetMsg.InkStrike && m.data is InkStrikeData d && Vector3.Distance(d.position, strike.Target) < 0.01f)) Fail("strike wasn't broadcast");
        if (strike.Struck) Fail("struck without the warning delay");
        sent.Clear();
        float markedAt = Time.time;
        yield return HoldUntil(Still, false, 3f, "strike", f => strike == null || strike.Struck);
        Note($"struck {Time.time - markedAt:F2}s after being marked");
        if (Mathf.Abs(Time.time - markedAt - loadout.InkStrikePrefab.Delay) > 0.1f) Fail("didn't strike after its delay");
        float strikeWidth = strike.Width;

        // It spreads out from the middle: halfway, the core is hit but the edge isn't yet.
        yield return HoldUntil(Still, false, strike.AttackTime + 1f, "spread", f => strike == null || strike.Front >= radius * 0.5f);
        bool coreHit = sent.Any(m => m.id == NetMsg.Damage && m.to == FoeIn), edgeHit = sent.Any(m => m.id == NetMsg.Damage && m.to == FoeEdge);
        Note($"halfway out: core {(coreHit ? "hit" : "not hit")}, edge {(edgeHit ? "hit" : "not hit")}");
        if (!coreHit || edgeHit) Fail("the damage didn't spread out from the middle");
        float halfwayWidth = strike.Width;
        yield return HoldUntil(Still, false, loadout.InkStrikePrefab.AttackTime + 0.3f, "after", f => strike.Front >= radius);
        yield return Hold(Still, false, 0.2f, "after");
        float fullWidth = strike.Width;
        Note($"column radius {strikeWidth:F2}m as it strikes, {halfwayWidth:F2}m halfway out, {fullWidth:F2}m at full size (thin {strike.TightRadius:F2}, full {strike.FullWidth:F2})");
        if (strikeWidth > strike.TightRadius * 0.5f) Fail("the column didn't grow out of nothing");
        if (halfwayWidth <= strike.TightRadius || halfwayWidth >= strike.FullWidth) Fail("the column wasn't widening from thin to full");
        if (Mathf.Abs(fullWidth - strike.FullWidth) > 0.05f) Fail("the column didn't reach its full size");
        yield return Hold(Still, false, 0.3f, "after");

        // Inside is our ink; enemies inside got lethal damage, nobody else did.
        int inked = 0, probes = 0;
        for (int i = 0; i < 24; i++)
        {
            Vector3 probe = target + Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward * radius * (0.2f + 0.6f * (i % 3) / 2f) + Vector3.up;
            if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink == null) continue;
            probes++;
            if (ink.getSurfaceTeam(ink.UVFromHit(hit)) == player.Team) inked++;
        }
        DamageData Hit(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Select(m => m.data as DamageData).FirstOrDefault();
        Note($"{inked}/{probes} points inside are our ink; damage to FoeIn {Hit(FoeIn)?.amount ?? 0f:0}, FoeHigh {Hit(FoeHigh)?.amount ?? 0f:0}, FoeEdge {Hit(FoeEdge)?.amount ?? 0f:0}, FoeOut {Hit(FoeOut)?.amount ?? 0f:0}, MateIn {Hit(MateIn)?.amount ?? 0f:0}");
        if (probes == 0 || inked < probes * 0.9f) Fail("the area isn't covered in our ink");
        if (Hit(FoeIn) == null || Hit(FoeIn).amount < 100f) Fail("the enemy in the core wasn't splatted");
        if (Hit(FoeHigh) == null || Hit(FoeHigh).amount < 100f) Fail("the enemy up the column wasn't splatted");
        if (Hit(FoeEdge) == null || Hit(FoeEdge).amount <= 0f || Hit(FoeEdge).amount >= 100f) Fail("the enemy at the edge should be hit, but not fatally");
        if (Hit(FoeOut) != null || Hit(MateIn) != null) Fail("hit someone outside the area (or a teammate)");

        // The whole column, even aimed at the top of something tall: down to the floor around a bare
        // 10m block, its sides all the way down, and whoever stands beside it.
        Transform wallStation = GameObject.Find(Name(Station.NeutralWall)).transform;
        SurfaceInkManager wall = wallStation.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Wall");
        Bounds wb = wall.GetComponent<Renderer>().bounds;
        Vector3 onTop = new Vector3(wb.center.x, wb.max.y, wb.center.z);
        Vector3 beside = new Vector3(wb.center.x, wb.min.y, wb.min.z - 1f);
        if (Physics.Raycast(beside + Vector3.up, Vector3.down, out RaycastHit ground, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) beside = ground.point;
        const ulong FoeBelow = 3055;
        SpawnRemote(FoeBelow, enemyTeam, beside + Vector3.up * 0.05f, "FoeBelow");
        yield return Hold(Still, false, 0.1f, "column");
        sent.Clear();
        loadout.SetSpecialPoints(1000f);
        loadout.LaunchInkStrike(onTop);
        InkStrike tall = FindObjectsByType<InkStrike>(FindObjectsSortMode.None).FirstOrDefault(k => k.IsOwnedLocally && !k.Struck);
        Note($"aimed at the block's top (y {onTop.y:0.0}); column bottom {(tall != null ? tall.Bottom : float.NaN):0.0}, floor {beside.y:0.0}");
        if (tall == null || Mathf.Abs(tall.Bottom - beside.y) > 0.2f) Fail("the column doesn't reach down to the floor");
        yield return Hold(Still, false, loadout.InkStrikePrefab.Delay + loadout.InkStrikePrefab.AttackTime + 0.6f, "column");
        if (strike != null && strike.Width > 0.01f) Fail($"the first column didn't shrink back to nothing (radius {strike.Width:F2}m)");
        int wallInked = 0, wallProbes = 0;
        for (float h = 1f; h < wb.size.y; h += 2f)
        for (int side = -1; side <= 1; side++)
        {
            Vector3 from = new Vector3(wb.center.x + side * 2f, beside.y + h, wb.min.z - 1f);
            if (!Physics.Raycast(from, Vector3.forward, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore) || hit.collider.GetComponent<SurfaceInkManager>() != wall) continue;
            wallProbes++;
            if (wall.getSurfaceTeam(wall.UVFromHit(hit)) == player.Team) wallInked++;
        }
        int floorInked = 0, floorProbes = 0;
        foreach (Vector3 offset in new[] { Vector3.left, Vector3.right, Vector3.forward, Vector3.back })
        {
            Vector3 probe = onTop + offset * 5f; // between the block (4m out) and the column's edge
            if (!Physics.Raycast(new Vector3(probe.x, beside.y + 1f, probe.z), Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) continue;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            if (ink == null) continue;
            floorProbes++;
            if (ink.getSurfaceTeam(ink.UVFromHit(hit)) == player.Team) floorInked++;
        }
        float belowHit = sent.Where(m => m.id == NetMsg.Damage && m.to == FoeBelow).Select(m => (m.data as DamageData).amount).FirstOrDefault();
        Note($"{wallInked}/{wallProbes} points down the block's side and {floorInked}/{floorProbes} on the floor around it are our ink; the enemy beside it took {belowHit:0}");
        if (wallProbes == 0 || wallInked < wallProbes) Fail("didn't ink the block's side all the way down");
        if (floorProbes == 0 || floorInked < floorProbes) Fail("didn't ink the floor around the block");
        if (belowHit <= 0f) Fail("didn't hit the enemy on the floor below");
        Despawn(FoeBelow);
        foreach (SurfaceInkManager ink in wallStation.GetComponentsInChildren<SurfaceInkManager>()) // back as the scene set it up
        {
            ink.FillRegion(new Rect(0, 0, 1, 1), 0);
            TestInkFill fill = ink.GetComponent<TestInkFill>();
            if (fill != null) foreach (TestInkFill.Region r in fill.regions) ink.FillRegion(r.uv, r.team);
        }

        // Theirs: marker and blast only, nothing sent from here.
        sent.Clear();
        gm.Receive(NetMsg.InkStrike, new InkStrikeData { ownerId = FoeOut, team = enemyTeam, position = station.TransformPoint(new Vector3(-6f, 0f, 6f)) }, FoeOut);
        yield return Hold(Still, false, 0.1f, "remote");
        InkStrike theirs = FindObjectsByType<InkStrike>(FindObjectsSortMode.None).FirstOrDefault(k => !k.IsOwnedLocally);
        if (theirs == null) Fail("their strike wasn't shown");
        yield return Hold(Still, false, theirs != null ? theirs.Delay + theirs.AttackTime + 0.3f : 1.2f, "remote");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("their strike was painted/damaged from here");

        foreach (ulong id in new[] { FoeIn, FoeOut, MateIn, FoeEdge, FoeHigh }) Despawn(id);
        loadout.SetSpecial(SpecialType.BubbleShield);
        yield return Hold(Still, false, 0.6f, "cleanup");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }
}
