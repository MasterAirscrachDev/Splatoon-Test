using UnityEngine;
using UnityEngine.EventSystems;

// Tracks whether the pointer is over this UI element, so one element's hover can highlight another.
public class HoverFlag : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public bool Hovered { get; private set; }

    public void OnPointerEnter(PointerEventData e) => Hovered = true;
    public void OnPointerExit(PointerEventData e)  => Hovered = false;
    void OnDisable() => Hovered = false; // no exit event when hidden mid-hover
}
