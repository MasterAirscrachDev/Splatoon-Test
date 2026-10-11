using UnityEngine;

// The game's sound, each kind at its volume setting (GameSettings): music, and one-off effects.
// Match music plays here (2D), fading between tracks: the match track while one runs, quickly
// crossfaded into Now or Never at a minute left, faded out when time's up. The lobby's music comes from
// its boombox instead (Boombox). Made at startup and kept across scenes.
public class AudioManager : MonoBehaviour
{
    const float FadeIn = 0.6f, FadeOut = 1.2f, QuickFade = 0.35f; // QuickFade: into Now or Never

    public static AudioManager Instance { get; private set; }
    public static float MusicVolume => GameSettings.MusicGain; // the settings' loudness (logarithmic sliders)
    public static float SfxVolume => GameSettings.SfxGain;

    MusicLibrary library;
    readonly AudioSource[] decks = new AudioSource[2];
    readonly float[] level = new float[2]; // each deck's fade, 0..1
    int live;                              // the deck with the current track
    AudioClip track;
    float fadeIn = FadeIn, fadeOut = FadeOut;

    public MusicLibrary Library => library;
    public AudioClip CurrentTrack => track;      // tests
    public AudioSource LiveDeck => decks[live];  // tests

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Instance != null) return;
        var go = new GameObject("AudioManager");
        DontDestroyOnLoad(go);
        go.AddComponent<AudioManager>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        library = Resources.Load<MusicLibrary>("MusicLibrary");
        for (int i = 0; i < decks.Length; i++)
        {
            AudioSource deck = gameObject.AddComponent<AudioSource>();
            deck.playOnAwake = false;
            deck.loop = true;
            deck.spatialBlend = 0f;
            deck.volume = 0f;
            decks[i] = deck;
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // What should be playing: the match track once play starts (after the countdown), Now or Never
    // in the last minute (when there is one), nothing otherwise.
    public AudioClip WantedTrack()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (library == null || gm == null) return null;
        switch (gm.Phase)
        {
            case MatchPhase.Playing: return gm.InFinalStretch && library.finalMinute != null ? library.finalMinute : library.match;
            default: return null;
        }
    }

    void Update()
    {
        AudioClip wanted = WantedTrack();
        if (wanted != track) Switch(wanted);
        float master = MusicVolume * (library != null ? library.volume : 1f);
        for (int i = 0; i < decks.Length; i++)
        {
            bool on = i == live && track != null;
            level[i] = Mathf.MoveTowards(level[i], on ? 1f : 0f, Time.deltaTime / (on ? fadeIn : fadeOut));
            decks[i].volume = level[i] * master;
            if (!on && level[i] <= 0f && decks[i].isPlaying) decks[i].Stop();
        }
    }

    // Onto the next track on the other deck, crossfading from the old one (quickly into Now or Never).
    void Switch(AudioClip next)
    {
        bool urgent = next != null && library != null && next == library.finalMinute;
        fadeIn = urgent ? QuickFade : FadeIn;
        fadeOut = urgent ? QuickFade : FadeOut;
        track = next;
        if (next == null) return; // the live deck fades out
        live = 1 - live;
        AudioSource deck = decks[live];
        deck.clip = next;
        deck.time = 0f;
        deck.Play();
        level[live] = 0f;
    }

    // A one-off sound at a point in the world (spatial 1) or anywhere (spatial 0), at the effects volume.
    public static AudioSource PlaySfx(AudioClip clip, Vector3 at, float volume = 1f, float spatial = 1f)
    {
        if (clip == null) return null;
        var go = new GameObject("Sfx " + clip.name);
        go.transform.position = at;
        AudioSource source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.spatialBlend = spatial;
        source.volume = volume * SfxVolume;
        source.Play();
        Destroy(go, clip.length + 0.1f);
        return source;
    }
}
