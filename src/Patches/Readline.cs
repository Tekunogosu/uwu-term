using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using TerminalPoolSystem;
using UnityEngine;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Readline editing on the terminal input line.
    ///
    /// Terminal.OnGUI is the game's keyboard handler - an Event.current switch that already
    /// maps Home/End, the arrows, Ctrl+C and Ctrl+Shift+C/V. Plain Ctrl+letter and Alt+letter
    /// are unclaimed, and unhandled keys fall through to a character filter that rejects
    /// control characters, so nothing is inserted today and nothing needs un-inserting.
    ///
    /// Every command reads the input line, computes the new text and caret, and writes both
    /// back in one go rather than driving the game's per-character InputChar/BackSpace. That
    /// is one text-mesh rebuild per command instead of one per character - the difference
    /// between a 60-character kill costing 0.1ms and costing a whole frame.
    /// </summary>
    [HarmonyPatch(typeof(Terminal), "OnGUI")]
    internal static class Readline
    {
        private const int UndoDepth = 64;

        private struct Snapshot
        {
            internal string Input;
            internal int Point;
        }

        private static readonly Dictionary<Terminal, List<Snapshot>> Undos =
            new Dictionary<Terminal, List<Snapshot>>();

        // Set by a yank so M-y knows what to replace, and where.
        private static int _yankAt, _yankLength;

        private static bool Prefix(Terminal __instance)
        {
            if (!UwUTermPlugin.EnableReadline.Value) return true;

            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return true;

            if (!__instance.inicializado || __instance.inputDisabled) return true;

            TerminalListAdapter adapter = __instance.listAdapter;
            if (adapter == null || !adapter.IsFocus()) return true;

            // A running script polling for raw keys wants the keystroke untouched.
            if (__instance.IsPollingScriptInputEnabled() || __instance.pendingAnyKey) return true;

            // Ctrl+Alt+Shift belongs to the window hotkeys; without this the terminal would
            // also move its caret on the arrow keys they use.
            if (e.control && e.alt && e.shift) { e.Use(); return false; }

            // A completion menu takes keys first, and hands back the ones that end it.
            if (Completion.IsActive(__instance))
            {
                if (Completion.HandleKey(__instance, adapter, e)) { e.Use(); return false; }
                return true;
            }

            // An incremental history search takes keys first, and hands back the ones that
            // end it so the terminal still acts on them.
            if (HistorySearch.IsActive(__instance))
            {
                if (HistorySearch.HandleKey(__instance, adapter, e)) { e.Use(); return false; }
                return true;
            }

            // An open search owns the keyboard until it is closed.
            if (Search.IsActive(__instance))
            {
                if (Search.HandleKey(__instance, adapter, e)) { e.Use(); return false; }
                return true;
            }

            // Any key that is not a bare modifier breaks a run of kills or yanks. Modifiers
            // arrive as their own KeyDown, so skipping them is what lets C-y M-y work at all.
            bool wasYank = KillRing.LastWasYank;
            bool wasKill = KillRing.LastWasKill;
            if (!IsModifier(e.keyCode))
            {
                KillRing.LastWasKill = false;
                KillRing.LastWasYank = false;
            }
            KillRing.ContinuingRun = wasKill;

            if (Matches(UwUTermPlugin.SearchScrollback.Value, e))
            {
                Search.Open(__instance, adapter); e.Use(); return false;
            }
            if (Matches(UwUTermPlugin.HistorySearchBackward.Value, e))
            {
                HistorySearch.Open(__instance, adapter, backwards: true); e.Use(); return false;
            }
            if (Matches(UwUTermPlugin.HistorySearchForward.Value, e))
            {
                HistorySearch.Open(__instance, adapter, backwards: false); e.Use(); return false;
            }

            bool ctrl = e.control && !e.alt && !e.shift && !e.command;
            bool alt = e.alt && !e.control && !e.shift && !e.command;
            if (!ctrl && !alt) return true;

            bool handled = ctrl
                ? Control(__instance, adapter, e.keyCode)
                : Meta(__instance, adapter, e.keyCode, wasYank);

            if (!handled) return true;

            e.Use();
            return false;
        }

        private static bool Control(Terminal terminal, TerminalListAdapter a, KeyCode key)
        {
            switch (key)
            {
                case KeyCode.A: return Move(a, _ => 0);
                case KeyCode.E: return Move(a, s => s.Length);
                case KeyCode.B: return Nudge(a, -1);
                case KeyCode.F: return Nudge(a, +1);
                case KeyCode.LeftArrow: return MoveWord(a, back: true);
                case KeyCode.RightArrow: return MoveWord(a, back: false);

                case KeyCode.K: return KillTo(terminal, a, s => s.Length, backward: false);
                case KeyCode.U: return KillTo(terminal, a, _ => 0, backward: true);
                case KeyCode.W: return KillWord(terminal, a, back: true, whitespaceOnly: true);
                case KeyCode.Backspace: return KillWord(terminal, a, back: true, whitespaceOnly: false);
                case KeyCode.D: return DeleteForward(terminal, a);

                case KeyCode.Y: return Yank(terminal, a);
                case KeyCode.T: return TransposeChars(terminal, a);
                case KeyCode.Z: return Undo(terminal, a);
                case KeyCode.L: ClearScreen(terminal, a); return true;

                case KeyCode.P: return History(terminal, back: true);
                case KeyCode.N: return History(terminal, back: false);

                default: return false;   // Ctrl+C and friends stay the game's
            }
        }

        private static bool Meta(Terminal terminal, TerminalListAdapter a, KeyCode key, bool wasYank)
        {
            switch (key)
            {
                case KeyCode.B: return MoveWord(a, back: true);
                case KeyCode.F: return MoveWord(a, back: false);
                case KeyCode.LeftArrow: return MoveWord(a, back: true);
                case KeyCode.RightArrow: return MoveWord(a, back: false);
                case KeyCode.D: return KillWord(terminal, a, back: false, whitespaceOnly: false);
                case KeyCode.Backspace: return KillWord(terminal, a, back: true, whitespaceOnly: false);
                case KeyCode.Y: return YankPop(terminal, a, wasYank);
                case KeyCode.T: return TransposeWords(terminal, a);
                case KeyCode.U: return CaseWord(terminal, a, Casing.Upper);
                case KeyCode.L: return CaseWord(terminal, a, Casing.Lower);
                case KeyCode.C: return CaseWord(terminal, a, Casing.Capital);
                default: return false;
            }
        }

        // ---- commands ---------------------------------------------------------------

        private static bool Move(TerminalListAdapter a, System.Func<string, int> target)
        {
            if (!Read(a, out string input, out _)) return false;
            Write(a, input, Mathf.Clamp(target(input), 0, input.Length));
            return true;
        }

        private static bool Nudge(TerminalListAdapter a, int delta)
        {
            if (!Read(a, out string input, out int point)) return false;
            Write(a, input, Mathf.Clamp(point + delta, 0, input.Length));
            return true;
        }

        private static bool MoveWord(TerminalListAdapter a, bool back)
        {
            if (!Read(a, out string input, out int point)) return false;
            Write(a, input, back ? BackBoundary(input, point, false) : ForwardBoundary(input, point, false));
            return true;
        }

        private static bool KillTo(Terminal t, TerminalListAdapter a, System.Func<string, int> target, bool backward)
        {
            if (!Read(a, out string input, out int point)) return false;

            int edge = Mathf.Clamp(target(input), 0, input.Length);
            int from = Mathf.Min(edge, point);
            int to = Mathf.Max(edge, point);
            if (to == from) return true;

            PushUndo(t, input, point);
            KillRing.Kill(input.Substring(from, to - from), backward);
            Write(a, input.Remove(from, to - from), from);
            return true;
        }

        private static bool KillWord(Terminal t, TerminalListAdapter a, bool back, bool whitespaceOnly)
        {
            if (!Read(a, out string input, out int point)) return false;

            int edge = back ? BackBoundary(input, point, whitespaceOnly)
                            : ForwardBoundary(input, point, whitespaceOnly);
            int from = Mathf.Min(edge, point);
            int to = Mathf.Max(edge, point);
            if (to == from) return true;

            PushUndo(t, input, point);
            KillRing.Kill(input.Substring(from, to - from), back);
            Write(a, input.Remove(from, to - from), from);
            return true;
        }

        /// <summary>C-d. Deletes forward only - readline would treat it as EOF on an empty
        /// line, which here would mean closing the terminal on a stray keypress.</summary>
        private static bool DeleteForward(Terminal t, TerminalListAdapter a)
        {
            if (!Read(a, out string input, out int point)) return false;
            if (point >= input.Length) return true;

            PushUndo(t, input, point);
            Write(a, input.Remove(point, 1), point);
            return true;
        }

        private static bool Yank(Terminal t, TerminalListAdapter a)
        {
            string text = KillRing.Current;
            if (string.IsNullOrEmpty(text)) return true;
            if (!Read(a, out string input, out int point)) return false;

            PushUndo(t, input, point);
            Write(a, input.Insert(point, text), point + text.Length);

            _yankAt = point;
            _yankLength = text.Length;
            KillRing.LastWasYank = true;
            return true;
        }

        private static bool YankPop(Terminal t, TerminalListAdapter a, bool wasYank)
        {
            if (!wasYank) return true;

            string text = KillRing.Rotate();
            if (text == null) return true;
            if (!Read(a, out string input, out int point)) return false;
            if (_yankAt + _yankLength > input.Length) return true;

            string without = input.Remove(_yankAt, _yankLength);
            Write(a, without.Insert(_yankAt, text), _yankAt + text.Length);

            _yankLength = text.Length;
            KillRing.LastWasYank = true;
            return true;
        }

        private static bool TransposeChars(Terminal t, TerminalListAdapter a)
        {
            if (!Read(a, out string input, out int point)) return false;
            if (input.Length < 2) return true;

            // At the end of the line readline swaps the final two characters.
            int right = point >= input.Length ? input.Length - 1 : point;
            int left = right - 1;
            if (left < 0) return true;

            PushUndo(t, input, point);
            char[] chars = input.ToCharArray();
            char swap = chars[left];
            chars[left] = chars[right];
            chars[right] = swap;
            Write(a, new string(chars), Mathf.Min(right + 1, input.Length));
            return true;
        }

        private static bool TransposeWords(Terminal t, TerminalListAdapter a)
        {
            if (!Read(a, out string input, out int point)) return false;

            int secondEnd = ForwardBoundary(input, point, false);
            int secondStart = BackBoundary(input, secondEnd, false);
            int firstEnd = BackBoundary(input, secondStart, false) == secondStart
                ? secondStart
                : PreviousWordEnd(input, secondStart);
            int firstStart = BackBoundary(input, firstEnd, false);

            if (firstStart >= firstEnd || secondStart >= secondEnd || firstEnd > secondStart) return true;

            PushUndo(t, input, point);
            string first = input.Substring(firstStart, firstEnd - firstStart);
            string gap = input.Substring(firstEnd, secondStart - firstEnd);
            string second = input.Substring(secondStart, secondEnd - secondStart);

            string rebuilt = input.Substring(0, firstStart) + second + gap + first + input.Substring(secondEnd);
            Write(a, rebuilt, secondEnd);
            return true;
        }

        private enum Casing { Upper, Lower, Capital }

        private static bool CaseWord(Terminal t, TerminalListAdapter a, Casing casing)
        {
            if (!Read(a, out string input, out int point)) return false;

            int end = ForwardBoundary(input, point, false);
            int start = point;
            while (start < end && !IsWordChar(input[start])) start++;
            if (start >= end) return true;

            PushUndo(t, input, point);
            string word = input.Substring(start, end - start);
            switch (casing)
            {
                case Casing.Upper: word = word.ToUpperInvariant(); break;
                case Casing.Lower: word = word.ToLowerInvariant(); break;
                default:
                    word = char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant();
                    break;
            }

            Write(a, input.Substring(0, start) + word + input.Substring(end), end);
            return true;
        }

        private static bool History(Terminal t, bool back)
        {
            if (t.isPasswordMode || !t.promptEnabled || !t.addToHistory) return false;
            if (t.historialComandos.Count == 0) return false;

            if (back)
            {
                if (t.indiceHistorial <= 0) return true;
                t.indiceHistorial--;
            }
            else
            {
                if (t.indiceHistorial >= t.historialComandos.Count - 1) return true;
                t.indiceHistorial++;
            }

            t.UpdateHistorialPrompt();
            return true;
        }

        /// <summary>
        /// Ctrl+L. LimpiarPantalla redraws Terminal.pwd, which is a hardcoded "localhost/>"
        /// that the game only refreshes on some paths - the live prompt comes from the
        /// server as ordinary output. So point pwd at the prompt actually on screen, which
        /// also makes AddTexto's texto.Equals(pwd) test true and set minPosCursor properly.
        /// </summary>
        private static void ClearScreen(Terminal terminal, TerminalListAdapter adapter)
        {
            string line = CurrentLine(adapter);
            int min = adapter.minPosCursor;
            string pending = "";

            if (line != null && min > 0 && line.Length > min)
            {
                terminal.pwd = Prompt.LastRenderedFor(adapter) ?? adapter.GetPromptText();
                if (!terminal.isPasswordMode && line.Length > min + 1)
                    pending = line.Substring(min + 1);
            }

            terminal.LimpiarPantalla(showPrompt: true);
            if (pending.Length > 0) Write(adapter, pending, pending.Length);
        }

        private static bool Undo(Terminal t, TerminalListAdapter a)
        {
            if (!Undos.TryGetValue(t, out List<Snapshot> stack) || stack.Count == 0) return true;

            Snapshot snapshot = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            Write(a, snapshot.Input, snapshot.Point);
            return true;
        }

        private static void PushUndo(Terminal t, string input, int point)
        {
            if (t == null) return;
            if (!Undos.TryGetValue(t, out List<Snapshot> stack))
                Undos[t] = stack = new List<Snapshot>();

            stack.Add(new Snapshot { Input = input, Point = point });
            while (stack.Count > UndoDepth) stack.RemoveAt(0);
        }

        // ---- line access ------------------------------------------------------------

        private static string CurrentLine(TerminalListAdapter a) =>
            a.Data.Count == 0 ? null : a.Data[a.Data.Count - 1].line;

        internal static bool ReadInput(TerminalListAdapter a, out string input, out int point) =>
            Read(a, out input, out point);

        internal static void WriteInput(TerminalListAdapter a, string input, int point) =>
            Write(a, input, point);

        /// <summary>The editable text and the caret's position within it. The caret sits
        /// *after* charIndexInput, so point is an insertion index, 0..input.Length.</summary>
        private static bool Read(TerminalListAdapter a, out string input, out int point)
        {
            input = null;
            point = 0;

            string line = CurrentLine(a);
            int min = a.minPosCursor;
            if (line == null || min < 0 || line.Length < min + 1) return false;

            input = line.Substring(min + 1);
            point = Mathf.Clamp(a.charIndexInput - min, 0, input.Length);
            return true;
        }

        /// <summary>Rewrite the whole input line and place the caret, in one view update.
        /// This is what InputChar and BackSpace do per character; batching costs one text
        /// rebuild instead of one per character.</summary>
        private static void Write(TerminalListAdapter a, string input, int point)
        {
            int last = a.Data.Count - 1;
            if (last < 0) return;

            string line = a.Data[last].line;
            int min = a.minPosCursor;
            if (line.Length < min + 1) return;

            a.Data[last].line = line.Substring(0, min + 1) + input;
            a.charIndexInput = min + Mathf.Clamp(point, 0, input.Length);

            TerminalListItemViewsHolder view = a.GetLastViewLine();
            if (view == null) return;

            a.UpdateItemViewText(view);
            a.CheckForceUpdateView(view, view.ItemIndex);
            a.SetCaretPos(view);
        }

        // ---- word boundaries ---------------------------------------------------------

        private static bool IsWordChar(char c) => UwUTerm.Screen.Words.IsWord(c);

        private static bool IsPart(char c, bool whitespaceOnly) =>
            whitespaceOnly ? !char.IsWhiteSpace(c) : IsWordChar(c);

        private static int BackBoundary(string s, int point, bool whitespaceOnly)
        {
            int p = Mathf.Clamp(point, 0, s.Length);
            while (p > 0 && !IsPart(s[p - 1], whitespaceOnly)) p--;
            while (p > 0 && IsPart(s[p - 1], whitespaceOnly)) p--;
            return p;
        }

        private static int ForwardBoundary(string s, int point, bool whitespaceOnly)
        {
            int p = Mathf.Clamp(point, 0, s.Length);
            while (p < s.Length && !IsPart(s[p], whitespaceOnly)) p++;
            while (p < s.Length && IsPart(s[p], whitespaceOnly)) p++;
            return p;
        }

        private static int PreviousWordEnd(string s, int from)
        {
            int p = Mathf.Clamp(from, 0, s.Length);
            while (p > 0 && !IsWordChar(s[p - 1])) p--;
            return p;
        }

        /// <summary>
        /// Match a configured shortcut against an IMGUI event rather than polling Input:
        /// OnGUI can run several times in a frame, and GetKeyDown would be true for each of
        /// them, firing one keystroke twice. Event carries no left/right distinction, so the
        /// shortcut's modifiers are normalised to the three flags it does have.
        /// </summary>
        internal static bool Matches(KeyboardShortcut shortcut, Event e)
        {
            if (e.keyCode == KeyCode.None || e.keyCode != shortcut.MainKey) return false;

            bool ctrl = false, alt = false, shift = false;
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (modifier == KeyCode.LeftControl || modifier == KeyCode.RightControl) ctrl = true;
                else if (modifier == KeyCode.LeftAlt || modifier == KeyCode.RightAlt) alt = true;
                else if (modifier == KeyCode.LeftShift || modifier == KeyCode.RightShift) shift = true;
            }

            return e.control == ctrl && e.alt == alt && e.shift == shift;
        }

        private static bool IsModifier(KeyCode key) =>
            key == KeyCode.None ||
            key == KeyCode.LeftControl || key == KeyCode.RightControl ||
            key == KeyCode.LeftAlt || key == KeyCode.RightAlt ||
            key == KeyCode.LeftShift || key == KeyCode.RightShift ||
            key == KeyCode.LeftCommand || key == KeyCode.RightCommand;
    }
}
