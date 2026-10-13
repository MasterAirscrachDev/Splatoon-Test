using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The Bucket (a slosher), and the shot lifespan debugging (ShotDebug).
public partial class ClimbTestRunner
{
    static float Elevation(Vector3 v) => Mathf.Asin(Mathf.Clamp(v.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

    // A slosh: a line of droplets with one big blob at the front, landing about as far as the
    // Roller's air flick (its pitch is a tuning choice, noted against the flick's); the blob lands furthest, splats biggest, is easier to hit with
    // and hits much harder than a droplet (once per enemy per slosh); holding keeps sloshing.
    IEnumerator BucketWeapon()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int rollerIndex = WeaponIndexOf(loadout, "Inkroller"), bucketIndex = WeaponIndexOf(loadout, "Bucket");
        if (rollerIndex < 0 || bucketIndex < 0) { Fail("no Inkroller or Bucket in the player's weapons"); yield break; }
        int weaponBefore = loadout.WeaponIndex;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 forward = Flat(player.transform.forward).normalized;
        Vector3 start = player.transform.position;
        CaptureSends();
        var ended = new List<(ProjectileManager.Shot shot, int size, float radius, float age, float distance, Vector3 at, ProjectileManager.EndReason reason)>(); // shots are pooled: copy what's needed
        string source = null;
        Action<ProjectileManager.Shot, Vector3, ProjectileManager.EndReason> onEnd = (s, at, why) => { if (s.source == source && s.authoritative) ended.Add((s, s.splashSize, s.radius, s.age, s.travelled, at, why)); };
        ProjectileManager.Ended += onEnd;

        // Aim level (an earlier scenario may have left the view tilted).
        player.ScriptedInput.recenter = true;
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;

        // The Inkroller's air flick, aimed level, for comparison.
        loadout.SetMainWeapon(rollerIndex);
        yield return Hold(Still, false, 0.3f, "equip");
        var roller = (Roller)loadout.CurrentWeapon;
        player.RefillInk();
        player.PressJump();
        yield return Hold(Still, false, 0.15f, "jump");
        roller.ScriptedFire = true; yield return null; roller.ScriptedFire = false;
        float airReach = 0f;
        Action<ProjectileManager.Shot, Vector3, ProjectileManager.EndReason> onAir = (s, at, why) => { if (s.source == roller.Source && s.authoritative) airReach = Mathf.Max(airReach, Flat(at - start).magnitude); };
        ProjectileManager.Ended += onAir;
        yield return Hold(Still, false, 1.2f, "air flick");
        ProjectileManager.Ended -= onAir;
        float airElevation = roller.LastFlickInAir && roller.LastFlick != null ? roller.LastFlick.Average(Elevation) : float.NaN;

        // A slosh across the empty floor.
        player.PlaceAt(start, player.transform.eulerAngles.y);
        frames.Clear();
        loadout.SetMainWeapon(bucketIndex);
        yield return Hold(Still, false, 0.4f, "equip");
        var bucket = (Bucket)loadout.CurrentWeapon;
        source = bucket.Source;
        ShotDebug.Instance.Clear();
        player.RefillInk();
        sent.Clear();
        ended.Clear();
        bucket.ScriptedFire = true; yield return null; bucket.ScriptedFire = false;
        float inkSpent = 1f - player.InkLevel; // paid as the slosh starts (before it refills)
        yield return Hold(Still, false, 1.5f, "slosh");
        Vector3[] launch = bucket.LastSlosh ?? new Vector3[0];
        int shots = launch.Length;
        Vector3 blobLaunch = shots > 0 ? launch[shots - 1] : Vector3.zero;
        bool blobFastest = shots > 1 && launch.Take(shots - 1).All(v => v.magnitude < blobLaunch.magnitude);
        float yawSpread = shots > 0 ? launch.Max(v => Vector3.SignedAngle(forward, Flat(v), Vector3.up)) - launch.Min(v => Vector3.SignedAngle(forward, Flat(v), Vector3.up)) : 0f;
        var blob = ended.Where(e => e.size == bucket.BlobSplat).ToList();
        var drops = ended.Where(e => e.size == bucket.DropletSplat).ToList();
        float blobReach = blob.Count > 0 ? Flat(blob[0].at - start).magnitude : 0f;
        float dropReach = drops.Count > 0 ? drops.Max(e => Flat(e.at - start).magnitude) : 0f;
        float blobRadius = blob.Count > 0 ? blob[0].radius : 0f, dropRadius = drops.Count > 0 ? drops[0].radius : 0f;
        var broadcast = sent.Where(m => m.id == NetMsg.ProjectileSpawn).Select(m => (ProjectileSpawnData)m.data).LastOrDefault();
        bool blobSent = broadcast != null && broadcast.splashSizes.Length == shots && broadcast.splashSizes.Last() == bucket.BlobSplat;
        Note($"slosh: {shots} shots ({bucket.Droplets} droplets + blob) in a line {yawSpread:F1}° wide, blob {(blobFastest ? "fastest" : "NOT fastest")} at {blobLaunch.magnitude:F1} m/s, launched {Elevation(blobLaunch):F1}° up (the air flick: {airElevation:F1}°); " +
             $"blob landed {blobReach:F1}m out (droplets up to {dropReach:F1}m; the air flick {airReach:F1}m), splat {bucket.BlobSplat} vs {bucket.DropletSplat}, hit radius {blobRadius:F2} vs {dropRadius:F2}; ink {inkSpent:P0}; broadcast {(blobSent ? "with the blob last" : "WRONG")}");
        if (shots != bucket.Droplets + 1 || !blobFastest) Fail("a slosh isn't its droplets then a faster blob");
        if (yawSpread > 2f * (bucket.Spread + bucket.Sway) + 1f) Fail("the slosh isn't a narrow line");
        if (blob.Count != 1 || blobReach <= dropReach) Fail("the blob didn't land out in front of the droplets");
        if (blob.Count == 1 && (blob[0].reason != ProjectileManager.EndReason.Surface || Mathf.Abs(blobReach - airReach) > airReach * 0.35f)) Fail("the blob doesn't land on the floor at about the air flick's reach");
        if (bucket.BlobSplat <= bucket.DropletSplat || blobRadius <= dropRadius * 1.3f || bucket.BlobSplat * bucket.BlobScale < bucket.DropletSplat * bucket.DropletScale * 2f) Fail("the blob isn't bigger (splat, hit radius, look)");
        if (Mathf.Abs(inkSpent - bucket.SloshInkCost * 0.001f) > 0.005f) Fail("a slosh didn't cost its ink");
        if (!blobSent) Fail("the slosh wasn't broadcast with its blob");

        // The shot debugger saw them: their ages, distances and landing damage.
        var seen = ShotDebug.Instance.Of(source).ToList();
        bool agesMatch = seen.Count == ended.Count && seen.Zip(ended, (r, e) => Mathf.Abs(r.age - e.age) < 1e-4f).All(x => x);
        var blobRecord = blob.Count > 0 ? seen.FirstOrDefault(r => Mathf.Abs(r.age - blob[0].age) < 1e-4f && Mathf.Abs(r.distance - blob[0].distance) < 1e-3f) : default;
        bool scaleRight = blob.Count > 0 && blobRecord.source == source && Mathf.Abs(blobRecord.scale - bucket.BlobDamage.Scale(blob[0].age)) < 1e-4f;
        Note($"shot debug: {ShotDebug.Summary(source, seen)}");
        if (!agesMatch) Fail("the shot debugger didn't record every slosh shot's lifespan");
        if (!scaleRight) Fail("the shot debugger's landing damage or distance is wrong");

        // An enemy where the blob lands: one hit, the blob's, harder than the air flick.
        const ulong Foe = 3401;
        Vector3 foeAt = new Vector3(start.x, station.position.y + 0.05f, start.z) + forward * Mathf.Max(2f, blobReach - 0.4f);
        SpawnRemote(Foe, enemyTeam, foeAt, "BucketFoe");
        yield return Hold(Still, false, 0.3f, "foe");
        player.RefillInk();
        sent.Clear();
        ended.Clear();
        bucket.ScriptedFire = true; yield return null; bucket.ScriptedFire = false;
        yield return Hold(Still, false, 1.2f, "slosh at foe");
        var hits = sent.Where(m => m.id == NetMsg.Damage && m.to == Foe).Select(m => ((DamageData)m.data).amount).ToList();
        var blobHit = ended.FirstOrDefault(e => e.size == bucket.BlobSplat);
        bool blobDidIt = blobHit.size == bucket.BlobSplat && blobHit.reason == ProjectileManager.EndReason.Target;
        float dropletMax = bucket.DropletDamage.damage;
        Note($"enemy {Flat(foeAt - start).magnitude:F1}m away: {hits.Count} hit(s) for {hits.Sum():0} ({(blobDidIt ? $"the blob, {blobHit.age:F2}s out" : "NOT the blob")}); a droplet hits for at most {dropletMax:0}");
        if (hits.Count != 1) Fail("the enemy wasn't hit exactly once by the slosh");
        else if (!blobDidIt || hits[0] <= dropletMax * 1.5f) Fail("the blob didn't land the hit, much harder than a droplet");
        Despawn(Foe);
        yield return Hold(Still, false, 0.3f, "despawn");

        // Held: it keeps sloshing, a slosh every sloshRate.
        player.RefillInk();
        int before = bucket.Sloshes;
        bucket.ScriptedFire = true;
        yield return Hold(Still, false, bucket.SloshRate * 2.5f, "held");
        bucket.ScriptedFire = false;
        int held = bucket.Sloshes - before;
        Note($"held for {bucket.SloshRate * 2.5f:F2}s: {held} sloshes (one every {bucket.SloshRate:F2}s)");
        if (held != 3) Fail("holding didn't keep sloshing at its rate");
        yield return Hold(Still, false, 1f, "settle");

        ProjectileManager.Ended -= onEnd;
        loadout.SetMainWeapon(weaponBefore);
        player.PlaceAt(start, player.transform.eulerAngles.y);
        frames.Clear();
        yield return Hold(Still, false, 0.3f, "cleanup");
        NetGameManager.SendOverride = null;
    }

    // Every weapon fired level across the floor: how long its shots really fly, against where its
    // damage falloff ends (the debug overlay's numbers, F10).
    IEnumerator ShotLifespans()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int weaponBefore = loadout.WeaponIndex;
        Vector3 start = player.transform.position;
        CaptureSends();
        ShotDebug debug = ShotDebug.Instance;
        if (debug == null) { Fail("no ShotDebug"); yield break; }
        debug.Clear();
        player.ScriptedInput.recenter = true; // aim level
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;
        var lines = new List<string>();
        for (int i = 0; i < loadout.Weapons.Count; i++)
        {
            player.PlaceAt(start, player.transform.eulerAngles.y);
            frames.Clear();
            loadout.SetMainWeapon(i);
            yield return Hold(Still, false, 0.4f, "equip");
            Weapon w = loadout.CurrentWeapon;
            void Trigger(bool on)
            {
                switch (w)
                {
                    case WeaponShooter s: s.ScriptedFire = on; break;
                    case Blaster b: b.ScriptedFire = on; break;
                    case Roller r: r.ScriptedFire = on; break;
                    case Brush b: b.ScriptedFire = on; break;
                    case Bucket b: b.ScriptedFire = on; break;
                }
            }
            player.RefillInk();
            Trigger(true);
            yield return Hold(Still, false, w is Roller || w is Brush ? 0.05f : 0.6f, "fire"); // a single flick (holding a roller rolls)
            Trigger(false);
            yield return Hold(Still, false, 2.5f, "landing");
            var list = debug.Of(w.DisplayName).ToList();
            lines.Add(ShotDebug.Summary(w.DisplayName, list));
            if (list.Count == 0) Fail($"{w.DisplayName}: no shot lifespans recorded");
        }
        Note(string.Join("; ", lines));
        loadout.SetMainWeapon(weaponBefore);
        player.PlaceAt(start, player.transform.eulerAngles.y);
        frames.Clear();
        yield return Hold(Still, false, 0.3f, "cleanup");
        NetGameManager.SendOverride = null;
    }
}
