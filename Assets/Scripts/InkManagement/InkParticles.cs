using UnityEngine;

// Shared spawn helper for one-shot ink splash/burst VFX (swim-enter splash, projectile
// impact splash) built on InkVolume.shader's vertex-colour support. Tints via Start Colour
// so the same shared material/shader drives every ink volume — mesh (MaterialPropertyBlock)
// or particle (vertex colour) — without per-effect shader variants.
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

        // A looping system (burst emission authored as "loop" so it can be re-triggered) never
        // stops on its own — stop emitting right away so already-emitted particles finish and
        // the prefab's own Stop Action (Destroy) cleans it up, instead of it looping forever.
        if (main.loop)
            ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }
}
