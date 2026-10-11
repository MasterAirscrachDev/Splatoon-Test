using UnityEditor;
using UnityEngine;

// The player prefab's effects added in place (create-once: an existing one is rebuilt).
public static class PlayerFx
{
    const string PlayerPrefab = "Assets/Prefabs/PlayerEntity - VOID.prefab";

    // A Super Jump trail (SuperJumpTrail): ink blobs in the swim wake's look, shrinking away.
    [MenuItem("Tools/Player/Add Super Jump Trail")]
    public static void AddSuperJumpTrail()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefab);
        try
        {
            foreach (SuperJumpTrail old in root.GetComponentsInChildren<SuperJumpTrail>(true)) Object.DestroyImmediate(old.gameObject);
            Transform body = root.GetComponentInChildren<PlayerController>(true).transform; // the root stays put: the player moves under it
            SwimWake wake = root.GetComponentInChildren<SwimWake>(true);
            ParticleSystem spray = wake != null ? (ParticleSystem)new SerializedObject(wake).FindProperty("spray").objectReferenceValue : null;

            var go = new GameObject("SuperJumpTrail");
            go.transform.SetParent(body, false);
            go.transform.localPosition = new Vector3(0f, 0.5f, 0f); // about the squid's middle
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.playOnAwake = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.65f);
            main.startSpeed = 0f;
            main.startSize = 0.5f;
            main.gravityModifier = 0.6f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 800;
            var emission = ps.emission;
            emission.rateOverTime = 0f; // emitted by distance, from SuperJumpTrail
            var shape = ps.shape;
            shape.enabled = false;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0f)));

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (spray != null) EditorUtility.CopySerialized(spray.GetComponent<ParticleSystemRenderer>(), renderer); // the wake's droplet look
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var trail = go.AddComponent<SuperJumpTrail>();
            var so = new SerializedObject(trail);
            so.FindProperty("blobs").objectReferenceValue = ps;
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefab);
            Debug.Log($"[PlayerFx] Super Jump trail on {PlayerPrefab} (look from {(spray != null ? spray.name : "nothing: default particles")})");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
