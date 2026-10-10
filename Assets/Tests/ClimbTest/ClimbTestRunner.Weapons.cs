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

// ClimbTestRunner scenarios: weapons, projectiles and body facing.
public partial class ClimbTestRunner
{

    IEnumerator ProjectilePooling()
    {
        if (projectilePrefab == null) { Fail("runner has no projectile prefab assigned (rebuild the test scene)"); yield break; }

        // Three volleys of 6 team-2 shots straight down onto the floor, half with hidden visuals.
        int before = ProjectileManager.VisualsCreated, afterFirst = 0;
        var targets = new List<Vector3>();
        for (int volley = 0; volley < 3; volley++)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector3 p = station.TransformPoint(new Vector3(-6f + i * 2.4f, 3f, 4f + volley * 2.5f));
                targets.Add(new Vector3(p.x, station.position.y, p.z));
                ProjectileManager.Fire(projectilePrefab, p, Vector3.down * 8f, 10, 2, visible: i % 2 == 0);
            }
            yield return Hold(Still, false, 0.6f, "volley");
            if (volley == 0) afterFirst = ProjectileManager.VisualsCreated - before;
        }
        yield return Hold(Still, false, 0.3f, "settle");

        int created = ProjectileManager.VisualsCreated - before, stillFlying = ProjectileManager.LiveCount;
        Note($"{targets.Count} shots used {created} visuals");
        if (afterFirst == 0 || afterFirst > 3) Fail($"expected a visual per visible shot of the first volley, got {afterFirst}");
        if (created > afterFirst) Fail($"visuals weren't reused ({afterFirst} after the first volley, {created} after three)");
        if (stillFlying > 0) Fail($"{stillFlying} shot(s) never finished");

        int painted = 0;
        foreach (Vector3 t in targets)
            if (Physics.Raycast(t + Vector3.up, Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)
                && hit.collider.TryGetComponent(out SurfaceInkManager ink) && ink.getSurfaceTeam(hit.textureCoord) == 2)
                painted++;
        if (painted < targets.Count) Fail($"only {painted}/{targets.Count} shots painted where they landed");
    }

    IEnumerator ProjectileBallistics()
    {
        NetGameManager gm = NetGameManager.Instance;
        CaptureSends();
        const ulong FoeId = 3060;
        int enemyTeam = player.Team == 1 ? 2 : 1;

        // Speed Ã—2 with gravity Ã—4 follows the same path in half the time.
        var landed = new Dictionary<ProjectileManager.Shot, (Vector3 point, float age)>();
        Action<ProjectileManager.Shot, Vector3> onImpact = (shot, point) => landed[shot] = (point, shot.age);
        ProjectileManager.Impact += onImpact;
        Vector3 from = station.TransformPoint(new Vector3(-8f, 2.5f, -8f));
        Vector3 launch = station.TransformDirection(new Vector3(0f, 0.3f, 1f)).normalized * 8f;
        var doubled = new Ballistics { velocityOverTime = AnimationCurve.Constant(0f, 1f, 2f), gravityOverTime = AnimationCurve.Constant(0f, 1f, 4f) };
        ProjectileManager.Shot plain = ProjectileManager.Fire(projectilePrefab, from, launch, 10, player.Team, ownerId: player.OwnerId);
        ProjectileManager.Shot quick = ProjectileManager.Fire(projectilePrefab, from + station.TransformDirection(Vector3.right) * 3f, launch, 10, player.Team, ownerId: player.OwnerId, ballistics: doubled);
        yield return HoldUntil(Still, false, 3f, "fly", f => landed.ContainsKey(plain) && landed.ContainsKey(quick));
        ProjectileManager.Impact -= onImpact;
        if (!landed.ContainsKey(plain) || !landed.ContainsKey(quick)) { Fail("shots didn't land"); NetGameManager.SendOverride = null; yield break; }
        Vector3 offset = station.TransformDirection(Vector3.right) * 3f;
        float pathError = Vector3.Distance(landed[plain].point + offset, landed[quick].point);
        Note($"plain landed after {landed[plain].age:F2}s, doubled after {landed[quick].age:F2}s, {pathError:F2}m apart (same path)");
        if (pathError > 0.25f) Fail("doubling speed and quadrupling gravity changed the path");
        if (Mathf.Abs(landed[quick].age / landed[plain].age - 0.5f) > 0.05f) Fail("doubled shot didn't take half the time");

        // Ballistics.Faster(2) with twice the launch speed: the same path through shaped curves, in half the time.
        var shaped = new Ballistics { velocityOverTime = AnimationCurve.Linear(0f, 1f, 1f, 0.6f), gravityOverTime = AnimationCurve.Linear(0f, 0.5f, 1f, 1.5f) };
        landed.Clear();
        ProjectileManager.Impact += onImpact;
        Vector3 aside = station.TransformDirection(Vector3.left) * 3f;
        ProjectileManager.Shot slow = ProjectileManager.Fire(projectilePrefab, from + aside * 2f, launch, 10, player.Team, ownerId: player.OwnerId, ballistics: shaped);
        ProjectileManager.Shot fast = ProjectileManager.Fire(projectilePrefab, from + aside, launch * 2f, 10, player.Team, ownerId: player.OwnerId, ballistics: shaped.Faster(2f));
        yield return HoldUntil(Still, false, 3f, "fly", f => landed.ContainsKey(slow) && landed.ContainsKey(fast));
        ProjectileManager.Impact -= onImpact;
        if (!landed.ContainsKey(slow) || !landed.ContainsKey(fast)) { Fail("paced shots didn't land"); NetGameManager.SendOverride = null; yield break; }
        float pacedError = Vector3.Distance(landed[slow].point - aside, landed[fast].point);
        Note($"shaped curves: {landed[slow].age:F2}s, Faster(2) {landed[fast].age:F2}s, {pacedError:F2}m apart");
        if (pacedError > 0.25f) Fail("Ballistics.Faster changed the path");
        if (Mathf.Abs(landed[fast].age / landed[slow].age - 0.5f) > 0.05f) Fail("Ballistics.Faster(2) didn't take half the time");

        // A very fast shot still can't skip past a hitbox between frames.
        SpawnRemote(FoeId, enemyTeam, station.TransformPoint(new Vector3(8f, 0.05f, 8f)), "Foe");
        yield return Hold(Still, false, 0.2f, "fast");
        PlayerController foe = gm.GetPlayer(FoeId);
        sent.Clear();
        Vector3 across = station.TransformDirection(Vector3.right);
        ProjectileManager.Fire(projectilePrefab, foe.BodyCenter - across * 12f, across * 150f, 10, player.Team, ownerId: player.OwnerId,
                               ballistics: new Ballistics { gravityOverTime = AnimationCurve.Constant(0f, 1f, 0f) });
        yield return Hold(Still, false, 0.3f, "fast");
        Note($"150 m/s shot: {(sent.Any(m => m.id == NetMsg.Damage && m.to == FoeId) ? "hit" : "missed")} (moves 2m per frame)");
        if (!sent.Any(m => m.id == NetMsg.Damage && m.to == FoeId)) Fail("a 150 m/s shot passed through the hitbox");

        // Damage falls off with flight time, along the weapon's curve: full close up, the curve's floor far off.
        var falloff = new DamageFalloff(40f, DamageFalloff.HoldThenFall(0.1f, 0.4f, 0.5f));
        var straight = new Ballistics { gravityOverTime = AnimationCurve.Constant(0f, 1f, 0f) };
        float Hit(float distance)
        {
            float before = sent.Where(m => m.id == NetMsg.Damage && m.to == FoeId).Sum(m => ((DamageData)m.data).amount);
            ProjectileManager.Fire(projectilePrefab, foe.BodyCenter - across * distance, across * 24f, 10, player.Team, ownerId: player.OwnerId, ballistics: straight, falloff: falloff);
            return before;
        }
        float DamageSince(float before) => sent.Where(m => m.id == NetMsg.Damage && m.to == FoeId).Sum(m => ((DamageData)m.data).amount) - before;
        float b0 = Hit(1f);
        yield return Hold(Still, false, 0.3f, "falloff");
        float near = DamageSince(b0);
        float b1 = Hit(14f);
        yield return Hold(Still, false, 0.9f, "falloff");
        float farHit = DamageSince(b1);
        Note($"falloff 40 x (1 until 0.1s, 0.5 from 0.4s): 1m away took {near:0.#}, 14m away (0.58s) took {farHit:0.#}");
        if (Mathf.Abs(near - 40f) > 0.5f || Mathf.Abs(farHit - 20f) > 0.5f) Fail("shot damage didn't follow its falloff curve");

        // Grates let ink through (the Projectile layer ignores MapGrate in the physics matrix).
        GameObject grate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        grate.layer = LayerMask.NameToLayer("MapGrate");
        Vector3 under = station.TransformPoint(new Vector3(-8f, 0f, 4f));
        grate.transform.position = under + Vector3.up * 1.5f;
        grate.transform.localScale = new Vector3(3f, 0.1f, 3f);
        Physics.SyncTransforms();
        Vector3? throughGrate = null;
        Action<ProjectileManager.Shot, Vector3> onGrate = (shot, point) => throughGrate = point;
        ProjectileManager.Impact += onGrate;
        ProjectileManager.Fire(projectilePrefab, under + Vector3.up * 3f, Vector3.down * 8f, 10, player.Team, ownerId: player.OwnerId);
        yield return HoldUntil(Still, false, 1f, "grate", f => throughGrate.HasValue);
        ProjectileManager.Impact -= onGrate;
        Destroy(grate);
        Note($"shot dropped through a grate 1.5m up: {(throughGrate.HasValue ? $"landed {throughGrate.Value.y - under.y:F2}m above the floor" : "never landed")}");
        if (!throughGrate.HasValue || throughGrate.Value.y > under.y + 0.5f) Fail("a shot stopped on a grate instead of going through");

        Despawn(FoeId);
        yield return Hold(Still, false, 0.1f, "fast");
        NetGameManager.SendOverride = null;
    }

    IEnumerator BlasterWeapon()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int blasterIndex = -1;
        for (int i = 0; i < loadout.Weapons.Count; i++) if (loadout.Weapons[i] is Blaster && blasterIndex < 0) blasterIndex = i; // the first: Solar Blaster
        if (blasterIndex < 0) { Fail("no Blaster in the player's weapons"); yield break; }
        CaptureSends();
        int weaponBefore = loadout.WeaponIndex;
        loadout.SetMainWeapon(blasterIndex);
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        int enemyTeam = player.Team == 1 ? 2 : 1;
        player.ScriptedInput.recenter = true; // aim level
        yield return null;
        player.ScriptedInput.recenter = false;
        yield return Hold(Still, false, 0.4f, "aim");
        yield return HoldUntil(Still, false, 3f, "ready", f => !player.IsSquid && !player.IsRespawning); // out of a respawn's swim hold
        Blaster blaster = loadout.CurrentWeapon as Blaster;
        if (blaster == null) { Fail("the Blaster didn't equip"); loadout.SetMainWeapon(weaponBefore); NetGameManager.SendOverride = null; yield break; }
        // Fire dead level along our facing (in play it aims at what the camera's looking at).
        WeaponCameraAim cameraAim = blaster.GetComponent<WeaponCameraAim>();
        if (cameraAim != null) cameraAim.enabled = false;
        blaster.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(player.transform.forward, Vector3.up));

        var blasts = new List<(Vector3 point, float travelled, float time)>();
        int blobs = 0;
        Action<ProjectileManager.Shot, Vector3> onBlast = (shot, point) =>
        {
            if (shot.source != blaster.Source || shot.blast == null) return; // not its feet ink
            blasts.Add((point, shot.travelled, Time.time));
            foreach (InkBlastEffect fx in FindObjectsByType<InkBlastEffect>(FindObjectsSortMode.None)) blobs = Mathf.Max(blobs, fx.BlobCount);
        };
        ProjectileManager.Impact += onBlast;
        int OnFloor(Vector3 at)
        {
            if (!Physics.Raycast(new Vector3(at.x, station.position.y + 1f, at.z), Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return -1;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            return ink != null ? ink.getSurfaceTeam(ink.UVFromHit(hit)) : -1;
        }
        float DamageTo(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Sum(m => ((DamageData)m.data).amount);

        // Into the open: explodes in mid-air at its range, straight along its line, at its fire rate.
        player.RefillInk();
        int shotsBefore = blaster.Shots;
        float firedAt = Time.time;
        blaster.ScriptedFire = true;
        yield return Hold(Still, false, 1.6f, "fire");
        blaster.ScriptedFire = false;
        yield return Hold(Still, false, 0.5f, "fire");
        int fired = blaster.Shots - shotsBefore;
        Vector3 muzzle = blaster.LastMuzzle, line = blaster.LastLaunch.normalized;
        Vector3 expected = muzzle + line * blaster.Range;
        var last = blasts.Count > 0 ? blasts[blasts.Count - 1] : (point: Vector3.zero, travelled: 0f, time: 0f);
        float offLine = Vector3.Distance(last.point, expected);
        float flight = blasts.Count > 0 ? blasts[0].time - firedAt : 0f;
        bool inkedBelow = OnFloor(last.point) == player.Team;
        int trail = 0, trailProbes = 0;
        Vector3 level = Vector3.ProjectOnPlane(line, Vector3.up).normalized;
        for (float x = 1f; x < blaster.Range - 1f; x += 0.5f) { trailProbes++; if (OnFloor(muzzle + level * x) == player.Team) trail++; }
        Note($"{fired} shots in 1.6s; {blasts.Count} blasts, each {last.travelled:F2}m out (range {blaster.Range}), {offLine:F2}m from its line, {last.point.y - station.position.y:F1}m up; first after {flight:F2}s; floor under it {(inkedBelow ? "inked" : "not inked")}; {trail}/{trailProbes} points along its path inked; explosions of {blobs} blobs");
        if (fired != Mathf.FloorToInt(1.6f / blaster.FireRate) + 1) Fail("didn't fire at its rate");
        if (blasts.Count != fired || Mathf.Abs(last.travelled - blaster.Range) > 0.05f) Fail("shots didn't explode at their range");
        if (offLine > 0.1f) Fail("the shot didn't fly straight (it dropped or slowed)");
        if (Mathf.Abs(flight - blaster.Range / blaster.ShotSpeed) > 0.05f) Fail("the shot took the wrong time to reach its range");
        if (!inkedBelow) Fail("the mid-air blast didn't ink the floor below it");
        if (trailProbes == 0 || trail < trailProbes * 0.8f) Fail("the shot didn't leave a trail of ink under its path");
        if (blobs < 4) Fail("the explosion isn't a burst of blobs");

        // How far its ink spreads on the floor (ahead of and beside the blast, clear of the trail).
        Vector3 under = new Vector3(last.point.x, station.position.y, last.point.z);
        Vector3 sideways = Vector3.Cross(Vector3.up, level);
        float spread = 0f;
        var reaches = new List<string>();
        foreach (Vector3 d in new[] { level, sideways, -sideways, (level + sideways).normalized, (level - sideways).normalized })
        {
            float reach = 0f;
            for (float r = 0.25f; r <= 6f && OnFloor(under + d * r) == player.Team; r += 0.25f) reach = r; // the patch joined to it
            reaches.Add(reach.ToString("F2"));
            spread = Mathf.Max(spread, reach);
        }
        Note($"its ink reaches {spread:F2}m out across the floor (ahead, sides, diagonals: {string.Join("/", reaches)}; blast reach {blaster.Blast.radius}, ink reach {blaster.Blast.inkReach}); centre {OnFloor(under)}");
        if (spread < 0.5f || spread > blaster.Blast.radius * 2f) Fail("the blast's ink didn't spread a sensible patch (tuning: Blast.inkReach and splashSize)");

        // High in the air, out of its ink's reach: the ink falls to the floor below.
        Vector3 high = station.TransformPoint(new Vector3(-6f, 3.5f, -6f));
        Vector3 ground = new Vector3(high.x, station.position.y, high.z);
        int belowBefore = OnFloor(ground);
        sent.Clear();
        InkExplosion.Detonate(high, blaster.Blast, player.Team, player.OwnerId, blaster.Source, true, Color.white, null, projectilePrefab);
        yield return Hold(Still, false, 0.6f, "falling ink");
        int fell = 0, fellProbes = 0;
        for (int i = 0; i < 8; i++) { fellProbes++; if (OnFloor(ground + Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward * 0.6f) == player.Team) fell++; }
        bool fellUnder = OnFloor(ground) == player.Team;
        Note($"a blast 3.5m up: floor under it {(belowBefore == player.Team ? "already ours" : "clear")} -> {(fellUnder ? "inked" : "not inked")}, {fell}/{fellProbes} points 0.6m around it; {sent.Count(m => m.id == NetMsg.Damage)} damage messages");
        if (belowBefore == player.Team || !fellUnder || fell < fellProbes / 2) Fail("a blast's ink didn't fall to the floor below it");
        if (sent.Any(m => m.id == NetMsg.Damage)) Fail("falling ink did damage");

        // Into a wall: explodes on it, sooner.
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Vector3 flat = Vector3.ProjectOnPlane(line, Vector3.up).normalized;
        wall.transform.position = muzzle + flat * 3f;
        wall.transform.rotation = Quaternion.LookRotation(flat);
        wall.transform.localScale = new Vector3(4f, 4f, 0.4f);
        Physics.SyncTransforms();
        blasts.Clear();
        player.RefillInk();
        yield return Hold(Still, false, blaster.FireRate, "wall"); // ready to fire again
        blaster.ScriptedFire = true;
        yield return Hold(Still, false, 0.1f, "wall");
        blaster.ScriptedFire = false;
        yield return Hold(Still, false, 0.5f, "wall");
        Destroy(wall);
        float wallAt = blasts.Count > 0 ? blasts[0].travelled : -1f;
        Note($"into a wall 3m out: exploded {wallAt:F2}m out");
        if (blasts.Count != 1 || wallAt < 2.5f || wallAt > 3f) Fail("didn't explode on the wall");
        yield return Hold(Still, false, 0.6f, "wall");

        // Damage: a direct hit does its full damage (and isn't splashed again); beside the blast, by distance; out of reach, nothing.
        const ulong FoeDirect = 3101, FoeSide = 3102, FoeFar = 3103;
        Vector3 ahead = muzzle + flat * 4f;
        Vector3 floorUnder = new Vector3(ahead.x, station.position.y + 0.05f, ahead.z);
        Vector3 side = Vector3.Cross(Vector3.up, flat);
        SpawnRemote(FoeDirect, enemyTeam, floorUnder, "FoeDirect");
        SpawnRemote(FoeSide, enemyTeam, floorUnder + side * 1.6f + flat * 0.6f, "FoeSide");
        SpawnRemote(FoeFar, enemyTeam, floorUnder + side * (blaster.Blast.radius + 2f), "FoeFar");
        yield return Hold(Still, false, 0.3f, "foes");
        sent.Clear();
        blasts.Clear();
        player.RefillInk();
        yield return Hold(Still, false, blaster.FireRate, "hit");
        blaster.ScriptedFire = true;
        yield return Hold(Still, false, 0.1f, "hit");
        blaster.ScriptedFire = false;
        yield return Hold(Still, false, 0.5f, "hit");
        float direct = DamageTo(FoeDirect), beside = DamageTo(FoeSide), far = DamageTo(FoeFar);
        bool inkedAround = OnFloor(floorUnder + side * 1f) == player.Team;
        Note($"direct hit {direct:0}, beside the blast {beside:0} (core {blaster.Blast.coreDamage:0} -> edge {blaster.Blast.edgeDamage:0}), out of reach {far:0}");
        if (Mathf.Abs(direct - blaster.DirectDamage.damage) > 0.01f) Fail("a close direct hit didn't do its full damage (only)");
        if (beside < blaster.Blast.edgeDamage - 0.01f || beside > blaster.Blast.coreDamage + 0.01f) Fail("the blast didn't hurt the enemy beside it by distance");
        if (far > 0f) Fail("the blast hurt an enemy out of its reach");
        if (!inkedAround) Fail("the blast didn't ink the floor around it");
        foreach (ulong id in new[] { FoeDirect, FoeSide, FoeFar }) Despawn(id);

        // Theirs: flies and explodes here too, painting and damaging nothing from our side.
        const ulong Shooter = 3104;
        Vector3 theirSpot = station.TransformPoint(new Vector3(6f, 0.05f, -6f));
        SpawnRemote(Shooter, enemyTeam, theirSpot, "TheirBlaster");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = Shooter, position = theirSpot, moveDir = Vector2.zero, team = enemyTeam, weapon = blasterIndex }, Shooter);
        yield return Hold(Still, false, 0.3f, "theirs");
        sent.Clear();
        blasts.Clear();
        gm.Receive(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = Shooter, team = enemyTeam, origin = theirSpot + Vector3.up * 1.2f, inherit = Vector3.zero,
            velocities = new[] { 0f, 0f, blaster.ShotSpeed }, splashSizes = new[] { 22 }, visible = new[] { true }
        }, Shooter);
        yield return Hold(Still, false, blaster.Range / blaster.ShotSpeed + 0.4f, "theirs");
        Note($"their shot: {blasts.Count} blast(s) {(blasts.Count > 0 ? $"{blasts[0].travelled:F2}m out" : "")}; we sent {sent.Count(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)} damage/splat messages");
        if (blasts.Count != 1 || Mathf.Abs(blasts[0].travelled - blaster.Range) > 0.05f) Fail("their Blaster shot didn't explode at its range here");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("their shot painted or damaged from here");
        Despawn(Shooter);

        ProjectileManager.Impact -= onBlast;
        if (cameraAim != null) cameraAim.enabled = true;
        loadout.SetMainWeapon(weaponBefore);
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.2f, "cleanup");
        NetGameManager.SendOverride = null;
    }

    IEnumerator RollerWeapon()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int rollerIndex = -1;
        for (int i = 0; i < loadout.Weapons.Count; i++) if (loadout.Weapons[i] is Roller && rollerIndex < 0) rollerIndex = i; // the first: Inkroller
        if (rollerIndex < 0) { Fail("no Roller in the player's weapons"); yield break; }
        CaptureSends();
        int weaponBefore = loadout.WeaponIndex;
        loadout.SetMainWeapon(rollerIndex);
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        int enemyTeam = player.Team == 1 ? 2 : 1;
        yield return Hold(Still, false, 0.3f, "equip");
        Roller roller = loadout.CurrentWeapon as Roller;
        if (roller == null) { Fail("the Roller didn't equip"); loadout.SetMainWeapon(weaponBefore); NetGameManager.SendOverride = null; yield break; }
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 forward = Flat(player.transform.forward).normalized;
        int OnFloor(Vector3 at)
        {
            if (!Physics.Raycast(new Vector3(at.x, station.position.y + 1f, at.z), Vector3.down, out RaycastHit hit, 2f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return -1;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            return ink != null ? ink.getSurfaceTeam(ink.UVFromHit(hit)) : -1;
        }
        float DamageTo(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Sum(m => ((DamageData)m.data).amount);
        int HitsTo(ulong id) => sent.Count(m => m.id == NetMsg.Damage && m.to == id);
        var landed = new List<Vector3>();
        Action<ProjectileManager.Shot, Vector3> onLand = (shot, point) => { if (shot.source == roller.Source) landed.Add(point); };
        ProjectileManager.Impact += onLand;
        float Spread(Vector3[] launches)
        {
            var yaws = launches.Select(v => Vector3.SignedAngle(forward, Flat(v), Vector3.up)).ToList();
            return yaws.Max() - yaws.Min();
        }
        IEnumerator Tap() { roller.ScriptedFire = true; yield return null; roller.ScriptedFire = false; }

        // Ground flick: a wide fan, short; the enemy in front takes its damage once, however many droplets land on them.
        const ulong Foe = 3201;
        Vector3 start = player.transform.position;
        SpawnRemote(Foe, enemyTeam, new Vector3(start.x, station.position.y + 0.05f, start.z) + forward * 3f, "RollerFoe");
        yield return Hold(Still, false, 0.3f, "foe");
        player.RefillInk();
        float ink0 = player.InkLevel;
        sent.Clear();
        landed.Clear();
        yield return Tap();
        float flickCost = ink0 - player.InkLevel; // before the tank refills any
        yield return Hold(Still, false, 1.2f, "ground flick");
        float groundSpread = Spread(roller.LastFlick ?? new Vector3[0]);
        float groundReach = landed.Count > 0 ? landed.Max(p => Flat(p - start).magnitude) : 0f;
        float launchSpeed = roller.LastFlick != null ? roller.LastFlick.Max(v => v.magnitude) : 0f;
        Note($"droplets launched at up to {launchSpeed:F1} m/s ({roller.FlickPace}x pace), shown {roller.DropletScale}x size");
        Note($"ground flick: {roller.LastFlick?.Length ?? 0} droplets across {groundSpread:F0}°, landing up to {groundReach:F1}m out; cost {flickCost * 100f:F1}% ink; the enemy 3m ahead took {DamageTo(Foe):0} in {HitsTo(Foe)} hit(s)");
        if (roller.Flicks != 1 || roller.LastFlickInAir) Fail("pressing on the ground didn't do a ground flick");
        if (groundSpread < 45f) Fail("the ground flick isn't a wide fan");
        if (groundReach < 2f || groundReach > 9f) Fail("the ground flick's reach is off");
        if (Mathf.Abs(flickCost - roller.FlickInkCost * 0.001f) > 0.005f) Fail("the flick didn't cost its ink");
        DamageFalloff groundDamage = roller.GroundDamage;
        if (HitsTo(Foe) != 1) Fail("the flick didn't hit the enemy exactly once");
        if (DamageTo(Foe) > groundDamage.damage + 0.01f || DamageTo(Foe) < groundDamage.MinDamage - 0.01f) Fail("the flick's damage is outside its falloff range");
        if (groundDamage.MinDamage < groundDamage.damage && DamageTo(Foe) >= groundDamage.damage - 0.01f) Fail("the flick didn't lose damage on its way 3m out");
        Despawn(Foe);

        // Aimed with the view: looking up raises the flick.
        float Elevation(Vector3[] launches) => launches.Average(v => Mathf.Atan2(v.y, Flat(v).magnitude) * Mathf.Rad2Deg);
        float levelElevation = Elevation(roller.LastFlick);
        player.ScriptedInput.lookStick = new Vector2(0f, 1f);
        yield return Hold(Still, false, 0.25f, "look up");
        player.ScriptedInput.lookStick = Vector2.zero;
        float lookedUp = -player.CameraPitch;
        yield return Hold(Still, false, roller.FlickRate, "cooldown");
        yield return Tap();
        yield return Hold(Still, false, roller.Windup, "aimed flick");
        float groundTurnAtTop = roller.FrameTurn;
        yield return Hold(Still, false, roller.FollowThrough + 0.1f, "aimed flick");
        float raisedElevation = Elevation(roller.LastFlick);
        player.ScriptedInput.recenter = true;
        yield return Hold(Still, false, 0.6f, "recenter");
        player.ScriptedInput.recenter = false;
        Note($"looking {lookedUp:F0}° up raised the flick from {levelElevation:F0}° to {raisedElevation:F0}°");
        if (Mathf.Abs(raisedElevation - levelElevation - lookedUp) > 5f) Fail("looking up didn't raise the flick by as much");
        yield return Hold(Still, false, 0.6f, "settle");

        // Air flick: a narrow line, further.
        yield return Hold(Still, false, roller.FlickRate, "cooldown");
        landed.Clear();
        player.PressJump();
        yield return Hold(Still, false, 0.15f, "jump");
        bool airborne = !player.IsGrounded;
        yield return Tap();
        yield return Hold(Still, false, roller.Windup, "air flick");
        float turnedAtTop = roller.FrameTurn;
        yield return Hold(Still, false, 1.6f - roller.Windup, "air flick");
        float turnedAfter = roller.FrameTurn;
        Note($"air flick: frame turned {turnedAtTop:F0}° at the top of the swing (ground flick {groundTurnAtTop:F0}°), {turnedAfter:F0}° after (folded idle)");
        if (Mathf.Abs(turnedAtTop - roller.VerticalTurn) > 3f) Fail("the air flick didn't turn the frame on its side");
        if (Mathf.Abs(groundTurnAtTop) > 3f) Fail("the ground flick didn't unfold the frame");
        float airSpread = Spread(roller.LastFlick ?? new Vector3[0]);
        float airReach = landed.Count > 0 ? landed.Max(p => Flat(p - start).magnitude) : 0f;
        Note($"air flick ({(airborne ? "airborne" : "NOT airborne")}): {roller.LastFlick?.Length ?? 0} droplets across {airSpread:F0}°, landing up to {airReach:F1}m out");
        if (!roller.LastFlickInAir) Fail("pressing in the air didn't do an air flick");
        if (airSpread > 15f) Fail("the air flick isn't a narrow line");
        if (airReach <= groundReach + 1f) Fail("the air flick doesn't reach further than the ground flick");

        // Plain walking speed, for comparison.
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        start -= forward * 4f; // room to roll
        player.PlaceAt(start, player.transform.eulerAngles.y);
        yield return Hold(Fwd, false, 0.8f, "placed: walk");
        Vector3 w0 = player.transform.position;
        yield return Hold(Fwd, false, 0.4f, "walk");
        float walkSpeed = Flat(player.transform.position - w0).magnitude / 0.4f;
        yield return Hold(Still, false, 0.5f, "stop");
        player.PlaceAt(start, player.transform.eulerAngles.y);
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.3f, "placed: reset");

        // Roll: flick, keep holding; down, ramping from 0.8x to 1.2x, inking a strip, ink by the metre.
        player.RefillInk();
        roller.ScriptedFire = true;
        yield return HoldUntil(Still, false, 1f, "to roll", f => !roller.Flicking && roller.Flicks > 0);
        float drumAfterFlick = roller.Drum.position.y - station.position.y;
        yield return HoldUntil(Still, false, 0.3f, "to roll", f => roller.Down);
        bool wentDown = roller.Down;
        Note($"ground flick ended with the drum {drumAfterFlick:F2}m up (radius {roller.Drum.lossyScale.x * 0.225f:F2}), down {(wentDown ? "straight after" : "NEVER")}");
        if (drumAfterFlick > 0.45f) Fail("the ground flick didn't come down into the rolling position");
        float inkDown = roller.RollInkSpent;
        Vector3 rollFrom = player.transform.position;
        // An enemy on the way: run over for its damage, once, carrying on.
        SpawnRemote(Foe, enemyTeam, new Vector3(rollFrom.x, station.position.y + 0.05f, rollFrom.z) + forward * 9f, "RollerFoe"); // past where the speed is measured
        yield return Hold(Still, false, 0.3f, "foe");
        sent.Clear();
        yield return Hold(Fwd, false, 0.05f, "roll");
        float earlyMul = player.WeaponSpeedMultiplier;
        yield return Hold(Fwd, false, 0.75f, "roll");
        float lateMul = player.WeaponSpeedMultiplier;
        Vector3 r0 = player.transform.position;
        yield return Hold(Fwd, false, 0.4f, "roll");
        float rollSpeedSeen = Flat(player.transform.position - r0).magnitude / 0.4f;
        Vector3 rollTo = player.transform.position;
        float rolled = Flat(rollTo - rollFrom).magnitude;
        float perMetre = (roller.RollInkSpent - inkDown) / Mathf.Max(rolled, 0.01f);
        yield return Hold(Fwd, false, 0.6f, "run over");
        Note($"ran over the enemy: {DamageTo(Foe):0} in {HitsTo(Foe)} hit(s)");
        if (HitsTo(Foe) != 1 || Mathf.Abs(DamageTo(Foe) - roller.RollDamage) > 0.01f) Fail("rolling into the enemy didn't hit them once for its damage");
        Despawn(Foe);
        yield return Hold(Still, false, 0.2f, "roll");
        // The strip: inked down the middle of the path and out to most of the drum's width.
        Vector3 mid = Vector3.Lerp(rollFrom, rollTo, 0.5f);
        Vector3 side = Vector3.Cross(Vector3.up, forward);
        int along = 0, alongProbes = 0;
        for (float d = 1.5f; d < rolled - 0.5f; d += 0.5f) { alongProbes++; if (OnFloor(rollFrom + forward * d) == player.Team) along++; }
        float half = roller.RollerWidth * 0.5f;
        bool edges = OnFloor(mid + side * half * 0.7f) == player.Team && OnFloor(mid - side * half * 0.7f) == player.Team;
        Note($"down {(wentDown ? "after the flick" : "NEVER")}; speed x{earlyMul:F2} at first -> x{lateMul:F2} (walking {walkSpeed:F1} m/s, rolling {rollSpeedSeen:F1} m/s); rolled {rolled:F1}m: {along}/{alongProbes} points along it inked, {(edges ? "both" : "not both")} edges at 70% of the {roller.RollerWidth:F2}m drum; {perMetre * 1000f:F1}/1000 ink a metre (set {roller.RollInkPerMetre})");
        if (!wentDown) Fail("holding after the flick didn't put the roller down");
        if (earlyMul > roller.RollStartSpeed + 0.1f || Mathf.Abs(lateMul - roller.RollSpeed) > 0.01f) Fail("rolling speed didn't build from its start to its full speed");
        if (Mathf.Abs(rollSpeedSeen / Mathf.Max(walkSpeed, 0.1f) - roller.RollSpeed) > 0.12f) Fail("full rolling speed isn't its multiple of walking speed");
        if (alongProbes == 0 || along < alongProbes * 0.9f || !edges) Fail("rolling didn't ink a strip the drum's width");
        if (Mathf.Abs(perMetre * 1000f - roller.RollInkPerMetre) > roller.RollInkPerMetre * 0.25f) Fail("rolling didn't cost its ink by the metre");

        // Rolling faces the way we move: strafing turns the body, and the roller with it.
        Vector3 rightOfView = Vector3.Cross(Vector3.up, Flat(player.transform.forward)).normalized;
        yield return Hold(new Vector2(1f, 0f), false, 0.7f, "roll right");
        float bodyTurn = Mathf.DeltaAngle(player.transform.eulerAngles.y, player.ModelYaw);
        float drumSide = Vector3.Dot(Flat(roller.Drum.position - player.transform.position), rightOfView);
        yield return Hold(Still, false, 0.2f, "roll right");
        Note($"rolling to the right: body turned {bodyTurn:F0}° from the view, drum {drumSide:F2}m to the right, {(player.Aiming ? "aiming" : "not aiming")}");
        if (Mathf.Abs(bodyTurn - 90f) > 12f || drumSide < 1f || player.Aiming) Fail("rolling didn't face (and roll) the way we move");

        // A jump lifts it (no flick); landing with it held puts it back down.
        int flicksBefore = roller.Flicks;
        player.PressJump();
        yield return Hold(Still, false, 0.15f, "jump");
        bool liftedInAir = !player.IsGrounded && !roller.Down;
        yield return HoldUntil(Still, false, 1.5f, "land", f => player.IsGrounded && roller.Down);
        bool downAgain = roller.Down;
        roller.ScriptedFire = false;
        yield return Hold(Still, false, 0.1f, "release");
        Note($"jump: {(liftedInAir ? "lifted in the air" : "NOT lifted")}, {(downAgain ? "down again on landing" : "NOT down again")}, {roller.Flicks - flicksBefore} flick(s); released: {(roller.Down ? "still down" : "up")}, speed x{player.WeaponSpeedMultiplier:F2}");
        if (!liftedInAir || !downAgain || roller.Flicks != flicksBefore) Fail("a jump while rolling should just lift it, and landing put it back down");
        if (roller.Down || player.WeaponSpeedMultiplier != 1f) Fail("letting go didn't lift it and give our speed back");
        yield return Hold(Still, false, 0.5f, "idle");
        Note($"idle: frame turned {roller.FrameTurn:F0}° (folded at {roller.VerticalTurn:F0}°), tilted {roller.Pitch:F0}° (held up at {roller.IdlePitch:F0}°)");
        if (Mathf.Abs(roller.FrameTurn - roller.VerticalTurn) > 3f || Mathf.Abs(roller.Pitch - roller.IdlePitch) > 3f) Fail("not attacking or rolling, it didn't fold up and hold at our side");

        // Theirs: down when their state says so; their flick flies here but paints and hurts nothing from our side.
        const ulong Them = 3202;
        Vector3 theirSpot = station.TransformPoint(new Vector3(6f, 0.05f, -6f));
        SpawnRemote(Them, enemyTeam, theirSpot, "TheirRoller");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = Them, position = theirSpot, moveDir = Vector2.zero, team = enemyTeam, weapon = rollerIndex, weaponDown = true }, Them);
        yield return Hold(Still, false, 0.3f, "theirs");
        PlayerController them = gm.GetPlayer(Them);
        Roller theirRoller = them != null ? them.GetComponent<PlayerLoadout>().CurrentWeapon as Roller : null;
        bool theirsDown = theirRoller != null && theirRoller.Down;
        float drumHeight = theirRoller != null && theirRoller.Drum != null ? theirRoller.Drum.position.y - station.position.y : -1f;
        sent.Clear();
        landed.Clear();
        gm.Receive(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = Them, team = enemyTeam, origin = theirSpot + Vector3.up * 1f, inherit = Vector3.zero,
            velocities = new[] { 0f, 1f, 9f, 3f, 1f, 8f, -3f, 1f, 8f }, splashSizes = new[] { 14, 14, 14 }, visible = new[] { true, true, true }
        }, Them);
        yield return null; // handled on the main-thread queue
        bool theirSwing = theirRoller != null && theirRoller.Flicking;
        float theirGroundTurn = theirRoller != null ? theirRoller.FrameTurn : -1f;
        yield return Hold(Still, false, 1f, "theirs");
        bool swingDone = theirRoller != null && !theirRoller.Flicking;
        int firstLanded = landed.Count;
        gm.Receive(NetMsg.ProjectileSpawn, new ProjectileSpawnData
        {
            shooterSteamId = Them, team = enemyTeam, origin = theirSpot + Vector3.up * 1f, inherit = Vector3.zero,
            velocities = new[] { 0f, 3f, 12f, 0.2f, 3f, 14f, -0.2f, 3f, 16f }, splashSizes = new[] { 12, 12, 12 }, visible = new[] { true, true, true }
        }, Them);
        yield return null;
        bool airSwing = theirRoller != null && theirRoller.Flicking;
        yield return Hold(Still, false, 0.05f, "theirs");
        float theirAirTurn = theirRoller != null ? theirRoller.FrameTurn : -1f;
        yield return Hold(Still, false, 1f, "theirs");
        Note($"their flicks: wide one swung {(theirSwing ? "yes" : "NO")} (frame {theirGroundTurn:F0}°, {(swingDone ? "finished" : "still swinging")}), narrow one swung {(airSwing ? "yes" : "NO")} (frame {theirAirTurn:F0}°)");
        if (!theirSwing || !swingDone || !airSwing) Fail("their roller didn't swing for their flicks");
        if (Mathf.Abs(theirGroundTurn) > 3f || Mathf.Abs(theirAirTurn - roller.VerticalTurn) > 3f) Fail("their air flick didn't turn the frame on its side (or the ground one did)");
        Note($"their roller {(theirsDown ? "down" : "NOT down")} (drum {drumHeight:F2}m up); their flick: {landed.Count} droplet(s) landed, we sent {sent.Count(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)} damage/splat messages");
        if (!theirsDown || drumHeight < 0f || drumHeight > roller.RollerWidth) Fail("their roller didn't show down");
        if (firstLanded != 3) Fail("their flick didn't fly here");
        if (sent.Any(m => m.id == NetMsg.Damage || m.id == NetMsg.Splat)) Fail("their flick painted or damaged from here");
        Despawn(Them);

        ProjectileManager.Impact -= onLand;
        loadout.SetMainWeapon(weaponBefore);
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.2f, "cleanup");
        NetGameManager.SendOverride = null;
    }

    IEnumerator BodyFacing()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int weaponBefore = loadout.WeaponIndex;
        loadout.SetMainWeapon(0);
        CaptureSends();
        yield return Hold(Still, false, 0.3f, "equip");
        WeaponShooter gun = loadout.CurrentWeapon as WeaponShooter;
        if (gun == null) { Fail("no shooter to fire"); loadout.SetMainWeapon(weaponBefore); NetGameManager.SendOverride = null; yield break; }
        float Turn() => Mathf.DeltaAngle(player.transform.eulerAngles.y, player.ModelYaw);

        // Moving: the body turns to the way we go (here, right of the view), smoothly.
        yield return Hold(new Vector2(1f, 0f), false, 0.8f, "strafe");
        float strafing = Turn();
        // Firing: straight round to the view, the gun aimed where we look.
        gun.ScriptedFire = true;
        yield return Hold(new Vector2(1f, 0f), false, 1f / 75f, "fire");
        float firing = Turn();
        int shotsBefore = gun.MainShots;
        Vector3 aimDir = Vector3.zero;
        Action<Vector3, Quaternion> onShot = (v, rot) => aimDir = rot * Vector3.forward;
        gun.MainShotFired += onShot;
        yield return Hold(new Vector2(1f, 0f), false, 0.4f, "fire");
        gun.MainShotFired -= onShot;
        float stillFacing = Turn();
        float aimOff = Vector3.Angle(Vector3.ProjectOnPlane(aimDir, Vector3.up), Vector3.ProjectOnPlane(player.transform.forward, Vector3.up));
        gun.ScriptedFire = false;
        // Stopped firing: back to the way we move, after a moment.
        yield return Hold(new Vector2(1f, 0f), false, 0.8f, "strafe");
        float after = Turn();
        // Standing still: keeps its heading.
        yield return Hold(Still, false, 0.4f, "still");
        float still = Turn();
        PlayerStateData state = player.GetNetState(0, 0);
        Note($"body vs view: strafing {strafing:F0}°, the frame firing starts {firing:F0}°, firing {stillFacing:F0}° (shots aimed {aimOff:F0}° off the view), after {after:F0}°, standing {still:F0}°; sent facing {Mathf.DeltaAngle(player.transform.eulerAngles.y, state.modelYaw):F0}°");
        if (Mathf.Abs(strafing - 90f) > 10f) Fail("moving didn't turn the body to the way we move");
        if (Mathf.Abs(firing) > 1f || Mathf.Abs(stillFacing) > 1f) Fail("firing didn't turn the body to the view at once");
        if (gun.MainShots == shotsBefore || aimOff > 20f) Fail("shots while strafing didn't go where we look");
        if (Mathf.Abs(after - 90f) > 10f) Fail("after firing the body didn't go back to the way we move");
        if (Mathf.Abs(still - after) > 2f) Fail("standing still changed the body's heading");
        if (Mathf.Abs(Mathf.DeltaAngle(state.modelYaw, player.ModelYaw)) > 0.1f) Fail("our state doesn't carry the body's facing");

        // A sub turns it round to the view too.
        yield return Hold(new Vector2(1f, 0f), false, 0.6f, "strafe");
        player.RefillInk();
        loadout.UseSub();
        yield return Hold(new Vector2(1f, 0f), false, 1f / 75f, "sub");
        float subTurn = Turn();
        Note($"using the sub: body {subTurn:F0}° from the view");
        if (Mathf.Abs(subTurn) > 1f) Fail("using the sub didn't turn the body to the view");
        yield return Hold(Still, false, 0.6f, "settle");
        foreach (SubDevice d in FindObjectsByType<SubDevice>(FindObjectsSortMode.None)) if (d.OwnerId == player.OwnerId) Destroy(d.gameObject);

        // Theirs: their model faces what their state says.
        const ulong Them = 3301;
        Vector3 spot = station.TransformPoint(new Vector3(6f, 0.05f, -6f));
        SpawnRemote(Them, player.Team == 1 ? 2 : 1, spot, "Facing");
        yield return Hold(Still, false, 0.2f, "theirs");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = Them, position = spot, moveDir = Vector2.zero, team = player.Team == 1 ? 2 : 1, bodyYaw = 0f, modelYaw = 120f }, Them);
        yield return Hold(Still, false, 0.6f, "theirs");
        PlayerController them = gm.GetPlayer(Them);
        float theirs = them != null ? them.ModelYaw : -1f;
        Note($"their body faces {theirs:F0}° (sent 120°, view 0°)");
        if (Mathf.Abs(Mathf.DeltaAngle(theirs, 120f)) > 3f) Fail("their model didn't face what their state says");

        // Their weapon tilts with their view while they aim, and rests level otherwise.
        Weapon theirGun = them != null ? them.GetComponent<PlayerLoadout>().CurrentWeapon : null;
        float Tilt() => theirGun != null ? Mathf.DeltaAngle(0f, theirGun.transform.localEulerAngles.x) : float.NaN;
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = Them, position = spot, moveDir = Vector2.zero, team = player.Team == 1 ? 2 : 1, modelYaw = 120f, camPitch = -30f, aiming = true }, Them);
        yield return Hold(Still, false, 0.5f, "their aim");
        float aimingTilt = Tilt();
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = Them, position = spot, moveDir = Vector2.zero, team = player.Team == 1 ? 2 : 1, modelYaw = 120f, camPitch = -30f, aiming = false }, Them);
        yield return Hold(Still, false, 0.5f, "their aim");
        float restingTilt = Tilt();
        Note($"their {theirGun?.DisplayName}: tilted {aimingTilt:F0}° aiming 30° up, {restingTilt:F0}° not aiming");
        if (Mathf.Abs(aimingTilt + 30f) > 2f) Fail("their weapon didn't follow their aim up");
        if (Mathf.Abs(restingTilt) > 2f) Fail("their weapon didn't rest level when not aiming");
        Despawn(Them);

        loadout.SetMainWeapon(weaponBefore);
        NetGameManager.SendOverride = null;
    }

    IEnumerator AttackOutOfSwim()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int weaponBefore = loadout.WeaponIndex;
        NetGameManager.SendOverride = (id, data, target) => { };
        var waits = new List<string>();
        for (int w = 0; w < loadout.Weapons.Count; w++)
        {
            loadout.SetMainWeapon(w);
            yield return Hold(Still, false, 0.8f, "equip");
            Weapon weapon = loadout.CurrentWeapon;
            Func<int> count = weapon is WeaponShooter s ? () => s.MainShots : weapon is Blaster b ? () => b.Shots : weapon is Roller r ? () => r.Flicks : (Func<int>)null;
            Action<bool> fire = on => { if (weapon is WeaponShooter s2) s2.ScriptedFire = on; else if (weapon is Blaster b2) b2.ScriptedFire = on; else if (weapon is Roller r2) r2.ScriptedFire = on; };
            if (count == null) continue;
            player.RefillInk();
            yield return Hold(Still, true, 0.4f, "swim");
            int atStart = count();
            fire(true);
            yield return Hold(Still, true, 0.6f, "swim, holding attack");
            int before = count();
            player.ScriptedInput.swim = false;
            int frames = 0;
            while (count() == before && frames < 60) { yield return null; Record("leave swim"); frames++; }
            fire(false);
            waits.Add($"{weapon.DisplayName} {(count() == before ? "never" : $"{frames} frame(s)")} ({before - atStart} while swimming)");
            if (before != atStart) Fail($"{weapon.DisplayName} attacked while in swim form");
            if (count() == before || frames > 3) Fail($"{weapon.DisplayName} didn't attack at once out of swim form");
            yield return Hold(Still, false, 0.8f, "settle");
        }
        Note("first attack after leaving swim form: " + string.Join(", ", waits));
        loadout.SetMainWeapon(weaponBefore);
        NetGameManager.SendOverride = null;
    }

    IEnumerator FeetInk()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int weaponBefore = loadout.WeaponIndex;
        NetGameManager.SendOverride = (id, data, target) => { };
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        for (int w = 0; w < loadout.Weapons.Count; w++)
        {
            if (!(loadout.Weapons[w] is WeaponShooter) && !(loadout.Weapons[w] is Blaster)) continue;
            loadout.SetMainWeapon(w);
            yield return null;
            Weapon weapon = loadout.CurrentWeapon;
            void SetFire(bool on) { if (weapon is WeaponShooter s) s.ScriptedFire = on; else if (weapon is Blaster b) b.ScriptedFire = on; }
            floor.FillRegion(new Rect(0, 0, 1, 1), 0);
            player.RefillInk();
            yield return Hold(Still, false, 0.8f, "ready"); // past any fire cooldown

            int before = weapon.FeetShots;
            SetFire(true);
            yield return Hold(Still, false, 1.3f, "fire");
            SetFire(false);
            int drops = weapon.FeetShots - before;
            yield return Hold(Still, false, 0.6f, "land");

            int underfoot = -1;
            if (Physics.Raycast(player.BodyCenter, Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
            {
                SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
                if (ink != null) underfoot = ink.getSurfaceTeam(ink.UVFromHit(hit));
            }
            Note($"{weapon.DisplayName}: {drops} feet drops in 1.3s; ink underfoot team {underfoot}");
            if (drops < 3 || drops > 4) Fail($"{weapon.DisplayName}: expected 3-4 feet drops in 1.3s (every 0.4s), got {drops}");
            if (underfoot != player.Team) Fail($"{weapon.DisplayName}: not standing in our own ink after shooting");
        }
        loadout.SetMainWeapon(weaponBefore);
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }

    IEnumerator WeaponVariety()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null || loadout.Weapons.Count < 2) { Fail("need two weapons"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        var shots = new int[2];
        var speed = new float[2];
        var maxYaw = new float[2];
        for (int w = 0; w < 2; w++)
        {
            loadout.SetMainWeapon(w);
            player.RefillInk();
            yield return Hold(Still, false, 0.3f, "equip");
            WeaponShooter weapon = loadout.CurrentWeapon as WeaponShooter;
            if (weapon == null) { Fail($"{loadout.Weapons[w].DisplayName} isn't a gun"); break; }
            int index = w;
            Action<Vector3, Quaternion> onShot = (v, aim) =>
            {
                speed[index] = v.magnitude;
                Vector3 flatAim = Vector3.ProjectOnPlane(aim * Vector3.forward, Vector3.up);
                maxYaw[index] = Mathf.Max(maxYaw[index], Mathf.Abs(Vector3.SignedAngle(flatAim, Vector3.ProjectOnPlane(v, Vector3.up), Vector3.up)));
            };
            weapon.MainShotFired += onShot;
            int before = weapon.MainShots;
            weapon.ScriptedFire = true;
            yield return Hold(Still, false, 1f, "fire " + weapon.DisplayName);
            weapon.ScriptedFire = false;
            weapon.MainShotFired -= onShot;
            shots[w] = weapon.MainShots - before;
        }
        Note($"Airspray SE: {shots[0]} shots/s, speed {speed[0]:F1}, max yaw {maxYaw[0]:F1}°; Inkshot: {shots[1]} shots/s, speed {speed[1]:F1}, max yaw {maxYaw[1]:F1}°");
        if (loadout.Weapons[0].DisplayName != "Airspray SE" || loadout.Weapons[1].DisplayName != "Inkshot") Fail("weapons aren't Airspray SE and Inkshot");
        if (shots[1] >= shots[0] * 0.8f) Fail("Inkshot doesn't fire slower");
        if (speed[1] < speed[0] * 1.2f) Fail("Inkshot doesn't reach further");
        if (maxYaw[1] >= maxYaw[0] * 0.5f) Fail("Inkshot's spread isn't much narrower");
        loadout.SetMainWeapon(0);
        yield return Hold(Still, false, 0.5f, "settle"); // let the shots land
        NetGameManager.SendOverride = null;
    }

    IEnumerator WeaponPrefabs()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null) { Fail("player has no PlayerLoadout"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        const ulong MateId = 3040;

        // Switching replaces the instance in our hands with the other prefab.
        loadout.SetMainWeapon(1);
        yield return null;
        Weapon held = loadout.CurrentWeapon;
        Transform mount = held != null ? held.transform.parent : null;
        int inHand = mount == null ? 0 : mount.GetComponentsInChildren<Weapon>().Length;
        Note($"holding {held?.DisplayName} under {mount?.name} ({inHand} weapon(s) there)");
        if (held == null || held.DisplayName != "Inkshot" || inHand != 1) Fail("switching didn't swap the weapon in our hands");
        if (player.GetNetState(0, 0).weapon != 1) Fail("our weapon choice isn't in our state");

        // Jumping doesn't carry into the shots; running still does.
        WeaponShooter gun = held as WeaponShooter;
        player.RefillInk();
        player.PressJump();
        gun.ScriptedFire = true;
        float risingY = 0f, inheritedY = 0f;
        for (int i = 0; i < 20; i++)
        {
            yield return null;
            Record("jumpfire");
            if (player.VerticalVelocity > risingY) { risingY = player.VerticalVelocity; inheritedY = gun.LastInherited.y; }
        }
        gun.ScriptedFire = false;
        Note($"rising at {risingY:F1} m/s, shots inherited {inheritedY:F2} m/s vertically");
        if (risingY < 1f) Fail("never left the ground");
        if (inheritedY != 0f) Fail("shots inherited our vertical velocity");
        yield return Hold(Still, false, 1f, "land");

        // A teammate's copy shows the weapon their state says, as a model only.
        SpawnRemote(MateId, player.Team, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Mate");
        yield return Hold(Still, false, 0.2f, "remote");
        PlayerController mate = gm.GetPlayer(MateId);
        PlayerLoadout mateLoadout = mate != null ? mate.GetComponent<PlayerLoadout>() : null;
        if (mateLoadout == null || mateLoadout.CurrentWeapon == null || mateLoadout.CurrentWeapon.DisplayName != "Airspray SE") Fail("remote copy didn't start with the default weapon");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = MateId, position = mate.transform.position, moveDir = Vector2.zero, team = player.Team, weapon = 1 }, MateId);
        yield return Hold(Still, false, 0.2f, "remote");
        Weapon theirs = mateLoadout != null ? mateLoadout.CurrentWeapon : null;
        if (theirs == null || theirs.DisplayName != "Inkshot") Fail("remote copy didn't switch to their Inkshot");
        else if (theirs.enabled) Fail("remote copy's weapon is live (it should only be a model)");

        Despawn(MateId);
        loadout.SetMainWeapon(0);
        yield return Hold(Still, false, 0.1f, "remote");
        NetGameManager.SendOverride = null;
    }

    IEnumerator LoadoutMenuPicks()
    {
        NetGameManager gm = NetGameManager.Instance;
        LoadoutMenu menu = FindFirstObjectByType<LoadoutMenu>();
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        LoadoutHUD gauges = FindFirstObjectByType<LoadoutHUD>();
        if (menu == null || loadout == null) { Fail("missing LoadoutMenu or PlayerLoadout"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };

        menu.SetOpen(true);
        yield return null;
        if (!menu.IsOpen || !InputGate.Blocked) Fail("didn't open (or didn't block gameplay input)");
        menu.WeaponButtons[1].onClick.Invoke();
        menu.SubButtons[1].onClick.Invoke();
        menu.SpecialButtons[1].onClick.Invoke();
        yield return null;
        if (loadout.Special != SpecialType.InkStrike || PlayerPrefs.GetInt("loadout.special", -1) != (int)SpecialType.InkStrike) Fail("picking InkStrike didn't select (and remember) it");
        if (loadout.WeaponIndex != 1 || loadout.CurrentWeapon == null || loadout.CurrentWeapon.DisplayName != "Inkshot") Fail("picking Inkshot didn't equip it");
        if (loadout.Sub != SubType.Sprinkler || Mathf.Abs(loadout.SubInkCost - loadout.SprinklerPrefab.InkCost) > 0.001f) Fail("picking the sprinkler didn't select it");
        if (PlayerPrefs.GetInt("loadout.weapon", -1) != 1 || PlayerPrefs.GetInt("loadout.sub", -1) != (int)SubType.Sprinkler) Fail("choices weren't remembered");
        menu.SetOpen(false);
        yield return Hold(Still, false, 0.1f, "menu");
        if (menu.IsOpen || InputGate.Blocked) Fail("didn't close");
        if (gauges != null && (gauges.SubName != "SPRINKLER" || gauges.SubIconShown != loadout.SprinklerPrefab.Icon)) Fail("sub gauge doesn't show the sprinkler");
        if (gauges != null && (gauges.SpecialIconShown == null || gauges.SpecialIconShown != loadout.SpecialIcon)) Fail("special gauge doesn't show the Inkstrike's icon");

        // Not during a match; one in progress closes it.
        gm.SetMatchTimings(5f, 60f, 30f, 0.5f, 2f);
        menu.SetOpen(true);
        gm.StartGame();
        frames.Clear();
        yield return Hold(Still, false, 0.1f, "match");
        if (menu.IsOpen) Fail("stayed open when a match started");
        menu.SetOpen(true);
        if (menu.IsOpen) Fail("opened during a match");
        gm.EndMatch();
        gm.SetMatchTimings(3f, 180f, 60f, 2.5f, 15f);
        yield return Hold(Still, false, 0.1f, "match");

        if (gauges != null && gauges.SpecialName != "INKSTRIKE") Fail("special gauge doesn't show InkStrike");

        loadout.SetMainWeapon(0);
        loadout.SetSub(SubType.Beacon);
        loadout.SetSpecial(SpecialType.BubbleShield);
        NetGameManager.SendOverride = null;
    }
}
