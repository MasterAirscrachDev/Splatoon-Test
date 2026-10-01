using UnityEngine;

// Spawns a one-shot ink particle effect tinted via Start Colour (InkVolume reads vertex colour).
public static class InkParticles
{
    public static void Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Color color)
    {
        if (prefab == null) return;
        GameObject go = Object.Instantiate(prefab, position, rotation);
        ParticleSystem ps = go.GetComponent<ParticleSystem>();
        if (ps == null) return;

        var main = ps.main;
        main.startColor = color;
        ps.Play();

        // Looping bursts would never end; stop emitting so the prefab's Destroy stop action fires.
        if (main.loop)
            ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }
}
