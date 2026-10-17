using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Shots at a player swimming in their ink (as an enemy's client fires them at our copy): what they
// hit, what they paint, and whether a fatal one kills.
public partial class ClimbTestRunner
{
    IEnumerator SwimHits()
    {
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), player.Team);
        int enemyTeam = player.Team == 1 ? 2 : 1;
        const ulong Shooter = 4401;
        player.Hitbox.ResetHealth();
        yield return Hold(Still, true, 0.6f, "dive"); // swimming in our ink, still
        Vector3 at = player.transform.position;
        int UnderUs()
        {
            Vector3 p = player.Hitbox.transform.position;
            if (!Physics.Raycast(p + Vector3.up, Vector3.down, out RaycastHit hit, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore)) return -1;
            SurfaceInkManager ink = hit.collider.GetComponent<SurfaceInkManager>();
            return ink != null ? ink.getSurfaceTeam(ink.UVFromHit(hit)) : -1;
        }
        var capsule = player.Hitbox.GetComponent<CapsuleCollider>();
        Bounds hb = capsule.bounds;
        float floorY = Physics.Raycast(player.Hitbox.transform.position + Vector3.up, Vector3.down, out RaycastHit fh, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore) ? fh.point.y : float.NaN;
        Note($"swimming: in ink {player.IsInInk}, squid {player.IsSquid}; hitbox {hb.min.y - floorY:F2}..{hb.max.y - floorY:F2}m about the floor, {hb.size.x:F2}m wide; under us: team {UnderUs()}");

        // A damaging shot, and a drip (no damage), each from 6m off at a shallow angle, aimed at the hitbox.
        var log = new List<string>();
        var outcome = new Dictionary<string, (ProjectileManager.EndReason reason, float lost, bool died, int under, bool stillInInk)>();
        int deaths = 0;
        System.Action<PlayerController, ulong, string> onSplat = (pc, k, c) => { if (pc == player) deaths++; };
        PlayerController.Splatted += onSplat;
        foreach ((string what, float damage) in new[] { ("a 40-damage shot", 40f), ("a no-damage filler", 0f), ("a fatal 150 shot", 150f) })
        {
            floor.FillRegion(new Rect(0, 0, 1, 1), player.Team); // back in our own ink each time
            player.Hitbox.ResetHealth();
            yield return Hold(Still, true, 0.3f, "settle");
            Vector3 target = player.Hitbox.transform.position;
            Vector3 from = target + player.transform.forward * 6f + Vector3.up * 1.2f;
            bool hit = false; Vector3 endAt = Vector3.zero; ProjectileManager.EndReason reason = ProjectileManager.EndReason.Air;
            System.Action<ProjectileManager.Shot, Vector3, ProjectileManager.EndReason> onEnd = (s, p, r) => { if (s.ownerId == Shooter) { hit = true; endAt = p; reason = r; } };
            ProjectileManager.Ended += onEnd;
            ProjectileManager.Fire(from, (target - from).normalized * 30f, 10, enemyTeam, true, authoritative: true, ownerId: Shooter, damage: damage, source: "Test",
                                   ballistics: new Ballistics { gravityOverTime = AnimationCurve.Constant(0f, 1f, 0f) }); // straight at it
            float health0 = player.Hitbox.Health;
            int deathsBefore = deaths;
            for (float t = 0f; t < 0.6f && !hit; t += Time.deltaTime) yield return null;
            ProjectileManager.Ended -= onEnd;
            float healthAfter = player.Hitbox.Health;
            bool died = player.IsDead || deaths > deathsBefore;
            int under0 = UnderUs();
            bool inInk0 = player.IsInInk;
            yield return Hold(Still, true, 0.5f, "after");
            int under1 = UnderUs();
            outcome[what] = (reason, health0 - healthAfter, died, under1, player.IsInInk);
            log.Add($"{what}: ended on {reason} {(endAt - target).magnitude:F2}m from the hitbox's middle; health {health0:0} -> {healthAfter:0}{(died ? " (SPLATTED)" : "")}; under us team {under0} (in ink {inInk0}) then {under1} 0.5s later");
            if (player.IsDead) yield return HoldUntil(Still, false, 6f, "respawn", f => !player.IsDead && !player.IsRespawning);
        }
        PlayerController.Splatted -= onSplat;
        foreach (string l in log) Note(l);
        var hurt = outcome["a 40-damage shot"]; var filler = outcome["a no-damage filler"]; var fatal = outcome["a fatal 150 shot"];
        if (hurt.reason != ProjectileManager.EndReason.Target || Mathf.Abs(hurt.lost - 40f) > 0.5f) Fail("a shot at us swimming didn't hit us for its damage");
        if (hurt.under != enemyTeam || hurt.stillInInk) Fail("a shot that hit us swimming didn't ink the spot under us (knocking us out of our ink)");
        if (filler.reason != ProjectileManager.EndReason.Surface || filler.under != enemyTeam) Fail("a no-damage filler didn't carry on through us to ink under us");
        if (!fatal.died) Fail("a fatal shot didn't splat us swimming");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.3f, "surface");
    }
}
