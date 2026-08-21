using HarmonyLib;
using UI.Dialogs;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Tear our overlays down the moment a window starts closing.
    ///
    /// The panels are children of the dialog, so they do get destroyed with it eventually -
    /// but uDialog animates the close and only destroys afterwards, and the animation acts
    /// on the dialog's own graphics, not on objects added later. The panel would sit there
    /// at full opacity while the window faded out from under it.
    ///
    /// Close() and Close(bool) both funnel into this overload, and Minimize() reaches it
    /// too, so one patch covers every way a window can go away.
    /// </summary>
    [HarmonyPatch(typeof(uDialog), "Close", new[] { typeof(bool), typeof(bool), typeof(bool) })]
    internal static class WindowClose
    {
        private static void Prefix(uDialog __instance)
        {
            Search.Dismiss(__instance);
            HistorySearch.Dismiss(__instance);
            Completion.Dismiss(__instance);
            MailHeaders.Dismiss(__instance);
            ScreenTakeover.Dismiss(__instance);
        }
    }
}
