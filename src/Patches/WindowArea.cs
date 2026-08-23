using System.Collections.Generic;
using HarmonyLib;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Maximise to the space the bar actually leaves.
    ///
    /// The game maximises to 99% by 92% of the desktop, centred - the 8% of height being the
    /// two bars, taken evenly off the top and the bottom because that is where they were.
    /// With a single bar along the top that is wrong twice over: too small, and centred, so a
    /// maximised window sits under the bar and leaves a strip bare at the bottom.
    ///
    /// The animation is dropped along with the geometry. It exists to slide a window out to
    /// the old size, and reproducing it around a different rectangle would mean reimplementing
    /// the coroutine to change two numbers inside it.
    ///
    /// Minimising still travels downward, and that is left alone deliberately.
    ///
    /// Which clip plays can be changed - MinimizeAnimation and RestoreAnimation name states by
    /// string - but what happens afterwards cannot. The animator is left enabled holding the
    /// last frame of whatever played, so it overwrites the position and alpha that
    /// ResetPositionAndAlpha restores half a second later, every frame. Whether a window comes
    /// back therefore depends on which states its own prefab's controller happens to contain,
    /// and that differs from window to window: pointing them all at SlideOut_Top brings the
    /// users panel and the chat back correctly and leaves the terminal and the browser at
    /// alpha zero, unreachable.
    ///
    /// Fixing it properly means authoring clips and injecting them into each controller, which
    /// is a great deal of machinery for the direction of a 300ms slide. A window that animates
    /// the wrong way is a cosmetic complaint; one that cannot be restored is a lost window.
    /// </summary>
    internal static class WindowArea
    {
        // What the window was before it was maximised, so the button restores it.
        private static readonly Dictionary<uDialog, Vector2> Restore = new Dictionary<uDialog, Vector2>();

        internal static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(uDialog), "Maximize");
            if (target == null)
            {
                UwUTermPlugin.Log.LogWarning("window area: uDialog.Maximize not found");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(
                typeof(WindowArea).GetMethod(nameof(OnMaximize),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        private static bool OnMaximize(uDialog __instance)
        {
            if (!DesktopArea.Claimed) return true;   // the game's own geometry still fits

            RectTransform rect = __instance.RectTransform;
            RectTransform parent = rect != null ? rect.parent as RectTransform : null;
            if (parent == null) return true;

            if (__instance.isMaximized)
            {
                if (Restore.TryGetValue(__instance, out Vector2 original))
                {
                    WindowGeometry.Place(rect, parent, original, parent.rect.center);
                    Restore.Remove(__instance);
                }
            }
            else
            {
                Restore[__instance] = rect.sizeDelta;

                Rect area = Area(parent.rect);
                WindowGeometry.Place(rect, parent, area.size, area.center);
            }

            __instance.isMaximized = !__instance.isMaximized;
            __instance.GetComponentInChildren<Ventana>()?.SetMaximizedIcon(__instance.isMaximized);
            return false;
        }

        /// <summary>
        /// The desktop minus whatever the bar is using. Shared with window snapping so a
        /// maximised window and a window snapped to fill land on exactly the same rectangle.
        /// </summary>
        internal static Rect Area(Rect desktop)
        {
            float inset = DesktopArea.TopInset;
            float width = desktop.width * 0.99f;

            var area = new Rect(
                desktop.center.x - width / 2f,
                desktop.yMin,
                width,
                desktop.height - inset);

            Report(desktop, inset, area);
            return area;
        }

        /// <summary>
        /// What a maximised window, a snapped window and the snapping ghost all land on.
        ///
        /// Three things read this and one of them being wrong looks like three separate faults,
        /// so it says once - whenever the answer changes - what it was given and what it made of
        /// it. A window that fills the width and not the height is an inset the size of the
        /// desktop, and that is visible here and nowhere else.
        /// </summary>
        private static void Report(Rect desktop, float inset, Rect area)
        {
            if (!UwUTermPlugin.ScreenDebug.Value) return;

            string now = $"window area: desktop {desktop.width:F0}x{desktop.height:F0} " +
                         $"(y {desktop.yMin:F0}..{desktop.yMax:F0}), bar takes {inset:F0} -> " +
                         $"{area.width:F0}x{area.height:F0} centred on " +
                         $"({area.center.x:F0},{area.center.y:F0})";

            if (now == _reported) return;

            _reported = now;
            UwUTermPlugin.Log.LogInfo(now);
        }

        private static string _reported = "";
    }
}
