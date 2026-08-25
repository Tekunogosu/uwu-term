using System.Reflection;
using HarmonyLib;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Answer which window is focused by looking at the windows.
    ///
    /// <c>uDialog.IsFocused</c> asks whether it is the last active child of its parent - which is
    /// how the game orders windows, and was true until our bar moved in beside them. The bar is a
    /// sibling of every window in the desktop and <c>TopBar.KeepOnTop</c> puts it last whenever a
    /// window is raised past it, so from that moment no window is ever the last child and every
    /// window answers "no". Exactly one window is frontmost; the game just stopped being able to
    /// say which.
    ///
    /// What that cost is easy to miss, because nothing throws. <c>HtmlBrowser.Update</c> follows
    /// the answer to decide whether its page takes clicks, and only writes that when the answer
    /// changes - an answer stuck at false never changes, so every browser keeps whatever its
    /// prefab shipped with. With one browser open nothing looks wrong. With several stacked on the
    /// same rectangle, which is what a tab is, PowerUI's <c>MapToUIPanel</c> walks its raycast
    /// results from the back and returns the first page it finds, so every click lands in the
    /// oldest browser - the one behind. Snapping from the keyboard was already looking for the
    /// frontmost window the same way and finding none.
    ///
    /// So the count is taken again over windows alone: the last visible child carrying a
    /// <see cref="uDialog"/>, which our bar is not. A tab held out of sight is not a candidate
    /// either - it is active, and would otherwise be the answer whenever it happened to sit later
    /// in the list than the tab on screen.
    /// </summary>
    internal static class WindowFocus
    {
        internal static void Apply(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(uDialog), "IsFocused");
            if (target == null)
            {
                UwUTermPlugin.Log.LogWarning("window focus: uDialog.IsFocused not found");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(
                typeof(WindowFocus).GetMethod(nameof(Recount),
                    BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void Recount(uDialog __instance, ref bool __result)
        {
            // Only where we caused the problem. With the game's own bars the desktop's last child
            // is a window again and its answer needs no correcting.
            if (!TopBar.Owns) return;

            Transform parent = __instance.transform.parent;
            if (parent == null) return;

            Transform front = Front(parent);
            __result = front != null && front == __instance.transform;
        }

        /// <summary>The frontmost window under a parent, or null when none of its children is one.</summary>
        private static Transform Front(Transform parent)
        {
            Transform front = null;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (!child.gameObject.activeInHierarchy) continue;

                // Notifications stack in the corner over everything and are not windows the player
                // is working in - the game's own count skips them by name and so does this.
                if (child.name.Contains("Notification")) continue;

                var window = child.GetComponent<uDialog>();
                if (window == null || !window.isVisible) continue;
                if (Browser.Tabs.IsBackground(window)) continue;

                front = child;
            }

            return front;
        }
    }
}
