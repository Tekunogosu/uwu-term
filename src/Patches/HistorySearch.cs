using System;
using System.Collections.Generic;
using TerminalPoolSystem;
using TMPro;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Ctrl+R / Ctrl+S incremental history search, readline's i-search.
    ///
    /// readline draws the search state over the prompt itself. Here the query goes in an
    /// overlay panel and only the matched command is written to the input line - rewriting
    /// the prompt would mean moving minPosCursor, which every caret index is measured
    /// against, and the prompt is already carrying our own colour tags.
    ///
    /// Ending the search follows readline: Enter accepts and runs, Escape restores what you
    /// were typing, and any key with no meaning here accepts the match and is then handled
    /// normally - so Ctrl+R ls Left-arrow leaves you editing the recalled command.
    /// </summary>
    internal static class HistorySearch
    {
        private sealed class Session
        {
            internal Overlay Panel;
            internal string Query = "";
            internal string Original = "";
            internal int Index = -1;
            internal bool Backwards = true;
            internal bool Failing;
        }

        private static readonly Dictionary<Terminal, Session> Sessions = new Dictionary<Terminal, Session>();

        // bash keeps the last search term, so Ctrl+R Ctrl+R resumes the previous search
        // instead of sitting there with nothing to match.
        private static string _lastQuery = "";

        internal static bool IsActive(Terminal terminal) => Sessions.ContainsKey(terminal);

        internal static void Open(Terminal terminal, TerminalListAdapter adapter, bool backwards)
        {
            if (Sessions.TryGetValue(terminal, out Session running))
            {
                running.Backwards = backwards;
                Step(terminal, adapter, running);
                return;
            }

            if (terminal.historialComandos == null || terminal.historialComandos.Count == 0) return;

            var session = new Session { Backwards = backwards };
            Readline.ReadInput(adapter, out session.Original, out _);
            session.Index = backwards ? terminal.historialComandos.Count : -1;
            session.Panel = Build(terminal, adapter);

            Sessions[terminal] = session;
            Status(session);
        }

        /// <summary>True when the key belonged to the search. False means the search has
        /// ended and the terminal should handle the key as usual.</summary>
        internal static bool HandleKey(Terminal terminal, TerminalListAdapter adapter, Event e)
        {
            if (!Sessions.TryGetValue(terminal, out Session session)) return false;

            bool ctrl = e.control && !e.alt && !e.shift;

            if (Readline.Matches(UwUTermPlugin.HistorySearchBackward.Value, e))
            {
                session.Backwards = true; Step(terminal, adapter, session); return true;
            }
            if (Readline.Matches(UwUTermPlugin.HistorySearchForward.Value, e))
            {
                session.Backwards = false; Step(terminal, adapter, session); return true;
            }

            switch (e.keyCode)
            {
                case KeyCode.Escape:
                    Readline.WriteInput(adapter, session.Original, session.Original.Length);
                    Finish(terminal);
                    return true;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    // Accepted: leave the command on the line and let the terminal run it.
                    Finish(terminal);
                    return false;

                case KeyCode.Backspace:
                    if (session.Query.Length > 0)
                    {
                        session.Query = session.Query.Substring(0, session.Query.Length - 1);
                        _lastQuery = session.Query;
                        Research(terminal, adapter, session);
                    }
                    return true;
            }

            if (ctrl && (e.keyCode == KeyCode.C || e.keyCode == KeyCode.G))
            {
                Readline.WriteInput(adapter, session.Original, session.Original.Length);
                Finish(terminal);
                return e.keyCode == KeyCode.G;   // Ctrl+C still reaches the terminal
            }

            char typed = e.character;
            if (typed >= ' ' && typed != 127 && !e.control && !e.alt)
            {
                session.Query += typed;
                _lastQuery = session.Query;
                Research(terminal, adapter, session);
                return true;
            }

            // Unity raises a second KeyDown for the same keystroke carrying only the
            // character, with keyCode None - and for Ctrl+R that character is a control
            // code. Treating those as "some other key" is what ended the search on the very
            // keystroke meant to cycle it. Modifier presses land here too.
            if (e.keyCode == KeyCode.None || IsModifier(e.keyCode) || e.control || e.alt) return true;

            // Anything else accepts the match and is handled normally.
            Finish(terminal);
            return false;
        }

        internal static void Dismiss(uDialog dialog)
        {
            Terminal owner = null;
            foreach (KeyValuePair<Terminal, Session> pair in Sessions)
                if (pair.Key != null && ReferenceEquals(pair.Key.dialogo, dialog)) { owner = pair.Key; break; }

            if (owner == null) return;
            Sessions[owner].Panel?.Destroy();
            Sessions.Remove(owner);
        }

        // ---- matching ---------------------------------------------------------------

        /// <summary>Re-run from where we are: a longer query should still match the entry
        /// on screen if it can, rather than jumping somewhere older.</summary>
        private static void Research(Terminal terminal, TerminalListAdapter adapter, Session session)
        {
            List<string> history = terminal.historialComandos;
            int from = session.Index;
            if (from < 0 || from >= history.Count) from = session.Backwards ? history.Count - 1 : 0;

            int found = Find(history, session.Query, from, session.Backwards);
            Apply(terminal, adapter, session, found);
        }

        private static void Step(Terminal terminal, TerminalListAdapter adapter, Session session)
        {
            if (session.Query.Length == 0 && _lastQuery.Length > 0) session.Query = _lastQuery;

            List<string> history = terminal.historialComandos;
            int from = session.Index + (session.Backwards ? -1 : 1);
            int found = Find(history, session.Query, from, session.Backwards);
            Apply(terminal, adapter, session, found);
        }

        private static void Apply(Terminal terminal, TerminalListAdapter adapter, Session session, int found)
        {
            if (found < 0)
            {
                session.Failing = true;
            }
            else
            {
                session.Failing = false;
                session.Index = found;
                string command = terminal.historialComandos[found];
                Readline.WriteInput(adapter, command, command.Length);
            }
            Status(session);
        }

        private static int Find(List<string> history, string query, int from, bool backwards)
        {
            if (history.Count == 0) return -1;
            if (query.Length == 0) return -1;

            // Smart case, as elsewhere in the mod: lowercase matches anything.
            StringComparison comparison = HasUpper(query)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            int i = Mathf.Clamp(from, backwards ? -1 : 0, backwards ? history.Count - 1 : history.Count);
            while (i >= 0 && i < history.Count)
            {
                if (history[i].IndexOf(query, comparison) >= 0) return i;
                i += backwards ? -1 : 1;
            }
            return -1;
        }

        private static bool IsModifier(KeyCode key) =>
            key == KeyCode.LeftControl || key == KeyCode.RightControl ||
            key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
            key == KeyCode.LeftShift || key == KeyCode.RightShift ||
            key == KeyCode.LeftCommand || key == KeyCode.RightCommand;

        private static bool HasUpper(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (char.IsUpper(text[i])) return true;
            return false;
        }

        // ---- presentation -----------------------------------------------------------

        private static void Status(Session session)
        {
            string label = session.Backwards ? "reverse-i-search" : "i-search";
            if (session.Failing) label = "failing " + label;
            session.Panel?.SetText("(" + label + ")`" + session.Query + "'");
        }

        private static void Finish(Terminal terminal)
        {
            if (!Sessions.TryGetValue(terminal, out Session session)) return;
            session.Panel?.Destroy();
            Sessions.Remove(terminal);
        }

        private static Overlay Build(Terminal terminal, TerminalListAdapter adapter)
        {
            RectTransform parent = terminal.dialogo != null ? terminal.dialogo.RectTransform : null;
            if (parent == null) return null;

            TMP_Text sample = adapter.GetLastViewLine()?.lineText
                              ?? terminal.GetComponentInChildren<TMP_Text>();

            Overlay panel = Overlay.CreateTopRight(
                parent, sample?.font, sample != null ? sample.fontSize : 14f, new Vector2(-12f, -34f));
            panel.Show();
            return panel;
        }
    }
}
