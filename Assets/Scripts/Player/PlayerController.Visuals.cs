using System.Collections;
using UnityEngine;

// PlayerController: how the player looks: team colour, the kid and squid models and which way they face, the swim trail, the tank.
public partial class PlayerController
{

    // Swaps every child renderer slot using `shared` for one new instance, and returns it.
    Material InstanceMaterial(Material shared)
    {
        Material inst = new Material(shared);
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] == shared) { mats[i] = inst; changed = true; }
            if (changed) r.sharedMaterials = mats;
        }
        return inst;
    }

    void ApplyTeamColor()
    {
        NetGameManager gm = NetGameManager.Instance;
        if (gm != null) teamColor = team == 2 ? gm.BetaTeam : gm.AlphaTeam;
        if (InkTankScaler != null)
        {
            Renderer r = InkTankScaler.GetComponentInChildren<Renderer>();
            if (r != null) r.material.color = teamColor;
        }
        if (ViewmodelSquid != null && ViewmodelSquid.transform.childCount > 0)
        {
            Renderer r = ViewmodelSquid.transform.GetChild(0).GetComponent<Renderer>();
            if (r != null) r.material.color = teamColor;
        }
        if (uiController != null) uiController.SetTeamColor(teamColor);
    }

    void UpdateViewmodels()
    {
        if (isDead) return;
        if (ViewmodelPlayer == null || ViewmodelSquid == null) return;

        Vector3 playerTarget = swimMode ? new Vector3(1,0,1) : Vector3.one;
        Vector3 squidTarget  = swimMode && (!IsInInk || IsSuperJumping) ? Vector3.one : Vector3.zero;

        ViewmodelPlayer.transform.localScale = !swimMode && (AttackHeld || ActionQueued)
            ? playerTarget // attacking out of swim form: straight up, so the weapon can go at once
            : Vector3.Lerp(ViewmodelPlayer.transform.localScale, playerTarget, 1f - Mathf.Exp(-ViewmodelSwitchSpeed * Time.deltaTime));
        ViewmodelSquid.transform.localScale  = Vector3.Lerp(ViewmodelSquid.transform.localScale,  squidTarget,  1f - Mathf.Exp(-ViewmodelSwitchSpeed * Time.deltaTime));
        ViewmodelPlayer.SetActive(ViewmodelPlayer.transform.localScale.y > 0.05f);
        UpdateSquidFacing();
        UpdateModelFacing();
    }

    void UpdateModelFacing()
    {
        if (playerMode == PlayerMode.Network) modelYaw = Mathf.LerpAngle(modelYaw, netModelYaw, 1f - Mathf.Exp(-NetLerpSpeed * Time.deltaTime));
        else if (Aiming) modelYaw = transform.eulerAngles.y;
        else if (swimMode)
        {
            // Come out of swim form facing the way the squid was heading.
            Vector3 heading = Vector3.ProjectOnPlane(ViewmodelSquid.transform.forward, Vector3.up);
            if (heading.sqrMagnitude > 0.01f && !effectivelyClimbing) modelYaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
        }
        else if (currentDir.magnitude > 0.1f)
        {
            Vector3 dir = transform.TransformDirection(new Vector3(currentDir.x, 0f, currentDir.y));
            modelYaw = Mathf.LerpAngle(modelYaw, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 1f - Mathf.Exp(-ModelTurnRate * Time.deltaTime));
        }
        ApplyModelYaw();
    }

    void ApplyModelYaw()
    {
        if (ViewmodelPlayer != null) ViewmodelPlayer.transform.rotation = Quaternion.Euler(0f, modelYaw, 0f);
    }

    // Submerged (or moving on ink) the squid lies along the surface facing the input direction.
    // Otherwise it points where it's actually travelling: arcing through the air, falling, hopping.
    void UpdateSquidFacing()
    {
        Transform squid = ViewmodelSquid.transform;
        bool airborne = swimMode && !IsInInk && !effectivelyClimbing;
        Quaternion target;
        if (superJumpPhase == SuperJumpPhase.Charging)
            target = FacingAlong(Vector3.ProjectOnPlane(superJumpLanding - transform.position, Vector3.up).normalized + Vector3.up * 2f); // nose up toward the launch
        else if (airborne && travelVelocity.sqrMagnitude > 0.25f)
            target = FacingAlong(travelVelocity);
        else if (swimMode && currentDir.magnitude > 0.05f)
        {
            GetSwimPose(out Vector3 moveDir, out Vector3 upHint);
            if (moveDir.sqrMagnitude > 0.001f) squid.rotation = Quaternion.LookRotation(moveDir, upHint);
            return;
        }
        else
        {
            // Idle: settle flat on whatever is below, keeping the heading.
            Vector3 heading = Vector3.ProjectOnPlane(squid.forward, groundNormal);
            if (heading.sqrMagnitude < 0.01f) heading = Vector3.ProjectOnPlane(transform.forward, groundNormal);
            target = Quaternion.LookRotation(heading.normalized, groundNormal);
        }
        squid.rotation = Quaternion.Slerp(squid.rotation, target, 1f - Mathf.Exp(-12f * Time.deltaTime)); // turn rate
    }

    // Nose along `dir`, back as close to world up as that allows.
    Quaternion FacingAlong(Vector3 dir)
    {
        dir.Normalize();
        Vector3 up = Vector3.ProjectOnPlane(Vector3.up, dir);
        if (up.sqrMagnitude < 0.0001f) up = -transform.forward; // straight up or down
        return Quaternion.LookRotation(dir, up);
    }

    void UpdateSquidTrail()
    {
        if (!Wading) return;
        if (effectivelyClimbing) { if (climbInk != null) climbInk.PaintTrailNormal(climbInkUV, 4, 4, 0.01f); }
        else if (groundInk != null) groundInk.PaintTrailNormal(groundInkUV, 4, 4, 0.01f);
    }

    // Movement direction and up vector for the squid and trail, flat on the wall or floor/slope.
    void GetSwimPose(out Vector3 moveDir, out Vector3 upHint)
    {
        if (effectivelyClimbing)
        {
            Vector3 wallRight = Vector3.Cross(Vector3.up, visualClimbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(visualClimbNormal, wallRight).normalized;
            moveDir = (wallUp * currentDir.y - wallRight * currentDir.x).normalized;
            upHint  = visualClimbNormal;
        }
        else
        {
            Vector3 flatDir = (transform.forward * currentDir.y + transform.right * currentDir.x).normalized;
            moveDir = Vector3.ProjectOnPlane(flatDir, groundNormal).normalized; // keep orthogonal to upHint
            upHint  = groundNormal;
        }
    }

    // The tank on the model's back (remote copies show the owner's level from state updates).
    void ShowInkLevel()
    {
        if (InkTankScaler == null) return;
        Vector3 s = InkTankScaler.transform.localScale;
        s.y = inkLevel;
        InkTankScaler.transform.localScale = s;
    }
    public void MarkAiming(float hold = AimHold)
    {
        if (!Aiming) { modelYaw = transform.eulerAngles.y; ApplyModelYaw(); } // snap round before the shot leaves
        aimUntil = Mathf.Max(aimUntil, Time.time + hold);
    }
}
