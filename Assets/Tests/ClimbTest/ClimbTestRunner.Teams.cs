using System.Collections;
using System.Linq;
using UnityEngine;
using static ClimbTestLayout;

// ClimbTestRunner scenarios: three and four teams (the map decides how many, see MapSettings).
public partial class ClimbTestRunner
{
    IEnumerator MoreTeams()
    {
        NetGameManager gm = NetGameManager.Instance;
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        PlayerRole ownRole = player.Role;
        var mapGo = new GameObject("TestFourTeamMap");
        MapSettings map = mapGo.AddComponent<MapSettings>();
        map.SetTeamCount(4);
        CaptureSends();

        // The rules: four teams, their roles and colours, new players dealt round them in turn.
        string order = string.Join(",", Enumerable.Range(0, 8).Select(Teams.ForJoinOrder));
        bool rolesRoundTrip = Enumerable.Range(1, 4).All(t => Teams.Of(Teams.Role(t)) == t) && Teams.Of(PlayerRole.Spectator) == 0 && Teams.Of(PlayerRole.NotPlaying) == 0;
        Color[] colours = Enumerable.Range(1, 4).Select(gm.TeamColour).ToArray();
        float closest = float.MaxValue;
        for (int a = 0; a < 4; a++) for (int b = a + 1; b < 4; b++)
            closest = Mathf.Min(closest, Vector3.Distance(new Vector3(colours[a].r, colours[a].g, colours[a].b), new Vector3(colours[b].r, colours[b].g, colours[b].b)));
        string ninth = Teams.ForJoinOrder(8).ToString();
        string sizes = "";
        foreach (int n in new[] { 2, 3, 4 }) { map.SetTeamCount(n); sizes += $"{n} teams: {Teams.SlotsPerTeam} each, ninth player -> {Teams.ForJoinOrder(8)}; "; }
        map.SetTeamCount(3);
        string order3 = string.Join(",", Enumerable.Range(0, 10).Select(Teams.ForJoinOrder));
        map.SetTeamCount(4);
        Note($"4 teams: join order -> {order}, the 9th -> {ninth} (spectating); {sizes}3 teams' order -> {order3}; roles round-trip {rolesRoundTrip}; colours {gm.ColourPairName}, closest pair {closest:F2} apart");
        if (Teams.Count != 4 || order != "1,2,3,4,1,2,3,4") Fail("new players weren't dealt round all four teams");
        if (ninth != "0" || order3 != "1,2,3,1,2,3,1,2,3,0" || !sizes.Contains("2 teams: 4 each") || !sizes.Contains("3 teams: 3 each") || !sizes.Contains("4 teams: 2 each"))
            Fail("teams aren't 4v4, 3v3v3 and 2v2v2v2 with everyone else spectating");
        if (!rolesRoundTrip) Fail("a team's roster role doesn't map back to it");
        if (closest < 0.3f) Fail("two of the four team colours are too alike");

        // Ink: each team owns its own splat; a later splat takes over; every team is counted.
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        var spots = new[] { new Vector2(0.2f, 0.2f), new Vector2(0.8f, 0.2f), new Vector2(0.2f, 0.8f), new Vector2(0.8f, 0.8f) };
        for (int t = 1; t <= 4; t++) floor.Splat(spots[t - 1], 12, t);
        var middle = new Vector2(0.5f, 0.5f);
        floor.Splat(middle, 12, 4);
        floor.Splat(middle, 12, 3);
        yield return Hold(Still, false, 0.3f, "readback");
        int[] owners = spots.Select(floor.getSurfaceTeam).ToArray();
        int atMiddle = floor.getSurfaceTeam(middle);
        int[] scores = null;
        floor.CheckScoresAsync(s => scores = s);
        yield return HoldUntil(Still, false, 1f, "scores", f => scores != null);
        Material mat = floor.GetComponent<Renderer>().material;
        bool inkColours = mat.GetColor("_GammaColor") == gm.TeamColour(3) && mat.GetColor("_DeltaColor") == gm.TeamColour(4);
        Note($"ink: corners owned by {string.Join(",", owners)}, the middle (4 then 3) by {atMiddle}; scores " +
             (scores == null ? "none" : $"neutral {scores[0]}, teams {scores[1]}/{scores[2]}/{scores[3]}/{scores[4]}") + $"; Gamma/Delta ink colours {(inkColours ? "set" : "NOT SET")}");
        if (!owners.SequenceEqual(new[] { 1, 2, 3, 4 })) Fail("a team's ink didn't own its splat");
        if (atMiddle != 3) Fail("a later splat didn't take over another team's ink");
        if (scores == null || Enumerable.Range(1, 4).Any(t => scores[t] <= 0) || scores[3] <= scores[4] || scores[0] <= 0) Fail("the four teams' turf wasn't counted");
        if (!inkColours) Fail("the ink doesn't know the Gamma and Delta colours");
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);

        // Spawning: joining Gamma puts us on Gamma's spawn (and our team-tinted weapon goes Gamma's colour).
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        int weaponBefore = loadout.WeaponIndex, rollerIndex = -1;
        for (int i = 0; i < loadout.Weapons.Count; i++) if (loadout.Weapons[i] is Roller && rollerIndex < 0) rollerIndex = i;
        if (rollerIndex >= 0) loadout.SetMainWeapon(rollerIndex);
        yield return Hold(Still, false, 0.2f, "equip");
        Color RollerInk()
        {
            var block = new MaterialPropertyBlock();
            if (loadout.CurrentWeapon is Roller roller)
                foreach (Renderer r in roller.GetComponentsInChildren<Renderer>(true))
                    for (int i = 0; i < r.sharedMaterials.Length; i++)
                        if (r.sharedMaterials[i] != null && r.sharedMaterials[i].name.StartsWith("Ink")) { r.GetPropertyBlock(block, i); return block.GetColor("_Color"); }
            return Color.clear;
        }
        var gammaSpawn = new GameObject("GammaSpawn_Test") { tag = Teams.SpawnTag(3) };
        gammaSpawn.transform.position = station.TransformPoint(new Vector3(-8f, 0.5f, 8f));
        player.SetRole(PlayerRole.Gamma);
        yield return Hold(Still, false, 0.3f, "gamma");
        frames.Clear(); // the respawn isn't a movement glitch
        float fromGammaSpawn = Vector3.Distance(new Vector3(player.transform.position.x, 0f, player.transform.position.z), new Vector3(gammaSpawn.transform.position.x, 0f, gammaSpawn.transform.position.z));
        Color rollerInk = RollerInk();
        bool tinted = rollerIndex < 0 || rollerInk == gm.TeamColour(3);
        Note($"as Gamma: team {player.Team}, {fromGammaSpawn:F2}m from Gamma's spawn; roller ink {(rollerIndex < 0 ? "no roller" : tinted ? "Gamma's colour" : "STILL THE OLD TEAM'S")}");
        if (player.Team != 3 || fromGammaSpawn > 1.5f) Fail("joining Gamma didn't put us on Gamma's team and spawn");
        if (!tinted) Fail("the weapon kept its old team's colour after we changed team");
        loadout.SetMainWeapon(weaponBefore);

        // The HUD and the host's team editor show the extra teams, and only while the map has them.
        const ulong GammaMate = 7301, DeltaFoe = 7401;
        SpawnRemote(GammaMate, 3, station.TransformPoint(new Vector3(-4f, 0.05f, 8f)), "Gina");
        SpawnRemote(DeltaFoe, 4, station.TransformPoint(new Vector3(4f, 0.05f, 8f)), "Dee");
        yield return Hold(Still, false, 0.3f, "hud");
        MatchHUD hud = FindFirstObjectByType<MatchHUD>();
        bool rowsShown = hud != null && hud.RowShown(3) && hud.RowShown(4);
        string gammaNames = hud == null ? "" : string.Join(",", hud.GammaSlots.Select(s => s.playerName.text).Where(n => n.Length > 0));
        string deltaNames = hud == null ? "" : string.Join(",", hud.DeltaSlots.Select(s => s.playerName.text).Where(n => n.Length > 0));
        HostMenu menu = FindFirstObjectByType<HostMenu>(FindObjectsInactive.Include);
        menu.SetOpen(true);
        menu.TeamsButton.onClick.Invoke();
        yield return null;
        bool columnsShown = menu.Columns.Length >= 6 && menu.Columns[4][0].button.gameObject.activeSelf && menu.Columns[5][0].button.gameObject.activeSelf;
        int alphaSlots4 = menu.Columns[0].Count(b => b.button.gameObject.activeSelf);
        bool noBench = !menu.Columns[3].Any(b => b.button.gameObject.activeSelf);
        string inGammaColumn = string.Join(",", menu.Columns[4].Select(b => b.label.text).Where(n => n != "—"));
        menu.SetOpen(false);

        map.SetTeamCount(2);
        yield return Hold(Still, false, 0.2f, "two teams");
        bool rowsHidden = hud != null && !hud.RowShown(3) && !hud.RowShown(4);
        menu.SetOpen(true);
        menu.TeamsButton.onClick.Invoke();
        yield return null;
        bool columnsHidden = !menu.Columns[4][0].button.gameObject.activeSelf && !menu.Columns[5][0].button.gameObject.activeSelf;
        int alphaSlots2 = menu.Columns[0].Count(b => b.button.gameObject.activeSelf);
        string benched = string.Join(",", menu.Columns[2].Select(b => b.label.text).Where(n => n != "—"));
        int spectateSlots = menu.Columns[2].Count(b => b.button.gameObject.activeSelf), watchers = menu.Columns[2].Count(b => b.label.text != "—");
        menu.SetOpen(false);
        Note($"4 teams: HUD rows {(rowsShown ? "shown" : "MISSING")} (Gamma {gammaNames}; Delta {deltaNames}), editor columns {(columnsShown ? "shown" : "MISSING")} (Gamma column: {inGammaColumn}), {alphaSlots4} slots a team, no Not playing column {noBench}; 2 teams: rows {(rowsHidden ? "hidden" : "STILL SHOWN")}, columns {(columnsHidden ? "hidden" : "STILL SHOWN")}, {alphaSlots2} slots a team, spectating: {benched} ({spectateSlots} slots for {watchers})");
        if (alphaSlots4 != 2 || alphaSlots2 != 4 || !noBench) Fail("the editor's team columns aren't the map's team size (or Not playing is still there)");
        if (spectateSlots <= watchers) Fail("Spectate has no free slot");
        if (!rowsShown || !gammaNames.Contains("Gina") || !deltaNames.Contains("Dee")) Fail("the HUD didn't show the Gamma and Delta teams");
        if (!columnsShown || !inGammaColumn.Contains("Gina")) Fail("the team editor didn't show the Gamma and Delta columns");
        if (!rowsHidden || !columnsHidden) Fail("the extra teams stayed on a two-team map");
        if (!benched.Contains("Gina")) Fail("a Gamma player wasn't moved to Spectate on a two-team map");

        // The turf bar: three teams, Gamma in the middle until pushed; four, Gamma and Delta at the
        // thirds. Pushed or not, the unclaimed share keeps gaps between neighbours (each at least half
        // an equal split), so they only touch at 100%.
        var left = new float[5];
        (float g, float d, float minGap) Lay(int count, float a, float b, float gs, float ds)
        {
            MatchHUD.BarLayout(new[] { 0f, a, b, gs, ds }, count, left);
            float gap = count == 3 ? Mathf.Min(left[3] - a, (1f - b) - (left[3] + gs))
                                   : Mathf.Min(left[3] - a, Mathf.Min(left[4] - (left[3] + gs), (1f - b) - (left[4] + ds)));
            return (left[3], left[4], gap);
        }
        bool Near(float x, float want) => Mathf.Abs(x - want) < 0.002f;
        var mid3 = Lay(3, 0.2f, 0.2f, 0.1f, 0f); var pushed3 = Lay(3, 0.5f, 0.1f, 0.2f, 0f); var right3 = Lay(3, 0.1f, 0.6f, 0.2f, 0f);
        var even4 = Lay(4, 0.1f, 0.1f, 0.1f, 0.1f); var pushed4 = Lay(4, 0.6f, 0.05f, 0.2f, 0.1f); var full3 = Lay(3, 0.4f, 0.3f, 0.3f, 0f);
        Note($"bar: 3 teams Gamma at {mid3.g:F3} (centred), {pushed3.g:F3} pushed by Alpha (gap {pushed3.minGap:F3}), {right3.g:F3} pushed by Beta (gap {right3.minGap:F3}), at 100% gap {full3.minGap:F3}; " +
             $"4 teams {even4.g:F3}/{even4.d:F3} (the thirds), {pushed4.g:F3}/{pushed4.d:F3} pushed along (gap {pushed4.minGap:F4})");
        if (!Near(mid3.g, 0.45f) || !Near(pushed3.g, 0.55f) || !Near(right3.g, 0.175f)) Fail("three teams' bar: Gamma didn't sit in the middle and get pushed aside");
        if (!Near(even4.g, 0.2833f) || !Near(even4.d, 0.6167f) || !Near(pushed4.g, 0.6083f) || !Near(pushed4.d, 0.8167f)) Fail("four teams' bar: Gamma and Delta didn't sit at the thirds and get pushed along");
        if (pushed3.minGap <= 0f || right3.minGap <= 0f || pushed4.minGap <= 0f || Mathf.Abs(full3.minGap) > 0.0001f) Fail("teams touched on the bar before the map was fully covered (or didn't at 100%)");

        // The HUD: the teams across the top in the bar's order, the bar under them, the timer under that.
        map.SetTeamCount(4);
        yield return null;
        yield return null;
        float[] rowX = Enumerable.Range(1, 4).Select(t => hud.RowOf(t).anchoredPosition.x).ToArray();
        bool barOrder = rowX[0] < rowX[2] && rowX[2] < rowX[3] && rowX[3] < rowX[1];   // Alpha, Gamma, Delta, Beta
        float rowsBottom = hud.RowOf(1).anchoredPosition.y - hud.RowOf(1).rect.height;
        bool stacked = hud.BarRect.anchoredPosition.y < rowsBottom && hud.TimerRect.anchoredPosition.y < hud.BarRect.anchoredPosition.y - hud.BarRect.rect.height + 1f;
        Note($"HUD: rows at x {string.Join("/", rowX.Select(x => x.ToString("0")))} (Alpha/Beta/Gamma/Delta), bar at y {hud.BarRect.anchoredPosition.y:0}, timer at y {hud.TimerRect.anchoredPosition.y:0}");
        if (!barOrder) Fail("the HUD's teams aren't in the bar's order (Alpha, Gamma, Delta, Beta)");
        if (!stacked) Fail("the HUD isn't players, then the bar, then the timer");

        // Results: the most turf wins among the map's teams; a tie at the top is a draw.
        map.SetTeamCount(4);
        int win = MatchFlowHUD.Winner(new[] { 0, 10, 20, 30, 5 }), tie = MatchFlowHUD.Winner(new[] { 0, 30, 10, 30, 0 });
        map.SetTeamCount(3);
        int ignoresDelta = MatchFlowHUD.Winner(new[] { 0, 10, 20, 15, 99 });
        Note($"winners: 10/20/30/5 -> {Teams.Name(win)}, 30/10/30/0 -> {(tie == 0 ? "draw" : Teams.Name(tie))}, 3 teams 10/20/15 (Delta 99) -> {Teams.Name(ignoresDelta)}");
        if (win != 3 || tie != 0 || ignoresDelta != 2) Fail("the winner wasn't the top team among the map's");

        Despawn(GammaMate); Despawn(DeltaFoe);
        Destroy(gammaSpawn);
        Destroy(mapGo);
        yield return null;
        player.SetRole(ownRole);
        yield return Hold(Still, false, 0.3f, "restore");
        frames.Clear();
        NetGameManager.SendOverride = null;
    }
}
