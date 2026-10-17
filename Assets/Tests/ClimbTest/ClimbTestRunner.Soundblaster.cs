using System.Collections;
using System.Linq;
using UnityEngine;

// The Soundblaster special: set down in front of us facing where we look; its beam shows at once,
// and after the windup blasts everyone inside it (through walls, across the map) for 33 every 0.2s
// for 4s; nobody outside it, no teammates. Theirs shows here but hurts nobody from here.
public partial class ClimbTestRunner
{
    IEnumerator SoundblasterSpecial()
    {
        NetGameManager gm = NetGameManager.Instance;
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (loadout == null || loadout.SoundblasterPrefab == null) { Fail("no PlayerLoadout or Soundblaster prefab"); yield break; }
        CaptureSends();
        player.ScriptedInput.recenter = true;
        yield return HoldUntil(Still, false, 2f, "level", f => Mathf.Abs(player.CameraPitch) < 0.01f);
        player.ScriptedInput.recenter = false;
        Soundblaster prefab = loadout.SoundblasterPrefab;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
        Vector3 forward = Flat(player.AimForward).normalized, side = Vector3.Cross(Vector3.up, forward);
        Vector3 start = player.transform.position;
        const ulong FoeNear = 4101, FoeFar = 4102, FoeSide = 4103, MateIn = 4104;
        SpawnRemote(FoeNear, enemyTeam, start + forward * 8f, "BlastNear");
        SpawnRemote(FoeFar, enemyTeam, start + forward * 60f, "BlastFar");            // way off past the station: across the map
        SpawnRemote(FoeSide, enemyTeam, start + forward * 10f + side * (prefab.Radius + 1.5f), "BlastSide");
        SpawnRemote(MateIn, player.Team, start + forward * 6f + side * 1f, "BlastMate");
        loadout.SetSpecial(SpecialType.Soundblaster);
        loadout.SetSpecialPoints(1000f);
        yield return Hold(Still, false, 0.3f, "ready");
        sent.Clear();

        if (!loadout.UseSpecial() || loadout.LastSoundblaster == null) { Fail("using it didn't set down a Soundblaster"); loadout.SetSpecial(SpecialType.BubbleShield); NetGameManager.SendOverride = null; yield break; }
        Soundblaster blaster = loadout.LastSoundblaster;
        float ahead = Vector3.Dot(blaster.transform.position - start, forward);
        float facingOff = Vector3.Angle(Flat(blaster.transform.forward), forward);
        bool told = sent.Any(m => m.id == NetMsg.Soundblaster && m.data is SoundblasterData d && Vector3.Distance(d.position, blaster.transform.position) < 0.01f);
        bool spent = loadout.SpecialPoints == 0f && !loadout.SpecialReady;
        yield return null;
        bool beamAtOnce = blaster.BeamShown;
        float DamageTo(ulong id) => sent.Where(m => m.id == NetMsg.Damage && m.to == id).Sum(m => ((DamageData)m.data).amount);
        int HitsTo(ulong id) => sent.Count(m => m.id == NetMsg.Damage && m.to == id);
        yield return Hold(Still, false, prefab.Windup * 0.8f, "windup");
        int earlyHits = HitsTo(FoeNear);
        yield return HoldUntil(Still, false, prefab.Windup + prefab.BlastTime + 1f, "blast", f => blaster == null || blaster.Age > prefab.Windup + prefab.BlastTime + 0.1f);
        int expected = Mathf.RoundToInt(prefab.BlastTime / prefab.TickInterval);
        Note($"set down {ahead:F1}m ahead, facing {facingOff:F0}° off our aim; charge {(spent ? "spent" : "NOT SPENT")}; {(told ? "broadcast" : "NOT BROADCAST")}; beam {(beamAtOnce ? "shown at once" : "NOT SHOWN")}; " +
             $"{earlyHits} hit(s) in the windup; then near {HitsTo(FoeNear)} hits for {DamageTo(FoeNear):0}, 60m away {HitsTo(FoeFar)} for {DamageTo(FoeFar):0}, beside {HitsTo(FoeSide)}, teammate {HitsTo(MateIn)} (expected {expected} x {prefab.TickDamage:0})");
        if (ahead < 0.5f || ahead > 2.5f || facingOff > 2f) Fail("it wasn't set down just in front of us, facing where we look");
        if (!spent || !told || !beamAtOnce) Fail("using it didn't spend the charge, broadcast it and show its beam at once");
        if (earlyHits > 0) Fail("it hit before its windup was over");
        if (HitsTo(FoeNear) != expected || Mathf.Abs(DamageTo(FoeNear) - expected * prefab.TickDamage) > 0.5f) Fail($"the enemy in the beam didn't take {prefab.TickDamage:0} every {prefab.TickInterval}s for {prefab.BlastTime}s");
        if (HitsTo(FoeFar) != expected) Fail("the beam didn't reach across the map");
        if (HitsTo(FoeSide) > 0 || HitsTo(MateIn) > 0) Fail("it hit someone outside the beam (or a teammate)");
        yield return Hold(Still, false, 0.6f, "fade");
        if (blaster != null) Fail("the Soundblaster didn't go away after its blast");

        // Theirs: shown here (speaker and beam), but it hurts nobody from here.
        const ulong Them = 4105;
        SpawnRemote(Them, enemyTeam, start + forward * 4f - side * 3f, "Blaster");
        yield return Hold(Still, false, 0.1f, "them");
        sent.Clear();
        gm.Receive(NetMsg.Soundblaster, new SoundblasterData { ownerId = Them, team = enemyTeam, position = start + forward * 4f - side * 3f, yaw = Quaternion.LookRotation(side).eulerAngles.y }, Them);
        yield return Hold(Still, false, prefab.Windup + 0.5f, "their blast");
        Soundblaster theirs = FindObjectsByType<Soundblaster>(FindObjectsSortMode.None).FirstOrDefault(b => b.Team == enemyTeam);
        bool shown = theirs != null && theirs.BeamShown && theirs.Blasting;
        Note($"theirs: {(shown ? "shown, blasting" : "NOT SHOWN")}; we sent {sent.Count(m => m.id == NetMsg.Damage)} damage message(s)");
        if (!shown) Fail("their Soundblaster wasn't shown");
        if (sent.Any(m => m.id == NetMsg.Damage)) Fail("their Soundblaster hurt someone from here");
        if (theirs != null) Destroy(theirs.gameObject);

        foreach (ulong id in new[] { FoeNear, FoeFar, FoeSide, MateIn, Them }) Despawn(id);
        loadout.SetSpecial(SpecialType.BubbleShield);
        loadout.SetSpecialPoints(0f);
        NetGameManager.SendOverride = null;
        yield return Hold(Still, false, 0.2f, "cleanup");
    }
}
