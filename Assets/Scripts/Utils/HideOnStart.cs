using UnityEngine;

// Removes this object's renderer at runtime, leaving it visible only in the editor.
public class HideOnStart : MonoBehaviour
{
    void Start()
    {
        Destroy(GetComponent<Renderer>());
        Destroy(this);
    }
}
