using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Middle-click on an object, which Unity's own <c>Button</c> does not offer - it answers the
    /// left button and nothing else.
    ///
    /// It has to sit on the same object as the button it accompanies rather than on a parent. The
    /// event system hands a click to the nearest ancestor that implements the interface at all,
    /// and <c>Button</c> implements it for every button before deciding it only wants the left one,
    /// so a handler further up is never reached.
    /// </summary>
    internal sealed class MiddleClick : MonoBehaviour, IPointerClickHandler
    {
        internal Action Clicked;

        internal static void On(GameObject target, Action clicked)
        {
            MiddleClick handler = target.AddComponent<MiddleClick>();
            handler.Clicked = clicked;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Middle) Clicked?.Invoke();
        }
    }
}
