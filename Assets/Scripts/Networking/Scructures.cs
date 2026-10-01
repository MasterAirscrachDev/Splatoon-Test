using UnityEngine;

// Network message types. Sent via BinaryFormatter, so they must be [Serializable] and use the
// NVector*/NColor wrappers instead of Unity types.

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

// ── Message IDs (the transport offsets these past its own internal IDs) ──
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
    Teleport,        // instant reposition (respawn) — bypasses PlayerState interpolation
    TeamAssign,      // host's roster: every player's role
    BeaconSpawn,     // a player placed a beacon (sub)
    BeaconDestroy,   // a beacon broke, expired or was landed on
    BeaconDamage     // a hit on someone's beacon, sent to its owner
}

// Where a player sits in the roster. Alpha/Beta match the team numbers used everywhere else.
public enum PlayerRole { Alpha = 1, Beta = 2, Spectator = 3, NotPlaying = 4 }

// ── Per-player state, broadcast by the owning client every tick ──────────
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
    public bool shielded;     // bubble shield (special) active
    public bool specialReady; // special charged, shown on everyone's player bar
}

// ── Spawn / despawn ───────────────────────────────────────────────────────
[System.Serializable]
public class PlayerSpawnData
{
    public ulong steamId;
    public int team;
    public NVector3 position;
    public bool isHostPlayer;
    public string playerName; // sender's Steam name
}

// ── Splat replay (surfaceId is the same on every client) ────────────────
[System.Serializable]
public class SplatData
{
    public int surfaceId;
    public NVector2 uv;
    public int splashSize;
    public int team;
}

// ── Instant reposition (respawn), bypassing interpolation ───────────────
[System.Serializable]
public class TeleportData
{
    public ulong steamId;
    public NVector3 position;
    public float bodyYaw;
}

// ── One volley of shots, replayed as visual-only projectiles on remotes ──
[System.Serializable]
public class ProjectileSpawnData
{
    public ulong shooterSteamId;
    public int team;
    public NVector3 origin;
    public float[] velocities; // xyz per shot
    public int[] splashSizes;  // per shot
    public bool[] visible;     // per shot
}

// ── Hit on another player, sent by the shooter to the victim's client ────
[System.Serializable]
public class DamageData
{
    public ulong targetSteamId;
    public ulong attackerSteamId;
    public float amount;
    public int fromTeam;
}

// ── Beacons (sub): ids are per owner ─────────────────────────────────────
[System.Serializable]
public class BeaconData
{
    public ulong ownerId;
    public int beaconId;
    public int team;
    public NVector3 position;
}

[System.Serializable]
public class BeaconDamageData
{
    public ulong ownerId;
    public int beaconId;
    public float amount;
    public int fromTeam;
    public ulong attackerSteamId;
}

// ── Roster from the host: roles[i] (a PlayerRole) for ids[i] ─────────────
[System.Serializable]
public class TeamAssignData
{
    public ulong[] ids;
    public int[] roles;
}

// ── Match-level state ─────────────────────────────────────────────────────
public enum MatchPhase { Lobby, Countdown, Playing, Ended }

[System.Serializable]
public class MatchEventData
{
    public MatchPhase phase;
    public float serverTime;
    public float duration; // match length in seconds, when starting
}
