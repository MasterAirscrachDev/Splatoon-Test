using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ClimbTestRunner scenarios: readying a sub (hold to see what it'll do, let go to use it, swim to call it off).
public partial class ClimbTestRunner
{
    IEnumerator SubReadying()
    {
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        NetGameManager.SendOverride = (id, data, target) => { };
        int Own() => SubDevice.All.Count(d => d.OwnerId == player.OwnerId);
        var results = new List<string>();

        // Beacon: a see-through copy where it'll stand while held; placed there on letting go.
        loadout.SetSub(SubType.Beacon);
        SubDevice.RemoveAll();
        player.RefillInk();
        yield return Hold(Still, false, 0.2f, "stand");
        bool pressed = loadout.PressSub();
        yield return Hold(Still, false, 0.3f, "ready beacon");
        SubPreview preview = loadout.Preview;
        bool ghost = preview != null && preview.GhostShown && !preview.PathShown;
        Vector3 ghostAt = preview != null ? preview.GhostPosition : Vector3.zero;
        bool placedEarly = Own() > 0, inkKept = player.InkLevel > 0.99f;
        bool released = loadout.ReleaseSub();
        yield return null;
        Beacon beacon = SubDevice.All.OfType<Beacon>().FirstOrDefault(b => b.OwnerId == player.OwnerId);
        float off = beacon != null ? Vector3.Distance(beacon.transform.position, ghostAt) : -1f;
        bool hidden = preview == null || !preview.GhostShown && !preview.PathShown;
        results.Add($"beacon: {(ghost ? "ghost shown" : "NO ghost")} while held, {(placedEarly ? "PLACED on press" : "not placed yet")}, placed {off:F2}m from the ghost on release, preview {(hidden ? "gone" : "STILL SHOWN")}");
        if (!pressed || !ghost) Fail("holding the beacon didn't show where it would stand");
        if (placedEarly || !inkKept) Fail("the beacon went (or cost ink) on press, not release");
        if (!released || beacon == null || off > 0.3f) Fail("letting go didn't place the beacon where its ghost was");
        if (!hidden) Fail("the preview stayed up after placing");

        // Sprinkler: its throw's arc while held; it lands where the arc ends.
        loadout.SetSub(SubType.Sprinkler);
        SubDevice.RemoveAll();
        player.RefillInk();
        yield return Hold(Still, false, 0.2f, "stand");
        loadout.PressSub();
        yield return Hold(Still, false, 0.3f, "ready sprinkler");
        preview = loadout.Preview;
        bool arc = preview != null && preview.PathShown && !preview.GhostShown;
        Vector3 arcEnd = preview != null ? preview.PathEnd : Vector3.zero;
        bool thrownEarly = Own() > 0;
        loadout.ReleaseSub();
        Sprinkler sprinkler = null;
        float waited = 0f;
        while (waited < 4f && (sprinkler == null || !sprinkler.Landed))
        {
            yield return null;
            waited += Time.deltaTime;
            sprinkler = SubDevice.All.OfType<Sprinkler>().FirstOrDefault(d => d.OwnerId == player.OwnerId);
        }
        float landedOff = sprinkler != null && sprinkler.Landed ? Vector3.Distance(sprinkler.transform.position, arcEnd) : -1f;
        results.Add($"sprinkler: {(arc ? "arc shown" : "NO arc")} while held, {(thrownEarly ? "THROWN on press" : "not thrown yet")}, landed {landedOff:F2}m from the arc's end");
        if (!arc) Fail("holding the sprinkler didn't trace its throw");
        if (thrownEarly) Fail("the sprinkler went on press, not release");
        if (landedOff < 0f || landedOff > 1f) Fail("the sprinkler didn't land where its arc ended");

        // Curling bomb: in hand, its slide traced from there; thrown on letting go.
        loadout.SetSub(SubType.CurlingBomb);
        SubDevice.RemoveAll();
        player.RefillInk();
        yield return Hold(Still, false, 0.2f, "stand");
        loadout.PressSub();
        yield return Hold(Still, false, 0.3f, "ready bomb");
        preview = loadout.Preview;
        bool slide = preview != null && preview.PathShown && loadout.HeldBomb != null;
        float slideLength = slide ? Vector3.Distance(loadout.HeldBomb.transform.position, preview.PathEnd) : 0f;
        loadout.ReleaseSub();
        yield return null;
        bool launched = loadout.LastBomb != null && loadout.LastBomb.Launched && loadout.HeldBomb == null;
        hidden = preview == null || !preview.PathShown;
        results.Add($"curling bomb: {(slide ? $"slide traced ({slideLength:F1}m)" : "NO slide traced")} while held, {(launched ? "thrown" : "NOT thrown")} on release, preview {(hidden ? "gone" : "STILL SHOWN")}");
        if (!slide || slideLength < 2f) Fail("holding the curling bomb didn't trace its slide");
        if (!launched || !hidden) Fail("letting go didn't throw the curling bomb (and drop the preview)");
        yield return Hold(Still, false, 0.3f, "settle");

        // Called off: readied, then swim form and let go. Nothing goes, nothing spent; the preview's
        // hidden while under.
        foreach (SubType type in new[] { SubType.Beacon, SubType.Sprinkler, SubType.CurlingBomb })
        {
            loadout.SetSub(type);
            SubDevice.RemoveAll();
            player.RefillInk();
            yield return Hold(Still, false, 0.2f, "stand");
            loadout.PressSub();
            yield return Hold(Still, false, 0.2f, "ready");
            bool shownFirst = loadout.Preview != null && (loadout.Preview.PathShown || loadout.Preview.GhostShown);
            yield return Hold(Still, true, 0.4f, "swim");
            bool hiddenUnder = !loadout.Preview.PathShown && !loadout.Preview.GhostShown;
            bool bombAway = loadout.HeldBomb == null || !loadout.HeldBomb.gameObject.activeSelf;
            bool used = loadout.ReleaseSub();
            player.ScriptedInput.swim = false;
            yield return Hold(Still, false, 0.6f, "surface");
            bool nothing = Own() == 0 && loadout.HeldBomb == null && !loadout.ReadyingSub && player.InkLevel > 0.99f;
            bool previewOff = !loadout.Preview.PathShown && !loadout.Preview.GhostShown;
            results.Add($"{type} called off: preview {(shownFirst ? "shown" : "NOT shown")}, {(hiddenUnder ? "hidden" : "SHOWN")} under{(type == SubType.CurlingBomb ? $" (bomb {(bombAway ? "tucked away" : "STILL SHOWN")})" : "")}, {(nothing && !used ? "nothing used" : "USED")}, {(previewOff ? "preview off" : "PREVIEW BACK")} after");
            if (!shownFirst) Fail($"{type}: no preview while readied");
            if (!hiddenUnder || !bombAway) Fail($"{type}: the preview (or bomb) showed in swim form");
            if (used || !nothing || !previewOff) Fail($"{type}: letting go in swim form didn't call it off");
        }

        // Readied, under and back up before letting go: it still goes.
        loadout.SetSub(SubType.Beacon);
        SubDevice.RemoveAll();
        player.RefillInk();
        loadout.PressSub();
        yield return Hold(Still, true, 0.4f, "swim");
        player.ScriptedInput.swim = false;
        yield return Hold(Still, false, 0.6f, "surface");
        bool back = loadout.Preview.GhostShown;
        loadout.ReleaseSub();
        yield return null;
        results.Add($"readied, swum and surfaced: ghost {(back ? "back" : "NOT back")}, {(Own() == 1 ? "placed" : "NOT placed")} on release");
        if (!back || Own() != 1) Fail("a sub readied before a swim didn't go when let go after surfacing");

        Note(string.Join("; ", results));
        SubDevice.RemoveAll();
        loadout.SetSub(SubType.Beacon);
        NetGameManager.SendOverride = null;
    }
}
