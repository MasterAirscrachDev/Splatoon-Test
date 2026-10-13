using UnityEngine;
using UnityEngine.InputSystem;

// The fluid lab's camera: hold the right mouse button to look around, and fly with WASD (Q/E down
// and up, Shift faster); the scroll wheel sets the speed. R goes back to where it started. Works
// while the lab's paused (unscaled time), to look at a frozen moment from any angle.
public class FlyCamera : MonoBehaviour
{
    [SerializeField] float speed = 6f;
    [SerializeField] float lookSensitivity = 0.15f; // degrees per pixel of mouse movement

    Vector3 homePos;
    Quaternion homeRot;
    float yaw, pitch;

    void Start()
    {
        homePos = transform.position;
        homeRot = transform.rotation;
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;
    }

    void Update()
    {
        Keyboard k = Keyboard.current;
        Mouse m = Mouse.current;
        if (k == null || m == null) return;
        if (k.rKey.wasPressedThisFrame)
        {
            transform.SetPositionAndRotation(homePos, homeRot);
            Start();
            return;
        }
        float scroll = m.scroll.ReadValue().y;
        if (scroll != 0f) speed = Mathf.Clamp(speed * (scroll > 0f ? 1.2f : 1f / 1.2f), 0.5f, 60f);
        if (!m.rightButton.isPressed) return;

        Vector2 look = m.delta.ReadValue() * lookSensitivity;
        yaw += look.x;
        pitch = Mathf.Clamp(pitch - look.y, -89f, 89f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        Vector3 move = Vector3.zero;
        if (k.wKey.isPressed) move += Vector3.forward;
        if (k.sKey.isPressed) move += Vector3.back;
        if (k.aKey.isPressed) move += Vector3.left;
        if (k.dKey.isPressed) move += Vector3.right;
        if (k.eKey.isPressed) move += Vector3.up;
        if (k.qKey.isPressed) move += Vector3.down;
        float fast = k.leftShiftKey.isPressed ? 3f : 1f;
        transform.position += transform.TransformDirection(move) * (speed * fast * Time.unscaledDeltaTime);
    }
}
