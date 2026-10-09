using System.Collections;
using UnityEngine;

// PlayerController: moving: walking, swimming, jumping, wall climbing and what is underfoot.
public partial class PlayerController
{

    void UpdateMovement()
    {
        if (isDead) return;
        if (superJumpPhase == SuperJumpPhase.Flying) { UpdateSuperJumpFlight(); return; }
        bool charging = superJumpPhase == SuperJumpPhase.Charging; // forced into swim form, can't move
        bool held = charging || IsRespawning;                         // likewise, just after respawning

        float prevHeight = controller.height;
        float prevRadius = controller.radius;
        bool wasClimbingWall = wasClimbing;
        bool swimHeld = held || !InputGate.MatchLocked && (ScriptedInput != null ? ScriptedInput.swim
                      : !InputGate.Blocked && input.Movement.Squidmode.ReadValue<float>() != 0);
        if (swimHeld)
        {
            swimMode = true;
            // Must drop before shrinking: Unity disables the controller if stepOffset > height + radius*2.
            controller.stepOffset = 0f;
            controller.height = 0.1f;
            controller.radius = 0.1f;
        }
        else
        {
            swimMode = false;
            isClimbing = false;
            controller.height = 1.92f;
            controller.radius = 0.5f;
        }
        if (controller.height != prevHeight)
        {
            // The capsule resizes around a fixed centre, so move the origin to keep its bottom on the floor.
            Vector3 beforeNudge = transform.position;
            transform.position += Vector3.up * ((controller.height - prevHeight) / 2f);

            // Growing against a wall (leaving swim mid-climb) would overlap it.
            if (wasClimbingWall && controller.radius > prevRadius)
                transform.position += climbNormal * (controller.radius - prevRadius + controller.skinWidth);

            Physics.SyncTransforms(); // Auto Sync Transforms is off; Move() would otherwise undo the nudge
            cameraFormOffset -= transform.InverseTransformVector(transform.position - beforeNudge); // eased out by UpdateCameraFormOffset
            lastFramePos += transform.position - beforeNudge; // not travel
        }
        bool enteredSwimModeThisFrame = swimMode && controller.height != prevHeight;

        bool grounded = controller.isGrounded;
        bool justLanded = grounded && !wasGroundedPrev;
        float fallHeight = justLanded ? lastGroundedY - transform.position.y : 0f;
        if (grounded) lastGroundedY = transform.position.y;
        if (grounded && transform.position.y > KillHeight) lastGroundPos = transform.position; // where a fall out of bounds is shown from
        wasGroundedPrev = grounded;

        gameObject.layer = swimMode ? swimLayer : playerLayer;
        if (surfaceTeam != 0)
        {
            bool inOwnInk = surfaceTeam == team;
            if (swimMode) realSpeed = inOwnInk ? swimSpeed + 2 : swimSpeed / 8; // enemy ink: slower than bare ground
            else          realSpeed = inOwnInk ? moveSpeed : moveSpeed - 2;
        }
        else
        {
            realSpeed = swimMode ? swimSpeed / 4 : moveSpeed;
        }
        if (!swimMode) realSpeed *= WeaponSpeedMultiplier;
        targetDir = held || InputGate.MatchLocked ? Vector2.zero
                  : ScriptedInput != null ? ScriptedInput.move
                  : InputGate.Blocked ? Vector2.zero : input.Movement.Move.ReadValue<Vector2>();
        targetDir.Normalize();
        currentDir = Vector2.SmoothDamp(currentDir, targetDir, ref currentDirVelocity, moveSmoothTime);
        if (held) currentDir = currentDirVelocity = Vector2.zero; // locked at once, no glide

        // Grabbing a wall needs input pushing into it; once on, only reaching the floor without
        // pressing up lets go (or the sensor losing it).
        bool wantsToClimb = currentDir.y > 0.01f;
        bool atWallBase = controller.isGrounded && !wantsToClimb;
        Vector3 flatMove = transform.forward * currentDir.y + transform.right * currentDir.x;
        bool pushingIntoWall = isClimbing && Vector3.Dot(flatMove, -climbNormal) > 0.3f; // how directly input must push in to grab
        bool regrabBlocked = Time.time < regrabBlockedUntil;
        bool canEngageClimb = !InWallJumpGrace && (wasClimbing ? !atWallBase : pushingIntoWall && !regrabBlocked);
        bool climbing = swimMode && isClimbing && canEngageClimb;
        effectivelyClimbing = climbing;
        if (climbing)
        {
            if (!wasClimbing) { velocityY = 0f; airMomentum = Vector3.zero; }
            wasClimbing = true;

            // Nothing below on a wall, so speed comes from own-ink swim speed, boosted by jumps.
            climbBoost = Mathf.Max(0f, climbBoost - 0.7f * Time.deltaTime); // boost lost per second
            realSpeed = (swimSpeed + 2) * Mathf.Lerp(0.8f, 1f, climbBoost); // 80% of own-ink swim speed, up to 100% boosted

            controller.slopeLimit = 90f;
            controller.stepOffset = 0f;

            velocityY += gravity * 0.1f * Time.deltaTime;
            velocityY = Mathf.Max(velocityY, -1.5f); // slide speed cap: below realSpeed so gravity slows a climb, never stops it

            Vector3 wallRight = Vector3.Cross(Vector3.up, climbNormal).normalized;
            Vector3 wallUp    = Vector3.Cross(climbNormal, wallRight).normalized;
            climbIntent = wallUp * currentDir.y - wallRight * currentDir.x;
            lastClimbVelocity = wallUp    * (currentDir.y * realSpeed + velocityY)
                              - wallRight *  currentDir.x * realSpeed;
            velocity = lastClimbVelocity - climbNormal * 2f;
            controller.Move(velocity * Time.deltaTime);
        }
        else
        {
            if (wasClimbing)
            {
                // Wall (or our ink on it) ran out, rather than leaving on purpose: keep momentum.
                bool lostWall = swimMode && !isClimbing && !InWallJumpGrace;
                if (lostWall) PopOffWall();
                else velocityY = Mathf.Max(velocityY, 0f);
            }
            wasClimbing = false;

            controller.slopeLimit = WalkableSlopeLimit;
            controller.stepOffset = swimMode ? 0f : 0.3f; // no stepping in squid form, it bounces off walls
            float startVelocityY = Mathf.Min(velocityY, MaxRiseSpeed);
            velocityY = startVelocityY + (gravity * 3) * Time.deltaTime;
            bool pressed = false;
            if (controller.isGrounded)
            {
                airSpeed = realSpeed;
                airMomentum = Vector3.zero;
                if (velocityY <= 0f) { velocityY = -1f; pressed = true; } // presses the capsule onto slopes; guarded so it can't cancel a jump this frame
            }

            // Moved by the average of this frame's start and end speeds: exact under steady gravity,
            // so jumps and hops reach the same height at any frame rate.
            float moveY = pressed ? velocityY : (startVelocityY + velocityY) * 0.5f;
            float hSpeed = controller.isGrounded ? realSpeed : airSpeed;
            climbIntent = flatMove;
            Vector3 move = flatMove * hSpeed;
            // Follow walkable slopes; a horizontal move launches off downhill slopes every frame.
            if (controller.isGrounded && velocityY <= 0f && groundNormal.y >= Mathf.Cos(WalkableSlopeLimit * Mathf.Deg2Rad))
                move = Vector3.ProjectOnPlane(move, groundNormal).normalized * move.magnitude;
            velocity = move + airMomentum + Vector3.up * moveY;
            controller.Move(velocity * Time.deltaTime);
        }

        // The other team's spawn barrier: back out to its edge, losing any carried speed into it.
        Vector3 pushOut = SpawnPad.PushOut(BodyCenter, controller.radius, team);
        if (pushOut != Vector3.zero)
        {
            controller.Move(pushOut);
            Vector3 outward = pushOut.normalized;
            if (Vector3.Dot(airMomentum, outward) < 0f) airMomentum = Vector3.ProjectOnPlane(airMomentum, outward);
        }

        bool onWallInk = effectivelyClimbing;
        bool inOwnInkNow = swimMode && team != 0 && (OnOwnSurfaceInk || onWallInk);
        Vector3 inkNormal = onWallInk ? climbNormal : groundNormal;

        // Enter splash only when entering swim form on ink or landing in ink from a height.
        bool enteredSwimModeOnInk  = enteredSwimModeThisFrame && surfaceTeam == team;
        bool landedOnInkFromHeight = justLanded && surfaceTeam == team && fallHeight > FallSplashMinHeight;
        if ((enteredSwimModeOnInk || landedOnInkFromHeight) && Time.time - lastEnterSplatTime >= 0.2f)
        {
            lastEnterSplatTime = Time.time;
            InkParticles.Spawn(swimSplashParticlesPrefab, transform.position + controller.center,
                Quaternion.FromToRotation(Vector3.up, groundNormal), teamColor);
        }

        // Exit splash on leaving own ink while still swimming.
        if (prevInOwnInk && !inOwnInkNow && swimMode && InkExitSplashPrefab != null && Time.time - lastExitSplatTime >= 0.2f)
        {
            lastExitSplatTime = Time.time;
            GameObject splash = Instantiate(InkExitSplashPrefab, transform.position + controller.center, Quaternion.FromToRotation(Vector3.up, inkNormal));
            splash.GetComponent<InkEmitter>().Setup(team, 3, true);
        }
        prevInOwnInk = inOwnInkNow;

        if (charging && (superJumpTimer += Time.deltaTime) >= superJumpChargeTime) LaunchSuperJump();
        if (transform.position.y < KillHeight) OnDeath(0, FellCause);
    }

    // Called every frame by WallClimbSensor while it sees an own-ink wall.
    public void SetClimbContact(Vector3 normal, SurfaceInkManager wallInk, Vector2 wallUV)
    {
        bool wasAlreadyClimbing = isClimbing;
        isClimbing = true;
        missedWallTime = 0f;
        climbNormal = normal; // raw, so movement can turn sharp corners at full speed
        visualClimbNormal = wasAlreadyClimbing
            ? Vector3.Slerp(visualClimbNormal, normal, 1f - Mathf.Exp(-15f * Time.deltaTime)) // visual easing toward a new wall
            : normal;
        climbInk = wallInk;
        climbInkUV = wallUV;
    }

    // Leave the wall keeping its momentum: scaled upward speed becomes a hop, plus a push over
    // the lip, limited air control until landing, and a brief no-regrab window.
    void PopOffWall()
    {
        float lift = lostWallAtInkEdge ? 0.25f : 0.8f; // share of upward speed kept: edge of our ink / top of the wall
        velocityY = Mathf.Max(lastClimbVelocity.y, 0f) * lift;
        airMomentum = Vector3.ProjectOnPlane(lastClimbVelocity, Vector3.up) - climbNormal * 2.5f; // push over the lip
        airSpeed = 3f;                               // air input speed until landing
        regrabBlockedUntil = Time.time + 0.3f;       // no re-grab for this long
    }

    // Brief misses (rounding a corner between rays) get WallGrace. Reaching the edge
    // of our ink or the top of the wall is definite and drops contact immediately.
    public void ClearClimbContact(bool atInkEdge = false, bool atWallTop = false)
    {
        missedWallTime += Time.deltaTime;
        if (atInkEdge || atWallTop || missedWallTime > WallGrace)
        {
            if (isClimbing) lostWallAtInkEdge = atInkEdge;
            isClimbing = false;
            climbInk = null;
        }
    }

    void Jump()
    {
        if (IsSuperJumping || isDead || IsRespawning || InputGate.MatchLocked) return;
        if (effectivelyClimbing)
        {
            float wallClimbSpeed = currentDir.y * realSpeed + velocityY;
            if (wallClimbSpeed < 0f)
            {
                // Descending: eject from the wall.
                velocityY += jump * 2 * 0.2f;            // eject: a fifth of a normal jump
                wallJumpGraceUntil = Time.time + 0.3f; // no re-grab for this long
            }
            else
            {
                // Climbing or holding: boost climb speed (decays, so spam to keep it) and cancel the slide.
                climbBoost = Mathf.Min(1f, climbBoost + 0.35f); // per jump
                velocityY = Mathf.Max(velocityY, 0f);
            }
            return;
        }
        if (controller.isGrounded || IsInInk) { velocityY += jump * 2; }
    }

    void GetInkTeam()
    {
        Vector3 origin = transform.position + controller.center;
        float reach = (controller.height / 2) + 0.3f;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit inkHit, reach, PhysicsLayers.Environment, QueryTriggerInteraction.Ignore))
        {
            Debug.DrawLine(origin, inkHit.point, Color.blue);
            groundInk = inkHit.collider.GetComponent<SurfaceInkManager>();
            groundInkUV = inkHit.textureCoord;
            groundNormal = inkHit.normal;
            surfaceTeam = groundInk != null ? groundInk.getSurfaceTeam(groundInkUV) : 0;
        }
        else if (controller.enabled && controller.isGrounded && superJumpPhase != SuperJumpPhase.Flying) // isGrounded is stale mid-flight
        {
            // On a ledge lip the centre ray misses; keep the last reading so speed isn't lost.
            Debug.DrawRay(origin, Vector3.down * reach, Color.yellow);
        }
        else
        {
            Debug.DrawRay(origin, Vector3.down * reach, Color.red);
            surfaceTeam = 0;
            groundInk = null;
            groundNormal = Vector3.up;
        }
    }
}
