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
            if (!DesktopBar.Active) return true;   // the game's own geometry still fits

            RectTransform rect = __instance.RectTransform;
            RectTransform parent = rect != null ? rect.parent as RectTransform : null;
            if (parent == null) return true;

            if (__instance.isMaximized)
            {
                if (Restore.TryGetValue(__instance, out Vector2 original))
                {
                    Place(__instance, parent, original, parent.rect.center);
                    Restore.Remove(__instance);
                }
            }
            else
            {
                Restore[__instance] = rect.sizeDelta;

                Rect area = Area(parent.rect);
                Place(__instance, parent, area.size, area.center);
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
            float inset = DesktopBar.TopInset;
            float width = desktop.width * 0.99f;

            return new Rect(
                desktop.center.x - width / 2f,
                desktop.yMin,
                width,
                desktop.height - inset);
        }

        private static void Place(uDialog dialog, RectTransform parent, Vector2 size, Vector2 centre)
        {
            dialog.SetPivot(new Vector2(0.5f, 0.5f));
            dialog.RectTransform.sizeDelta = size;

            Vector3 world = parent.TransformPoint(new Vector3(centre.x, centre.y, 0f));
            dialog.RectTransform.position = new Vector3(world.x, world.y, dialog.RectTransform.position.z);
        }
    }
}
