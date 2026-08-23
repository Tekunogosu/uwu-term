using System.Reflection;
using HarmonyLib;
using UI.Dialogs;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Hand the desktop's bar over to <see cref="TopBar"/>.
    ///
    /// Two seams, and neither of them hides anything. The game's bar is not drawn behind ours
    /// with its colour turned down - it is never built: <c>UpdateDisplay</c> is the only place a
    /// task button is instantiated, and it does so by destroying every existing one first, so
    /// stopping it both empties the bar and ends the churn. A window opening used to throw away
    /// a GameObject per window and make a fresh one, on every focus change as well, which is
    /// what made dragging a window with several open cost what it did.
    ///
    /// The other seam is the theme. <c>DesktopFinder.SetColors</c> is what the appearance menu
    /// drives, so following it is what keeps our bar the player's colour rather than a colour of
    /// our own that drifts from the rest of the desktop.
    /// </summary>
    internal static class TopBarTakeover
    {
        internal static void Apply(Harmony harmony)
        {
            MethodInfo display = AccessTools.Method(typeof(uDialog_TaskBar), "UpdateDisplay");
            if (display != null)
                harmony.Patch(display, prefix: new HarmonyMethod(
                    typeof(TopBarTakeover).GetMethod(nameof(SkipBuildingButtons),
                        BindingFlags.Static | BindingFlags.NonPublic)));
            else
                UwUTermPlugin.Log.LogWarning("topbar: uDialog_TaskBar.UpdateDisplay not found");

            MethodInfo colors = AccessTools.Method(typeof(DesktopFinder), "SetColors");
            if (colors != null)
                harmony.Patch(colors, postfix: new HarmonyMethod(
                    typeof(TopBarTakeover).GetMethod(nameof(FollowTheme),
                        BindingFlags.Static | BindingFlags.NonPublic)));
            else
                UwUTermPlugin.Log.LogWarning("topbar: DesktopFinder.SetColors not found");
        }

        /// <summary>
        /// Nothing is built and nothing is destroyed. Ours are children of the same object, so a
        /// rebuild would take them with it - the method destroys every task button it finds, not
        /// only the ones it made.
        /// </summary>
        private static bool SkipBuildingButtons() => !TopBar.Owns;

        private static void FollowTheme(UI_Theme theme) => TopBar.Paint(theme);
    }
}
