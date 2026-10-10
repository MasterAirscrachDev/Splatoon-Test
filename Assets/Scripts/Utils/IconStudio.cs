using System.Collections.Generic;
using UnityEngine;

// The icon studio (Assets/Scenes/IconStudio.unity): a camera and lights that render each weapon and
// sub prefab to a transparent sprite, which is then set as the prefab's icon for the HUD. Render
// with Tools/Icons/Render Icons, or the button on this component. Each entry's angle, zoom and
// offset live here, so tweak them and re-render. The camera looks along +Z; the items are posed at
// the origin.
public class IconStudio : MonoBehaviour
{
    [System.Serializable]
    public class Entry
    {
        public GameObject prefab;
        public Vector3 angle = new Vector3(10f, 110f, 0f);
        [Min(0.1f)] public float zoom = 1f;
        public Vector2 offset;                 // nudges it in the frame, as a share of the frame
        public string[] show;                  // parts (child names) switched on for the shot
        public string[] hide;                  // parts switched off (with everything under them)
        public string[] inkParts;              // parts the game tints that the prefab doesn't say (e.g. the player's bubble)
        public bool special;                   // the icon is a special's (set on PlayerLoadout), not the prefab's own
        public SpecialType specialType;
        public string fileName;                // the PNG's name; the prefab's by default
    }

    public Camera cam;                         // copied into an isolated preview scene to render
    public Light[] lights;                     // likewise
    public int size = 256;
    [Range(1f, 1.5f)] public float padding = 1.08f;
    public Color inkColour = new Color(1f, 0.2f, 0.72f);    // team-tinted parts: bright pink
    public string outputFolder = "Assets/Textures/Icons";
    public List<Entry> entries = new List<Entry>();
}
