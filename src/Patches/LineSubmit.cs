using HarmonyLib;
using TerminalPoolSystem;
using UnityEngine;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Two corrections to how a submitted line is handled.
    ///
    /// The first is a bug in the game. Terminal.ProcesaLinea reads the line by calling the
    /// adapter's ProcesaLinea, and only then checks whether it is allowed to submit anything:
    ///
    ///     string text = listAdapter.ProcesaLinea(pendingAnyKey);
    ///     if (!pendingInputScript) return text;
    ///
    /// The adapter's version is not a read. It closes the current line and appends a fresh
    /// model to hold the next one. So an Enter arriving while a command is still running adds
    /// a blank line and sends nothing - press Enter twice quickly and the second one leaves a
    /// gap behind. Refusing to run at all when input is not being accepted is the whole fix.
    ///
    /// The second is what a shell does anyway. An empty line reprints the prompt and runs
    /// nothing, so there is nothing to ask a server across the network about; answering it
    /// here means the prompt appears at once instead of after a round trip. Only a line that
    /// is empty or all spaces takes this path - anything with content is sent as it always
    /// was.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), "ProcesaLinea")]
    internal static class LineSubmit
    {
        private static bool Prefix(Terminal __instance, KeyCode keyCode, ref string __result)
        {
            // Nothing is being accepted, so the line must not be closed either.
            if (!__instance.pendingInputScript)
            {
                __result = "";
                return false;
            }

            // Only a plain Enter on a live prompt. A key handed in as a keyCode is a script
            // waiting on a keypress, and password or any-key input is never empty by accident.
            if (keyCode != KeyCode.None) return true;
            if (__instance.isPasswordMode || __instance.pendingAnyKey || !__instance.promptEnabled) return true;

            TerminalListAdapter adapter = __instance.listAdapter;
            if (adapter == null) return true;
            if (!Readline.ReadInput(adapter, out string input, out _)) return true;
            if (input.Trim().Length != 0) return true;

            Reprompt(__instance, adapter);
            __result = "";
            return false;
        }

        /// <summary>
        /// Close the empty line and put a fresh prompt under it, without telling the server.
        ///
        /// pwd is what AddTexto compares against to decide a line is an input line and where
        /// its editable region starts, and the game only ever refreshes it during completion -
        /// it is otherwise still the "localhost/&gt;" it was initialised with, because the live
        /// prompt arrives from the server as ordinary output. So it has to be pointed at the
        /// prompt actually on screen first, exactly as Ctrl+L does.
        /// </summary>
        private static void Reprompt(Terminal terminal, TerminalListAdapter adapter)
        {
            string prompt = Prompt.LastRenderedFor(adapter) ?? adapter.GetPromptText();
            if (string.IsNullOrEmpty(prompt)) return;

            adapter.ProcesaLinea(false);

            terminal.pwd = prompt;
            terminal.AddTexto(prompt);
            terminal.SetPromptEnabled(isEnabled: true);
        }
    }
}
