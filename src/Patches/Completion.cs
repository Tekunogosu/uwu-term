using System.Collections.Generic;
using System.Text;
using CompressString;
using HarmonyLib;
using TerminalPoolSystem;
using TMPro;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// zsh-style menu completion: Tab cycles through the candidates instead of printing
    /// them and stopping at the longest common prefix.
    ///
    /// Completion itself is server-side, but the reply already carries everything needed.
    /// ResumeAutoCompletar receives a newline-separated candidate list whenever listFiles
    /// is set - the stock client only uses it to compute a common prefix and dump the list
    /// into the scrollback. Holding on to that list and stepping through it locally means
    /// no extra round trips: one request, then cycling is instant.
    /// </summary>
    internal static class Completion
    {
        private sealed class Menu
        {
            internal Overlay Panel;
            internal string[] Candidates;
            internal int Index;
            internal string Head = "";
            internal string Tail = "";
            internal string Original = "";
            internal int OriginalPoint;
            internal RectTransform Viewport;
            internal Vector2 ViewportOffset;
        }

        private static readonly Dictionary<Terminal, Menu> Menus = new Dictionary<Terminal, Menu>();
        private static HashSet<string> _learned;

        // A completion is a round trip, so its answer can arrive after the line it was asked
        // about has already been sent. Each terminal counts submitted lines; a request notes
        // the count at the time it was made, and an answer that no longer matches is stale.
        private static readonly Dictionary<Terminal, int> Submitted = new Dictionary<Terminal, int>();
        private static readonly Dictionary<Terminal, int> Requested = new Dictionary<Terminal, int>();

        internal static bool IsActive(Terminal terminal) => Menus.ContainsKey(terminal);

        internal static void Apply(Harmony harmony)
        {
            var resume = AccessTools.Method(typeof(Terminal), "ResumeAutoCompletar");
            if (resume == null)
            {
                UwUTermPlugin.Log.LogWarning("completion: Terminal.ResumeAutoCompletar not found");
                return;
            }

            harmony.Patch(resume, prefix: new HarmonyMethod(
                typeof(Completion).GetMethod(nameof(OnCandidates),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));

            var request = AccessTools.Method(typeof(Terminal), "AutoCompletar");
            if (request != null)
                harmony.Patch(request, postfix: new HarmonyMethod(
                    typeof(Completion).GetMethod(nameof(OnRequested),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));

            var processed = AccessTools.Method(typeof(Terminal), "ProcesaLinea", new[] { typeof(KeyCode) });
            if (processed != null)
                harmony.Patch(processed, postfix: new HarmonyMethod(
                    typeof(Completion).GetMethod(nameof(OnLineSubmitted),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        private static void OnRequested(Terminal __instance)
        {
            Submitted.TryGetValue(__instance, out int count);
            Requested[__instance] = count;
        }

        /// <summary>A submitted line invalidates any completion still in flight, and closes
        /// a menu the command would otherwise leave stranded on screen.</summary>
        private static void OnLineSubmitted(Terminal __instance)
        {
            Submitted.TryGetValue(__instance, out int count);
            Submitted[__instance] = count + 1;
            Close(__instance);
        }

        /// <summary>False suppresses the stock behaviour - the list dump and the prefix
        /// append - in favour of the menu.</summary>
        private static bool OnCandidates(Terminal __instance, byte[] zipOutput, bool listFiles)
        {
            bool empty = zipOutput == null || zipOutput.Length == 0;

            // A single match comes back as the finished line rather than a list, and taking it
            // has to happen whether or not the menu is switched on - it is the drawn prompt
            // being protected, not the menu.
            if (!empty && !listFiles && CompletionRequest.TakeSingleMatch(__instance, zipOutput))
                return false;

            if (!UwUTermPlugin.MenuComplete.Value) return true;
            if (!listFiles || empty) return true;

            // Stale: the line moved on while this was in flight. Swallow it rather than
            // letting the stock handler paste a completion into whatever is on screen now.
            Submitted.TryGetValue(__instance, out int submitted);
            if (Requested.TryGetValue(__instance, out int requested) && requested != submitted)
            {
                if (UwUTermPlugin.CompletionDebug.Value)
                    UwUTermPlugin.Log.LogInfo("completion: discarded a stale reply");
                return false;
            }

            string payload = StringCompressor.Unzip(zipOutput);
            if (UwUTermPlugin.CompletionDebug.Value)
                UwUTermPlugin.Log.LogInfo("completion payload: " + payload.Replace("\n", " | "));

            var cleaned = new List<string>();
            foreach (string candidate in payload.Split('\n'))
            {
                string trimmed = candidate.Trim();
                if (trimmed.Length > 0) cleaned.Add(trimmed);
            }
            if (cleaned.Count == 0) return true;

            LearnCommands(cleaned);

            TerminalListAdapter adapter = __instance.listAdapter;
            if (adapter == null) return true;
            if (!Readline.ReadInput(adapter, out string input, out int point)) return true;

            string before = input.Substring(0, point);
            int lastSpace = before.LastIndexOf(' ');
            string head = lastSpace >= 0 ? before.Substring(0, lastSpace + 1) : "";
            string token = before.Substring(lastSpace + 1);

            cleaned = Narrow(__instance, cleaned, head, token);
            if (cleaned.Count == 0) return true;

            var menu = new Menu
            {
                Candidates = cleaned.ToArray(),
                Index = 0,
                Head = head,
                Tail = input.Substring(point),
                Original = input,
                OriginalPoint = point,
            };
            // Two Tabs in quick succession means two replies, and the earlier menu has to
            // go before the new one is built. Its panel would otherwise be orphaned -
            // visible, unreachable by any key, and only cleaned up when the window closes.
            // Build measures the viewport to work out the new menu's baseline, so with the
            // earlier shrink still applied it would record a baseline one line short and
            // the terminal would never get that line back.
            if (Menus.TryGetValue(__instance, out Menu previous)) Restore(previous);

            Build(__instance, adapter, menu);
            Menus[__instance] = menu;
            Show(adapter, menu);
            return false;
        }

        /// <summary>
        /// The server answers a completion with everything in scope - directory entries and
        /// every command on the machine - because it does not know which slot is being
        /// filled. That is fine for the command itself and useless for an argument.
        ///
        /// Folder-only filtering works from the local filesystem the client already holds,
        /// so it applies on your own machine and quietly does nothing over a connection to
        /// someone else's, where the client has no such model. Dropping command names needs
        /// nothing but the list, so that part always applies.
        /// </summary>
        private static List<string> Narrow(Terminal terminal, List<string> candidates, string head, string token)
        {
            // Completing the first word is the one time you do want every command - and the
            // one time the answer tells us what the commands actually are.
            if (head.Trim().Length == 0) return candidates;

            var withoutCommands = new List<string>();
            foreach (string candidate in candidates)
            {
                if (UwUTermPlugin.FilterCommandNames.Value && IsCommand(terminal, candidate)) continue;

                // .exe here means a windowed program, which is never an argument to anything.
                if (HasIgnoredExtension(candidate)) continue;

                withoutCommands.Add(candidate);
            }

            // Never filter everything away - an empty menu is worse than a noisy one.
            return withoutCommands.Count > 0 ? withoutCommands : candidates;
        }

        /// <summary>
        /// Learn command names from a reply that names them as paths.
        ///
        /// The server sometimes answers with entries like "/bin/ls" rather than bare names,
        /// and anything living in /bin is a command by definition. Learning only from those is
        /// what keeps this honest: the reply for a bare command slot is everything in scope,
        /// working directory included, so learning from that taught it that every local file
        /// was a command - and it then hid those same files from argument completions, which
        /// is the one place they are what you actually want.
        /// </summary>
        private static void LearnCommands(List<string> candidates)
        {
            int learned = 0;
            foreach (string candidate in candidates)
            {
                if (!candidate.StartsWith("/bin/", System.StringComparison.Ordinal)) continue;

                string name = candidate.Substring("/bin/".Length);
                if (name.Length == 0 || name.IndexOf('/') >= 0) continue;

                if (_learned == null) _learned = new HashSet<string>();
                if (_learned.Add(name)) learned++;
            }

            if (learned > 0 && UwUTermPlugin.CompletionDebug.Value)
                UwUTermPlugin.Log.LogInfo($"completion: learned {learned} command names from /bin");
        }

        private static bool IsCommand(Terminal terminal, string candidate) =>
            Contains(UwUTermPlugin.KnownCommands.Value, candidate) ||
            (_learned != null && _learned.Contains(candidate));

        private static bool HasIgnoredExtension(string candidate)
        {
            string list = UwUTermPlugin.IgnoreArgumentExtensions.Value;
            if (string.IsNullOrEmpty(list)) return false;

            foreach (string entry in list.Split(','))
            {
                string suffix = entry.Trim();
                if (suffix.Length > 0 && candidate.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool Contains(string list, string value)
        {
            if (string.IsNullOrEmpty(list) || string.IsNullOrEmpty(value)) return false;

            foreach (string entry in list.Split(','))
                if (entry.Trim() == value) return true;

            return false;
        }

        /// <summary>True when the key belonged to the menu. False ends it and lets the
        /// terminal handle the key, so typing simply carries on from the selection.</summary>
        internal static bool HandleKey(Terminal terminal, TerminalListAdapter adapter, Event e)
        {
            if (!Menus.TryGetValue(terminal, out Menu menu)) return false;

            if (e.keyCode == KeyCode.Tab)
            {
                menu.Index = e.shift
                    ? (menu.Index - 1 + menu.Candidates.Length) % menu.Candidates.Length
                    : (menu.Index + 1) % menu.Candidates.Length;
                Show(adapter, menu);
                return true;
            }

            if (e.keyCode == KeyCode.Escape)
            {
                Readline.WriteInput(adapter, menu.Original, menu.OriginalPoint);
                Close(terminal);
                return true;
            }

            // Modifier presses and the character half of a keystroke must not end the menu.
            if (e.keyCode == KeyCode.None || IsModifier(e.keyCode)) return true;

            Close(terminal);
            return false;
        }

        internal static void Dismiss(uDialog dialog)
        {
            Terminal owner = null;
            foreach (KeyValuePair<Terminal, Menu> pair in Menus)
                if (pair.Key != null && ReferenceEquals(pair.Key.dialogo, dialog)) { owner = pair.Key; break; }

            if (owner == null) return;
            Restore(Menus[owner]);
            Menus.Remove(owner);
        }

        private static void Close(Terminal terminal)
        {
            if (!Menus.TryGetValue(terminal, out Menu menu)) return;
            Restore(menu);
            Menus.Remove(terminal);
        }

        /// <summary>Give the terminal its line back. OSA notices the viewport size change on
        /// its own the next frame and reflows, so nothing else has to be told.</summary>
        private static void Restore(Menu menu)
        {
            menu.Panel?.Destroy();
            if (menu.Viewport == null) return;

            menu.Viewport.offsetMin = menu.ViewportOffset;
        }

        private static void Show(TerminalListAdapter adapter, Menu menu)
        {
            string candidate = menu.Candidates[menu.Index];
            string line = menu.Head + candidate;
            Readline.WriteInput(adapter, line + menu.Tail, line.Length);

            menu.Panel?.SetText(Render(menu));
        }

        private static string Render(Menu menu)
        {
            var sb = new StringBuilder(160);
            sb.Append('[').Append(menu.Index + 1).Append('/').Append(menu.Candidates.Length).Append("]  ");

            // Enough context to see what is next without letting a large directory push the
            // panel off the window.
            int from = Mathf.Max(0, menu.Index - 3);
            int to = Mathf.Min(menu.Candidates.Length, from + 8);

            for (int i = from; i < to; i++)
            {
                if (i > from) sb.Append("   ");
                if (i == menu.Index)
                    sb.Append("<color=").Append(UwUTermPlugin.CompletionSelectedColor.Value).Append('>')
                      .Append(menu.Candidates[i]).Append("</color>");
                else
                    sb.Append(menu.Candidates[i]);
            }

            if (to < menu.Candidates.Length) sb.Append("   ...");
            return sb.ToString();
        }

        private static bool IsModifier(KeyCode key) =>
            key == KeyCode.LeftControl || key == KeyCode.RightControl ||
            key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
            key == KeyCode.LeftShift || key == KeyCode.RightShift ||
            key == KeyCode.LeftCommand || key == KeyCode.RightCommand;

        /// <summary>
        /// Shorten the scroll viewport by one line and put the menu in the gap, so the
        /// prompt rides up instead of being covered - the same thing a real terminal does by
        /// printing the list and reflowing. The panel is parented outside the viewport, or
        /// shrinking the viewport would carry it up too and cover the prompt anyway.
        /// </summary>
        private static void Build(Terminal terminal, TerminalListAdapter adapter, Menu menu)
        {
            RectTransform viewport = adapter.Viewport;
            RectTransform parent = viewport != null ? viewport.parent as RectTransform : null;
            if (parent == null)
            {
                parent = terminal.dialogo != null ? terminal.dialogo.RectTransform : null;
                viewport = null;
            }
            if (parent == null) return;

            TMP_Text sample = adapter.GetLastViewLine()?.lineText
                              ?? terminal.GetComponentInChildren<TMP_Text>();

            float size = (sample != null ? sample.fontSize : 14f) - 1f;
            float height = size * 1.4f + 14f;

            if (viewport != null)
            {
                menu.Viewport = viewport;
                menu.ViewportOffset = viewport.offsetMin;
                viewport.offsetMin = new Vector2(viewport.offsetMin.x, viewport.offsetMin.y + height);
            }

            menu.Panel = Overlay.CreateBottom(parent, sample?.font, size, 6f, height,
                UwUTermPlugin.CompletionBackgroundAlpha.Value);
            menu.Panel.Show();
        }
    }
}
