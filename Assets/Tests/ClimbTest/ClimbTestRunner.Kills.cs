using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Kills: the victim's client tells everyone; the killfeed says who did what to whom, the killer
// gets a crossed-out marker where it happened, and the victim bursts into the killer's ink.
public partial class ClimbTestRunner
{
    IEnumerator KillsAndFeed()
    {
        NetGameManager gm = NetGameManager.Instance;
        KillFeed feed = FindFirstObjectByType<KillFeed>(FindObjectsInactive.Include);
        if (feed == null) { Fail("no KillFeed in the HUD"); yield break; }
        var sent = new List<(NetMsg id, object data, ulong target)>();
        NetGameManager.SendOverride = (id, data, target) => sent.Add((id, data, target));
        const ulong FoeId = 3090;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        Vector3 foeSpot = player.transform.position + player.transform.forward * 6f; // in front of us, so the marker's on screen
        SpawnRemote(FoeId, enemyTeam, foeSpot, "Rival");
        player.Hitbox.ResetHealth();
        yield return Hold(Still, false, 0.3f, "stand");
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();

        // How: a main weapon by its kind, subs and specials by name.
        string kinds = string.Join(", ", loadout.Weapons.Select(w => $"{w.DisplayName} {KillFeed.Describe(w.DisplayName, player)}"))
                     + $", {KillFeed.Describe(CurlingBomb.Cause, player)}, {KillFeed.Describe("Sprinkler", player)}, {KillFeed.Describe("Inkstrike", player)}";
        bool kindsRight = loadout.Weapons.All(w => KillFeed.Describe(w.DisplayName, player) == (w is Roller ? "rolled" : w is Blaster ? "blasted" : "splatted"));

        // We splat them (their client tells us): a row, and a marker for us over where they were.
        string ourWeapon = loadout.CurrentWeapon.DisplayName;
        gm.Receive(NetMsg.Kill, new KillData { victimId = FoeId, killerId = player.OwnerId, cause = ourWeapon, position = foeSpot }, FoeId);
        yield return null;
        yield return null;
        string row1 = feed.Rows.LastOrDefault();
        bool marked = feed.Markers.Contains("Rival"), onScreen = feed.MarkerOnScreen;
        string expect1 = $"{player.DisplayName} {KillFeed.Describe(ourWeapon, player)} Rival";

        // They splat us: we tell everyone, burst into their ink where we were; a row, no marker.
        Vector3 ourSpot = player.transform.position;
        int before = TeamAt(ourSpot);
        gm.Receive(NetMsg.Damage, new DamageData { targetSteamId = player.OwnerId, attackerSteamId = FoeId, amount = 250f, fromTeam = enemyTeam, source = CurlingBomb.Cause }, FoeId);
        yield return HoldUntil(Still, false, 0.5f, "dead", f => f.dead);
        yield return Hold(Still, false, 0.3f, "burst");
        KillData told = sent.Where(s => s.id == NetMsg.Kill).Select(s => s.data as KillData).LastOrDefault();
        int inked = TeamAt(ourSpot);
        string row2 = feed.Rows.LastOrDefault();
        int markers = feed.Markers.Count();
        player.Respawn();
        yield return Hold(Still, false, 0.2f, "respawn");
        frames.Clear(); // a teleport

        // Falling: just us.
        player.OnDeath(0, PlayerController.FellCause);
        yield return null;
        string row3 = feed.Rows.LastOrDefault();
        player.Respawn();
        yield return Hold(Still, false, 0.2f, "respawn");
        frames.Clear();

        // A burst of kills: only the latest five stay.
        for (int i = 0; i < 4; i++)
            gm.Receive(NetMsg.Kill, new KillData { victimId = FoeId, killerId = player.OwnerId, cause = "Sprinkler", position = foeSpot }, FoeId);
        yield return null;
        int rows = feed.Rows.Count();

        Note($"kinds: {kinds}; rows: \"{row1}\" (marker {(marked ? onScreen ? "on screen" : "off screen" : "MISSING")}), \"{row2}\", \"{row3}\"; ink at our spot {before} -> {inked}; " +
             $"told everyone: {(told == null ? "NOTHING" : $"{told.victimId} by {told.killerId} with {told.cause}, {Vector3.Distance(told.position, ourSpot):F2}m from where we were")}; {rows} rows after a burst of kills");
        if (!kindsRight || KillFeed.Describe(CurlingBomb.Cause, player) != "bombed" || KillFeed.Describe("Inkstrike", player) != "inkstruck") Fail("the feed doesn't say how by what did it");
        if (row1 != expect1) Fail($"our splat isn't in the feed as \"{expect1}\"");
        if (!marked) Fail("no crossed-out marker for our splat");
        if (!onScreen) Fail("the marker isn't on screen over where they were");
        if (told == null || told.victimId != player.OwnerId || told.killerId != FoeId || told.cause != CurlingBomb.Cause || Vector3.Distance(told.position, ourSpot) > 0.5f)
            Fail("getting splatted didn't tell everyone who, by whom, with what and where");
        if (row2 != $"Rival bombed {player.DisplayName}") Fail("our being splatted isn't in the feed");
        if (markers != 1) Fail("we got a marker for being splatted");
        if (inked != enemyTeam) Fail("we didn't burst into their ink where we were splatted");
        if (row3 != $"{player.DisplayName} fell out of bounds") Fail("a fall isn't in the feed");
        if (rows != 5) Fail("the feed doesn't keep just the latest five");

        Despawn(FoeId);
        NetGameManager.SendOverride = null;
        yield return Hold(Still, false, 0.1f, "cleanup");
    }
}
