using UnityEngine;

// Networked data structures for this game. Sent through SteamGlobal.SendAllData /
// SendDirectData (BinaryFormatter), so every type here must be [System.Serializable]
// and must avoid raw Unity types — use the NVector*/NColor wrappers below.
//
// (The previous contents were example data classes from another project and have been
// replaced. SteamNetwork / SteamGlobal are untouched.)

// ── Serialization helpers ─────────────────────────────────────────────────
[System.Serializable]
public class NVector2
{
    public float x, y;
    public NVector2(Vector2 v) { x = v.x; y = v.y; }
    public static implicit operator Vector2(NVector2 v) => new Vector2(v.x, v.y);
    public static implicit operator NVector2(Vector2 v) => new NVector2(v);
}

[System.Serializable]
public class NVector3
{
    public float x, y, z;
    public NVector3(Vector3 v) { x = v.x; y = v.y; z = v.z; }
    public static implicit operator Vector3(NVector3 v) => new Vector3(v.x, v.y, v.z);
    public static implicit operator NVector3(Vector3 v) => new NVector3(v);
}

[System.Serializable]
public class NColor
{
    public float r, g, b, a;
    public NColor(Color c) { r = c.r; g = c.g; b = c.b; a = c.a; }
    public static implicit operator Color(NColor c) => new Color(c.r, c.g, c.b, c.a);
    public static implicit operator NColor(Color c) => new NColor(c);
}

// ── Message IDs ───────────────────────────────────────────────────────────
// Passed as the dataID to SteamGlobal.SendAllData / SendDirectData / Bind.
// The transport reserves its own low IDs internally, so these can start at 0.
public enum NetMsg : ushort
{
    PlayerSpawn,     // a player entity exists / should be created on remotes
    PlayerDespawn,   // a player left — destroy their entity
    PlayerState,     // per-tick transform + state for one player (unreliable)
    Splat,           // a paint event to replay on remote clients
    ProjectileSpawn, // spawn a visual-only projectile on remote clients
    Damage,          // a player took damage / died
    MatchEvent,      // match phase changes (start / end / reset)
    InkReset,        // clear all ink surfaces before a match
    Teleport         // instant reposition (respawn) — bypasses PlayerState interpolation
}

// ── Per-player networked state (owning client broadcasts every send tick) ──
[System.Serializable]
public class PlayerStateData
{
    public ulong steamId;
    public NVector3 position;
    public float bodyYaw;    // horizontal facing (degrees)
    public float camPitch;   // head/camera pitch (degrees)
    public NVector2 moveDir; // smoothed input dir — drives remote trail/anim
    public int team;
    public bool swimMode;
    public bool climbing;
    public bool dead;
    public uint tick;
}

// ── Spawn / despawn ───────────────────────────────────────────────────────
[System.Serializable]
public class PlayerSpawnData
{
    public ulong steamId;
    public int team;
    public NVector3 position;
    public bool isHostPlayer;
    public string playerName; // sender's Steam persona name, so remotes can label the entity
}

// ── Splat replay. surfaceId is a deterministic index shared across clients
//    (all inkable surfaces are scene-placed, so order is identical everywhere). ──
[System.Serializable]
public class SplatData
{
    public int surfaceId;
    public NVector2 uv;
    public int splashSize;
    public int team;
}

// ── Instant reposition (respawn). Skips PlayerState interpolation so the
//    remote copy snaps instead of sliding across the level. ─────────────────
[System.Serializable]
public class TeleportData
{
    public ulong steamId;
    public NVector3 position;
    public float bodyYaw;
}

// ── Visual-only projectile spawn for remote clients ───────────────────────
[System.Serializable]
public class ProjectileSpawnData
{
    public ulong shooterSteamId;
    public NVector3 origin;
    public NVector3 velocity;
    public int team;
    public int splashSize;
    public float seed;
}

// ── Damage / death ────────────────────────────────────────────────────────
[System.Serializable]
public class DamageData
{
    public ulong targetSteamId;
    public float amount;
    public int fromTeam;
    public bool lethal;
}

// ── Match-level state ─────────────────────────────────────────────────────
public enum MatchPhase { Lobby, Countdown, Playing, Ended }

[System.Serializable]
public class MatchEventData
{
    public MatchPhase phase;
    public float serverTime;
}
