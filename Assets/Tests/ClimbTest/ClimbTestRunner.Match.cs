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

// ClimbTestRunner scenarios: lobby, roster, colours, match flow, HUD and ink sync.
public partial class ClimbTestRunner
{

    IEnumerator TeamColourPairs()
    {
        NetGameManager gm = NetGameManager.Instance;
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        Material ink = floor.GetComponent<Renderer>().material;
        int start = gm.ColourPair;
        Note($"{gm.ColourPairCount} colour pairs; this load picked {start} ({gm.ColourPairName})");
        if (gm.ColourPairCount < 5) Fail("expected a set of colour pairs");
        if (start < 0 || start >= gm.ColourPairCount) Fail("no colour pair picked on load");

        // The host's roster carries its pick; everything recolours.
        int other = (start + 1) % gm.ColourPairCount;
        int changes = 0;
        System.Action counted = () => changes++;
        NetGameManager.TeamColoursChanged += counted;
        gm.Receive(NetMsg.TeamAssign, new TeamAssignData { ids = new ulong[0], roles = new int[0], colourPair = other }, 0);
        yield return Hold(Still, false, 0.2f, "colours");
        NetGameManager.TeamColoursChanged -= counted;
        bool inkOk = ink.GetColor("_AlphaColor") == gm.AlphaTeam && ink.GetColor("_BetaColor") == gm.BetaTeam;
        Color own = player.Team == 2 ? gm.BetaTeam : gm.AlphaTeam;
        bool hudOk = hud == null || hud.AlphaBarColour == gm.AlphaTeam;
        Note($"after the host's roster: pair {gm.ColourPair} ({gm.ColourPairName}), {changes} change event(s); ink {(inkOk ? "recoloured" : "stale")}, HUD bar {(hudOk ? "recoloured" : "stale")}, our colour {own}");
        if (gm.ColourPair != other || changes != 1) Fail("the host's colour pick wasn't applied (once)");
        if (!inkOk) Fail("ink surfaces kept the old colours");
        if (!hudOk) Fail("the turf bar kept the old colours");
        if (player.TeamColour != own) Fail("our player kept the old colour");

        gm.SetColourPair(start);
        yield return Hold(Still, false, 0.1f, "colours");
    }

    // Drives the host menu through its real buttons.
    IEnumerator HostMenuTeams()
    {
        HostMenu menu = FindFirstObjectByType<HostMenu>();
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        if (menu == null || hud == null) { Fail("no HostMenu/MatchHUD in the scene"); yield break; }
        NetGameManager gm = NetGameManager.Instance;
        const ulong RemoteId = 3003;
        var sent = new List<(NetMsg id, object data, ulong target)>();
        NetGameManager.SendOverride = (id, data, target) => sent.Add((id, data, target));

        if (hud.ShowPercentages) Fail("coverage percentages are shown by default");

        Vector3 remotePos = station.TransformPoint(new Vector3(6f, 0.05f, 6f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = RemoteId, team = 2, position = remotePos, playerName = "Benchwarmer" }, RemoteId);
        yield return Hold(Still, false, 0.1f, "menu");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = RemoteId, position = remotePos, moveDir = Vector2.zero, team = 2 }, RemoteId);
        yield return Hold(Still, false, 0.1f, "menu");
        PlayerController remote = gm.GetPlayer(RemoteId);

        menu.Toggle();
        if (!menu.IsOpen || !InputGate.Blocked) Fail("menu didn't open (or didn't block gameplay input)");

        menu.PercentButton.onClick.Invoke();
        if (!hud.ShowPercentages) Fail("Toggle percentages didn't show them");
        menu.PercentButton.onClick.Invoke();
        if (hud.ShowPercentages) Fail("Toggle percentages didn't hide them again");
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        loadout.SetSpecialPoints(0f);
        menu.FillSpecialButton.onClick.Invoke();
        if (!loadout.SpecialReady) Fail("Fill special didn't fill it");
        if (menu.IsOpen) Fail("menu stayed open after filling the special");
        loadout.SetSpecialPoints(0f);
        menu.SetOpen(true); // carry on with the rest of the menu

        menu.TeamsButton.onClick.Invoke();
        yield return Hold(Still, false, 0.05f, "menu");
        HostMenu.SlotButton[][] cols = menu.Columns; // Alpha, Beta, Spectate, Not playing
        if (!menu.TeamEditorOpen) { Fail("team editor didn't open"); NetGameManager.SendOverride = null; yield break; }
        if (!cols[0][0].label.text.StartsWith(player.DisplayName) || cols[1][0].label.text != "Benchwarmer")
            Fail($"editor layout wrong: alpha0 \"{cols[0][0].label.text}\", beta0 \"{cols[1][0].label.text}\"");

        void Move(int fromCol, int fromSlot, int toCol, int toSlot)
        {
            cols[fromCol][fromSlot].button.onClick.Invoke();
            cols[toCol][toSlot].button.onClick.Invoke();
        }

        // Enemy to Spectate: hidden, off the HUD, roster broadcast.
        Move(1, 0, 2, 0);
        yield return Hold(Still, false, 0.1f, "menu");
        if (remote.Role != PlayerRole.Spectator || remote.transform.root.gameObject.activeSelf) Fail("spectator wasn't benched/hidden");
        if (hud.BetaSlots[0].playerName.text != "") Fail("spectator still shown on the HUD");
        bool broadcast = sent.Any(m => m.id == NetMsg.TeamAssign && m.data is TeamAssignData d
                                       && System.Array.IndexOf(d.ids, RemoteId) is int k && k >= 0 && d.roles[k] == (int)PlayerRole.Spectator);
        if (!broadcast) Fail("no TeamAssign broadcast for the change");

        // Us to Beta: team changes, HUD follows, we respawn.
        Move(0, 0, 1, 0);
        yield return Hold(Still, false, 0.2f, "menu");
        if (player.Team != 2 || hud.BetaSlots[0].playerName.text != player.DisplayName) Fail($"moving us to Beta failed (team {player.Team})");

        // Us to Not playing, then back to Alpha.
        Move(1, 0, 3, 0);
        yield return Hold(Still, false, 0.1f, "menu");
        if (player.transform.root.gameObject.activeSelf || player.Role != PlayerRole.NotPlaying) Fail("benching ourselves didn't hide our player");
        Move(3, 0, 0, 0);
        yield return Hold(Still, false, 0.3f, "menu");
        if (!player.transform.root.gameObject.activeSelf || player.Team != 1) Fail("returning to Alpha didn't restore our player");

        menu.CloseButton.onClick.Invoke();
        if (menu.IsOpen || InputGate.Blocked) Fail("menu didn't close (or left input blocked)");

        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = RemoteId }, RemoteId);
        yield return Hold(Still, false, 0.1f, "menu");
        NetGameManager.SendOverride = null;
    }

    IEnumerator SplatBatching()
    {
        NetGameManager gm = NetGameManager.Instance;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.2f, "clear");
        CaptureSends();
        List<SplatBatchData> Batches() => sent.Where(m => m.id == NetMsg.Splat).Select(m => m.data as SplatBatchData).ToList();

        // Three in one frame: one message carrying all three.
        for (int i = 0; i < 3; i++) floor.Splat(new Vector2(0.2f + 0.1f * i, 0.2f), 8, player.Team);
        yield return null; yield return null;
        var three = Batches();
        // A hundred in one frame: split 64 + 36.
        sent.Clear();
        for (int i = 0; i < 100; i++) floor.Splat(new Vector2(0.1f + 0.008f * i, 0.8f), 4, player.Team);
        yield return null; yield return null;
        var hundred = Batches();
        Note($"3 splats -> {three.Count} message(s) of {string.Join("+", three.Select(b => b?.surfaceIds?.Length ?? -1))}; 100 -> {hundred.Count} message(s) of {string.Join("+", hundred.Select(b => b?.surfaceIds?.Length ?? -1))}");
        if (three.Count != 1 || three[0] == null || three[0].surfaceIds.Length != 3) Fail("splats in one frame didn't go out as one batch");
        if (hundred.Count != 2 || hundred.Sum(b => b.surfaceIds.Length) != 100 || hundred.Max(b => b.surfaceIds.Length) > 64) Fail("a big batch wasn't split into messages of at most 64");

        // Theirs: every splat in a received batch lands here, and isn't sent on.
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        yield return Hold(Still, false, 0.1f, "clear");
        int enemyTeam = player.Team == 1 ? 2 : 1;
        var spots = new[] { new Vector2(0.3f, 0.3f), new Vector2(0.6f, 0.4f), new Vector2(0.45f, 0.7f) };
        sent.Clear();
        gm.Receive(NetMsg.Splat, new SplatBatchData
        {
            surfaceIds = spots.Select(_ => (ushort)floor.SurfaceId).ToArray(),
            uvs = spots.SelectMany(v => new[] { SplatBatchData.PackUV(v.x), SplatBatchData.PackUV(v.y) }).ToArray(),
            splashSizes = spots.Select(_ => (byte)10).ToArray(),
            teams = spots.Select(_ => (byte)enemyTeam).ToArray(),
        }, 3401);
        yield return Hold(Still, false, 0.2f, "theirs");
        int painted = spots.Count(v => floor.getSurfaceTeam(v) == enemyTeam);
        Note($"their batch of {spots.Length}: {painted} painted here; we sent {sent.Count(m => m.id == NetMsg.Splat)} splat message(s)");
        if (painted != spots.Length) Fail("a received batch didn't paint every splat");
        if (sent.Any(m => m.id == NetMsg.Splat)) Fail("their splats were sent on");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }

    IEnumerator LobbyMenuList()
    {
        LobbyMenu menu = FindFirstObjectByType<LobbyMenu>();
        if (menu == null) { Fail("no LobbyMenu in the scene"); yield break; }
        yield return Hold(Still, false, 0.1f, "menu");
        if (menu.IsShown) Fail("shown in dev mode");

        menu.ForceShow = true;
        yield return Hold(Still, false, 0.1f, "menu");
        if (!menu.IsShown) Fail("didn't show when forced");
        NetGameManager.LobbyInfo L(string host, int n) => new NetGameManager.LobbyInfo { host = host, players = n, maxPlayers = 8 };
        menu.ShowLobbies(new[] { L("Alpha Tester", 3), L("Beta Tester", 1), L("Gamma Tester", 7) });
        var active = menu.Rows.Where(r => r.gameObject.activeSelf).ToList();
        if (active.Count != 3 || active[1].hostName.text != "Beta Tester" || active[2].players.text != "7/8" || menu.EmptyShown)
            Fail($"rows don't match the lobbies ({active.Count} shown)");
        menu.ShowLobbies(new[] { L("Solo", 1) });
        if (menu.Rows.Count(r => r.gameObject.activeSelf) != 1) Fail("rows weren't reused/hidden for a shorter list");

        // No Steam in dev mode, so joining fails: reported, then the list refreshes (to empty here).
        menu.ForceShow = false; // let it run its own logic for the click
        menu.Rows.First(r => r.gameObject.activeSelf).join.onClick.Invoke();
        Note($"after a failed join: \"{menu.Status}\"");
        if (!menu.EmptyShown || menu.Rows.Any(r => r.gameObject.activeSelf)) Fail("failed join didn't refresh the list");
        yield return Hold(Still, false, 0.1f, "menu");
        if (menu.IsShown) Fail("still shown after un-forcing in dev mode");
        Cursor.lockState = CursorLockMode.Locked;
    }

    IEnumerator MatchFlow()
    {
        NetGameManager gm = NetGameManager.Instance;
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        MatchFlowHUD flow = FindFirstObjectByType<MatchFlowHUD>();
        HostMenu menu = FindFirstObjectByType<HostMenu>();
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (hud == null || flow == null || menu == null || loadout == null) { Fail("missing MatchHUD, MatchFlowHUD, HostMenu or PlayerLoadout"); yield break; }
        float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
        bool Broadcast(MatchPhase phase) => sent.Any(m => m.id == NetMsg.MatchEvent && ((MatchEventData)m.data).phase == phase);
        CaptureSends();
        gm.SetMatchTimings(1f, 3f, 1.5f, 0.6f, 5.5f);
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), player.Team);
        loadout.SetSpecialPoints(600f);
        hud.ShowPercentages = true;
        yield return Hold(Still, false, 0.3f, "free");

        // Start: everyone to their spawn, locked, map and loadout reset, host features off; new colours.
        int pairBefore = gm.ColourPair;
        gm.StartGame();
        frames.Clear(); // the respawn teleport
        Vector3 start = player.transform.position;
        if (gm.Phase != MatchPhase.Countdown || !Broadcast(MatchPhase.Countdown)) Fail("countdown didn't start (or wasn't broadcast)");
        bool pairSent = sent.Any(m => m.id == NetMsg.MatchEvent && m.data is MatchEventData d && d.phase == MatchPhase.Countdown && d.colourPair == gm.ColourPair);
        Note($"colours {pairBefore} -> {gm.ColourPair} ({gm.ColourPairName}) for the new game");
        if (gm.ColourPair == pairBefore || !pairSent) Fail("a new game didn't pick (and send) new team colours");
        yield return Hold(Fwd, true, 0.5f, "countdown");
        Note($"countdown banner \"{flow.BannerText}\"");
        if (flow.BannerText != "1") Fail("countdown number not shown");
        if (!InputGate.MatchLocked || Flat(player.transform.position - start) > 0.05f || player.IsSquid) Fail("could move or swim during the countdown");
        if (loadout.SpecialPoints != 0f || player.InkLevel < 0.999f) Fail("special/ink not reset for the match");
        if (floor.getSurfaceTeam(new Vector2(0.5f, 0.5f)) != 0) Fail("map wasn't reset");
        if (hud.PercentagesShown) Fail("percentages shown during a match");
        menu.SetOpen(true);
        yield return null;
        if (menu.ResetButton.interactable || menu.TeamsButton.interactable || menu.PercentButton.interactable || menu.StartButtonText != "End match")
            Fail("host menu still offers free-roam features mid-match");
        menu.SetOpen(false);

        // Playing: GO!, free to move.
        yield return HoldUntil(Still, false, 2f, "play", f => gm.Phase == MatchPhase.Playing);
        yield return null; // the HUD may have updated before the phase changed that frame
        if (flow.BannerText != "GO!" || InputGate.MatchLocked) Fail("no GO! (or still locked) when play starts");
        Vector3 before = player.transform.position;
        yield return Hold(Fwd, false, 0.4f, "play");
        if (Flat(player.transform.position - before) < 0.3f) Fail("can't move once playing");
        floor.FillRegion(new Rect(0, 0, 1, 0.6f), player.Team); // our win

        // Final stretch: no banner (music will announce it), and the coverage bar fades out.
        bool stretchEvent = false;
        Action onStretch = () => stretchEvent = true;
        NetGameManager.FinalStretchStarted += onStretch;
        yield return HoldUntil(Still, false, 3f, "play", f => gm.InFinalStretch);
        yield return Hold(Still, false, 0.1f, "final");
        NetGameManager.FinalStretchStarted -= onStretch;
        if (!stretchEvent) Fail("FinalStretchStarted wasn't raised (the music hook)");
        if (flow.BannerText != "") Fail($"final stretch shows a banner (\"{flow.BannerText}\")");
        yield return Hold(Still, false, 1.1f, "final");
        Note($"turf bar alpha {hud.TurfBarAlpha:F2} 1.2s into the final stretch");
        if (hud.TurfBarAlpha > 0.05f) Fail("coverage bar didn't fade out for the final stretch");

        // Time's up: locked again.
        yield return HoldUntil(Still, false, 2f, "timesup", f => gm.Phase == MatchPhase.TimesUp);
        yield return Hold(Fwd, false, 0.2f, "timesup");
        if (flow.BannerText != "TIME'S UP!" || !InputGate.MatchLocked) Fail("no TIME'S UP (or not locked)");

        // Results: the host's score for everyone, overhead view, suspense fill, then the real split.
        yield return HoldUntil(Still, false, 4f, "results", f => gm.Phase == MatchPhase.Results);
        Vector3Int r = gm.MatchResult;
        Note($"result alpha {r.x}, beta {r.y}, neutral {r.z}");
        var resultMsg = sent.LastOrDefault(m => m.id == NetMsg.MatchEvent && ((MatchEventData)m.data).phase == MatchPhase.Results);
        if (resultMsg.data == null || ((MatchEventData)resultMsg.data).alphaScore != r.x) Fail("results weren't broadcast with the score");
        int ours = player.Team == 1 ? r.x : r.y, theirs = player.Team == 1 ? r.y : r.x;
        if (ours <= theirs) Fail("our painted turf didn't win");
        yield return Hold(Still, false, 0.3f, "results");
        if (!flow.OverheadActive) Fail("no overhead view");
        yield return Hold(Still, false, 2.6f, "results"); // into the pause
        float alphaShare = r.x / (float)(r.x + r.y);
        float suspense = Mathf.Max(0.2f, 0.8f * Mathf.Min(alphaShare, 1f - alphaShare));
        Note($"suspense fill {flow.ResultAlphaShown:F2}/{flow.ResultBetaShown:F2} (expected {suspense:F2} each)");
        if (Mathf.Abs(flow.ResultAlphaShown - suspense) > 0.01f || Mathf.Abs(flow.ResultBetaShown - suspense) > 0.01f) Fail("bar didn't hold both sides at the suspense point");
        if (flow.ResultsRevealed) Fail("revealed before the pause ended");
        yield return Hold(Still, false, 1f, "results");
        string winner = player.Team == 1 ? "ALPHA TEAM WINS!" : "BETA TEAM WINS!";
        Note($"revealed {flow.ResultAlphaShown:F3}/{flow.ResultBetaShown:F3}, banner \"{flow.BannerText}\"");
        if (Mathf.Abs(flow.ResultAlphaShown - alphaShare) > 0.001f || Mathf.Abs(flow.ResultBetaShown - (1f - alphaShare)) > 0.001f) Fail("bar didn't settle on the real split");
        if (flow.BannerText != winner) Fail("winner not announced");

        // Back to free roam: unlocked, player view, bar and the host's percentages back.
        yield return HoldUntil(Still, false, 3f, "free", f => gm.Phase == MatchPhase.FreeRoam);
        yield return Hold(Still, false, 0.8f, "free");
        if (InputGate.MatchLocked || flow.OverheadActive) Fail("still locked (or overhead) after the results");
        if (hud.TurfBarAlpha < 0.95f || !hud.PercentagesShown) Fail("coverage bar or percentages didn't come back");

        // The host can abandon a match.
        gm.StartGame();
        frames.Clear();
        yield return Hold(Still, false, 0.2f, "abort");
        gm.EndMatch();
        yield return Hold(Still, false, 0.1f, "abort");
        if (gm.Phase != MatchPhase.FreeRoam || InputGate.MatchLocked) Fail("End match didn't return to free roam");

        hud.ShowPercentages = false;
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        gm.SetMatchTimings(3f, 180f, 60f, 2.5f, 15f);
        NetGameManager.SendOverride = null;
    }

    IEnumerator LateJoin()
    {
        NetGameManager gm = NetGameManager.Instance;
        const ulong LateId = 3020;
        CaptureSends();
        gm.SetMatchTimings(0.3f, 60f, 30f, 0.5f, 2f);
        gm.StartGame();
        frames.Clear();
        yield return HoldUntil(Still, false, 2f, "match", f => gm.Phase == MatchPhase.Playing);
        sent.Clear();

        SpawnRemote(LateId, 2, station.TransformPoint(new Vector3(-9f, 0.05f, 8f)), "Latecomer");
        yield return Hold(Still, false, 0.2f, "join");
        PlayerController late = gm.GetPlayer(LateId);
        if (late == null) { Fail("late joiner didn't spawn"); gm.EndMatch(); NetGameManager.SendOverride = null; yield break; }
        bool rosterSaysSpectate = sent.Any(m => m.id == NetMsg.TeamAssign && m.data is TeamAssignData t
            && Array.IndexOf(t.ids, LateId) is int i && i >= 0 && t.roles[i] == (int)PlayerRole.Spectator);
        var phaseMsg = sent.FirstOrDefault(m => m.id == NetMsg.MatchEvent && m.to == LateId);
        var phaseData = phaseMsg.data as MatchEventData;
        Note($"late joiner role {late.Role}; told phase {phaseData?.phase} with {phaseData?.duration:F1}s left");
        if (late.Role != PlayerRole.Spectator || !rosterSaysSpectate) Fail("late joiner wasn't made a spectator");
        if (phaseData == null || phaseData.phase != MatchPhase.Playing || !phaseData.lateJoin || Mathf.Abs(phaseData.duration - gm.PhaseTimeRemaining) > 0.5f)
            Fail("late joiner wasn't told the current phase and time left");
        if (late.Team != 0) Fail("a spectator still has a team");

        // The match ends: back to the team they joined on.
        sent.Clear();
        gm.EndMatch();
        yield return Hold(Still, false, 0.2f, "after");
        if (late.Role != PlayerRole.Beta || !sent.Any(m => m.id == NetMsg.TeamAssign)) Fail($"late joiner didn't get their team back ({late.Role})");
        if (gm.IsLateJoiner(LateId)) Fail("still marked as a late joiner");

        Despawn(LateId);
        yield return Hold(Still, false, 0.1f, "after");
        gm.SetMatchTimings(3f, 180f, 60f, 2.5f, 15f);
        NetGameManager.SendOverride = null;
    }

    IEnumerator SpectatorView()
    {
        NetGameManager gm = NetGameManager.Instance;
        MatchFlowHUD flow = FindFirstObjectByType<MatchFlowHUD>();
        if (flow == null) { Fail("no MatchFlowHUD"); yield break; }
        NetGameManager.SendOverride = (id, data, target) => { };
        int team = player.Team;
        if (flow.OverheadActive) Fail("overhead view while playing");

        // The host moves us to spectate: our player goes away, the overhead view takes over.
        gm.Receive(NetMsg.TeamAssign, new TeamAssignData { ids = new[] { player.OwnerId }, roles = new[] { (int)PlayerRole.Spectator } }, 0);
        yield return Hold(Still, false, 0.2f, "spectate");
        if (player.gameObject.activeInHierarchy) Fail("our player is still in the level");
        if (!flow.OverheadActive || !flow.SpectatingShown) Fail("no overhead view (or label) while spectating");

        gm.Receive(NetMsg.TeamAssign, new TeamAssignData { ids = new[] { player.OwnerId }, roles = new[] { team } }, 0);
        yield return Hold(Still, false, 0.2f, "back");
        frames.Clear(); // back at our spawn
        if (!player.gameObject.activeInHierarchy || flow.OverheadActive || flow.SpectatingShown) Fail("overhead view didn't hand back to our player");
        NetGameManager.SendOverride = null;
    }

    // Runs last: starting the match clears all ink.
    IEnumerator Hud()
    {
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        if (hud == null) { Fail("no MatchHUD in the scene"); yield break; }
        NetGameManager gm = NetGameManager.Instance;
        const ulong RemoteId = 2002;
        int enemyTeam = player.Team == 1 ? 2 : 1;
        NetGameManager.SendOverride = (id, data, target) => { };
        MatchHUD.PlayerSlot[] own   = player.Team == 1 ? hud.AlphaSlots : hud.BetaSlots;
        MatchHUD.PlayerSlot[] enemy = player.Team == 1 ? hud.BetaSlots : hud.AlphaSlots;
        yield return Hold(Still, false, 0.1f, "roster");

        // Rosters: us in our team's first slot (highlighted), the enemy side empty.
        if (own[0].playerName.text != player.DisplayName || !own[0].localMark.enabled)
            Fail($"local player not shown in our first slot (\"{own[0].playerName.text}\", highlighted {own[0].localMark.enabled})");
        if (enemy.Any(s => s.playerName.text != "")) Fail("enemy slots aren't empty before anyone joins");

        // An enemy joins, dies, then leaves.
        Vector3 remotePos = station.TransformPoint(new Vector3(6f, 0.05f, 6f));
        gm.Receive(NetMsg.PlayerSpawn, new PlayerSpawnData { steamId = RemoteId, team = enemyTeam, position = remotePos, playerName = "Rival" }, RemoteId);
        yield return Hold(Still, false, 0.1f, "roster");
        if (enemy[0].playerName.text != "Rival" || enemy[0].initial.text != "R" || enemy[0].localMark.enabled)
            Fail($"joined enemy not shown correctly (\"{enemy[0].playerName.text}\"/\"{enemy[0].initial.text}\")");
        gm.Receive(NetMsg.PlayerState, new PlayerStateData { steamId = RemoteId, position = remotePos, moveDir = Vector2.zero, team = enemyTeam, dead = true }, RemoteId);
        yield return Hold(Still, false, 0.1f, "roster");
        if (!enemy[0].deadMark.activeSelf) Fail("dead enemy isn't marked");
        gm.Receive(NetMsg.PlayerDespawn, new PlayerSpawnData { steamId = RemoteId }, RemoteId);
        yield return Hold(Still, false, 0.1f, "roster");
        if (enemy[0].playerName.text != "") Fail("enemy slot not cleared after they left");

        // Timer: full duration before the match, counting down after it starts.
        string before = hud.TimerText;
        gm.SetMatchTimings(0.3f, 180f, 60f, 2.5f, 15f); // short countdown
        gm.StartGame();
        frames.Clear(); // starting a match respawns us at our spawn
        yield return Hold(Still, false, 1.5f, "timer");
        string after = hud.TimerText;
        Note($"timer {before} -> {after} after 1.2s");
        if (gm.Phase != MatchPhase.Playing) Fail("match didn't start");
        if (after == before) Fail($"timer isn't counting down ({before} -> {after})");

        // Turf bar: everything neutral after the reset, then our share grows as we paint.
        yield return Hold(Still, false, 1.2f, "turf");
        float ourShare() => player.Team == 1 ? hud.AlphaShare : hud.BetaShare;
        if (ourShare() > 0.0005f) Fail($"coverage not reset by the match start ({ourShare():P1})");
        for (int i = 0; i < 12; i++)
        {
            Vector3 p = station.TransformPoint(new Vector3(-9f + (i % 6) * 3.5f, 3f, -4f - (i / 6) * 3.5f));
            ProjectileManager.Fire(projectilePrefab, p, Vector3.down * 8f, 30, player.Team, true, authoritative: true, ownerId: player.OwnerId);
        }
        yield return Hold(Still, false, 2.5f, "turf");
        Note($"our coverage {ourShare():P2}, bar fill {(player.Team == 1 ? hud.ShownAlphaFill : 0f):F4}");
        if (ourShare() <= 0f) Fail("turf bar didn't pick up the painted floor");
        if (player.Team == 1 && hud.ShownAlphaFill <= 0f) Fail("alpha bar didn't move");

        gm.EndMatch();
        gm.SetMatchTimings(3f, 180f, 60f, 2.5f, 15f);
        NetGameManager.SendOverride = null;
    }

    IEnumerator SeamBridging()
    {
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        NetGameManager.SendOverride = (id, data, target) => { };
        Vector3 right = station.TransformDirection(Vector3.right), forward = station.TransformDirection(Vector3.forward);
        Vector3 origin = station.TransformPoint(new Vector3(0f, 8f, 0f)); // clear air over the lobby
        SurfaceInkManager Make(string name, Vector3 at)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Plane); // 10 x 10m, a concave mesh collider
            go.name = name;
            go.layer = floor.gameObject.layer;
            go.transform.SetPositionAndRotation(at, station.rotation);
            go.GetComponent<Renderer>().sharedMaterial = floor.GetComponent<Renderer>().sharedMaterial;
            return go.AddComponent<SurfaceInkManager>();
        }
        SurfaceInkManager a = Make("SeamA", origin), b = Make("SeamB", origin + right * 10f); // they meet at origin + right * 5
        Physics.SyncTransforms();
        for (int i = 0; i < 3; i++) yield return null; // their Start
        RaycastHit Down(Vector3 at, SurfaceInkManager on) { on.GetComponent<Collider>().Raycast(new Ray(at + Vector3.up, Vector3.down), out RaycastHit h, 2f); return h; }
        int TeamAt(SurfaceInkManager on, Vector3 at) { RaycastHit h = Down(at, on); return h.collider != null ? on.getSurfaceTeam(on.UVFromHit(h)) : -1; }
        int size = Mathf.RoundToInt(1f / a.SplatMetresPerSize); // a splat about 1m in radius

        // 0.4m from the join: it carries on over it.
        Vector3 near = origin + right * 4.6f + forward * 2f;
        a.SplatAt(Down(near, a), size, player.Team);
        // 7m from it: b untouched.
        Vector3 far = origin - right * 2f - forward * 2f;
        a.SplatAt(Down(far, a), size, player.Team);
        // Switched off: stops at its own edge.
        SurfaceInkManager.BridgeSeams = false;
        Vector3 off = origin + right * 4.6f - forward * 3f;
        a.SplatAt(Down(off, a), size, player.Team);
        SurfaceInkManager.BridgeSeams = true;
        yield return Hold(Still, false, 0.3f, "readback");

        float radius = size * a.SplatMetresPerSize;
        int ours = player.Team;
        var probes = new (string what, int team, int want)[]
        {
            ("this side of the join", TeamAt(a, near + right * 0.35f), ours),
            ("just over the join", TeamAt(b, near + right * 0.5f), ours),
            ("over the join, inside the circle", TeamAt(b, near + right * (radius * 0.85f)), ours),
            ("over the join, outside the circle", TeamAt(b, near + right * (radius + 0.6f)), 0),
            ("b beside the far splat", TeamAt(b, origin + right * 5.3f - forward * 2f), 0),
            ("switched off: just over the join", TeamAt(b, off + right * 0.5f), 0),
            ("switched off: this side", TeamAt(a, off + right * 0.3f), ours),
        };
        Note($"splat radius {radius:F2}m, 0.4m from the join: " + string.Join(", ", probes.Select(p => $"{p.what} {(p.team == p.want ? "ok" : $"WRONG ({p.team})")}")));
        foreach (var p in probes) if (p.team != p.want) Fail($"seam bridging: {p.what} was {p.team}, expected {p.want}");
        Destroy(a.gameObject);
        Destroy(b.gameObject);
        NetGameManager.SendOverride = null;
    }

    IEnumerator SplatGrouping()
    {
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = (id, data, target) => { };
        yield return Hold(Still, false, 0.2f, "clear");
        int enemyTeam = player.Team == 1 ? 2 : 1;

        // 64 close together: a single dispatch and readback.
        int d0 = SurfaceInkManager.SplatDispatches, r0 = SurfaceInkManager.TeamMapReadbacks;
        for (int i = 0; i < 64; i++) floor.Splat(new Vector2(0.3f + 0.0008f * (i % 8), 0.3f + 0.0008f * (i / 8)), 6, player.Team);
        yield return null;
        int clusterDispatches = SurfaceInkManager.SplatDispatches - d0, clusterReadbacks = SurfaceInkManager.TeamMapReadbacks - r0;

        // Two far apart: one each, not one dispatch over everything between them.
        d0 = SurfaceInkManager.SplatDispatches;
        floor.Splat(new Vector2(0.05f, 0.05f), 6, player.Team);
        floor.Splat(new Vector2(0.95f, 0.95f), 6, player.Team);
        yield return null;
        int apartDispatches = SurfaceInkManager.SplatDispatches - d0;

        // The same spot twice in a frame: the later splat wins, as if painted one by one.
        var first = new Vector2(0.6f, 0.2f);
        var second = new Vector2(0.75f, 0.2f);
        floor.Splat(first, 8, player.Team); floor.Splat(first, 8, enemyTeam);
        floor.Splat(second, 8, enemyTeam); floor.Splat(second, 8, player.Team);
        // ...and a fill straight after a splat still clears it (the splat goes first).
        var cleared = new Vector2(0.5f, 0.6f);
        floor.Splat(cleared, 8, player.Team);
        floor.FillRegion(new Rect(0.45f, 0.55f, 0.1f, 0.1f), 0);
        yield return Hold(Still, false, 0.2f, "readback");
        int atFirst = floor.getSurfaceTeam(first), atSecond = floor.getSurfaceTeam(second), atCleared = floor.getSurfaceTeam(cleared);
        int atCluster = floor.getSurfaceTeam(new Vector2(0.3028f, 0.3028f));
        Note($"64 close splats: {clusterDispatches} dispatch(es), {clusterReadbacks} readback(s), painted {(atCluster == player.Team ? "ours" : atCluster.ToString())}; 2 far apart: {apartDispatches} dispatches; same spot ours-then-theirs -> {atFirst}, theirs-then-ours -> {atSecond} (we're {player.Team}); splat then fill -> {atCleared}");
        if (clusterDispatches != 1 || clusterReadbacks != 1) Fail("a close cluster wasn't one dispatch and one readback");
        if (atCluster != player.Team) Fail("the cluster didn't paint");
        if (apartDispatches != 2) Fail("far-apart splats weren't painted separately");
        if (atFirst != enemyTeam || atSecond != player.Team) Fail("splats in one frame didn't paint in order");
        if (atCleared != 0) Fail("a fill after a splat didn't clear it");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        NetGameManager.SendOverride = null;
    }

    IEnumerator LobbyFlow()
    {
        NetGameManager gm = NetGameManager.Instance;
        LobbyMenu menu = FindFirstObjectByType<LobbyMenu>(FindObjectsInactive.Include);
        if (menu == null) { Fail("no LobbyMenu in the scene"); yield break; }
        CaptureSends();
        var loads = new List<int>();
        System.Action<int> realLoader = NetGameManager.SceneLoader;
        NetGameManager.SceneLoader = i => loads.Add(i); // no real loads here: this scene is the test

        // The kiosk: walk up, it opens; walk away, it closes.
        Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
        var kioskGo = new GameObject("TestKiosk");
        kioskGo.transform.position = player.transform.position + forward * 5f;
        kioskGo.SetActive(false);
        LobbyKiosk kiosk = kioskGo.AddComponent<LobbyKiosk>();
        typeof(LobbyKiosk).GetField("menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(kiosk, menu);
        kioskGo.SetActive(true);
        yield return Hold(Still, false, 0.2f, "kiosk");
        bool closedAway = !menu.IsOpen;
        yield return HoldUntil(Fwd, false, 2f, "walk up", f => kiosk.PlayerInside);
        yield return Hold(Still, false, 0.1f, "kiosk");
        bool openedAt = menu.IsOpen;
        yield return HoldUntil(Back, false, 2f, "walk away", f => !kiosk.PlayerInside);
        yield return Hold(Still, false, 0.1f, "kiosk");
        bool closedLeaving = !menu.IsOpen;
        Destroy(kioskGo);
        Note($"kiosk: {(closedAway ? "closed" : "OPEN")} from afar, {(openedAt ? "opened" : "NOT opened")} walking up, {(closedLeaving ? "closed" : "STILL OPEN")} walking away");
        if (!closedAway || !openedAt || !closedLeaving) Fail("the kiosk didn't open the online menu at it, and close it away from it");

        // The host's map switch: everyone told, and loaded here.
        HostMenu host = FindFirstObjectByType<HostMenu>(FindObjectsInactive.Include);
        int scenes = NetGameManager.SceneCount;
        string mapList = "";
        if (host != null && gm.CanSwitchMap && scenes > 0)
        {
            host.SetOpen(true);
            host.OpenMaps();
            yield return null;
            mapList = string.Join(", ", host.MapButtons.Select(b => b.GetComponentInChildren<TMPro.TMP_Text>().text));
            int buttons = host.MapButtons.Count;
            sent.Clear();
            host.MapButtons[scenes - 1].onClick.Invoke();
            yield return null;
            var told = sent.Where(m => m.id == NetMsg.LoadScene).Select(m => ((SceneLoadData)m.data).buildIndex).ToList();
            Note($"host menu maps: {mapList}; picking the last sent {string.Join(",", told)} and loaded {string.Join(",", loads)}; menu {(host.IsOpen ? "OPEN" : "closed")}");
            if (buttons != scenes) Fail("the host menu doesn't list every scene in the build");
            if (told.Count != 1 || told[0] != scenes - 1 || loads.Count != 1 || loads[0] != scenes - 1) Fail("switching map didn't tell everyone and load it");
            if (host.IsOpen) { host.SetOpen(false); Fail("the host menu stayed open after switching map"); }
        }
        else Fail($"no map switch to test (host menu {(host != null)}, can switch {gm.CanSwitchMap}, scenes {scenes})");

        // On the lobby map (no matches): Start game is off, and starting does nothing.
        if (host != null)
        {
            var lobbyMap = new GameObject("TestLobbyMap").AddComponent<MapSettings>();
            lobbyMap.Set(false);
            host.SetOpen(true);
            yield return null;
            bool offInLobby = !host.StartButton.interactable, couldStart = gm.CanStartMatch;
            string label = host.StartButtonText;
            gm.StartGame();
            yield return null;
            bool started = gm.InMatch;
            if (started) gm.EndMatch();
            Destroy(lobbyMap.gameObject);
            yield return null;
            bool onAgain = host.StartButton.interactable && gm.CanStartMatch;
            host.SetOpen(false);
            Note($"lobby map: start button {(offInLobby ? "off" : "ON")} (\"{label}\"), StartGame {(started ? "STARTED a match" : "did nothing")}; on another map it's {(onAgain ? "on" : "STILL OFF")}");
            if (!offInLobby || couldStart || started) Fail("a match could be started on the lobby map");
            if (!onAgain) Fail("Start game stayed off after leaving the lobby map");
        }

        // A LoadScene from the host loads it here; nonsense indices don't.
        loads.Clear();
        gm.Receive(NetMsg.LoadScene, new SceneLoadData { buildIndex = 0 }, 4401);
        gm.Receive(NetMsg.LoadScene, new SceneLoadData { buildIndex = 999 }, 4401);
        yield return null;
        Note($"received LoadScene 0 and 999: loaded {string.Join(",", loads)}");
        if (loads.Count != 1 || loads[0] != 0) Fail("a LoadScene message didn't load just the valid scene");

        // A scene's GameCore, with one already running: destroyed, not started.
        var spare = new GameObject("SpareGameCore");
        spare.SetActive(false);
        var bootGo = new GameObject("SpareBoot");
        bootGo.SetActive(false);
        GameCoreBootstrap boot = bootGo.AddComponent<GameCoreBootstrap>();
        typeof(GameCoreBootstrap).GetField("gameCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(boot, spare);
        bootGo.SetActive(true);
        yield return null;
        bool spareGone = spare == null;
        Note($"spare GameCore {(spareGone ? "destroyed" : "STILL THERE")}, the game's manager {(NetGameManager.Instance == gm ? "unchanged" : "REPLACED")}");
        if (!spareGone || NetGameManager.Instance != gm) Fail("a scene's spare GameCore wasn't destroyed");
        Destroy(bootGo);

        NetGameManager.SceneLoader = realLoader;
        NetGameManager.SendOverride = null;
    }

    IEnumerator NetCodecMessages()
    {
        NVector3 V(float x, float y, float z) => new NVector3(new Vector3(x, y, z));
        var samples = new object[]
        {
            new PlayerStateData { steamId = 76561198000000001UL, position = V(1.5f, -2f, 3.25f), bodyYaw = 270f, camPitch = -12.5f, moveDir = new NVector2(new Vector2(0.3f, -0.7f)),
                                  team = 2, swimMode = true, climbing = false, dead = false, tick = 123456u, shielded = true, specialReady = false, subReady = true,
                                  ink = 0.42f, weapon = 3, weaponDown = true, modelYaw = 91f },
            new PlayerSpawnData { steamId = 42UL, team = 1, position = V(0f, 1f, 0f), isHostPlayer = true, playerName = "Spläter ✓" },
            new SplatData { surfaceId = 7, uv = new NVector2(new Vector2(0.25f, 0.75f)), splashSize = 14, team = 2 },
            new SplatBatchData { surfaceIds = Enumerable.Range(0, 64).Select(i => (ushort)i).ToArray(), uvs = Enumerable.Range(0, 128).Select(i => SplatBatchData.PackUV(i / 128f)).ToArray(),
                                 splashSizes = Enumerable.Repeat((byte)10, 64).ToArray(), teams = Enumerable.Repeat((byte)1, 64).ToArray() },
            new TeleportData { steamId = 9UL, position = V(4f, 5f, 6f), bodyYaw = 45f },
            new ProjectileSpawnData { shooterSteamId = 5UL, team = 1, origin = V(1f, 2f, 3f), inherit = null, velocities = new[] { 1f, 2f, 3f }, splashSizes = new[] { 14 }, visible = new[] { true } },
            new DamageData { targetSteamId = 1UL, attackerSteamId = 2UL, amount = 37.5f, fromTeam = 2, source = "Inkroller" },
            new SubData { ownerId = 3UL, subId = 11, team = 1, subType = 2, position = V(1f, 0f, 1f), velocity = V(0f, 3f, 9f), normal = null, landed = false, fuse = 2.5f },
            new SubDamageData { ownerId = 3UL, subId = 11, amount = 15f, fromTeam = 2, attackerSteamId = 8UL },
            new InkStrikeData { ownerId = 4UL, team = 2, position = V(10f, 0f, -4f) },
            new TeamAssignData { ids = new[] { 1UL, 2UL, 3UL }, roles = new[] { 1, 2, 3 }, colourPair = 4 },
            new MatchEventData { phase = MatchPhase.Results, serverTime = 12.5f, duration = 3f, alphaScore = 100, betaScore = 90, neutralScore = 5, lateJoin = true, colourPair = 2 },
            1.25f, (byte)7,
        };

        // Every type: the same bytes after a round trip, and smaller than BinaryFormatter's.
        var bf = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
        var sizes = new List<string>();
        int codecTotal = 0, bfTotal = 0;
        foreach (object sample in samples)
        {
            string name = sample.GetType().Name;
            byte[] bytes;
            object back;
            try { bytes = NetCodec.Encode(sample); back = NetCodec.Decode(bytes, 0, bytes.Length); }
            catch (Exception e) { Fail($"{name} didn't round-trip: {e.Message}"); continue; }
            byte[] again = NetCodec.Encode(back);
            if (back == null || back.GetType() != sample.GetType() || !again.SequenceEqual(bytes)) Fail($"{name} changed on the round trip");
            using var ms = new System.IO.MemoryStream();
            bf.Serialize(ms, sample);
            codecTotal += bytes.Length;
            bfTotal += (int)ms.Length;
            if (name == "PlayerStateData" || name == "SplatData" || name == "SplatBatchData" || name == "DamageData") sizes.Add($"{name} {bytes.Length}B (BinaryFormatter {ms.Length}B)");
        }
        var state = (PlayerStateData)NetCodec.Decode(NetCodec.Encode(samples[0]), 0, NetCodec.Encode(samples[0]).Length);
        if (state.steamId != 76561198000000001UL || state.position.z != 3.25f || state.moveDir.y != -0.7f || !state.weaponDown || state.modelYaw != 91f) Fail("PlayerStateData's fields didn't come back");
        var spawn = (PlayerSpawnData)NetCodec.Decode(NetCodec.Encode(samples[1]), 0, NetCodec.Encode(samples[1]).Length);
        if (spawn.playerName != "Spläter ✓") Fail("a non-ASCII name didn't come back");
        var proj = (ProjectileSpawnData)NetCodec.Decode(NetCodec.Encode(samples[5]), 0, NetCodec.Encode(samples[5]).Length);
        if (proj.inherit != null || proj.velocities.Length != 3) Fail("a null field or an array didn't come back");
        Note($"{samples.Length} types round-trip; {string.Join(", ", sizes)}; all {codecTotal}B vs {bfTotal}B");
        if (codecTotal * 3 > bfTotal) Fail("NetCodec messages aren't much smaller than BinaryFormatter's");
        int batchBytes = NetCodec.Encode(samples[3]).Length;
        Note($"a full batch of 64 splats: {batchBytes}B ({batchBytes / 64f:F1}B a splat)");
        if (batchBytes > 64 * 10) Fail("a splat batch is bigger than ~10 bytes a splat");

        // SteamGlobal really uses it (its private converters).
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var toBytes = typeof(SteamGlobal).GetMethod("ObjectToByteArray", flags);
        var toObject = typeof(SteamGlobal).GetMethod("ByteArrayToObject", flags);
        byte[] wire = (byte[])toBytes.Invoke(null, new object[] { samples[2] });
        bool viaCodec = wire.SequenceEqual(NetCodec.Encode(samples[2])) && toObject.Invoke(null, new object[] { wire, 0, wire.Length }) is SplatData;

        // Refused: an unknown tag, cut short, a huge length, bytes left over, a BinaryFormatter payload, an unlisted type.
        bool Refused(byte[] b) { try { NetCodec.Decode(b, 0, b.Length); return false; } catch { return true; } }
        byte[] good = NetCodec.Encode(samples[3]);
        byte[] huge = (byte[])good.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 1); // the first array's length
        byte[] padded = good.Concat(new byte[] { 0 }).ToArray();
        byte[] bfPayload;
        using (var ms = new System.IO.MemoryStream()) { bf.Serialize(ms, new List<string> { "not a message" }); bfPayload = ms.ToArray(); }
        bool unlisted;
        try { NetCodec.Encode(new List<int>()); unlisted = false; } catch { unlisted = true; }
        var refusals = new (string what, bool refused)[]
        {
            ("unknown tag", Refused(new byte[] { 250, 1, 2, 3 })),
            ("cut short", Refused(good.Take(good.Length - 3).ToArray())),
            ("huge length", Refused(huge)),
            ("bytes left over", Refused(padded)),
            ("BinaryFormatter payload", Refused(bfPayload)),
            ("unlisted type sent", unlisted),
        };
        Note($"SteamGlobal {(viaCodec ? "encodes and decodes with NetCodec" : "does NOT use NetCodec")}; refused: {string.Join(", ", refusals.Select(r => $"{r.what} {(r.refused ? "yes" : "NO")}"))}");
        if (!viaCodec) Fail("SteamGlobal's converters don't go through NetCodec");
        foreach (var r in refusals) if (!r.refused) Fail($"NetCodec accepted: {r.what}");
        yield return null;
    }
}
