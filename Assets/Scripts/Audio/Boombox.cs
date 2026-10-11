using UnityEngine;

// The lobby's music, played from a boombox: mostly heard everywhere, but leaning toward where it
// stands (partly spatial). Quiet while a match runs. Its speaker cones push out with the beat.
[RequireComponent(typeof(AudioSource))]
public class Boombox : MonoBehaviour
{
    [SerializeField] AudioSource source;
    [SerializeField] Transform[] cones;
    [SerializeField, Range(0f, 1f)] float spatial = 0.35f; // 0: the same everywhere, 1: fully from here
    [SerializeField, Range(0f, 1f)] float volume = 1f;     // its share, before the music setting
    [SerializeField] float pulse = 0.18f;                  // cone push at full loudness
    [SerializeField] float fadeTime = 1f;

    readonly float[] samples = new float[256];
    Vector3[] coneRest;
    float fade = 1f, loudness;

    public bool Playing => source != null && source.isPlaying; // tests
    public float Volume => source != null ? source.volume : 0f;
    public float Loudness => loudness;
    public AudioSource Source => source;

    void Awake()
    {
        if (source == null) source = GetComponent<AudioSource>();
        coneRest = new Vector3[cones != null ? cones.Length : 0];
        for (int i = 0; i < coneRest.Length; i++) coneRest[i] = cones[i].localScale;
    }

    void Start()
    {
        MusicLibrary library = AudioManager.Instance != null ? AudioManager.Instance.Library : Resources.Load<MusicLibrary>("MusicLibrary");
        if (library == null || library.lobby == null) return;
        source.clip = library.lobby;
        source.loop = true;
        source.spatialBlend = spatial;
        source.volume = Target();
        source.Play();
    }

    float Target() => volume * fade * GameSettings.MusicGain;

    void Update()
    {
        bool quiet = NetGameManager.Instance != null && NetGameManager.Instance.InMatch;
        fade = Mathf.MoveTowards(fade, quiet ? 0f : 1f, Time.deltaTime / fadeTime);
        source.volume = Target();

        // The cones: out with the loudness (measured before our volume, so they bounce at any setting).
        float level = 0f;
        if (source.isPlaying && source.volume > 0.001f)
        {
            source.GetOutputData(samples, 0);
            float sum = 0f;
            foreach (float s in samples) sum += s * s;
            level = Mathf.Clamp01(Mathf.Sqrt(sum / samples.Length) / source.volume * 2.5f);
        }
        loudness = Mathf.Lerp(loudness, level, 1f - Mathf.Exp((level > loudness ? -30f : -8f) * Time.deltaTime)); // quick out, slower back
        for (int i = 0; i < coneRest.Length; i++)
            if (cones[i] != null) cones[i].localScale = coneRest[i] * (1f + pulse * loudness);
    }
}
