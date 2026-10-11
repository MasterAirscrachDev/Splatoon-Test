using System.Collections.Generic;
using UnityEngine;

// What each scene in the build is, without loading it (Resources/MapCatalog, kept up to date in the
// editor whenever a scene is saved or the build's scene list changes): its team count and whether
// matches can be played there. The host menu's map list shows it.
public class MapCatalog : ScriptableObject
{
    [System.Serializable]
    public class Map
    {
        public string scene;       // its asset path
        public int teams = 2;
        public bool matches = true;
    }

    public List<Map> maps = new List<Map>();

    static MapCatalog instance;
    public static MapCatalog Instance => instance != null ? instance : instance = Resources.Load<MapCatalog>("MapCatalog");

    public static Map Find(string scenePath)
    {
        MapCatalog c = Instance;
        return c != null ? c.maps.Find(m => m.scene == scenePath) : null;
    }

    // "3 teams", or "no matches" (the lobby), or "" if it isn't known.
    public static string Describe(string scenePath)
    {
        Map m = Find(scenePath);
        if (m == null) return "";
        return m.matches ? $"{m.teams} teams" : "no matches";
    }
}
