using TMPro;
using UnityEngine;

// The game's typefaces for texts made in code (Resources/GameFonts, set up by Tools/UI/Apply Fonts):
// Super Waffles for names, headers and numbers, Kronika for detail, each with its outlined preset.
// A text's font and material must go together (a material belongs to its font's atlas).
[CreateAssetMenu(menuName = "UI/Game Fonts")]
public class GameFonts : ScriptableObject
{
    public TMP_FontAsset heading, detail;
    public Material headingOutline, detailOutline;

    static GameFonts instance;
    public static GameFonts Instance => instance != null ? instance : instance = Resources.Load<GameFonts>("GameFonts");

    // The heading face, outlined (or Liberation Sans', if the set's missing).
    public static void Heading(TMP_Text t, bool outlined = true)
    {
        GameFonts f = Instance;
        if (f != null && f.heading != null)
        {
            t.font = f.heading;
            t.fontSharedMaterial = outlined && f.headingOutline != null ? f.headingOutline : f.heading.material;
            return;
        }
        t.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (outlined) t.fontSharedMaterial = Resources.Load<Material>("Fonts & Materials/LiberationSans SDF - Outline");
    }
}
