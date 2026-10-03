using System.Collections;
using UnityEngine;

// PlayerController: Super Jumps: charge, flight and landing.
public partial class PlayerController
{

    // ── Super Jump ─────────────────────────────────────────────────────────

    // Charges for superJumpChargeTime (forced into swim form, movement locked), then launches on
    // a high arc to where the target (teammate, beacon or spawn) was when the jump started.
    // Once started, only dying (or a respawn/teleport) cancels it. Returns false if it can't start.
    public bool StartSuperJump(ISuperJumpTarget target)
    {
        if (playerMode != PlayerMode.Client || isDead || IsRespawning || IsSuperJumping || team == 0 || InputGate.MatchLocked) return false;
        if (!SuperJumpTargets.Alive(target) || ReferenceEquals(target, this) || !target.IsValidTargetFor(this)) return false;
        superJumpTarget = target;
        superJumpLanding = target.JumpPosition;
        superJumpLandsOnGround = Physics.Raycast(superJumpLanding + Vector3.up, Vector3.down, out RaycastHit ground, 8f, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore);
        if (superJumpLandsOnGround) superJumpLanding = ground.point;
        superJumpPhase = SuperJumpPhase.Charging;
        superJumpTimer = 0f;
        return true;
    }

    public void CancelSuperJump()
    {
        superJumpPhase = SuperJumpPhase.None;
        superJumpTarget = null;
    }
    public bool IsValidTargetFor(PlayerController jumper) =>
        jumper != null && jumper != this && isActiveAndEnabled && !isDead && team != 0 && team == jumper.team;
    public void OnSuperJumpLanded(PlayerController jumper) { }

    void LaunchSuperJump()
    {
        // Our capsule's bottom just above the locked landing spot (the capsule is squid-sized now).
        superJumpFrom = transform.position;
        superJumpTo = superJumpLanding;
        if (superJumpLandsOnGround) superJumpTo += Vector3.up * (controller.height / 2f - controller.center.y + 0.2f);

        float distance = Vector3.ProjectOnPlane(superJumpTo - superJumpFrom, Vector3.up).magnitude;
        superJumpDuration = superJumpFlightTime;
        superJumpArc = superJumpHeight;
        superJumpTimer = 0f;
        superJumpPhase = SuperJumpPhase.Flying;
        isClimbing = wasClimbing = effectivelyClimbing = false;
        airMomentum = Vector3.zero;
        prevInOwnInk = false; // leaving the ink by jump isn't an exit splash
    }

    // Moves along the arc directly (no collision), then hands back to normal movement.
    void UpdateSuperJumpFlight()
    {
        superJumpTimer += Time.deltaTime;
        float t = Mathf.Clamp01(superJumpTimer / superJumpDuration);
        transform.position = Vector3.Lerp(superJumpFrom, superJumpTo, t) + Vector3.up * (superJumpArc * 4f * t * (1f - t));
        Physics.SyncTransforms();
        velocityY = (superJumpTo.y - superJumpFrom.y + superJumpArc * (4f - 8f * t)) / superJumpDuration;
        if (t < 1f) return;

        ISuperJumpTarget landedOn = superJumpTarget;
        CancelSuperJump();
        if (SuperJumpTargets.Alive(landedOn)) landedOn.OnSuperJumpLanded(this); // a beacon breaks, if it's still there
        velocityY = 0;
        wasGroundedPrev = false;
        lastGroundedY = transform.position.y + FallSplashMinHeight + 1f; // splash if we land in our ink
    }
}
