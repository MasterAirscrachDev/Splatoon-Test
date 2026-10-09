using System.Collections;
using UnityEngine;

// PlayerController: dying, the death camera, respawning and placing the player.
public partial class PlayerController
{

    void HideModels()
    {
        if (ViewmodelPlayer != null) ViewmodelPlayer.SetActive(false);
        if (ViewmodelSquid  != null) ViewmodelSquid.SetActive(false);
    }

    void ShowModels()
    {
        if (ViewmodelPlayer != null) ViewmodelPlayer.SetActive(true);
        if (ViewmodelSquid  != null) ViewmodelSquid.SetActive(true);
    }

    // killerId: who splatted us (0: nobody, e.g. we fell); cause: with what.
    public void OnDeath(ulong killerId = 0, string cause = null)
    {
        if (isDead || playerMode != PlayerMode.Client) return;
        bool fell = transform.position.y < KillHeight;
        isDead   = true;
        swimMode = true;
        CancelSuperJump();
        velocity = Vector3.zero;
        velocityY = 0f;
        HideModels();
        if (Hitbox != null) Hitbox.ResetHealth(); // a fall skips the hitbox, so respawn healthy anyway
        KilledBy = killerId;
        KilledWith = cause;
        deathSpot = fell ? lastGroundPos : transform.position;
        deathCamYaw = transform.eulerAngles.y;
        deathCam = playerCamera != null;
        respawnHoldUntil = -1f;
        respawnAt = Time.time + SplattedTime;
        Splatted?.Invoke(this, killerId, cause);
        pendingRespawn = StartCoroutine(RespawnAfterDeath());
    }

    IEnumerator RespawnAfterDeath()
    {
        yield return new WaitForSeconds(SplattedTime);
        pendingRespawn = null;
        Respawn();
        respawnHoldUntil = Time.time + RespawnHoldTime; // in swim form, still (UpdateMovement)
    }

    // Circles the rig over the spot; the camera hangs 7.5m back along it, so it orbits.
    void UpdateDeathCamera()
    {
        deathCamYaw += DeathCamSpin * Time.deltaTime;
        playerCamera.SetPositionAndRotation(deathSpot + Vector3.up * DeathCamHeight, Quaternion.Euler(DeathCamPitch, deathCamYaw, 0f));
    }

    public void Respawn()
    {
        if (pendingRespawn != null) { StopCoroutine(pendingRespawn); pendingRespawn = null; } // respawned some other way first
        respawnHoldUntil = -1f;
        if (deathCam)
        {
            deathCam = false; // back on our shoulder
            playerCamera.localPosition = cameraBaseLocalPos;
            playerCamera.localRotation = Quaternion.Euler(cameraPitch, 0f, 0f);
        }
        string tag = team == 1 ? "AlphaSpawn" : "BetaSpawn";
        GameObject[] pts = GameObject.FindGameObjectsWithTag(tag);
        Vector3 pos = pts.Length > 0
            ? pts[Random.Range(0, pts.Length)].transform.position
            : spawnPoint;
        PlaceAt(pos, transform.eulerAngles.y);
        isDead = false;
        ShowModels();

        if (playerMode == PlayerMode.Client)
            SteamGlobal.SendAllData((ushort)NetMsg.Teleport, new TeleportData
            {
                steamId  = localSteamId,
                position = pos,
                bodyYaw  = transform.eulerAngles.y
            });
    }

    // Instantly moves the local player (feet at feetPos) and resets all motion and climb state.
    public void PlaceAt(Vector3 feetPos, float yaw)
    {
        controller.enabled = false;
        try
        {
            swimMode = false;
            controller.height = 1.92f;
            controller.radius = 0.5f;
            transform.SetPositionAndRotation(feetPos, Quaternion.Euler(0f, yaw, 0f));
            modelYaw = yaw;
            ApplyModelYaw();
        }
        finally
        {
            controller.enabled = true; // never leave it disabled
        }
        velocityY  = 0f;
        velocity   = Vector3.zero;
        currentDir = currentDirVelocity = Vector2.zero;
        isClimbing = false;
        wasClimbing = false;
        effectivelyClimbing = false;
        missedWallTime = 0f;
        regrabBlockedUntil = -1f;
        airMomentum = Vector3.zero;
        climbBoost = 0f;
        wallJumpGraceUntil = -1f;
        prevInOwnInk = false;
        wasGroundedPrev = false;
        lastGroundedY = feetPos.y;
        lastGroundPos = feetPos;
        cameraFormOffset = cameraFormOffsetVelocity = Vector3.zero; // a teleport snaps the camera
        CancelSuperJump();
        lastFramePos = feetPos;
        travelVelocity = Vector3.zero;
    }

    // Remote copies: snap on Teleport instead of interpolating across the map.
    public void ApplyTeleport(Vector3 pos, float yaw)
    {
        if (playerMode != PlayerMode.Network) return;
        netTargetPos = pos;
        netTargetYaw = yaw;
        transform.position = pos;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }
}
