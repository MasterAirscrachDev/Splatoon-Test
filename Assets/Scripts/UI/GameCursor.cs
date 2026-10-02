using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

// The game's cursor: a white ring, shown while a menu or the map is open (the system cursor stays
// hidden; it's locked again when they close). The mouse, or gyro (Steam Input sends it as mouse
// motion), places it directly; a gamepad's sticks move it at a rate. With a gamepad, A clicks where
// it is, through a virtual mouse, so the UI sees an ordinary pointer. In menus a gamepad without
// gyro picks buttons with the stick or d-pad instead: the ring hides and a frame shows the
// selection. Recenter (Y) brings the cursor back to the middle, the reset for gyro drift.
public class GameCursor : MonoBehaviour
{
    public enum Use { Menu, Map }
    public enum Mode { Pointer, Stick, Select }

    const float RingSize = 34f;        // pixels at 1080p
    const float StickSpeed = 1.1f;     // screen heights per second at full tilt
    const float StickCurve = 2f;       // gentle near the middle for fine placing
    const float StickDeadZone = 0.15f;
    const float FramePadding = 6f;     // around the selected button
    const float PressedScale = 0.8f;   // the ring while clicking
    const float GyroReach = 25f;       // degrees of controller turn to cross the screen's height

    static GameCursor instance;

    readonly List<(Object owner, Use use, GameObject first)> owners = new List<(Object, Use, GameObject)>();
    RectTransform ring, frame;
    Mouse virtualMouse;
    Mouse lastMouse;               // the real mouse (or gyro) that last moved it
    Mouse LastMouse => lastMouse != null && lastMouse.added ? lastMouse : null; // unplugged: none
    ControlLayer input;            // Recenter, Click
    Vector2 position;
    Mode mode;
    bool gyro;                     // mouse motion while using a gamepad: gyro aiming
    bool click;                    // A held, for the virtual mouse's left button

    public static bool Active => instance != null && instance.owners.Count > 0;
    public static Mode CurrentMode => instance != null ? instance.mode : Mode.Pointer;
    public static bool Shown => Active && instance.mode != Mode.Select;
    public static Vector2 Position => instance != null ? instance.position : new Vector2(Screen.width, Screen.height) * 0.5f;
    public static bool IsVirtual(InputDevice device) => instance != null && device != null && device == instance.virtualMouse;
    public static RectTransform Ring => instance != null ? instance.ring : null;   // tests
    public static RectTransform Frame => instance != null ? instance.frame : null;

    static GameCursor Instance
    {
        get
        {
            if (instance != null) return instance;
            var go = new GameObject("GameCursor", typeof(RectTransform), typeof(Canvas));
            DontDestroyOnLoad(go);
            return instance = go.AddComponent<GameCursor>();
        }
    }

    // A menu or the map wants the cursor (first: what a gamepad selects first, if anything).
    public static void Open(Object owner, Use use, GameObject first = null)
    {
        GameCursor c = Instance;
        bool wasActive = c.owners.Count > 0;
        c.owners.RemoveAll(o => o.owner == owner);
        c.owners.Add((owner, use, first));
        HostMenu.EnsureEventSystem();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = false;
        if (!wasActive) c.position = InputMode.Scheme == ControlScheme.Gamepad ? Centre : c.RealPointer();
        c.SetMode(c.StartMode());
    }

    public static void Close(Object owner)
    {
        if (instance == null) return;
        instance.owners.RemoveAll(o => o.owner == owner || o.owner == null);
        if (instance.owners.Count > 0) { instance.SetMode(instance.StartMode()); return; }
        instance.Release();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // Puts the cursor somewhere (tests, Recenter).
    public static void Warp(Vector2 screen)
    {
        if (instance == null) return;
        instance.position = Clamp(screen);
        if (instance.mode == Mode.Pointer && instance.LastMouse != null) instance.LastMouse.WarpCursorPosition(instance.position);
    }

    static Vector2 Centre => new Vector2(Screen.width, Screen.height) * 0.5f;
    static Vector2 Clamp(Vector2 p) => new Vector2(Mathf.Clamp(p.x, 0f, Screen.width), Mathf.Clamp(p.y, 0f, Screen.height));
    Use CurrentUse => owners.Exists(o => o.use == Use.Map) ? Use.Map : Use.Menu;

    Mode StartMode()
    {
        if (InputMode.Scheme == ControlScheme.KeyboardMouse || gyro) return Mode.Pointer;
        return CurrentUse == Use.Map ? Mode.Stick : Mode.Select;
    }

    void Awake()
    {
        var canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000; // over every menu
        frame = MakeImage("Frame", LoadSprite("UI/CursorFrame") ?? OutlineSprite(), true);
        ring = MakeImage("Ring", LoadSprite("UI/CursorRing") ?? RingSprite(), false);
        input = new ControlLayer();
        input.Movement.Recenter.Enable();
        input.GameControl.Click.Enable();
        InputSystem.onAfterUpdate += DriveVirtualMouse;
        InputMode.Changed += OnInputChanged;
        Release();
    }

    void OnDestroy()
    {
        InputSystem.onAfterUpdate -= DriveVirtualMouse;
        InputMode.Changed -= OnInputChanged;
        input?.Disable();
        input?.Dispose();
        if (virtualMouse != null && virtualMouse.added) InputSystem.RemoveDevice(virtualMouse);
        if (instance == this) instance = null;
    }

    // Back on keyboard and mouse: no more gyro, and the mouse drives it.
    void OnInputChanged()
    {
        if (InputMode.Scheme != ControlScheme.KeyboardMouse) return;
        gyro = false;
        if (owners.Count > 0) SetMode(Mode.Pointer);
    }

    RectTransform MakeImage(string name, Sprite sprite, bool sliced)
    {
        var rt = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
        rt.SetParent(transform, false);
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        var image = rt.GetComponent<Image>();
        image.sprite = sprite;
        image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
        image.raycastTarget = false;
        return rt;
    }

    static Sprite LoadSprite(string path) => Resources.Load<Sprite>(path);

    void Update()
    {
        owners.RemoveAll(o => o.owner == null);
        if (owners.Count == 0) { ring.gameObject.SetActive(false); frame.gameObject.SetActive(false); return; }
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = false;
        float dt = Time.unscaledDeltaTime;
        // Menus always take the d-pad/stick and A/B (nothing's selected while pointing, so they do
        // nothing then); the map is pointed at, never navigated.
        if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = CurrentUse == Use.Menu;

        // The real mouse (or gyro) places it directly.
        Mouse moved = RealMouseMoved();
        if (moved != null)
        {
            lastMouse = moved;
            position = Clamp(moved.position.ReadValue());
            gyro = InputMode.Scheme == ControlScheme.Gamepad;
            SetMode(Mode.Pointer);
        }

        // Gyro (Steam Input) moves it directly too.
        if (InputMode.Scheme == ControlScheme.Gamepad && SteamGyro.Enabled && SteamGyro.Moving)
        {
            gyro = true;
            if (mode == Mode.Select) SetMode(Mode.Pointer);
            Vector2 rate = Vector2.Scale(SteamGyro.Rate, new Vector2(GameSettings.GyroSensitivityX, GameSettings.GyroSensitivityY));
            position = Clamp(position + rate * (Screen.height / GyroReach * dt));
        }

        // A gamepad's sticks move it at a rate, or (in menus, without gyro) the stick and d-pad pick buttons.
        Gamepad pad = Gamepad.current;
        Vector2 stick = pad != null ? Vector2.ClampMagnitude(pad.leftStick.ReadValue() + pad.rightStick.ReadValue(), 1f) : Vector2.zero;
        bool dpad = pad != null && pad.dpad.ReadValue().sqrMagnitude > 0.25f;
        bool menu = CurrentUse == Use.Menu;
        if (menu && (dpad || stick.magnitude > StickDeadZone && !gyro)) SetMode(Mode.Select);
        else if (stick.magnitude > StickDeadZone)
        {
            SetMode(Mode.Stick);
            float tilt = Mathf.InverseLerp(StickDeadZone, 1f, stick.magnitude);
            position = Clamp(position + stick.normalized * (Mathf.Pow(tilt, StickCurve) * StickSpeed * GameSettings.StickSensitivity * Screen.height * dt));
        }

        if (input.Movement.Recenter.WasPressedThisFrame() && mode != Mode.Select) Warp(Centre);
        InputAction clickAction = input.GameControl.Click;
        click = mode != Mode.Select && InputMode.Scheme == ControlScheme.Gamepad && clickAction.IsPressed() && clickAction.activeControl?.device is Gamepad;

        // Show it: the ring where it is, or the frame around the selection.
        float scale = Screen.height / 1080f;
        ring.gameObject.SetActive(mode != Mode.Select);
        ring.position = position;
        ring.sizeDelta = Vector2.one * RingSize * scale * (click || mode == Mode.Pointer && LastMouse != null && LastMouse.leftButton.isPressed ? PressedScale : 1f);
        frame.gameObject.SetActive(mode == Mode.Select && FrameSelection(scale));
    }

    // Any real mouse that moved this frame (not our virtual one).
    Mouse RealMouseMoved()
    {
        foreach (InputDevice d in InputSystem.devices)
            if (d is Mouse m && m != virtualMouse && m.enabled && m.delta.ReadValue().sqrMagnitude > 0.01f) return m;
        return null;
    }

    Vector2 RealPointer()
    {
        foreach (InputDevice d in InputSystem.devices)
            if (d is Mouse m && m != virtualMouse && m.enabled) return Clamp(m.position.ReadValue());
        return Centre;
    }

    void SetMode(Mode next)
    {
        EventSystem es = EventSystem.current;
        if (next == mode && (next != Mode.Select || es == null || es.currentSelectedGameObject != null)) return;
        if (mode == Mode.Select && next != Mode.Select && es != null) es.SetSelectedGameObject(null);
        mode = next;
        if (mode == Mode.Select) EnsureSelection();
    }

    // Something to navigate from: the menu's first choice, or its first usable button.
    void EnsureSelection()
    {
        EventSystem es = EventSystem.current;
        if (es == null || owners.Count == 0) return;
        GameObject current = es.currentSelectedGameObject;
        if (current != null && current.activeInHierarchy && current.TryGetComponent(out Selectable s) && s.IsInteractable()) return;
        var top = owners[owners.Count - 1];
        GameObject pick = top.first != null && top.first.activeInHierarchy ? top.first : null;
        if (pick == null && top.owner is Component c)
            foreach (Selectable candidate in c.GetComponentsInChildren<Selectable>())
                if (candidate.IsInteractable()) { pick = candidate.gameObject; break; }
        es.SetSelectedGameObject(pick);
    }

    // The frame around the selected button; false if there's none.
    bool FrameSelection(float scale)
    {
        EnsureSelection();
        GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected == null || !(selected.transform is RectTransform target)) return false;
        var corners = new Vector3[4];
        target.GetWorldCorners(corners);
        Canvas canvas = target.GetComponentInParent<Canvas>();
        Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
        float pad = FramePadding * scale;
        frame.position = (min + max) * 0.5f;
        frame.sizeDelta = max - min + Vector2.one * (2f * pad);
        return true;
    }

    // With a gamepad (stick, or gyro + A), a virtual mouse points and clicks for us.
    void DriveVirtualMouse()
    {
        bool drive = owners.Count > 0 && mode != Mode.Select && InputMode.Scheme == ControlScheme.Gamepad;
        if (!drive)
        {
            ReleaseMouse();
            return;
        }
        if (virtualMouse == null || !virtualMouse.added) virtualMouse = InputSystem.AddDevice<Mouse>("GameCursorMouse");
        if (!virtualMouse.enabled) InputSystem.EnableDevice(virtualMouse);
        virtualMouse.CopyState(out MouseState state);
        Vector2 last = state.position;
        state.position = position;
        state.delta = position - last;
        state = state.WithButton(MouseButton.Left, click);
        InputState.Change(virtualMouse, state);
    }

    // Lets go of the virtual mouse (button up) and turns it off.
    void ReleaseMouse()
    {
        click = false;
        if (virtualMouse == null || !virtualMouse.added || !virtualMouse.enabled) return;
        virtualMouse.CopyState(out MouseState state);
        InputState.Change(virtualMouse, state.WithButton(MouseButton.Left, false));
        InputSystem.DisableDevice(virtualMouse);
    }

    // The menus are gone: no virtual mouse, nothing shown, nothing selected.
    void Release()
    {
        ReleaseMouse();
        if (ring != null) ring.gameObject.SetActive(false);
        if (frame != null) frame.gameObject.SetActive(false);
        EventSystem es = EventSystem.current;
        if (es != null) { es.SetSelectedGameObject(null); es.sendNavigationEvents = true; }
    }

    // ── Default art (Resources/UI/CursorRing and CursorFrame override them) ──

    // A white ring with a soft dark edge, so it reads on bright ink too.
    static Sprite RingSprite()
    {
        const int size = 64;
        const float outer = 30f, inner = 22f, edge = 2.5f;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "CursorRing", filterMode = FilterMode.Bilinear };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = new Vector2(x - (size - 1) * 0.5f, y - (size - 1) * 0.5f).magnitude;
                float white = Mathf.Clamp01(outer - r) * Mathf.Clamp01(r - inner);
                float shadow = Mathf.Clamp01(outer + edge - r) * Mathf.Clamp01(r - (inner - edge));
                Color c = Color.Lerp(new Color(0f, 0f, 0f, 0.45f * shadow), Color.white, white);
                tex.SetPixel(x, y, c);
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    // A rounded white outline that slices to any button.
    static Sprite OutlineSprite()
    {
        const int size = 48, radius = 14, thickness = 4;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "CursorFrame", filterMode = FilterMode.Bilinear };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                Vector2 q = new Vector2(Mathf.Abs(p.x - size * 0.5f), Mathf.Abs(p.y - size * 0.5f)) - Vector2.one * (size * 0.5f - radius);
                float d = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius; // <0 inside
                float a = Mathf.Clamp01(0.5f - d) * Mathf.Clamp01(d + thickness + 0.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, Vector4.one * (radius + 2));
    }
}
