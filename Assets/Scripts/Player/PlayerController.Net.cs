using System.Collections;
using UnityEngine;

// PlayerController: networking: identity and roster, state sent and applied, remote interpolation.
public partial class PlayerController
{

    void UpdateNetInterpolation()
    {
        if (!netInitialised) return;
        float ease = 1f - Mathf.Exp(-NetLerpSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, netTargetPos, ease);
        Quaternion targetRot = Quaternion.Euler(0f, netTargetYaw, 0f);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, ease);
        if (playerCamera != null) playerCamera.localEulerAngles = Vector3.right * netCamPitch;
    }

    // ── Networking API (driven by NetGameManager) ─────────────────────────
    public void SetPlayerMode(PlayerMode mode) => playerMode = mode;
    public void SetTeam(int t)
    {
        bool changed = t != team;
        team = t; Role = Teams.Role(t);
        if (changed) { ApplyTeamColor(); TeamChanged?.Invoke(); }
    }

    // Where this player is looking up/down (degrees, down positive): theirs as sent over the network
    // for a remote player. Spectators watch from behind at this pitch.
    public float ViewPitch => playerMode == PlayerMode.Network ? netCamPitch : cameraPitch;

    // Our team changed (spawned onto one, or moved by the roster): team-tinted parts re-tint.
    public event System.Action TeamChanged;

    // Applies a roster role. Spectators and benched players have no team and their entity is
    // hidden; a local player joining (or switching) a team respawns at that team's spawn.
    public void SetRole(PlayerRole role)
    {
        int newTeam = Teams.Of(role);
        bool playing = newTeam != 0;
        bool wasPlaying = transform.root.gameObject.activeSelf;
        bool teamChanged = newTeam != team;
        Role = role;
        team = newTeam;
        if (teamChanged && playing) ApplyTeamColor();
        if (teamChanged) TeamChanged?.Invoke();
        transform.root.gameObject.SetActive(playing);
        if (playing && IsLocalPlayer && controller != null && (teamChanged || !wasPlaying)) Respawn();
    }
    public void SetOwner(ulong steamId) => OwnerId = steamId;
    public void SetDisplayName(string name) => DisplayName = name;

    public PlayerStateData GetNetState(ulong steamId, uint tick)
    {
        return new PlayerStateData
        {
            steamId  = steamId,
            position = transform.position,
            bodyYaw  = transform.eulerAngles.y,
            camPitch = cameraPitch,
            moveDir  = new Vector2(currentDir.x, currentDir.y),
            team     = team,
            swimMode = swimMode,
            climbing = effectivelyClimbing,
            dead     = isDead,
            tick     = tick,
            shielded = Shielded,
            specialReady = SpecialCharged,
            subReady = SubReady,
            ink      = inkLevel,
            weapon   = WeaponIndex,
            weaponDown = WeaponDown,
            modelYaw = modelYaw,
            aiming   = Aiming
        };
    }

    public void ApplyNetState(PlayerStateData s)
    {
        if (playerMode != PlayerMode.Network) return;

        netTargetPos = s.position;
        netTargetYaw = s.bodyYaw;
        netCamPitch  = s.camPitch;
        currentDir   = s.moveDir;
        swimMode     = s.swimMode; // team comes from the host's roster, not from state updates
        Shielded     = s.shielded;
        SpecialCharged = s.specialReady;
        SubReady     = s.subReady;
        inkLevel     = s.ink;
        ShowInkLevel();
        WeaponIndex  = s.weapon;
        WeaponDown   = s.weaponDown;
        netModelYaw  = s.modelYaw;
        netAiming    = s.aiming;
        isClimbing = s.climbing;
        effectivelyClimbing = s.climbing;

        if (s.dead != isDead)
        {
            isDead = s.dead;
            if (isDead) HideModels(); else ShowModels();
        }

        if (controller != null)
        {
            controller.height = swimMode ? 0.1f : 1.92f;
            controller.radius = swimMode ? 0.1f : 0.5f;
        }

        if (!netInitialised)
        {
            // Remotes are driven by interpolation only; snap to the first pose.
            netInitialised = true;
            if (controller != null) controller.enabled = false;
            transform.position = netTargetPos;
            transform.rotation = Quaternion.Euler(0f, netTargetYaw, 0f);
        }
    }

    // Off the main thread: only touches the cached snapshot.
    void NetTickBroadcast(uint tick)
    {
        PlayerStateData snap = cachedNetState;
        if (snap == null) return;
        snap.tick = tick;
        // Unreliable: a newer snapshot follows every tick, and reliable delivery stalled on loss.
        SteamGlobal.SendAllData((ushort)NetMsg.PlayerState, snap, reliable: false);
    }
}
