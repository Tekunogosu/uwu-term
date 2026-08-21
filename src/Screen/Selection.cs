using System.Collections.Generic;
using System.Text;

namespace UwUTerm.Screen
{
    /// <summary>A place in the scrollback: which line, and how many drawn runes into it.</summary>
    internal struct ScreenPoint
    {
        internal int Line;
        internal int Rune;

        internal static readonly ScreenPoint None = new ScreenPoint { Line = -1, Rune = 0 };

        internal bool Exists => Line >= 0;

        internal bool Before(ScreenPoint other) =>
            Line != other.Line ? Line < other.Line : Rune < other.Rune;

        internal bool Equals(ScreenPoint other) => Line == other.Line && Rune == other.Rune;
    }

    /// <summary>
    /// A run of selected text, held against the scrollback rather than against the screen.
    ///
    /// Screen coordinates would be the obvious choice and the wrong one: scrolling, resizing
    /// and new output all move a line to a different row, and a selection anchored to a row
    /// would slide onto whatever text arrived there. Anchored to the line itself, it stays on
    /// the words it was dragged over.
    /// </summary>
    internal struct ScreenSpan
    {
        internal ScreenPoint Anchor;
        internal ScreenPoint Head;

        internal bool Exists => Anchor.Exists && Head.Exists && !Anchor.Equals(Head);

        /// <summary>The two ends in reading order, whichever way the drag went.</summary>
        internal void Ordered(out ScreenPoint from, out ScreenPoint to)
        {
            if (Head.Before(Anchor)) { from = Head; to = Anchor; }
            else { from = Anchor; to = Head; }
        }

        internal bool Covers(int line, int rune)
        {
            if (!Exists) return false;

            Ordered(out ScreenPoint from, out ScreenPoint to);
            if (line < from.Line || line > to.Line) return false;
            if (line == from.Line && rune < from.Rune) return false;
            if (line == to.Line && rune >= to.Rune) return false;
            return true;
        }

        /// <summary>The selected columns on one line, as a half-open range of runes. The end
        /// is int.MaxValue when the selection runs past what the line holds.</summary>
        internal bool RunesOn(int line, out int first, out int last)
        {
            first = 0;
            last = 0;
            if (!Exists) return false;

            Ordered(out ScreenPoint from, out ScreenPoint to);
            if (line < from.Line || line > to.Line) return false;

            first = line == from.Line ? from.Rune : 0;
            last = line == to.Line ? to.Rune : int.MaxValue;
            return last > first;
        }
    }

    internal static class SelectionText
    {
        /// <summary>
        /// The word under a point, as a double-click selects it.
        ///
        /// Landing on a gap selects the run of gap rather than nothing, which is what makes a
        /// double-click between two words feel like it did something. Word here means what it
        /// means to Alt+B, so a path or a flag comes out whole.
        /// </summary>
        internal static ScreenSpan Word(IReadOnlyList<string> lines, ScreenPoint at)
        {
            if (lines == null || at.Line < 0 || at.Line >= lines.Count) return default(ScreenSpan);

            int[] runes = MarkupReader.Runes(lines[at.Line]);
            if (runes.Length == 0) return default(ScreenSpan);

            int index = at.Rune;
            if (index >= runes.Length) index = runes.Length - 1;
            if (index < 0) return default(ScreenSpan);

            bool word = Words.IsWord(runes[index]);

            int first = index;
            while (first > 0 && Words.IsWord(runes[first - 1]) == word) first--;

            int last = index;
            while (last + 1 < runes.Length && Words.IsWord(runes[last + 1]) == word) last++;

            return new ScreenSpan
            {
                Anchor = new ScreenPoint { Line = at.Line, Rune = first },
                Head = new ScreenPoint { Line = at.Line, Rune = last + 1 },
            };
        }

        /// <summary>
        /// A whole logical line, as a triple-click selects it - all of it, however many rows it
        /// wrapped onto, because the wrapping is a property of the window rather than of the
        /// text.
        /// </summary>
        internal static ScreenSpan Line(IReadOnlyList<string> lines, int line)
        {
            if (lines == null || line < 0 || line >= lines.Count) return default(ScreenSpan);

            int count = MarkupReader.RuneCount(lines[line]);
            if (count == 0) return default(ScreenSpan);

            return new ScreenSpan
            {
                Anchor = new ScreenPoint { Line = line, Rune = 0 },
                Head = new ScreenPoint { Line = line, Rune = count },
            };
        }

        /// <summary>
        /// The selected text, as it would be pasted.
        ///
        /// Lines join with a newline and nothing else: a terminal's wrapping is a property of
        /// how wide the window happens to be, and a copied command should not come back with
        /// breaks in it that only ever existed on screen.
        ///
        /// Trailing blanks are dropped only where the selection runs to the end of a line,
        /// which is where they are padding the server put there to line columns up. Where the
        /// selection stops mid-line the end was chosen deliberately, and a space selected on
        /// purpose is text like any other.
        /// </summary>
        internal static string Extract(IReadOnlyList<string> lines, ScreenSpan span)
        {
            if (!span.Exists || lines == null) return "";

            span.Ordered(out ScreenPoint from, out ScreenPoint to);
            if (from.Line < 0 || from.Line >= lines.Count) return "";

            int lastLine = to.Line < lines.Count ? to.Line : lines.Count - 1;

            var sb = new StringBuilder();
            for (int line = from.Line; line <= lastLine; line++)
            {
                if (!span.RunesOn(line, out int first, out int last)) continue;

                if (sb.Length > 0) sb.Append('\n');

                string piece = MarkupReader.Slice(lines[line], first, last);
                sb.Append(last == int.MaxValue ? piece.TrimEnd() : piece);
            }
            return sb.ToString();
        }
    }
}
