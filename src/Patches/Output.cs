using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TerminalPoolSystem;
using UnityEngine;

namespace UwUTerm.Patches
{
    /// <summary>
    /// The shell is server-side: SendInputUserToServer ships the typed line off as an
    /// InputUserServerRpc, and everything that comes back is printed through the single
    /// choke point Terminal.AddTexto. So `ls` cannot be changed - but the text on its
    /// way in, and the command on its way out, are both ours.
    ///
    /// This reflows single-column `ls` output into columns the way `ls -C` does.
    /// </summary>
    [HarmonyPatch]
    internal static class Output
    {
        private const int FallbackColumns = 80;
        private const int Gap = 2;

        // The terminal whose next output block should be treated as `ls` output.
        private static Terminal _pendingLs;

        [HarmonyPatch(typeof(Terminal), "SendInputUserToServer")]
        [HarmonyPrefix]
        private static void OnSend(Terminal __instance, ref string input)
        {
            if (UwUTermPlugin.CommandTidyEnabled(UwUTermPlugin.TidyLs))
            {
                string normalized = NormalizeLs(input);
                if (normalized != null && normalized != input)
                {
                    if (UwUTermPlugin.DebugOutput.Value)
                        UwUTermPlugin.Log.LogInfo($"rewrite: {input}  ->  {normalized}");
                    input = normalized;
                }
            }

            if (UwUTermPlugin.DebugOutput.Value)
                UwUTermPlugin.Log.LogInfo("send -> " + Escape(input));

            _pendingLs = IsPlainLs(input) ? __instance : null;
        }

        /// <summary>
        /// The server accepts exactly `ls [-a] [-l] [-la]` - one flag argument, and only
        /// in that spelling. So `ls -al`, `ls -a -l` and `ls -l -a` all fail today. Fold
        /// whatever the user typed into the accepted form on the way out; the scrollback
        /// still shows what they actually typed, since the terminal echoes locally.
        ///
        /// Returns null to leave the command alone - including when an unrecognised flag
        /// is present, so the server's own error message stays honest.
        /// </summary>
        private static string NormalizeLs(string input)
        {
            if (input == null) return null;
            string trimmed = input.Trim();
            if (trimmed != "ls" && !trimmed.StartsWith("ls ", StringComparison.Ordinal)) return null;

            string[] parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool all = false, longFormat = false;
            var operands = new List<string>();

            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
                bool isShortFlag = part.Length > 1 && part[0] == '-' && part[1] != '-';
                if (!isShortFlag)
                {
                    operands.Add(part);
                    continue;
                }

                foreach (char c in part.Substring(1))
                {
                    if (c == 'a') all = true;
                    else if (c == 'l') longFormat = true;
                    else return null;       // unknown flag - hands off entirely
                }
            }

            if (!all && !longFormat) return null;

            string flag = (all && longFormat) ? "-la" : (longFormat ? "-l" : "-a");
            string rebuilt = "ls " + flag;
            if (operands.Count > 0) rebuilt += " " + string.Join(" ", operands.ToArray());
            return rebuilt;
        }

        [HarmonyPatch(typeof(Terminal), "AddTexto")]
        [HarmonyPrefix]
        private static void OnAddTexto(Terminal __instance, ref string texto, bool isMsgInput, bool isPassword)
        {
            if (UwUTermPlugin.DebugOutput.Value && !string.IsNullOrEmpty(texto))
                UwUTermPlugin.Log.LogInfo("recv <- " + Escape(texto));

            if (!UwUTermPlugin.CommandTidyEnabled(UwUTermPlugin.TidyLs)) return;
            if (!ReferenceEquals(_pendingLs, __instance)) return;
            if (isMsgInput || isPassword) return;
            if (string.IsNullOrEmpty(texto)) return;
            if (texto.Equals(__instance.pwd)) return;   // the prompt, not output

            _pendingLs = null;

            try
            {
                string columns = Columnize(__instance, texto);
                if (columns != null) texto = columns;
            }
            catch (Exception e)
            {
                UwUTermPlugin.Log.LogError("ls columnize: " + e);
            }
        }

        /// <summary>An `ls` that produces a bare list - `ls -l` is already formatted.</summary>
        private static bool IsPlainLs(string input)
        {
            if (string.IsNullOrEmpty(input)) return false;
            string[] parts = input.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] != "ls") return false;

            for (int i = 1; i < parts.Length; i++)
                if (parts[i].StartsWith("-") && parts[i].Contains("l")) return false;

            return true;
        }

        private static string Columnize(Terminal terminal, string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            var entries = new List<string>();
            var widths = new List<int>();
            int longest = 0;

            foreach (string rawLine in lines)
            {
                // The server already pads every name out to a fixed column width and then
                // prints one per line, so the padding has to come off before anything else.
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                string visible = TerminalListAdapter.StripTagsRegexCompiled(line);

                // An interior space means this is not a bare name list - `ls -l`, `ps`, an
                // error message, or a filename with a space. Leave all of those alone.
                if (visible.IndexOf(' ') >= 0 || visible.IndexOf('\t') >= 0) return null;

                entries.Add(line);
                widths.Add(visible.Length);
                if (visible.Length > longest) longest = visible.Length;
            }

            if (entries.Count < 2) return null;

            int terminalWidth = TerminalWidth(terminal);
            int columnWidth = longest + Gap;
            int columns = Math.Max(1, terminalWidth / columnWidth);
            if (columns < 2) return null;   // nothing gained

            int rows = (entries.Count + columns - 1) / columns;
            columns = (entries.Count + rows - 1) / rows;   // no trailing empty columns

            var sb = new StringBuilder(text.Length + entries.Count * Gap);
            for (int row = 0; row < rows; row++)
            {
                int lineStart = sb.Length;
                for (int col = 0; col < columns; col++)
                {
                    // Column-major, the way real ls fills: down first, then across.
                    int index = col * rows + row;
                    if (index >= entries.Count) break;

                    sb.Append(entries[index]);
                    if (col + 1 < columns && col * rows + rows + row < entries.Count)
                        sb.Append(' ', columnWidth - widths[index]);
                }
                TrimTrailingSpaces(sb, lineStart);
                if (row + 1 < rows) sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Visible width of the terminal in characters.
        ///
        /// The screen already decided this - it is the number of columns it laid the grid out
        /// in, arrived at from the same font metrics it draws with and after the padding it
        /// leaves at the edges. Measuring a glyph here again would be a second answer to a
        /// question that already has one, and the two would differ by however much the padding
        /// happens to be.
        /// </summary>
        private static int TerminalWidth(Terminal terminal)
        {
            Ui.ScreenView screen = ScreenTakeover.ViewFor(terminal.listAdapter);
            if (screen != null && screen.Columns >= 2) return Mathf.Clamp(screen.Columns, 20, 500);

            // No grid, so the game's rows are the screen and one of them has to be measured.
            TerminalListAdapter adapter = terminal.listAdapter;
            if (adapter == null || adapter.Data == null || adapter.Data.Count == 0) return FallbackColumns;

            TerminalListItemViewsHolder view =
                adapter.GetItemViewsHolderIfVisible(adapter.Data.Count - 1) ??
                adapter.GetItemViewsHolderIfVisible(0);

            if (view == null || view.lineText == null) return FallbackColumns;

            float rectWidth = view.lineText.rectTransform.rect.width;
            float charWidth = view.lineText.GetPreferredValues("MMMMMMMMMM").x / 10f;
            if (rectWidth <= 0f || charWidth <= 0f) return FallbackColumns;

            return Mathf.Clamp(Mathf.FloorToInt(rectWidth / charWidth), 20, 500);
        }

        private static void TrimTrailingSpaces(StringBuilder sb, int from)
        {
            int end = sb.Length;
            while (end > from && sb[end - 1] == ' ') end--;
            sb.Length = end;
        }

        private static string Escape(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
