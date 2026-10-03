using UnityEngine;

// Network message types. Sent via NetCodec (add new ones to NetCodec.Types); they use the
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
    Splat,           // paint events to replay on remote clients: a SplatBatchData per net tick
    ProjectileSpawn, // spawn a visual-only projectile on remote clients
    Damage,          // a player took damage / died
    MatchEvent,      // match phase changes, from the host (see MatchPhase)
    InkReset,        // host's "Reset map" in free roam: clear all ink
    Teleport,        // instant reposition (respawn) — bypasses PlayerState interpolation
    TeamAssign,      // host's roster: every player's role
    SubSpawn,        // a player placed/threw a sub (beacon, sprinkler), or it landed
    SubDestroy,      // a sub broke, expired, or (beacon) was landed on
    SubDamage,       // a hit on someone's sub, sent to its owner
    InkStrike        // a player called in an Inkstrike (special)
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
    public bool subReady;     // enough ink for the sub: the light on their tank
    public float ink;         // tank level, shown on their model's tank
    public int weapon;        // index into PlayerLoadout's weapons, so remotes show the right one
    public bool weaponDown;   // its down pose (a roller rolling)
    public float modelYaw;    // which way the kid model faces (it turns to the way they move unless aiming)
    public bool aiming;       // firing or using a sub: their weapon follows camPitch
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

// A net tick's worth of our splats in one message, as compact flat arrays (index i is one splat).
[System.Serializable]
public class SplatBatchData
{
    public ushort[] surfaceIds;
    public ushort[] uvs;       // x, y per splat, -0.5..1.5 of the texture in 65535 steps (a splat bridged over a seam can be centred off its texture)
    public byte[] splashSizes;
    public byte[] teams;

    const float UVMin = -0.5f, UVSpan = 2f, Steps = 65535f;
    public static ushort PackUV(float v) => (ushort)Mathf.RoundToInt(Mathf.Clamp01((v - UVMin) / UVSpan) * Steps);
    public static float UnpackUV(ushort v) => UVMin + v / Steps * UVSpan;
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
    public NVector3 inherit;   // the shooter's carried velocity, shared by the volley
    public float[] velocities; // launch velocity per shot (xyz), before the weapon's ballistics
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
    public string source; // weapon, sub or special that did it ("splatted with")
}

// ── Sub weapons: ids are per owner ───────────────────────────────────────
public enum SubType { Beacon, Sprinkler, CurlingBomb }

[System.Serializable]
public class SubData
{
    public ulong ownerId;
    public int subId;
    public int team;
    public int subType;       // SubType
    public NVector3 position;
    public NVector3 velocity; // thrown subs, until they land
    public NVector3 normal;   // the surface it's stuck to, once landed
    public bool landed;
    public float fuse;        // curling bomb: seconds left until it explodes
}

[System.Serializable]
public class SubDamageData
{
    public ulong ownerId;
    public int subId;
    public float amount;
    public int fromTeam;
    public ulong attackerSteamId;
}

// ── Specials ──────────────────────────────────────────────────────────────
public enum SpecialType { BubbleShield, InkStrike }

[System.Serializable]
public class InkStrikeData
{
    public ulong ownerId;
    public int team;
    public NVector3 position; // the target, on the ground
}

// ── Roster from the host: roles[i] (a PlayerRole) for ids[i] ─────────────
[System.Serializable]
public class TeamAssignData
{
    public ulong[] ids;
    public int[] roles;
    public int colourPair; // the host's team colours (NetGameManager's pairs)
}

// ── Match-level state ─────────────────────────────────────────────────────
// FreeRoam between matches; a match runs Countdown -> Playing -> TimesUp -> Results -> FreeRoam.
public enum MatchPhase { FreeRoam, Countdown, Playing, TimesUp, Results }

[System.Serializable]
public class MatchEventData
{
    public MatchPhase phase;
    public float serverTime;
    public float duration;                             // how long this phase lasts, in seconds
    public int alphaScore, betaScore, neutralScore;    // Results: the host's final turf (texels)
    public bool lateJoin;                              // sent to a mid-match joiner: they spectate this round
    public int colourPair;                             // the host's team colours (a new game picks new ones)
}
