using UnityEngine;

// The game's music (Resources/MusicLibrary, read by AudioManager and the lobby's boombox).
[CreateAssetMenu(menuName = "Splatoon/Music Library")]
public class MusicLibrary : ScriptableObject
{
    public AudioClip lobby;       // played from the lobby's boombox
    public AudioClip match;       // while a match runs (none yet: quiet)
    public AudioClip finalMinute; // "Now or Never": the match's last minute, in place of the match track
    [Range(0f, 1f)] public float volume = 1f; // all of it, before the music setting
}
