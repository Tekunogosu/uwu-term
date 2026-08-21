using UnityEngine;
using UnityEngine.EventSystems;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Takes the mouse wheel away from OSA for a terminal the grid screen has taken over.
    ///
    /// OSA receives scrolling as IScrollHandler, and Unity delivers that to the first
    /// ancestor of whatever the pointer hit that implements the interface. OSA lives on the
    /// scroll view; the viewport sits between it and the rows, so a handler put there is
    /// found first and the event stops with it.
    ///
    /// Doing it this way rather than polling the wheel means the EventSystem decides which
    /// terminal is under the pointer, which it already does correctly for stacked windows -
    /// and OSA stops scrolling rows nobody can see, which is what was still dragging the
    /// scrollbar around.
    /// </summary>
    internal sealed class ScreenScroll : MonoBehaviour, IScrollHandler
    {
        private const int Step = 3;

        internal ScreenView View;

        public void OnScroll(PointerEventData eventData)
        {
            if (View == null) return;

            // Wheel up walks back into history, which is further from the newest line.
            float delta = eventData.scrollDelta.y;
            if (Mathf.Approximately(delta, 0f)) return;

            View.Scroll(delta > 0f ? Step : -Step);
        }
    }
}
