using System;
using System.Collections.Generic;
using System.Text;
using TerminalPoolSystem;
using TMPro;
using UnityEngine;
using UI.Dialogs;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Ctrl+F search over the terminal scrollback.
    ///
    /// Keys are taken from the Terminal.OnGUI prefix rather than a TMP_InputField: the
    /// terminal already owns the keyboard through OnGUI, so a real input field would mean
    /// fighting the EventSystem for focus and handling every keystroke twice. While a
    /// search is open this swallows everything.
    ///
    /// Matching runs against the *visible* text - lines carry rich-text tags, including the
    /// ones our own prompt renderer adds - so every match offset is mapped back through the
    /// tags before a highlight is spliced in. The input line is never touched: its indices
    /// are what minPosCursor and charIndexInput are measured in, and shifting them would
    /// corrupt the caret or send prompt text to the server.
    /// </summary>
    internal static class Search
    {
        private struct Hit
        {
            internal int Line;
            internal int Start;    // offset into the line's visible text
            internal int Length;
        }

        private sealed class Session
        {
            internal Overlay Panel;
            internal string Query = "";
            internal readonly List<Hit> Hits = new List<Hit>();
            internal int Current = -1;
            internal readonly Dictionary<int, string> Original = new Dictionary<int, string>();
            internal double Scroll;
        }

        private static readonly Dictionary<Terminal, Session> Sessions = new Dictionary<Terminal, Session>();

        internal static bool IsActive(Terminal terminal) => Sessions.ContainsKey(terminal);

        // ---- entry points -----------------------------------------------------------

        internal static void Open(Terminal terminal, TerminalListAdapter adapter)
        {
            if (Sessions.TryGetValue(terminal, out Session existing))
            {
                Step(terminal, adapter, existing, backwards: true);
                return;
            }

            var session = new Session { Scroll = adapter.GetNormalizedPosition() };
            session.Panel = Build(terminal, adapter);
            Sessions[terminal] = session;
            Refresh(terminal, adapter, session);
        }

        /// <summary>Returns true when the key was consumed. While a search is open that is
        /// everything - the terminal must not also act on it.</summary>
        internal static bool HandleKey(Terminal terminal, TerminalListAdapter adapter, Event e)
        {
            if (!Sessions.TryGetValue(terminal, out Session session)) return false;

            switch (e.keyCode)
            {
                case KeyCode.Escape:
                    Close(terminal, adapter, restoreScroll: true);
                    return true;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Step(terminal, adapter, session, backwards: !e.shift);
                    return true;

                case KeyCode.Backspace:
                    if (session.Query.Length > 0)
                    {
                        session.Query = session.Query.Substring(0, session.Query.Length - 1);
                        Refresh(terminal, adapter, session);
                    }
                    return true;

                case KeyCode.UpArrow:
                    Step(terminal, adapter, session, backwards: true);
                    return true;

                case KeyCode.DownArrow:
                    Step(terminal, adapter, session, backwards: false);
                    return true;
            }

            if (e.control && e.keyCode == KeyCode.F)
            {
                Step(terminal, adapter, session, backwards: true);
                return true;
            }

            // Ctrl+C keeps its usual meaning as "stop what you are doing".
            if (e.control && e.keyCode == KeyCode.C)
            {
                Close(terminal, adapter, restoreScroll: true);
                return true;
            }

            char typed = e.character;
            if (typed >= ' ' && typed != 127)
            {
                session.Query += typed;
                Refresh(terminal, adapter, session);
            }
            return true;
        }

        /// <summary>The window is going away - drop the session without touching the
        /// scrollback, which is about to be destroyed anyway.</summary>
        internal static void Dismiss(uDialog dialog)
        {
            Terminal owner = null;
            foreach (System.Collections.Generic.KeyValuePair<Terminal, Session> pair in Sessions)
            {
                if (pair.Key != null && ReferenceEquals(pair.Key.dialogo, dialog)) { owner = pair.Key; break; }
            }
            if (owner == null) return;

            Session session = Sessions[owner];
            Sessions.Remove(owner);
            session.Panel?.Destroy();
        }

        internal static void Close(Terminal terminal, TerminalListAdapter adapter, bool restoreScroll)
        {
            if (!Sessions.TryGetValue(terminal, out Session session)) return;
            Sessions.Remove(terminal);

            Restore(adapter, session);
            if (restoreScroll) adapter.SetNormalizedPosition(session.Scroll);
            session.Panel?.Destroy();
        }

        // ---- search -----------------------------------------------------------------

        private static void Refresh(Terminal terminal, TerminalListAdapter adapter, Session session)
        {
            Restore(adapter, session);
            session.Hits.Clear();
            session.Current = -1;

            if (session.Query.Length > 0)
            {
                // Smart case: an uppercase anywhere in the query means you meant it.
                StringComparison comparison = HasUpper(session.Query)
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                // The last row is the input line - never searched, never tagged.
                int lines = adapter.Data.Count - 1;
                for (int i = 0; i < lines; i++)
                {
                    string visible = VisibleText(adapter.Data[i].line, out _);
                    int from = 0;
                    while (from <= visible.Length - session.Query.Length)
                    {
                        int at = visible.IndexOf(session.Query, from, comparison);
                        if (at < 0) break;
                        session.Hits.Add(new Hit { Line = i, Start = at, Length = session.Query.Length });
                        from = at + session.Query.Length;
                    }
                }

                // Terminals search backwards, so start at the most recent match.
                if (session.Hits.Count > 0) session.Current = session.Hits.Count - 1;
            }

            Paint(adapter, session);
            ShowStatus(session);
            if (session.Current >= 0) adapter.ScrollTo(session.Hits[session.Current].Line, 0.5f, 0.5f);
        }

        private static void Step(Terminal terminal, TerminalListAdapter adapter, Session session, bool backwards)
        {
            if (session.Hits.Count == 0) return;

            session.Current = backwards
                ? (session.Current - 1 + session.Hits.Count) % session.Hits.Count
                : (session.Current + 1) % session.Hits.Count;

            Paint(adapter, session);
            ShowStatus(session);
            adapter.ScrollTo(session.Hits[session.Current].Line, 0.5f, 0.5f);
        }

        private static void ShowStatus(Session session)
        {
            string count = session.Query.Length == 0
                ? "type to search"
                : session.Hits.Count == 0
                    ? "no matches"
                    : (session.Current + 1) + "/" + session.Hits.Count;

            session.Panel?.SetText("find: " + session.Query + "    " + count);
        }

        // ---- highlighting -----------------------------------------------------------

        private static void Paint(TerminalListAdapter adapter, Session session)
        {
            Restore(adapter, session);
            if (session.Hits.Count == 0) return;


            int index = 0;
            while (index < session.Hits.Count)
            {
                int line = session.Hits[index].Line;
                int start = index;
                while (index < session.Hits.Count && session.Hits[index].Line == line) index++;

                if (line >= adapter.Data.Count) continue;

                string raw = adapter.Data[line].line;
                session.Original[line] = raw;
                adapter.Data[line].line = Splice(raw, session, start, index);

                TerminalListItemViewsHolder view = adapter.GetItemViewsHolderIfVisible(line);
                if (view != null) adapter.UpdateItemViewText(view);
            }
        }

        /// <summary>Wrap each hit on one line in a colour tag. Offsets are in visible-text
        /// space, so they are mapped back through the tags, and splicing runs right to left
        /// so earlier offsets stay valid.</summary>
        private static string Splice(string raw, Session session, int from, int to)
        {
            VisibleText(raw, out int[] map);
            var sb = new StringBuilder(raw);

            for (int i = to - 1; i >= from; i--)
            {
                Hit hit = session.Hits[i];
                if (hit.Start + hit.Length > map.Length) continue;

                int rawStart = map[hit.Start];
                int rawEnd = map[hit.Start + hit.Length - 1] + 1;
                bool active = i == session.Current;

                string text = active
                    ? UwUTermPlugin.SearchActiveTextColor.Value
                    : UwUTermPlugin.SearchMatchTextColor.Value;
                string highlight = active
                    ? UwUTermPlugin.SearchActiveHighlightColor.Value
                    : UwUTermPlugin.SearchMatchHighlightColor.Value;

                // <color> recolours the glyphs; <mark> paints a quad over them, so the
                // background is optional and the text colour is what makes a match readable.
                if (!string.IsNullOrEmpty(highlight)) sb.Insert(rawEnd, "</mark>");
                sb.Insert(rawEnd, "</color>");
                sb.Insert(rawStart, "<color=" + text + ">");
                if (!string.IsNullOrEmpty(highlight)) sb.Insert(rawStart, "<mark=" + highlight + ">");
            }
            return sb.ToString();
        }

        private static void Restore(TerminalListAdapter adapter, Session session)
        {
            foreach (KeyValuePair<int, string> entry in session.Original)
            {
                if (entry.Key >= adapter.Data.Count) continue;
                adapter.Data[entry.Key].line = entry.Value;

                TerminalListItemViewsHolder view = adapter.GetItemViewsHolderIfVisible(entry.Key);
                if (view != null) adapter.UpdateItemViewText(view);
            }
            session.Original.Clear();
        }

        /// <summary>The text as rendered, with a map back to raw indices so a match found in
        /// visible space can be tagged in the real string.</summary>
        private static string VisibleText(string raw, out int[] map)
        {
            var sb = new StringBuilder(raw.Length);
            var indices = new List<int>(raw.Length);

            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '<')
                {
                    int close = raw.IndexOf('>', i);
                    if (close > i) { i = close; continue; }
                }
                indices.Add(i);
                sb.Append(raw[i]);
            }

            map = indices.ToArray();
            return sb.ToString();
        }

        private static bool HasUpper(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (char.IsUpper(text[i])) return true;
            return false;
        }

        // ---- panel ------------------------------------------------------------------

        private static Overlay Build(Terminal terminal, TerminalListAdapter adapter)
        {
            RectTransform parent = terminal.dialogo != null ? terminal.dialogo.RectTransform : null;
            if (parent == null) return null;

            TMP_Text sample = adapter.GetLastViewLine()?.lineText
                              ?? terminal.GetComponentInChildren<TMP_Text>();

            Overlay panel = Overlay.CreateTopRight(
                parent,
                sample?.font,
                sample != null ? sample.fontSize : 14f,
                new Vector2(-12f, -34f));

            panel.Show();
            return panel;
        }
    }
}
