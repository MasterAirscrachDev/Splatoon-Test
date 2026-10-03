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

// ClimbTestRunner scenarios: controls, icons, cursor, gyro and the pause menu.
public partial class ClimbTestRunner
{

    // Input tests drive virtual devices; the real ones are switched off meanwhile so a hand on the
    // mouse or a controller on the desk can't change the outcome.
    List<InputDevice> MuteRealInput()
    {
        var muted = new List<InputDevice>();
        foreach (InputDevice d in InputSystem.devices.ToArray())
            if (d.enabled && !GameCursor.IsVirtual(d) && (d is Mouse || d is Keyboard || d is Gamepad)) { InputSystem.DisableDevice(d); muted.Add(d); }
        return muted;
    }

    static void Unmute(List<InputDevice> muted)
    {
        foreach (InputDevice d in muted) if (d.added) InputSystem.EnableDevice(d);
    }

    IEnumerator Controls()
    {
        List<InputDevice> muted = MuteRealInput();
        // The actions: everything reachable from both schemes (bar the mouse-only and stick-only ones).
        var controls = new ControlLayer();
        string[] schemes = controls.controlSchemes.Select(c => c.name).ToArray();
        var kbmOnly = new HashSet<string> { "Debug", "LookDelta" };
        var padOnly = new HashSet<string> { "LookStick", "Recenter", "QuickJumpUp", "QuickJumpLeft", "QuickJumpRight", "QuickJumpBase" };
        var gaps = new List<string>();
        foreach (InputAction a in controls)
        {
            bool Has(string group) => a.bindings.Any(b => !b.isComposite && b.groups != null && b.groups.Contains(group));
            if (!Has("Keyboard&Mouse") && !padOnly.Contains(a.name)) gaps.Add(a.name + " (keyboard/mouse)");
            if (!Has("Gamepad") && !kbmOnly.Contains(a.name)) gaps.Add(a.name + " (gamepad)");
            if (a.bindings.Any(b => !b.isComposite && string.IsNullOrEmpty(b.groups))) gaps.Add(a.name + " (a binding in no scheme)");
        }
        controls.Dispose();
        Note($"schemes: {string.Join(", ", schemes)}");
        if (!schemes.Contains("Keyboard&Mouse") || !schemes.Contains("Gamepad")) Fail("missing the keyboard/mouse or gamepad scheme");
        if (gaps.Count > 0) Fail("unbound: " + string.Join(", ", gaps));

        yield return Hold(Still, false, 0.2f, "stand");
        IEnumerator For(float seconds) { float end = Time.time + seconds - 1e-4f; while (Time.time < end) yield return null; }
        float yaw = 0f, since = 0f;
        void Mark() { yaw = player.transform.eulerAngles.y; since = Time.time; }
        float Rate() => Mathf.DeltaAngle(yaw, player.transform.eulerAngles.y) / Mathf.Max(1e-4f, Time.time - since);

        // Half tilt: the same turn rate at 75 and 30 frames a second.
        player.ScriptedInput.lookStick = new Vector2(0.5f, 0f);
        yield return null;
        Mark(); yield return For(0.5f);
        float at75 = Rate();
        float savedDt = Time.captureDeltaTime;
        Time.captureDeltaTime = 1f / 30f;
        yield return null;
        Mark(); yield return For(0.5f);
        float at30 = Rate();
        Time.captureDeltaTime = savedDt;

        // Full tilt: the stick speed at once, speeding up after a moment at the edge.
        Mark();
        player.ScriptedInput.lookStick = new Vector2(1f, 0f);
        yield return null;
        float early = Rate(); // the first frame: before the edge boost builds
        yield return For(0.5f);
        Mark(); yield return For(0.2f);
        float late = Rate();
        player.ScriptedInput.lookStick = Vector2.zero;
        Note($"half tilt: {at75:F0}°/s at 75fps, {at30:F0}°/s at 30fps; full tilt: {early:F0}°/s at first, {late:F0}°/s held (full speed {player.StickSpeed.x:F0}°/s)");
        if (at75 <= 5f || Mathf.Abs(at75 - at30) > at75 * 0.05f) Fail("the stick's turn rate depends on the frame rate");
        if (Mathf.Abs(early - player.StickSpeed.x) > player.StickSpeed.x * 0.15f) Fail("full tilt doesn't turn at the stick speed");
        if (late < early * 1.3f) Fail("holding the stick at the edge didn't speed the turn up");

        // Up on the stick looks up (from level, well clear of the limits); Recenter brings it back.
        player.ScriptedInput.recenter = true;
        yield return null;
        player.ScriptedInput.recenter = false;
        yield return For(0.3f);
        float pitchBefore = player.CameraPitch;
        player.ScriptedInput.lookStick = new Vector2(0f, 1f);
        yield return For(0.3f);
        player.ScriptedInput.lookStick = Vector2.zero;
        float raised = player.CameraPitch - pitchBefore;
        player.ScriptedInput.recenter = true;
        yield return null;
        player.ScriptedInput.recenter = false;
        yield return For(0.3f);
        Note($"pitch moved {raised:F0}° in 0.3s of up (from {pitchBefore:F0}°), {player.CameraPitch:F1}° after Recenter");
        if (raised > -20f) Fail("up on the stick didn't look up");
        if (Mathf.Abs(player.CameraPitch) > 0.5f) Fail("Recenter didn't level the view");

        // A real (virtual) gamepad through the bindings: the stick turns us; B closes the loadout menu.
        ScriptedPlayerInput scripted = player.ScriptedInput;
        player.ScriptedInput = null;
        Gamepad pad = InputSystem.AddDevice<Gamepad>("TestGamepad");
        InputSystem.QueueStateEvent(pad, new GamepadState { rightStick = new Vector2(0.5f, 0f) });
        yield return null;
        Mark(); yield return For(0.4f);
        float padRate = Rate();
        InputSystem.QueueStateEvent(pad, new GamepadState());
        LoadoutMenu menu = FindFirstObjectByType<LoadoutMenu>();
        bool opened = false, closed = false;
        if (menu != null)
        {
            menu.SetOpen(true);
            yield return null;
            opened = menu.IsOpen;
            InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(GamepadButton.East));
            yield return null;
            yield return null;
            closed = !menu.IsOpen;
            InputSystem.QueueStateEvent(pad, new GamepadState());
            yield return null;
            if (menu.IsOpen) menu.SetOpen(false);
        }
        InputSystem.RemoveDevice(pad);
        player.ScriptedInput = scripted;
        Note($"virtual gamepad: half tilt turned {padRate:F0}°/s (scripted {at75:F0}°/s; the stick's dead zone rescales it); loadout menu {(opened ? (closed ? "closed by B" : "stayed open on B") : "didn't open")}");
        if (padRate < at75 * 0.7f || padRate > at75 * 1.1f) Fail("the gamepad's right stick doesn't drive the look");
        if (menu == null || !opened || !closed) Fail("B (Cancel) didn't close the loadout menu");
        yield return Hold(Still, false, 0.2f, "settle");
        Unmute(muted);
    }

    IEnumerator ControlIcons()
    {
        List<InputDevice> muted = MuteRealInput();
        ControlGlyphs glyphs = ControlGlyphs.Instance;
        if (glyphs == null || glyphs.SpriteAsset == null) { Fail("no ControlGlyphs (or its sprite asset) in Resources"); Unmute(muted); yield break; }

        // What each action shows, per input.
        const ControlScheme Keys = ControlScheme.KeyboardMouse, Pad = ControlScheme.Gamepad;
        var cases = new (ControlScheme scheme, PadFamily family, string template, string[] shows)[]
        {
            (Keys, PadFamily.Xbox, "{Weapon/Sub}", new[] { "buttonsheet_E" }),
            (Keys, PadFamily.Xbox, "{Movement/Jump}", new[] { "buttonsheet_space" }),
            (Keys, PadFamily.Xbox, "{Weapon/Attack}", new[] { "buttonsheet_MouseL" }),
            (Keys, PadFamily.Xbox, "{Movement/Move}", new[] { "buttonsheet_W", "buttonsheet_S", "buttonsheet_A", "buttonsheet_D" }),
            (Keys, PadFamily.Xbox, "{GameControl/Map}", new[] { "buttonsheet_Tab" }),
            (Pad, PadFamily.Xbox, "{GameControl/Loadout}", new[] { "buttonsheet_controllerSelect" }), // the sheet's names are swapped (see ControlGlyphsBuilder)
            (Pad, PadFamily.Xbox, "{GameControl/Map}", new[] { "buttonsheet_controllerX" }),
            (Pad, PadFamily.PlayStation, "{GameControl/Map}", new[] { "buttonsheet_controllerPSSquare" }),
            (Pad, PadFamily.Xbox, "{GameControl/QuickJumpBase}", new[] { "buttonsheet_controllerDpadDown" }), // DpadSelected turned (buttonsheet_dpad.png)
            (Pad, PadFamily.Xbox, "{GameControl/QuickJumpLeft}", new[] { "buttonsheet_controllerDpadLeft" }),
            (Pad, PadFamily.Xbox, "{GameControl/QuickJumpRight}", new[] { "buttonsheet_controllerDpadRight" }),
            (Pad, PadFamily.Xbox, "{GameControl/QuickJumpUp}", new[] { "buttonsheet_controllerDpadSelected" }),
            (Pad, PadFamily.Xbox, "{Weapon/Sub}", new[] { "buttonsheet_controllerRB" }),
            (Pad, PadFamily.Xbox, "{Movement/Jump}", new[] { "buttonsheet_controllerA" }),
            (Pad, PadFamily.Xbox, "{GameControl/Cancel}", new[] { "buttonsheet_controllerB" }),
            (Pad, PadFamily.Xbox, "{Weapon/Special}", new[] { "buttonsheet_controllerRS" }),
            (Pad, PadFamily.Xbox, "{Movement/Move}", new[] { "buttonsheet_controllerLS" }),
            (Pad, PadFamily.Xbox, "{GameControl/GameStart}", new[] { "buttonsheet_controllerDpadSelected" }),
            (Pad, PadFamily.PlayStation, "{Movement/Jump}", new[] { "buttonsheet_controllerPSX" }),
            (Pad, PadFamily.PlayStation, "{GameControl/Cancel}", new[] { "buttonsheet_controllerPSCircle" }),
            (Pad, PadFamily.Nintendo, "{Movement/Jump}", new[] { "buttonsheet_controllerB" }),
            (Pad, PadFamily.Nintendo, "{GameControl/Cancel}", new[] { "buttonsheet_controllerA" }),
        };
        var wrong = new List<string>();
        foreach (var c in cases)
        {
            InputMode.Use(c.scheme, c.family);
            string shown = ControlPrompt.Format(c.template);
            int at = 0;
            foreach (string part in c.shows)
            {
                int found = shown.IndexOf(part.StartsWith("[") ? part : "\"" + part + "\"", at, StringComparison.Ordinal);
                if (found < 0) { wrong.Add($"{c.scheme}/{c.family} {c.template} shows {shown}"); break; }
                at = found + part.Length;
            }
        }
        var missingArt = new List<string>();
        foreach (var c in cases)
            foreach (string part in c.shows)
                if (!part.StartsWith("[") && glyphs.SpriteAsset.GetSpriteIndexFromName(part) < 0
                    && (glyphs.SpriteAsset.fallbackSpriteAssets == null || glyphs.SpriteAsset.fallbackSpriteAssets.All(f => f == null || f.GetSpriteIndexFromName(part) < 0)))
                    missingArt.Add(part); // TMP looks in the fallback sprite assets too
        Note($"keyboard: Sub {ControlPromptAs(Keys, PadFamily.Xbox, "{Weapon/Sub}")}, Map {ControlPromptAs(Keys, PadFamily.Xbox, "{GameControl/Map}")}; PlayStation jump {ControlPromptAs(Pad, PadFamily.PlayStation, "{Movement/Jump}")}");
        if (wrong.Count > 0) Fail("wrong icons: " + string.Join("; ", wrong));
        if (missingArt.Count > 0) Fail("not in the sprite asset: " + string.Join(", ", missingArt.Distinct()));

        // Switching: the last device pressed picks the icons (mouse movement alone doesn't); the HUD follows.
        ControlPrompt subKey = FindObjectsByType<ControlPrompt>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(p => p.Template == "{Weapon/Sub}");
        Gamepad pad = InputSystem.AddDevice<Gamepad>("IconPad");
        Keyboard keyboard = InputSystem.AddDevice<Keyboard>("IconKeyboard");
        Mouse mouse = InputSystem.AddDevice<Mouse>("IconMouse");
        Gamepad ps = null;
        try { ps = InputSystem.AddDevice<DualShock4GamepadHID>("IconDualShock"); } catch (Exception e) { Note("couldn't add a virtual DualShock: " + e.Message); }
        void Send<T>(InputDevice device, InputControl<T> control, T value) where T : struct
        {
            using (StateEvent.From(device, out InputEventPtr e)) { control.WriteValueIntoEvent(value, e); InputSystem.QueueEvent(e); }
        }
        IEnumerator Settle() { yield return null; yield return null; }

        InputMode.Use(Keys, PadFamily.Xbox);
        yield return null;
        string hudKeys = subKey != null ? subKey.Text : "";
        Send(pad, pad.buttonNorth, 1f); yield return Settle();
        Send(pad, pad.buttonNorth, 0f);
        var afterPad = (InputMode.Scheme, InputMode.Family);
        string hudPad = subKey != null ? subKey.Text : "";
        Send(mouse, mouse.delta, new Vector2(40f, 0f)); yield return Settle();
        Send(mouse, mouse.delta, Vector2.zero);
        var afterMouse = InputMode.Scheme;
        Send(keyboard, keyboard.kKey, 1f); yield return Settle();
        Send(keyboard, keyboard.kKey, 0f);
        var afterKey = InputMode.Scheme;
        var afterPs = (ControlScheme.KeyboardMouse, PadFamily.Xbox);
        if (ps != null) { Send(ps, ps.buttonNorth, 1f); yield return Settle(); Send(ps, ps.buttonNorth, 0f); afterPs = (InputMode.Scheme, InputMode.Family); }
        Send(pad, pad.leftStick, new Vector2(0.8f, 0f)); yield return Settle();
        Send(pad, pad.leftStick, Vector2.zero); yield return Settle();
        var afterStick = (InputMode.Scheme, InputMode.Family);
        foreach (InputDevice d in new InputDevice[] { pad, keyboard, mouse, ps }) if (d != null) InputSystem.RemoveDevice(d);
        InputMode.Use(Keys, PadFamily.Xbox);

        Note($"pad button -> {afterPad.Item1}/{afterPad.Item2}; mouse moved -> {afterMouse}; key -> {afterKey}; DualShock -> {afterPs.Item1}/{afterPs.Item2}; pad stick -> {afterStick.Item1}/{afterStick.Item2}; HUD sub key {hudKeys} -> {hudPad}");
        if (afterPad != (Pad, PadFamily.Xbox)) Fail("a gamepad button didn't switch to gamepad icons");
        if (afterMouse != Pad) Fail("moving the mouse switched away from gamepad icons");
        if (afterKey != Keys) Fail("a key didn't switch back to keyboard icons");
        if (ps != null && afterPs != (Pad, PadFamily.PlayStation)) Fail("a DualShock didn't switch to PlayStation icons");
        if (afterStick != (Pad, PadFamily.Xbox)) Fail("pushing a gamepad stick didn't switch to its icons");
        if (subKey == null || !hudKeys.Contains("buttonsheet_E") || !hudPad.Contains("controllerRB")) Fail("the HUD's sub key didn't follow the input");
        yield return Hold(Still, false, 0.1f, "settle");
        Unmute(muted);
    }

    IEnumerator CursorControl()
    {
        List<InputDevice> muted = MuteRealInput();
        LoadoutMenu menu = FindFirstObjectByType<LoadoutMenu>();
        MapScreen map = FindFirstObjectByType<MapScreen>();
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (menu == null || map == null || loadout == null) { Fail("missing the loadout menu, map or loadout"); Unmute(muted); yield break; }
        Mouse mouse = InputSystem.AddDevice<Mouse>("CursorMouse");
        Gamepad pad = InputSystem.AddDevice<Gamepad>("CursorPad");
        void Send<T>(InputDevice device, InputControl<T> control, T value) where T : struct
        {
            using (StateEvent.From(device, out InputEventPtr e)) { control.WriteValueIntoEvent(value, e); InputSystem.QueueEvent(e); }
        }
        IEnumerator Settle() { yield return null; yield return null; }
        IEnumerator MouseTo(Vector2 at) // one event: each carries the whole mouse state
        {
            using (StateEvent.From(mouse, out InputEventPtr e))
            {
                mouse.position.WriteValueIntoEvent(at, e);
                mouse.delta.WriteValueIntoEvent(new Vector2(12f, 0f), e);
                InputSystem.QueueEvent(e);
            }
            yield return Settle();
        }
        IEnumerator Tap(ButtonControl button) { Send(pad, button, 1f); yield return Settle(); Send(pad, button, 0f); yield return Settle(); }
        bool Near(Vector2 a, Vector2 b) => Vector2.Distance(a, b) < 3f;
        Vector2 centre = new Vector2(Screen.width, Screen.height) * 0.5f;

        // Mouse: the ring sits where it points; the system cursor's hidden, and locked again on close.
        InputMode.Use(ControlScheme.KeyboardMouse, PadFamily.Xbox);
        menu.SetOpen(true);
        Vector2 spot = new Vector2(Screen.width * 0.3f, Screen.height * 0.4f);
        yield return MouseTo(spot);
        bool mouseOk = GameCursor.Shown && GameCursor.CurrentMode == GameCursor.Mode.Pointer && Near(GameCursor.Position, spot) && Near(GameCursor.Ring.position, spot);
        bool systemHidden = Cursor.lockState == CursorLockMode.None && !Cursor.visible;
        menu.SetOpen(false);
        yield return null;
        bool lockedAfter = Cursor.lockState == CursorLockMode.Locked && !GameCursor.Active;
        Note($"mouse: ring at {GameCursor.Position} for {spot}; system cursor {(systemHidden ? "hidden" : "showing")}, {(lockedAfter ? "locked" : "unlocked")} after closing");
        if (!mouseOk) Fail("the ring doesn't follow the mouse");
        if (!systemHidden || !lockedAfter) Fail("the system cursor isn't hidden while open (or locked again after)");

        // Gamepad, no gyro: the menu picks buttons; the d-pad moves the choice and A presses it.
        InputMode.Use(ControlScheme.Gamepad, PadFamily.Xbox);
        int weaponBefore = loadout.WeaponIndex;
        menu.SetOpen(true);
        yield return Settle();
        GameObject first = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        bool selecting = GameCursor.CurrentMode == GameCursor.Mode.Select && !GameCursor.Ring.gameObject.activeSelf && GameCursor.Frame.gameObject.activeSelf;
        yield return Tap(pad.dpad.down);
        GameObject second = EventSystem.current.currentSelectedGameObject;
        yield return Tap(pad.buttonSouth);
        int weaponPicked = loadout.WeaponIndex;
        menu.SetOpen(false);
        loadout.SetMainWeapon(weaponBefore);
        Note($"gamepad menu: starts on {(first != null ? first.name : "nothing")}, d-pad down to {(second != null ? second.name : "nothing")}, A picked weapon {weaponPicked}");
        if (!selecting) Fail("a gamepad in a menu should select buttons (frame, no ring)");
        if (first != menu.WeaponButtons[weaponBefore].gameObject) Fail("the menu didn't start on the current weapon");
        if (second != menu.WeaponButtons[1].gameObject) Fail("the d-pad didn't move the selection down");
        if (weaponPicked != 1) Fail("A didn't press the selected button");

        // The map: the stick moves the ring at a rate, and A clicks where it is.
        Vector3 target = station.TransformPoint(new Vector3(-4f, 0f, 4f));
        Vector3? picked = null;
        bool aiming = map.BeginTargeting(2f, w => picked = w, () => { });
        yield return Settle();
        bool stickMode = GameCursor.CurrentMode == GameCursor.Mode.Stick && GameCursor.Shown;
        Vector2 from = GameCursor.Position;
        Send(pad, pad.leftStick, new Vector2(1f, 0f));
        yield return Hold(Still, false, 0.3f, "cursor");
        Send(pad, pad.leftStick, Vector2.zero);
        yield return Settle();
        Vector2 stickMoved = GameCursor.Position - from;
        GameCursor.Warp(map.ScreenPoint(target));
        yield return null;
        yield return Tap(pad.buttonSouth);
        if (map.IsTargeting) map.CancelTargeting();
        float miss = picked.HasValue ? new Vector2(picked.Value.x - target.x, picked.Value.z - target.z).magnitude : -1f;
        Note($"map: started at {from}, the stick moved it {stickMoved}; A over the spot picked {(picked.HasValue ? $"{miss:F2}m from it" : "nothing")}");
        if (!aiming || !stickMode) Fail("the map with a gamepad should show the ring, moved by the stick");
        if (stickMoved.x < 50f || Mathf.Abs(stickMoved.y) > 5f) Fail("the stick didn't move the ring");
        if (!picked.HasValue || miss > 1.5f) Fail("A didn't click the map where the ring was");

        // Gyro (mouse motion on a gamepad): drives the ring directly in menus too; Y recentres it; the stick adds to it.
        menu.SetOpen(true);
        yield return Settle();
        Vector2 gyroSpot = new Vector2(Screen.width * 0.7f, Screen.height * 0.6f);
        yield return MouseTo(gyroSpot);
        bool gyroOk = GameCursor.CurrentMode == GameCursor.Mode.Pointer && Near(GameCursor.Position, gyroSpot) && InputMode.Scheme == ControlScheme.Gamepad;
        yield return Tap(pad.buttonNorth);
        bool recentred = Near(GameCursor.Position, centre);
        Send(pad, pad.leftStick, new Vector2(0f, 1f));
        yield return Hold(Still, false, 0.2f, "cursor");
        Send(pad, pad.leftStick, Vector2.zero);
        yield return Settle();
        bool additive = GameCursor.CurrentMode == GameCursor.Mode.Stick && GameCursor.Position.y > centre.y + 20f;
        menu.SetOpen(false);
        Note($"gyro: ring {(gyroOk ? "follows" : "doesn't follow")} the motion, Y {(recentred ? "recentres" : "doesn't recentre")} it, the stick then {(additive ? "moves" : "doesn't move")} it");
        if (!gyroOk) Fail("mouse motion on a gamepad (gyro) didn't drive the ring");
        if (!recentred) Fail("Recenter didn't bring the ring to the middle");
        if (!additive) Fail("with gyro the stick should move the ring, not pick buttons");

        InputSystem.RemoveDevice(mouse);
        InputSystem.RemoveDevice(pad);
        InputMode.Use(ControlScheme.KeyboardMouse, PadFamily.Xbox);
        yield return Hold(Still, false, 0.1f, "settle");
        Unmute(muted);
    }

    IEnumerator Gyro()
    {
        List<InputDevice> muted = MuteRealInput();
        // The Steam calls exist in the library this build ships with.
        string bindings = SteamGyro.CheckBindings();
        Note(bindings == null ? "Steam Input calls resolve" : "Steam Input calls: " + bindings);
        if (bindings != null) Fail("the Steam Input calls don't resolve: " + bindings);
        if (SteamGyro.FamilyFor(SteamGyro.PS5) != PadFamily.PlayStation || SteamGyro.FamilyFor(SteamGyro.SwitchPro) != PadFamily.Nintendo
            || SteamGyro.FamilyFor(14) != PadFamily.Xbox || SteamGyro.FamilyFor(-1) != null)
            Fail("Steam's controller types don't map to the right icons");

        IEnumerator For(float seconds) { float end = Time.time + seconds - 1e-4f; while (Time.time < end) yield return null; }
        ScriptedPlayerInput scripted = player.ScriptedInput;
        player.ScriptedInput = null; // the gyro is real input
        Gamepad pad = InputSystem.AddDevice<Gamepad>("GyroPad");
        InputMode.Use(ControlScheme.Gamepad, PadFamily.PlayStation);
        float yaw = 0f, pitch = 0f, since = 0f;
        void Mark() { yaw = player.transform.eulerAngles.y; pitch = player.CameraPitch; since = Time.time; }
        Vector2 Rates() => new Vector2(Mathf.DeltaAngle(yaw, player.transform.eulerAngles.y), pitch - player.CameraPitch) / Mathf.Max(1e-4f, Time.time - since);

        // Turning and tilting the controller moves the view, scaled by the sensitivity.
        SteamGyro.TestRate = new Vector2(40f, 0f);
        yield return null;
        Mark(); yield return For(0.4f);
        Vector2 turning = Rates();
        SteamGyro.TestRate = new Vector2(0f, 30f);
        yield return null;
        Mark(); yield return For(0.3f);
        Vector2 tilting = Rates();

        // With a gyro the stick only turns.
        SteamGyro.TestRate = Vector2.zero;
        using (StateEvent.From(pad, out InputEventPtr e)) { pad.rightStick.WriteValueIntoEvent(new Vector2(0.5f, 1f), e); InputSystem.QueueEvent(e); }
        yield return null;
        Mark(); yield return For(0.3f);
        Vector2 stickWithGyro = Rates();
        using (StateEvent.From(pad, out InputEventPtr e)) { pad.rightStick.WriteValueIntoEvent(Vector2.zero, e); InputSystem.QueueEvent(e); }

        // On keyboard and mouse the gyro does nothing.
        InputMode.Use(ControlScheme.KeyboardMouse, PadFamily.Xbox);
        SteamGyro.TestRate = new Vector2(40f, 0f);
        yield return null;
        Mark(); yield return For(0.3f);
        Vector2 onKeyboard = Rates();

        // In a menu the gyro takes the cursor straight from button-picking to pointing.
        LoadoutMenu menu = FindFirstObjectByType<LoadoutMenu>();
        InputMode.Use(ControlScheme.Gamepad, PadFamily.PlayStation);
        SteamGyro.TestRate = Vector2.zero;
        menu.SetOpen(true);
        yield return null;
        bool picking = GameCursor.CurrentMode == GameCursor.Mode.Select;
        Vector2 from = GameCursor.Position;
        SteamGyro.TestRate = new Vector2(0f, 20f);
        yield return For(0.2f);
        Vector2 cursorMoved = GameCursor.Position - from;
        bool pointing = GameCursor.CurrentMode == GameCursor.Mode.Pointer;
        menu.SetOpen(false);

        SteamGyro.EndTest();
        InputSystem.RemoveDevice(pad);
        InputMode.Use(ControlScheme.KeyboardMouse, PadFamily.Xbox);
        player.ScriptedInput = scripted;
        float sens = player.GyroSensitivity;
        Note($"gyro x{sens}: 40°/s turn -> {turning.x:F0}°/s view; 30°/s tilt -> {tilting.y:F0}°/s; stick up+right with a gyro -> {stickWithGyro.x:F0}°/s turn, {stickWithGyro.y:F1}°/s up/down; on keyboard -> {onKeyboard.x:F1}°/s; menu cursor {(picking ? "picking" : "pointing")} then {(pointing ? "pointing" : "picking")}, moved {cursorMoved}");
        if (Mathf.Abs(turning.x - 40f * sens) > 40f * sens * 0.1f || Mathf.Abs(turning.y) > 1f) Fail("turning the controller didn't turn the view by the sensitivity");
        if (Mathf.Abs(tilting.y - 30f * sens) > 30f * sens * 0.1f) Fail("tilting the controller up didn't look up");
        if (stickWithGyro.x < 5f || Mathf.Abs(stickWithGyro.y) > 0.5f) Fail("with a gyro the stick should turn but not look up or down");
        if (Mathf.Abs(onKeyboard.x) > 0.5f) Fail("the gyro moved the view while on keyboard and mouse");
        if (!picking || !pointing || cursorMoved.y <= 0f) Fail("the gyro didn't take over the menu cursor");
        yield return Hold(Still, false, 0.1f, "settle");
        Unmute(muted);
    }

    IEnumerator MapPadButtons()
    {
        List<InputDevice> muted = MuteRealInput();
        NetGameManager gm = NetGameManager.Instance;
        MapScreen map = FindFirstObjectByType<MapScreen>();
        if (map == null) { Fail("no map"); Unmute(muted); yield break; }
        CaptureSends();
        Gamepad pad = InputSystem.AddDevice<Gamepad>("MapPad");
        IEnumerator Settle() { yield return null; yield return null; }
        IEnumerator Tap(ButtonControl button)
        {
            using (StateEvent.From(pad, out InputEventPtr e)) { button.WriteValueIntoEvent(1f, e); InputSystem.QueueEvent(e); }
            yield return Settle();
            using (StateEvent.From(pad, out InputEventPtr e)) { button.WriteValueIntoEvent(0f, e); InputSystem.QueueEvent(e); }
            yield return Settle();
        }
        InputMode.Use(ControlScheme.Gamepad, PadFamily.Xbox);
        const ulong MateId = 3090;
        SpawnRemote(MateId, player.Team, station.TransformPoint(new Vector3(6f, 0.05f, -6f)), "Mate");
        yield return Hold(Still, false, 0.3f, "stand");

        // The west button toggles it: it stays open after letting go; B closes it; pressing again closes it too.
        yield return Tap(pad.buttonWest);
        bool openedByTap = map.IsOpen;
        yield return Hold(Still, false, 0.3f, "map");
        bool stayed = map.IsOpen;
        yield return Tap(pad.buttonEast);
        bool closedByB = !map.IsOpen;
        yield return Tap(pad.buttonWest);
        yield return Tap(pad.buttonWest);
        bool closedByWest = !map.IsOpen;
        Note($"west button: {(openedByTap ? "opened" : "didn't open")} the map, {(stayed ? "stayed" : "didn't stay")} open after letting go, B {(closedByB ? "closed" : "didn't close")} it, pressing again {(closedByWest ? "closed" : "didn't close")} it");
        if (!openedByTap || !stayed) Fail("the west button should toggle the map open");
        if (!closedByB || !closedByWest) Fail("B or the west button didn't close a toggled map");

        // Entries show their d-pad direction; up jumps to the first teammate, down to base.
        ISuperJumpTarget requested = null;
        System.Action<ISuperJumpTarget> onRequest = t => requested = t;
        MapScreen.SuperJumpRequested += onRequest;
        yield return Tap(pad.buttonWest);
        yield return Hold(Still, false, 0.2f, "map");
        string mateStatus = map.Entries.Select(e => e.status.text).FirstOrDefault(t => t.Contains("Super Jump")) ?? "";
        string baseStatus = map.SpawnEntry.status.text;
        PlayerController mate = gm.GetPlayer(MateId);
        yield return Tap(pad.dpad.up);
        bool toMate = player.IsSuperJumping && mate != null && ReferenceEquals(requested, mate);
        bool closedOnJump = !map.IsOpen;
        player.PlaceAt(player.transform.position, player.transform.eulerAngles.y); // calls it off
        frames.Clear();
        yield return Hold(Still, false, 0.6f, "settle");
        yield return Tap(pad.buttonWest);
        yield return Hold(Still, false, 0.2f, "map");
        requested = null;
        yield return Tap(pad.dpad.down);
        bool toBase = player.IsSuperJumping && requested is SpawnJumpTarget;
        MapScreen.SuperJumpRequested -= onRequest;
        player.PlaceAt(player.transform.position, player.transform.eulerAngles.y);
        frames.Clear();
        if (map.IsOpen) map.SetOpen(false);

        Despawn(MateId);
        InputSystem.RemoveDevice(pad);
        InputMode.Use(ControlScheme.KeyboardMouse, PadFamily.Xbox);
        yield return Hold(Still, false, 0.3f, "settle");
        NetGameManager.SendOverride = null;
        Note($"teammate entry \"{mateStatus}\", base \"{baseStatus}\"; d-pad up {(toMate ? "jumps to the teammate" : "didn't jump to the teammate")}{(closedOnJump ? " (map closed)" : "")}, down {(toBase ? "jumps to base" : "didn't jump to base")}");
        if (!mateStatus.Contains("controllerDpadSelected") || !baseStatus.Contains("controllerDpad")) Fail("the entries don't show their d-pad directions");
        if (!toMate || !closedOnJump) Fail("d-pad up didn't Super Jump to the first teammate");
        if (!toBase) Fail("d-pad down didn't Super Jump to base");
        Unmute(muted);
    }

    IEnumerator PauseAndSettings()
    {
        List<InputDevice> muted = MuteRealInput();
        PauseMenu pause = FindFirstObjectByType<PauseMenu>(FindObjectsInactive.Include);
        LoadoutMenu loadoutMenu = FindFirstObjectByType<LoadoutMenu>();
        PlayerLoadout loadout = player.GetComponent<PlayerLoadout>();
        if (pause == null || loadoutMenu == null) { Fail("no pause menu (or loadout menu)"); Unmute(muted); yield break; }
        Keyboard keyboard = InputSystem.AddDevice<Keyboard>("PauseKeyboard");
        Gamepad pad = InputSystem.AddDevice<Gamepad>("PausePad");
        IEnumerator Settle() { yield return null; yield return null; }
        IEnumerator Tap(InputDevice device, ButtonControl button)
        {
            using (StateEvent.From(device, out InputEventPtr e)) { button.WriteValueIntoEvent(1f, e); InputSystem.QueueEvent(e); }
            yield return Settle();
            using (StateEvent.From(device, out InputEventPtr e)) { button.WriteValueIntoEvent(0f, e); InputSystem.QueueEvent(e); }
            yield return Settle();
        }
        IEnumerator For(float seconds) { float end = Time.time + seconds - 1e-4f; while (Time.time < end) yield return null; }
        GameObject Selected() => EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;

        // Esc opens and closes it, stopping our input meanwhile.
        InputMode.Use(ControlScheme.KeyboardMouse, PadFamily.Xbox);
        yield return Tap(keyboard, keyboard.escapeKey);
        bool escOpened = pause.IsOpen && InputGate.Blocked && GameCursor.Active;
        yield return Tap(keyboard, keyboard.escapeKey);
        bool escClosed = !pause.IsOpen && !InputGate.Blocked;

        // The Esc that closes another menu doesn't open this one.
        loadoutMenu.SetOpen(true);
        yield return Settle();
        yield return Tap(keyboard, keyboard.escapeKey);
        bool onlyLoadoutClosed = !loadoutMenu.IsOpen && !pause.IsOpen;

        // Select on a gamepad: buttons picked from Resume; Settings; the d-pad nudges a slider; B backs out, then closes.
        InputMode.Use(ControlScheme.Gamepad, PadFamily.Xbox);
        yield return Tap(pad, pad.selectButton);
        bool padOpened = pause.IsOpen && Selected() == pause.ResumeButton.gameObject;
        yield return Tap(pad, pad.dpad.down);
        bool onSettings = Selected() == pause.SettingsButton.gameObject;
        yield return Tap(pad, pad.buttonSouth);
        bool inSettings = pause.SettingsOpen && Selected() == pause.MouseSlider.gameObject;
        float before = GameSettings.MouseSensitivity;
        yield return Tap(pad, pad.dpad.right);
        float nudged = GameSettings.MouseSensitivity;
        yield return Tap(pad, pad.buttonEast);
        bool backToMain = pause.IsOpen && !pause.SettingsOpen;
        yield return Tap(pad, pad.buttonEast);
        bool padClosed = !pause.IsOpen;
        Note($"Esc {(escOpened ? "opens" : "doesn't open")} it and {(escClosed ? "closes" : "doesn't close")} it; Esc out of the loadout menu {(onlyLoadoutClosed ? "only closes that" : "opened pause too")}; Select {(padOpened ? "opens it on Resume" : "didn't open it on Resume")}, d-pad/A {(inSettings ? "reach the settings" : "didn't reach the settings")}, right on the slider {before:F2} -> {nudged:F2}, B {(backToMain ? "backs out" : "didn't back out")} then {(padClosed ? "closes" : "didn't close")}");
        if (!escOpened || !escClosed) Fail("Esc didn't open and close the pause menu");
        if (!onlyLoadoutClosed) Fail("the Esc that closed the loadout menu also opened the pause menu");
        if (!padOpened || !onSettings || !inSettings) Fail("Select and the d-pad didn't open the menu and reach the settings");
        if (nudged <= before) Fail("the d-pad didn't move the sensitivity slider");
        if (!backToMain || !padClosed) Fail("B didn't back out of settings and close the menu");

        // Settings take effect: mouse and stick sensitivity scale the turn.
        GameSettings.ResetToDefaults();
        float yaw = 0f, pitch = 0f, since = 0f;
        void Mark() { yaw = player.transform.eulerAngles.y; pitch = player.CameraPitch; since = Time.time; }
        float Turned() => Mathf.DeltaAngle(yaw, player.transform.eulerAngles.y);
        Vector2 Rates() => new Vector2(Turned(), pitch - player.CameraPitch) / Mathf.Max(1e-4f, Time.time - since);
        IEnumerator MouseTurn(float sensitivity)
        {
            GameSettings.MouseSensitivity = sensitivity;
            player.ScriptedInput.look = new Vector2(4f, 0f);
            Mark(); yield return For(0.3f);
            player.ScriptedInput.look = Vector2.zero;
            yield return For(0.3f); // the smoothing settles
        }
        yield return MouseTurn(1f);
        float mouseAt1 = Turned();
        yield return MouseTurn(2f);
        float mouseAt2 = Turned();
        GameSettings.MouseSensitivity = 1f;
        player.ScriptedInput.lookStick = new Vector2(0.5f, 0f);
        yield return null;
        Mark(); yield return For(0.3f);
        float stickAt1 = Rates().x;
        GameSettings.StickSensitivity = 2f;
        Mark(); yield return For(0.3f);
        float stickAt2 = Rates().x;
        player.ScriptedInput.lookStick = Vector2.zero;
        GameSettings.StickSensitivity = 1f;

        // Gyro: turning and up/down scale apart; off means off. Y recentres even while it's moving.
        ScriptedPlayerInput scripted = player.ScriptedInput;
        player.ScriptedInput = null; // the gyro and Y are real input
        GameSettings.GyroSensitivityX = 2f;
        GameSettings.GyroSensitivityY = 0.5f;
        GameSettings.Apply();
        SteamGyro.TestRate = new Vector2(20f, 20f);
        yield return null;
        Mark(); yield return For(0.3f);
        Vector2 gyroRates = Rates();
        GameSettings.GyroEnabled = false;
        GameSettings.Apply();
        yield return null;
        Mark(); yield return For(0.2f);
        Vector2 gyroOff = Rates();
        GameSettings.ResetToDefaults();
        SteamGyro.TestRate = new Vector2(0f, 40f);
        yield return For(0.5f);
        float lookedUp = player.CameraPitch;
        SteamGyro.TestRate = new Vector2(0f, 2f); // a hand's never quite still
        yield return Tap(pad, pad.buttonNorth);
        yield return For(0.3f);
        float afterRecentre = player.CameraPitch;
        SteamGyro.TestRate = Vector2.zero;

        // RT clicks where the gyro cursor is.
        int weaponBefore = loadout.WeaponIndex;
        loadoutMenu.SetOpen(true);
        yield return Settle();
        SteamGyro.TestRate = new Vector2(10f, 0f);
        yield return Settle();
        SteamGyro.TestRate = Vector2.zero;
        bool gyroPointing = GameCursor.CurrentMode == GameCursor.Mode.Pointer;
        GameCursor.Warp(loadoutMenu.WeaponButtons[1].transform.position);
        yield return null;
        yield return Tap(pad, pad.rightTrigger);
        int weaponPicked = loadout.WeaponIndex;
        loadoutMenu.SetOpen(false);
        loadout.SetMainWeapon(weaponBefore);
        SteamGyro.EndTest();
        player.ScriptedInput = scripted;

        // Saved and read back through FileSuper.
        GameSettings.MouseSensitivity = 1.35f;
        GameSettings.GyroSensitivityY = 0.6f;
        GameSettings.GyroEnabled = false;
        var saving = GameSettings.SaveNow();
        while (!saving.IsCompleted) yield return null;
        GameSettings.ResetToDefaults();
        var loading = GameSettings.Load();
        while (!loading.IsCompleted) yield return null;
        bool restored = Mathf.Approximately(GameSettings.MouseSensitivity, 1.35f) && Mathf.Approximately(GameSettings.GyroSensitivityY, 0.6f) && !GameSettings.GyroEnabled;
        GameSettings.DeleteFile();
        GameSettings.ResetToDefaults();

        InputSystem.RemoveDevice(keyboard);
        InputSystem.RemoveDevice(pad);
        InputMode.Use(ControlScheme.KeyboardMouse, PadFamily.Xbox);
        Note($"mouse x1 turned {mouseAt1:F1}°, x2 {mouseAt2:F1}°; stick x1 {stickAt1:F0}°/s, x2 {stickAt2:F0}°/s; gyro 20°/s both ways at turn x2, up/down x0.5 -> {gyroRates.x:F0}°/s, {gyroRates.y:F0}°/s; gyro off -> {gyroOff.x:F1}°/s; looked up to {lookedUp:F0}°, Y brought it to {afterRecentre:F1}° with the gyro still moving; RT with the gyro cursor picked weapon {weaponPicked}; saved settings {(restored ? "read back" : "didn't read back")}");
        if (Mathf.Abs(mouseAt2 - 2f * mouseAt1) > Mathf.Abs(mouseAt1) * 0.1f) Fail("mouse sensitivity didn't scale the turn");
        if (Mathf.Abs(stickAt2 - 2f * stickAt1) > stickAt1 * 0.1f) Fail("controller sensitivity didn't scale the stick");
        if (Mathf.Abs(gyroRates.x - 60f) > 6f || Mathf.Abs(gyroRates.y - 15f) > 2f) Fail("gyro turning and up/down sensitivity didn't apply separately");
        if (Mathf.Abs(gyroOff.x) > 0.5f) Fail("the gyro still moved the view with gyro aiming off");
        if (lookedUp > -10f || Mathf.Abs(afterRecentre) > 2f) Fail("Y didn't recentre the view while the gyro was moving");
        if (!gyroPointing || weaponPicked != 1) Fail("RT didn't click with the gyro cursor");
        if (!restored) Fail("settings didn't save and load");
        Unmute(muted);
        yield return Hold(Still, false, 0.1f, "settle");
    }

    static string ControlPromptAs(ControlScheme scheme, PadFamily family, string template)
    {
        ControlScheme s = InputMode.Scheme; PadFamily f = InputMode.Family;
        InputMode.Use(scheme, family);
        string shown = ControlPrompt.Format(template);
        InputMode.Use(s, f);
        return shown;
    }
}
