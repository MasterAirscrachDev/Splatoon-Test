using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

// Text with control icons in it: "{Weapon/Sub}" (an action, as Map/Action in ControlLayer) shows
// that action's key, button or stick for whatever the player is using right now, and swaps when they
// switch between keyboard/mouse and gamepad (or to a different kind of gamepad).
[RequireComponent(typeof(TMP_Text))]
public class ControlPrompt : MonoBehaviour
{
    [SerializeField, TextArea] string template;   // empty: the text it starts with
    [SerializeField] float iconScale = 1.3f;      // icons' size relative to the text

    static readonly Regex Token = new Regex(@"\{(\w+)/(\w+)\}");

    TMP_Text text;

    public string Template
    {
        get => template;
        set { template = value; Refresh(); }
    }

    public string Text => text != null ? text.text : ""; // tests

    void Awake()
    {
        text = GetComponent<TMP_Text>();
        if (string.IsNullOrEmpty(template)) template = text.text;
    }

    void OnEnable()
    {
        InputMode.Changed += Refresh;
        Refresh();
    }

    void OnDisable() => InputMode.Changed -= Refresh;

    public void Refresh()
    {
        if (text == null) return;
        ControlGlyphs glyphs = ControlGlyphs.Instance;
        if (glyphs != null && glyphs.SpriteAsset != null) text.spriteAsset = glyphs.SpriteAsset;
        text.text = Format(template, iconScale);
    }

    // The template with each {Map/Action} replaced by its control's icon(s) or label.
    public static string Format(string template, float iconScale = 1f)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return Token.Replace(template, m =>
        {
            var map = InputMode.Actions.asset.FindActionMap(m.Groups[1].Value);
            return ControlGlyphs.Tag(map?.FindAction(m.Groups[2].Value), iconScale);
        });
    }
}
