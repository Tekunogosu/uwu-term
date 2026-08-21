using HarmonyLib;
using TerminalPoolSystem;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Point Ctrl+Shift+C and Ctrl+Shift+V at the grid's selection.
    ///
    /// The game's copy reads a selection held against its own rows. Those rows are still there
    /// and still selectable, but nobody can see them any more, so what they hold is whatever
    /// the pointer happened to do rather than what the player watched themselves highlight.
    /// Answering from the grid's selection instead makes the keys agree with the screen.
    ///
    /// Only the copy is redirected. Pasting keeps the system clipboard, because that is where
    /// text from outside the game arrives; the primary buffer that selecting fills is reached
    /// with the middle mouse button, as it is on a desktop.
    /// </summary>
    internal static class ScreenClipboard
    {
        internal static void Apply(Harmony harmony)
        {
            Patch(harmony, "CopyText", nameof(OnCopy));
            Patch(harmony, "PasteText", nameof(OnPaste));
        }

        private static void Patch(Harmony harmony, string method, string handler)
        {
            var target = AccessTools.Method(typeof(TerminalListAdapter), method);
            if (target == null)
            {
                UwUTermPlugin.Log.LogWarning($"clipboard: TerminalListAdapter.{method} not found");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(
                typeof(ScreenClipboard).GetMethod(handler,
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        private static bool OnCopy(TerminalListAdapter __instance)
        {
            ScreenView view = ScreenTakeover.ViewFor(__instance);
            if (view == null) return true;

            string selected = view.SelectedText();
            if (selected.Length == 0) return false;   // nothing highlighted, nothing to copy

            TerminalClipboard.Shared = selected;
            return false;
        }

        private static bool OnPaste(TerminalListAdapter __instance)
        {
            ScreenView view = ScreenTakeover.ViewFor(__instance);
            if (view == null) return true;

            view.Paste(TerminalClipboard.Shared);
            return false;
        }
    }
}
