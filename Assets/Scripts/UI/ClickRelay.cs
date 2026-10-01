using UnityEngine;
using UnityEngine.EventSystems;

// Passes clicks on this UI element (with where they landed) to whoever listens.
public class ClickRelay : MonoBehaviour, IPointerClickHandler
{
    public event System.Action<PointerEventData> Clicked;

    public void OnPointerClick(PointerEventData e) => Clicked?.Invoke(e);
}
