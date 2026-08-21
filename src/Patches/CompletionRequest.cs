using System;
using CompressString;
using HarmonyLib;
using TerminalPoolSystem;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Build the completion request from the prompt the server wrote, not the one we drew.
    ///
    /// TerminalListAdapter.AutoCompletar takes the whole input line - prompt and all - splits
    /// it on spaces, and sends the last piece as the token to complete:
    ///
    ///     string text = Data[last].line.Substring(0, charIndexInput + 1);
    ///     lineaPrompt = text.Split(' ');
    ///     comandoCompletar = lineaPrompt[lineaPrompt.Length - 1];
    ///
    /// That works because the server's prompt ends "…# " - a space outside any markup, so the
    /// last piece is exactly what was typed. A custom prompt need not end that way. A template
    /// finishing "{sym}{sp}" renders as "…] &lt;/color&gt;", putting the space inside the tag, and
    /// the last piece comes out as "&lt;/color&gt;shell" - which matches nothing on the server,
    /// so the reply is empty and the terminal rings the bell.
    ///
    /// So the request is rebuilt here from the prompt as it arrived and the text as typed. The
    /// prompt on screen is untouched; only what goes out over the wire changes, and it goes
    /// out identical to what an unmodified client would send.
    /// </summary>
    [HarmonyPatch(typeof(TerminalListAdapter), "AutoCompletar")]
    internal static class CompletionRequest
    {
        private static bool Prefix(TerminalListAdapter __instance, ref string originalLinea,
                                   ref string restoLinea, ref string comandoCompletar,
                                   ref string[] lineaPrompt)
        {
            // With the prompt left alone there is nothing to correct for, and the captured
            // prompt may be left over from before the setting was turned off.
            if (!UwUTermPlugin.ColorizePrompt.Value) return true;

            string prompt = Prompt.LastRawFor(__instance);
            if (string.IsNullOrEmpty(prompt)) return true;

            if (!Readline.ReadInput(__instance, out string input, out int point)) return true;

            string typedSoFar = input.Substring(0, point);

            originalLinea = input;
            restoLinea = input.Substring(point);
            lineaPrompt = (prompt + typedSoFar).Split(' ');
            comandoCompletar = lineaPrompt[lineaPrompt.Length - 1];

            if (UwUTermPlugin.CompletionDebug.Value)
                UwUTermPlugin.Log.LogInfo($"completion: asking to complete \"{comandoCompletar}\"");

            return false;
        }

        /// <summary>
        /// Take a single-match completion without letting it overwrite the prompt.
        ///
        /// When exactly one thing matches, the server does not send a list - it sends the
        /// whole finished line back, and the client replaces the input line with it wholesale.
        /// That line carries the server's prompt, because the server's prompt is what we now
        /// send it, so the drawn prompt gets swapped for the plain one.
        ///
        /// Worse than the look of it: minPosCursor still measures where the drawn prompt
        /// ended, so every index the editing keys work in is wrong afterwards. That is why a
        /// one-match completion left the line typeable but impossible to move around in or
        /// backspace through, and why submitting it put everything right - the next prompt is
        /// drawn from scratch.
        ///
        /// Writing just the input part keeps the prompt, and minPosCursor with it.
        /// </summary>
        internal static bool TakeSingleMatch(Terminal terminal, byte[] zipOutput)
        {
            if (!UwUTermPlugin.ColorizePrompt.Value) return false;

            TerminalListAdapter adapter = terminal == null ? null : terminal.listAdapter;
            if (adapter == null) return false;

            string prompt = Prompt.LastRawFor(adapter);
            if (string.IsNullOrEmpty(prompt)) return false;

            string completed = StringCompressor.Unzip(zipOutput);
            if (completed == null || !completed.StartsWith(prompt, StringComparison.Ordinal)) return false;

            string line = completed.Substring(prompt.Length);

            // Completing in the middle of a line leaves what followed the caret untouched at
            // the end, so the caret belongs where that tail starts rather than after it.
            int caret = line.Length;
            if (Readline.ReadInput(adapter, out string current, out int point))
            {
                string tail = current.Substring(point);
                if (tail.Length > 0 && line.EndsWith(tail, StringComparison.Ordinal))
                    caret = line.Length - tail.Length;
            }

            Readline.WriteInput(adapter, line, caret);

            if (UwUTermPlugin.CompletionDebug.Value)
                UwUTermPlugin.Log.LogInfo($"completion: one match, line is now \"{line}\"");

            return true;
        }
    }
}
