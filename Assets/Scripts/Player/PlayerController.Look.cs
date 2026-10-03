using System.Collections;
using UnityEngine;

// PlayerController: the view: mouse, stick and gyro look, recentring, the form-switch camera glide.
public partial class PlayerController
{

    // Eases out the offset that cancels form-switch nudges, so the camera glides instead of snapping.
    void UpdateCameraFormOffset()
    {
        if (playerCamera == null) return;
        cameraFormOffset = Vector3.SmoothDamp(cameraFormOffset, Vector3.zero, ref cameraFormOffsetVelocity, 0.12f); // settle time
        playerCamera.localPosition = cameraBaseLocalPos + cameraFormOffset;
    }

    // The mouse moves the view by how far it moved (degrees per count, lightly smoothed); the right
    // stick turns it at a rate (degrees per second, shaped for fine aim and sped up at the edge), so
    // neither depends on the frame rate. Each is scaled by the player's settings. Recenter levels the
    // view (only the mouse or stick looking up or down cuts it short; the gyro rides on top).
    void UpdateLook()
    {
        bool hardware = ScriptedInput == null && !InputGate.Blocked;
        Vector2 delta = ScriptedInput != null ? ScriptedInput.look : hardware ? input.Movement.LookDelta.ReadValue<Vector2>() : Vector2.zero;
        Vector2 stick = ScriptedInput != null ? ScriptedInput.lookStick : hardware ? input.Movement.LookStick.ReadValue<Vector2>() : Vector2.zero;
        // Gyro (on a gamepad): moves the view directly; with one, the stick only turns (Splatoon-style).
        bool gyroAiming = hardware && InputMode.Scheme == ControlScheme.Gamepad && SteamGyro.Enabled && SteamGyro.HasGyro;
        Vector2 gyro = gyroAiming ? Vector2.Scale(SteamGyro.Rate, new Vector2(GameSettings.GyroSensitivityX, GameSettings.GyroSensitivityY)) * gyroSensitivity : Vector2.zero;
        if (gyroInvertY) gyro.y = -gyro.y;
        if (gyroAiming) stick.y = 0f;
        if (ScriptedInput != null ? ScriptedInput.recenter : hardware && input.Movement.Recenter.WasPressedThisFrame()) recentering = true;

        currentMouseDelta = Vector2.SmoothDamp(currentMouseDelta, delta, ref currentMouseDeltaVelocity, mouseSmoothTime);
        Vector2 aim = currentMouseDelta * (mouseSensitivity * GameSettings.MouseSensitivity)
                    + StickTurn(stick) * (GameSettings.StickSensitivity * Time.deltaTime); // degrees this frame
        if (invertY) aim.y = -aim.y;
        if (Mathf.Abs(aim.y) > 0.01f) recentering = false; // looking up or down takes over again
        Vector2 turn = aim + gyro * Time.deltaTime;
        cameraPitch -= turn.y;
        if (recentering)
        {
            cameraPitch = Mathf.MoveTowards(cameraPitch, 0f, RecenterSpeed * Time.deltaTime);
            recentering = cameraPitch != 0f;
        }
        cameraPitch = Mathf.Clamp(cameraPitch, -70.0f, 80.0f);
        playerCamera.localEulerAngles = Vector3.right * cameraPitch;
        transform.Rotate(Vector3.up * turn.x);
    }

    // The right stick's turn rate, degrees per second.
    Vector2 StickTurn(Vector2 stick)
    {
        float tilt = Mathf.Clamp01(stick.magnitude);
        if (tilt < 1e-4f) { stickEdgeTime = 0f; return Vector2.zero; }
        stickEdgeTime = tilt >= StickEdge ? stickEdgeTime + Time.deltaTime : 0f;
        Vector2 shaped = stick / stick.magnitude * Mathf.Pow(tilt, stickCurve);
        float boost = Mathf.Lerp(1f, stickBoost, Mathf.Clamp01(stickEdgeTime / stickBoostTime)); // turning only
        return new Vector2(shaped.x * stickSpeed.x * boost, shaped.y * stickSpeed.y);
    }
}
