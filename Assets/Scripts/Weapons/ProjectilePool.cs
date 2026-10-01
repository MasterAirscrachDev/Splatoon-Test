using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;

// Reuses projectile instances (one pool per prefab) instead of Instantiate/Destroy per shot.
// Pools are dropped on scene unload and on entering play mode, since pooled objects live in the scene.
public static class ProjectilePool
{
    static readonly Dictionary<GameObject, ObjectPool<ProjectileSystem>> pools = new Dictionary<GameObject, ObjectPool<ProjectileSystem>>();
    static readonly Dictionary<ProjectileSystem, ObjectPool<ProjectileSystem>> ownerOf = new Dictionary<ProjectileSystem, ObjectPool<ProjectileSystem>>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Clear();
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    static void OnSceneUnloaded(Scene _) => Clear();

    static void Clear()
    {
        pools.Clear();
        ownerOf.Clear();
    }

    // Returns an active projectile at the given pose; call Setup on it next.
    public static ProjectileSystem Get(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        if (!pools.TryGetValue(prefab, out var pool))
        {
            ObjectPool<ProjectileSystem> created = null;
            created = new ObjectPool<ProjectileSystem>(
                createFunc: () =>
                {
                    GameObject go = Object.Instantiate(prefab);
                    go.SetActive(false); // activated by Get once it has its real pose
                    ProjectileSystem p = go.GetComponent<ProjectileSystem>();
                    ownerOf[p] = created;
                    return p;
                },
                actionOnRelease: p => p.gameObject.SetActive(false),
                actionOnDestroy: p => { if (p != null) { ownerOf.Remove(p); Object.Destroy(p.gameObject); } },
                collectionCheck: false, defaultCapacity: 32, maxSize: 256);
            pool = created;
            pools[prefab] = pool;
        }

        ProjectileSystem proj;
        do { proj = pool.Get(); } while (proj == null); // skip instances destroyed externally
        proj.transform.SetPositionAndRotation(position, rotation);
        proj.gameObject.SetActive(true);
        return proj;
    }

    // Destroys it instead if it didn't come from a pool.
    public static void Release(ProjectileSystem p)
    {
        if (p == null) return;
        if (ownerOf.TryGetValue(p, out var pool)) pool.Release(p);
        else Object.Destroy(p.gameObject);
    }
}
