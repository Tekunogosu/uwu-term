using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using TerminalPoolSystem;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Replaces the user@host:path prompt with one built from a template.
    ///
    /// Patched at TerminalListAdapter.AddText rather than Terminal.AddTexto, because by
    /// then the game has already decided whether the line is an input line. AddTexto
    /// decides via texto.Equals(pwd), so rewriting there would fail that comparison,
    /// minPosCursor would stay 0, and the caret could walk into the prompt - or worse,
    /// ProcesaLinea would send the prompt to the server as part of the command. After the
    /// decision, every index the game computes already accounts for our tags.
    ///
    /// Multi-line templates work because AddText splits on \n into separate rows and
    /// anchors the caret to the last one. The two paths that rebuild a prompt cope too:
    /// UpdateHistorialPrompt keeps line.Substring(0, minPosCursor + 1) verbatim, and
    /// AutoCompletar refreshes pwd from GetPromptText() before using pwd.Length as a caret
    /// offset, so it measures the rendered prompt rather than the server's.
    ///
    /// Colour model - the palette dresses fields, the template dresses literals:
    ///   * literal text is untagged unless a {#...} is in effect, so it follows the game's
    ///     own theme colour
    ///   * a variable takes its colour from the palette and then hands it back, which is
    ///     why no {/} is ever needed after one
    ///   * a variable with no palette entry inherits whatever {#...} is running
    /// </summary>
    [HarmonyPatch(typeof(TerminalListAdapter), "AddText")]
    internal static class Prompt
    {
        // Excluding < and > means an already-rendered prompt can never match twice.
        private static readonly Regex Shape = new Regex(
            @"^(?<user>[^@\s<>]+)@(?<host>[^:\s<>]+):(?<path>[^\s<>]*)(?<sym>[#$])(?<tail>\s*)$",
            RegexOptions.Compiled);

        private static readonly Regex Token = new Regex(@"\{([^{}]*)\}", RegexOptions.Compiled);
        private static readonly Regex Hex = new Regex(@"^[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);

        // Ctrl+L redraws the whole prompt, and GetPromptText() returns only the last row -
        // which would lose everything above it in a multi-line template.
        private static readonly Dictionary<TerminalListAdapter, string> LastRendered =
            new Dictionary<TerminalListAdapter, string>();

        private static readonly Map Palette = new Map(() => UwUTermPlugin.PromptPalette.Value, true);
        private static readonly Map PaletteRemote = new Map(() => UwUTermPlugin.PromptPaletteRemote.Value, true);
        private static readonly Map Symbols = new Map(() => UwUTermPlugin.PromptSym.Value, false);
        private static readonly Map SymbolsRemote = new Map(() => UwUTermPlugin.PromptSymRemote.Value, false);

        internal static string LastRenderedFor(TerminalListAdapter adapter) =>
            LastRendered.TryGetValue(adapter, out string rendered) ? rendered : null;

        // The prompt exactly as the server sent it. Completion is built by splitting the
        // whole input line - prompt included - on spaces, so it has to be given a prompt that
        // splits the way the server's own does.
        private static readonly Dictionary<TerminalListAdapter, string> LastRaw =
            new Dictionary<TerminalListAdapter, string>();

        internal static string LastRawFor(TerminalListAdapter adapter) =>
            LastRaw.TryGetValue(adapter, out string raw) ? raw : null;

        private static void Prefix(TerminalListAdapter __instance, ref string rawText, bool isPrompt, bool isMsgInput)
        {
            if (!UwUTermPlugin.FeatureTerminal.Value) return;
            if (!isPrompt && !isMsgInput) return;
            if (string.IsNullOrEmpty(rawText)) return;

            Match m = Shape.Match(rawText);
            if (!m.Success) return;

            Terminal terminal = __instance.GetComponentInParent<Terminal>();
            bool remote = terminal != null && terminal.isRemoteConnection;

            LastRaw[__instance] = rawText;

            // The prompt left as the game wrote it, and everything else about the terminal
            // still ours. What was captured above is what the rest of the mod reads to know
            // where a line begins, so recording it here is what keeps input handling, history
            // and completion working against the game's own prompt.
            if (!UwUTermPlugin.CustomPrompt.Value)
            {
                LastRendered[__instance] = rawText;
                return;
            }

            string template = UwUTermPlugin.PromptTemplate.Value;
            if (remote && !string.IsNullOrEmpty(UwUTermPlugin.PromptTemplateRemote.Value))
                template = UwUTermPlugin.PromptTemplateRemote.Value;

            // The server's own trailing space is deliberately dropped: the template is the
            // whole prompt, so trailing space is {sp}'s job. Appending both gave two.
            string rendered = Render(template, m, terminal, remote);
            LastRendered[__instance] = rendered;
            rawText = rendered;
        }

        private static string Render(string template, Match m, Terminal terminal, bool remote)
        {
            string user = m.Groups["user"].Value;
            string role = user == "root" ? "root" : (user == "guest" ? "guest" : null);

            var sb = new StringBuilder(160);
            string running = null;   // null = no tag at all, i.e. the terminal's theme colour
            int cursor = 0;

            foreach (Match token in Token.Matches(template))
            {
                AppendLiteral(sb, template.Substring(cursor, token.Index - cursor), running);
                cursor = token.Index + token.Length;

                string name = token.Groups[1].Value;

                if (name == "/") { running = null; continue; }
                if (name == "nl") { sb.Append('\n'); continue; }
                // BepInEx trims config values, so a trailing space has to be explicit.
                if (name == "sp") { sb.Append(Tag(running, " ")); continue; }

                if (name.Length > 1 && name[0] == '#')
                {
                    string colour = ColourToken(name.Substring(1), role, remote);
                    // An unresolvable colour prints itself, so a typo is visible on screen
                    // rather than silently rendering as no colour at all.
                    if (colour == null) AppendLiteral(sb, token.Value, running);
                    else running = colour;
                    continue;
                }

                string text;
                switch (name)
                {
                    case "user":   text = user; break;
                    case "host":   text = m.Groups["host"].Value; break;
                    case "path":   text = m.Groups["path"].Value; break;
                    case "sym":    text = Symbol(role, remote, m.Groups["sym"].Value); break;
                    case "ip":     text = terminal == null ? "" : terminal.remotePublicIP; break;
                    case "device": text = terminal == null ? "" : terminal.deviceName; break;
                    case "pid":    text = terminal == null ? "" : terminal.GetPID().ToString(); break;
                    default:
                        AppendLiteral(sb, token.Value, running);
                        continue;
                }

                // The palette dresses the field; anything it does not name inherits the
                // running colour, and afterwards the running colour simply continues.
                sb.Append(Tag(Lookup(name, role, remote) ?? running, text));
            }

            AppendLiteral(sb, template.Substring(cursor), running);
            return sb.ToString();
        }

        private static string ColourToken(string name, string role, bool remote) =>
            Hex.IsMatch(name) ? "#" + name : Lookup(name, role, remote);

        /// <summary>Palette lookup: the role-specific name first, then the plain one, with
        /// PaletteRemote overlaying Palette on a remote machine. Null when undefined.</summary>
        private static string Lookup(string name, string role, bool remote)
        {
            if (role != null)
            {
                string byRole = Get(Palette, PaletteRemote, name + "." + role, remote);
                if (byRole != null) return byRole;
            }
            return Get(Palette, PaletteRemote, name, remote);
        }

        private static string Symbol(string role, bool remote, string fromServer)
        {
            string key = role ?? "user";
            return Get(Symbols, SymbolsRemote, key, remote) ?? fromServer;
        }

        private static string Get(Map local, Map overlay, string key, bool remote)
        {
            if (remote && overlay.Current.TryGetValue(key, out string value)) return value;
            return local.Current.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>Literal template text in the running colour. Newlines are emitted raw -
        /// a colour tag spanning a line break would be split by AddText's \n handling.</summary>
        private static void AppendLiteral(StringBuilder sb, string text, string colour)
        {
            if (text.Length == 0) return;

            string[] lines = text.Replace("\\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(Tag(colour, lines[i]));
            }
        }

        private static string Tag(string colour, string text)
        {
            if (text.Length == 0) return "";
            return colour == null ? text : "<color=" + colour + ">" + text + "</color>";
        }

        /// <summary>A name:value config line, reparsed whenever the setting changes.</summary>
        private sealed class Map
        {
            private readonly System.Func<string> _source;
            private readonly bool _colours;
            private string _parsed;

            internal Dictionary<string, string> Entries { get; private set; } = new Dictionary<string, string>();

            internal Map(System.Func<string> source, bool colours)
            {
                _source = source;
                _colours = colours;
            }

            private Dictionary<string, string> Build()
            {
                string source = _source();
                if (_parsed == source) return Entries;
                _parsed = source;

                var map = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(source))
                {
                    foreach (string entry in source.Split(','))
                    {
                        int split = entry.IndexOf(':');
                        if (split <= 0) continue;

                        string key = entry.Substring(0, split).Trim();
                        string value = entry.Substring(split + 1).Trim();
                        if (key.Length == 0 || value.Length == 0) continue;
                        if (_colours && value[0] != '#') value = "#" + value;
                        map[key] = value;
                    }
                }
                Entries = map;
                return Entries;
            }

            internal Dictionary<string, string> Current => Build();
        }
    }
}
