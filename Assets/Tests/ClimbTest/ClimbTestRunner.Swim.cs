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

// ClimbTestRunner scenarios: swimming and the form-switch camera.
public partial class ClimbTestRunner
{

    IEnumerator CameraFormSwitch()
    {
        // Each form switch moves the origin ~0.9m; the camera rig must ease across it.
        Transform rig = player.CameraRig;
        if (rig == null) { Fail("player has no camera rig"); yield break; }
        var heights = new List<float> { rig.position.y };
        var phases = new[] { (true, "to squid"), (false, "to kid"), (true, "to squid again") };
        foreach (var (swim, label) in phases)
        {
            player.ScriptedInput.swim = swim;
            float maxStep = 0f, start = rig.position.y;
            for (int i = 0, n = Mathf.RoundToInt(0.5f / Dt); i < n; i++) // 0.5s
            {
                yield return null;
                Record(label);
                heights.Add(rig.position.y);
                maxStep = Mathf.Max(maxStep, Mathf.Abs(heights[heights.Count - 1] - heights[heights.Count - 2]));
            }
            float moved = Mathf.Abs(rig.position.y - start);
            float fastest = maxStep / Dt; // speeds, so the limits hold at any frame rate
            Note($"{label}: camera moved {moved:F2}m, largest single-frame step {maxStep:F3}m ({fastest:F1} m/s)");
            if (moved < 0.5f) Fail($"{label}: camera didn't follow the form change (moved {moved:F2}m)");
            if (fastest > 15f) Fail($"{label}: camera snapped {maxStep:F2}m in one frame");
            float settle = Mathf.Abs(heights[heights.Count - 1] - heights[heights.Count - 2]) / Dt;
            if (settle > 0.4f) Fail($"{label}: camera still moving after 0.5s ({settle:F2} m/s)");
        }
    }

    IEnumerator FrameRateIndependence()
    {
        float saved = Time.captureDeltaTime;
        var peaks = new List<string>();
        var heights = new List<float>();
        foreach (float fps in new[] { 30f, 75f, 144f })
        {
            Time.captureDeltaTime = 1f / fps;
            yield return HoldUntil(Still, false, 1f, "settle", f => player.IsGrounded);
            float ground = player.transform.position.y, top = ground;
            player.PressJump();
            for (float t = 0f; t < 1.5f; t += Time.deltaTime)
            {
                yield return null;
                Record($"jump {fps:0}");
                top = Mathf.Max(top, player.transform.position.y);
                if (t > 0.2f && player.IsGrounded) break;
            }
            heights.Add(top - ground);
            peaks.Add($"{fps:0} fps {top - ground:F3}m");
        }

        // Standing at 1000 fps: the press onto the floor is a millimetre a frame.
        Time.captureDeltaTime = 1f / 1000f;
        yield return null;
        int ungrounded = 0, frames = 0;
        for (; frames < 200; frames++) { yield return null; if (!player.IsGrounded) ungrounded++; }
        Time.captureDeltaTime = saved;
        yield return Hold(Still, false, 0.2f, "settle");

        float spread = heights.Max() - heights.Min();
        Note($"jump peaks: {string.Join(", ", peaks)} (spread {spread * 100f:F1}cm); at 1000 fps not grounded on {ungrounded}/{frames} frames standing still");
        if (spread > heights.Max() * 0.03f) Fail("a jump's height depends on the frame rate");
        if (ungrounded > 0) Fail("standing still lost isGrounded at a high frame rate");
    }

    IEnumerator SwimWakeTrail()
    {
        SwimWake wake = player.GetComponent<SwimWake>();
        if (wake == null) { Fail("player has no SwimWake"); yield break; }
        Transform entity = player.transform.parent != null ? player.transform.parent : player.transform;
        if (!wake.View.transform.IsChildOf(entity)) Fail("the wake isn't under the player entity");
        ParticleSystemRenderer sprayLook = wake.GetComponentInChildren<ParticleSystemRenderer>(true);
        if (wake.View.sharedMaterial == null || wake.View.sharedMaterial.renderQueue > 2500 || sprayLook == null || sprayLook.sharedMaterial.renderQueue > 2500)
            Fail("the wake (or its spray) is drawn as transparent, so it won't take shadows");
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        floor.FillRegion(new Rect(0, 0, 1, 1), player.Team);
        Physics.Raycast(player.BodyCenter, Vector3.down, out RaycastHit ground, 3f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore);

        // Submerged and still: nothing (hidden squids stay hidden).
        yield return Hold(Still, true, 0.6f, "dive");
        Note($"still: shown {wake.Shown:F2}, ring {wake.RingShown:F2}, {wake.VertexCount} vertices, side wakes {wake.WakeLength:F2}m");
        if (wake.VertexCount > 0 || wake.WakeLength > 0.1f) Fail("a wake around a squid sitting still");

        // Swimming: the ring up around us, side wakes trailing back, and spray off them.
        ParticleSystem spray = wake.GetComponentInChildren<ParticleSystem>(true);
        int sprayBefore = spray != null ? spray.particleCount : 0;
        yield return Hold(Fwd, true, 0.6f, "swim");
        int sprayed = spray != null ? spray.particleCount : 0;
        Bounds moving = wake.Bounds;
        float behind = Vector3.Dot(player.BodyCenter - moving.center, player.transform.forward);
        float top = moving.max.y - ground.point.y;
        Note($"swimming at {wake.Speed * 100f:F0}% of full speed: ring {wake.RingShown:F2}, side wakes {wake.WakeLength:F2}m, footprint {moving.size.x:F2} x {moving.size.z:F2}m centred {behind:F2}m behind us, {top:F2}m high");
        if (wake.RingShown < 0.99f || wake.VertexCount == 0) Fail("no ring around the swimming squid");
        if (wake.WakeLength < 1f) Fail("the side wakes didn't grow with speed");
        if (behind < 0.3f) Fail("the side wakes don't trail behind");
        if (top < 0.03f || top > 0.45f) Fail("the wake isn't a low ridge on the surface");
        Note($"{sprayed} spray droplets in the air (was {sprayBefore})");
        if (spray == null || sprayed <= sprayBefore) Fail("no spray off the side wakes");

        // Out of swim form: it fades away.
        yield return Hold(Still, false, 0.5f, "stand");
        Note($"standing: {wake.VertexCount} vertices");
        if (wake.VertexCount > 0) Fail("the wake didn't fade after leaving the ink");
        yield return Hold(Back, false, 0.6f, "back"); // stay on the station
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
    }

    IEnumerator SwimSpeeds()
    {
        SurfaceInkManager floor = station.GetComponentsInChildren<SurfaceInkManager>().First(m => m.name == "Floor");
        int enemyTeam = player.Team == 1 ? 2 : 1;
        var speeds = new Dictionary<string, float>();
        foreach (var (label, team) in new[] { ("own", player.Team), ("ground", 0), ("enemy", enemyTeam) })
        {
            floor.FillRegion(new Rect(0, 0, 1, 1), team);
            yield return Hold(Still, true, 0.3f, "dive " + label); // readback, and into swim form
            yield return Hold(Fwd, true, 0.4f, "swim " + label);
            speeds[label] = player.CurrentSpeed;
            yield return Hold(Back, true, 0.4f, "back " + label); // stay on the station
        }
        floor.FillRegion(new Rect(0, 0, 1, 1), 0);
        Note($"swim speed: own {speeds["own"]:F2}, ground {speeds["ground"]:F2}, enemy {speeds["enemy"]:F2}");
        if (!(speeds["own"] > speeds["ground"] && speeds["ground"] > speeds["enemy"])) Fail("swim speeds aren't own > ground > enemy");
    }
}
