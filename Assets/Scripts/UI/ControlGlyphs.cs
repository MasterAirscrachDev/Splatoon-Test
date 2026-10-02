using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

// The control icons: which sprite (from the button sheet's TMP sprite asset) shows each key, mouse
// button or gamepad control, per gamepad family where the art differs (face buttons). Lives in
// Resources so prompts anywhere can find it. Controls without art fall back to a text label.
// Built by ControlGlyphsBuilder from the sheet's sprite names.
[CreateAssetMenu(menuName = "Game/Control Glyphs")]
public class ControlGlyphs : ScriptableObject
{
    [Serializable]
    public struct Glyph
    {
        public string control;    // "Keyboard/e", "Mouse/leftButton", "Gamepad/buttonSouth", "Gamepad/dpad/up"
        public bool anyFamily;    // the same art for every gamepad
        public PadFamily family;  // otherwise just this one
        public Sprite sprite;
    }

    [SerializeField] TMP_SpriteAsset spriteAsset;
    [SerializeField] Glyph[] glyphs;

    public TMP_SpriteAsset SpriteAsset => spriteAsset;

    static ControlGlyphs instance;
    public static ControlGlyphs Instance => instance != null ? instance : instance = Resources.Load<ControlGlyphs>("ControlGlyphs");

    // The sprite for a control path like "<Keyboard>/e" (null if there's no art for it). Gamepads of
    // any layout ("<DualShockGamepad>/...") count as "Gamepad"; "dpad/up" falls back to "dpad".
    public Sprite SpriteFor(string path, PadFamily family)
    {
        if (!Split(path, out string device, out string control)) return null;
        while (true)
        {
            string key = device + "/" + control;
            foreach (Glyph g in glyphs)
                if (g.sprite != null && g.control == key && (g.anyFamily || g.family == family)) return g.sprite;
            int slash = control.LastIndexOf('/');
            if (slash < 0) return null;
            control = control.Substring(0, slash);
        }
    }

    static bool Split(string path, out string device, out string control)
    {
        device = control = null;
        if (string.IsNullOrEmpty(path) || path[0] != '<') return false;
        int close = path.IndexOf('>');
        int slash = path.IndexOf('/', close + 1);
        if (close < 0 || slash < 0) return false;
        string layout = path.Substring(1, close - 1);
        device = layout == "Keyboard" || layout == "Mouse" ? layout
               : layout == "Gamepad" || InputSystem.IsFirstLayoutBasedOnSecond(layout, "Gamepad") ? "Gamepad" : layout;
        control = path.Substring(slash + 1);
        return true;
    }

    // ── Prompts ────────────────────────────────────────────────────────────

    // An action's control for the current input mode, as TMP rich text: its icon(s) as sprites, or
    // "[Label]" where there's no art. Composites (WASD) give one per part. Empty if it has no
    // binding for the current scheme.
    public static string Tag(InputAction action, float iconScale = 1f)
    {
        if (action == null) return "";
        var paths = new List<string>();
        var bindings = action.bindings;
        string group = InputMode.Group;
        for (int i = 0; i < bindings.Count && paths.Count == 0; i++)
        {
            InputBinding b = bindings[i];
            if (b.isComposite)
            {
                for (int j = i + 1; j < bindings.Count && bindings[j].isPartOfComposite; j++)
                    if (InGroup(bindings[j], group)) paths.Add(bindings[j].effectivePath);
            }
            else if (!b.isPartOfComposite && InGroup(b, group)) paths.Add(b.effectivePath);
        }

        var text = new StringBuilder();
        ControlGlyphs glyphs = Instance;
        foreach (string path in paths)
        {
            Sprite sprite = glyphs != null ? glyphs.SpriteFor(path, InputMode.Family) : null;
            if (sprite != null)
            {
                bool scaled = !Mathf.Approximately(iconScale, 1f);
                if (scaled) text.Append("<size=").Append(Mathf.RoundToInt(iconScale * 100f)).Append("%>");
                text.Append("<sprite name=\"").Append(sprite.name).Append("\">");
                if (scaled) text.Append("</size>");
            }
            else text.Append('[').Append(InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice)).Append(']');
        }
        return text.ToString();
    }

    static bool InGroup(InputBinding b, string group) => b.groups != null && Array.IndexOf(b.groups.Split(InputBinding.Separator), group) >= 0;
}
