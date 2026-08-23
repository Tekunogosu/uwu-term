using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using Util;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Two corrections to the desktop's icons.
    ///
    /// A drag carries its icon in one static, <c>OS.iconDrag</c>: <c>IconoVentana.OnBeginDrag</c>
    /// writes it, and whichever drop handler decides it has dealt with the icon clears it. Several
    /// never get the chance. Dropping an icon on a file rather than a folder, on a window, or on
    /// anything that handles no drop at all ends the gesture with the static still holding it, and
    /// nothing else ever clears it - there is no <c>OnEndDrag</c> that does. The next drop
    /// anywhere then acts on an icon nobody is dragging: a press and drag on the empty desktop
    /// moves the last icon there, and the same gesture over the trash sends it to the trash.
    ///
    /// The event knows what the static does not. <c>pointerDrag</c> is the object this gesture
    /// picked up, so an icon that does not match it is one left behind by a gesture that is over.
    /// Every drop handler already asks whether an icon is being carried; the answer is made honest
    /// before they ask, rather than each of them growing a check of its own.
    ///
    /// The second is what an icon costs per frame. Nothing redraws them - the game lays them out
    /// once at boot and on demand - but every icon carries an <c>OnGUI</c>, for no other purpose
    /// than to note whether the last input was a key or a click, and IMGUI runs its layout pass
    /// for each one on every event. The pass buys nothing here: that handler draws nothing and
    /// calls no GUILayout. Turning it off leaves the handler running and the key events arriving.
    /// </summary>
    internal static class DesktopIcons
    {
        internal static void Apply(Harmony harmony)
        {
            // A virtual method and each override of it are separate methods, and all four read the
            // static. The trash is the one that matters most: it moves a file, not a rectangle.
            Guard(harmony, typeof(DesktopFinder));
            Guard(harmony, typeof(VentanaFinder));
            Guard(harmony, typeof(IconoVentana));
            Guard(harmony, typeof(IconoPapelera));

            Patch(harmony, typeof(IconoVentana), "Awake", nameof(SkipGuiLayout), prefix: false);
        }

        private static void Guard(Harmony harmony, Type type) =>
            Patch(harmony, type, "OnDrop", nameof(ForgetAnAbandonedDrag), prefix: true);

        private static void Patch(Harmony harmony, Type type, string method, string handler, bool prefix)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, method);
            if (target == null)
            {
                UwUTermPlugin.Log.LogWarning($"icons: {type.Name}.{method} not found");
                return;
            }

            var patch = new HarmonyMethod(typeof(DesktopIcons).GetMethod(handler,
                BindingFlags.Static | BindingFlags.NonPublic));

            harmony.Patch(target, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
        }

        /// <summary>
        /// Put down an icon this gesture is not carrying, before the drop is offered it.
        /// </summary>
        private static void ForgetAnAbandonedDrag(PointerEventData eventData)
        {
            IconoVentana carried = OS.iconDrag;
            if (carried == null || eventData == null) return;
            if (eventData.pointerDrag == carried.gameObject) return;

            OS.iconDrag = null;
            UwUTermPlugin.Log.LogInfo(
                $"icons: dropped '{carried.GetNombre()}', which the last drag left behind - " +
                "this gesture is carrying nothing");
        }

        /// <summary>Spare each icon the IMGUI layout pass its handler has no use for.</summary>
        private static void SkipGuiLayout(IconoVentana __instance) => __instance.useGUILayout = false;
    }
}
