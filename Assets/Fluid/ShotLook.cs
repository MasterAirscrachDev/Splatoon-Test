using UnityEngine;

// How a kind of shot is drawn as ink (InkFluid): its size, its stretch in flight, and the companion
// blobs drawn flying alongside it. Each weapon has one per kind of shot it fires (a shooter's main
// shots and fillers, a roller's ground and air flicks, the bucket's droplets and blob...), so each
// can be tuned on its own. Look only: none of it changes where shots go, what they paint or hit.
[System.Serializable]
public class ShotLook
{
    [Tooltip("Drawn size, x the usual for its splat. The usual is InkFluidSettings' Blob Fill: a share of the size of the paint it leaves (so bigger splats are already drawn bigger)")]
    public float size = 1f;

    [Tooltip("How much it stretches into a teardrop as it flies: its length grows with its speed (so faster shots already stretch longer), x this. 0: always round")]
    public float stretch = 0.1f;

    [Header("Companions")]
    [Tooltip("Extra blobs drawn flying alongside each shot, to give it volume. Look only: they don't paint or hit. 0: none")]
    public int companions = 2;

    [Tooltip("Each companion's size, a random share of the shot's between these (min, max)")]
    public Vector2 companionSize = new Vector2(0.5f, 0.85f);

    [Tooltip("How far each companion's direction is randomly tipped up or down from the shot's (degrees, ±): spreads them into a taller cluster")]
    public float upDownSpread = 2f;

    [Tooltip("How far each companion's direction is randomly turned left or right from the shot's (degrees, ±): spreads them into a wider cluster")]
    public float sideSpread = 2f;

    [Tooltip("How much each companion's speed randomly differs from the shot's (share, ±; 0.1 = up to 10% faster or slower): strings them out along the line of flight")]
    public float speedSpread = 0.05f;
}
