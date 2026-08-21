// The grid's shape - a rectangle of cells, wide glyphs claiming two columns, a line too
// long for the width continuing on the next row - follows XtermSharp's Buffer/BufferLine.
// See THIRD-PARTY-NOTICES.md.
//
// One thing is deliberately not carried over: reflow. XtermSharp has to re-flow cells when
// the width changes because the bytes that produced them are long gone. Here the lines the
// server sent are still on hand, so a resize re-runs the layout from those instead. That
// makes ReflowNarrower and ReflowWider - the two most intricate files in the original -
// unnecessary rather than merely unported, and it is exact where a reflow is approximate.

using System;
using System.Collections.Generic;

namespace UwUTerm.Screen
{
    /// <summary>
    /// A rectangle of cells, composed on demand from the lines behind it.
    ///
    /// Only what is visible is ever materialised. The scrollback stays as the strings the
    /// server sent, which is both far smaller than a cell per character and the thing a
    /// resize needs anyway, so the cost of the grid is the viewport rather than the history.
    /// </summary>
    internal sealed class ScreenGrid
    {
        private readonly MarkupReader _reader = new MarkupReader();

        private Cell[] _cells = new Cell[0];

        // Display rows each logical line occupies at the current width. Measuring means
        // scanning the line's markup, so the answers are kept and only the ones that could
        // have changed are recomputed.
        private readonly List<int> _heights = new List<int>();
        private int _dirtyFrom;

        internal int Columns { get; private set; }
        internal int Rows { get; private set; }

        /// <summary>Total display rows across every line, which is what scrolling is
        /// measured in. Valid after Compose.</summary>
        internal int TotalRows { get; private set; }

        /// <summary>Where the caret landed in the grid, or -1 when it is scrolled out of
        /// sight.</summary>
        internal int CaretRow { get; private set; } = -1;
        internal int CaretColumn { get; private set; } = -1;

        internal Cell this[int row, int column] => _cells[row * Columns + column];

        /// <summary>
        /// Which logical line a display row came from, and how much of it landed there.
        ///
        /// A selection has to survive scrolling, so it is held as a position in the scrollback
        /// rather than a position on screen. Only this pass knows how the two line up - it is
        /// the thing that decided where a long line wrapped - so it records the mapping as it
        /// goes rather than making anything else work it out again.
        /// </summary>
        internal struct RowOrigin
        {
            internal int Line;
            internal int FirstRune;
            internal int RuneCount;
        }

        private RowOrigin[] _origins = new RowOrigin[0];

        internal RowOrigin OriginOf(int row) =>
            row >= 0 && row < _origins.Length ? _origins[row] : new RowOrigin { Line = -1 };

        /// <summary>
        /// How many display rows sit above a line. Where that line begins in the scrollback,
        /// measured in rows rather than lines, which is the unit scrolling works in.
        /// </summary>
        internal int RowsBefore(int lineIndex)
        {
            int rows = 0;
            int limit = lineIndex < _heights.Count ? lineIndex : _heights.Count;
            for (int i = 0; i < limit; i++) rows += _heights[i];
            return rows;
        }

        /// <summary>
        /// The nearest row carrying text, at or above the one asked for.
        ///
        /// Dragging below the last line of output lands on blank rows that map to nothing. A
        /// selection has to keep meaning something there, so it settles on the last row that
        /// does - which is what makes dragging off the bottom select to the end rather than
        /// stopping dead.
        /// </summary>
        internal int NearestRow(int row)
        {
            if (Rows <= 0) return -1;
            if (row < 0) row = 0;
            if (row >= Rows) row = Rows - 1;

            for (int r = row; r >= 0; r--)
                if (_origins[r].Line >= 0) return r;

            for (int r = row + 1; r < Rows; r++)
                if (_origins[r].Line >= 0) return r;

            return -1;
        }

        /// <summary>Turn a cell on screen into a position in the scrollback. False for a row
        /// with nothing on it.</summary>
        internal bool TryLocate(int row, int column, out int line, out int rune)
        {
            line = -1;
            rune = 0;
            if (row < 0 || row >= Rows) return false;

            RowOrigin origin = OriginOf(row);
            if (origin.Line < 0) return false;

            // Columns and runes only agree until a wide glyph turns up, so the offset is
            // counted in cells that actually carry one.
            int counted = 0;
            for (int c = 0; c < column && c < Columns; c++)
                if (_cells[row * Columns + c].Width != 0) counted++;

            line = origin.Line;
            rune = origin.FirstRune + System.Math.Min(counted, origin.RuneCount);
            return true;
        }

        // ---- direct writing --------------------------------------------------------------
        //
        // Composing a grid from lines is one way to fill it; being handed cells is another.
        // Neovim's UI protocol reports what changed - a run of cells at a position, a scrolled
        // region, a cleared screen - so a caller speaking that needs to put cells in rather
        // than describe the text they came from.

        internal void Put(int row, int column, int rune, int width, Style style) =>
            Write(row, column, rune, width, style);

        internal void Fill(Style style)
        {
            Cell blank = Cell.Blank;
            blank.Foreground = style.Foreground;
            blank.Background = style.Background;

            for (int i = 0; i < _cells.Length; i++) _cells[i] = blank;
        }

        internal void PlaceCaret(int row, int column)
        {
            CaretRow = row;
            CaretColumn = column;
        }

        /// <summary>
        /// Move a rectangle of cells up or down within itself, as a scrolled window does.
        ///
        /// The rows uncovered at the trailing edge are left as they are: neovim always follows
        /// a scroll with the lines to draw into them, and blanking here would show a flash of
        /// empty rows in between.
        /// </summary>
        internal void ScrollRegion(int top, int bottom, int left, int right, int rows)
        {
            if (rows == 0) return;

            int step = rows > 0 ? 1 : -1;
            int from = rows > 0 ? top + rows : bottom - 1 + rows;
            int to = rows > 0 ? top : bottom - 1;

            for (int moved = 0; moved < bottom - top - Math.Abs(rows); moved++)
            {
                int source = from + moved * step;
                int target = to + moved * step;
                if (source < 0 || source >= Rows || target < 0 || target >= Rows) continue;

                for (int column = left; column < right && column < Columns; column++)
                    _cells[target * Columns + column] = _cells[source * Columns + column];
            }
        }

        internal void Resize(int columns, int rows)
        {
            columns = columns < 1 ? 1 : columns;
            rows = rows < 1 ? 1 : rows;
            if (columns == Columns && rows == Rows) return;

            // Height depends on width, so every measurement taken at the old width is void.
            if (columns != Columns) _dirtyFrom = 0;

            Columns = columns;
            Rows = rows;
            _cells = new Cell[columns * rows];
            _origins = new RowOrigin[rows];
        }

        /// <summary>The line at this index changed, so its height has to be measured again.
        /// Everything after it is invalidated too, which costs nothing when - as is almost
        /// always the case - the line that changed is the one being typed on.</summary>
        internal void Invalidate(int lineIndex)
        {
            if (lineIndex < _dirtyFrom) _dirtyFrom = lineIndex < 0 ? 0 : lineIndex;
        }

        internal void InvalidateAll() => _dirtyFrom = 0;

        /// <summary>
        /// Fill the grid with the last Rows display rows, skipping <paramref name="scrollBack"/>
        /// of them at the bottom.
        ///
        /// The caret is given as a logical line and a column in that line's visible text; it
        /// comes back as a position in the grid, because the row it lands on depends on how
        /// the line wrapped, which only this pass knows.
        /// </summary>
        internal void Compose(IReadOnlyList<string> lines, int scrollBack, int caretLine, int caretColumn)
        {
            Blank();
            CaretRow = -1;
            CaretColumn = -1;

            Measure(lines);
            if (Rows <= 0 || Columns <= 0) return;

            scrollBack = scrollBack < 0 ? 0 : scrollBack;
            int wanted = Rows + scrollBack;

            // Walk back from the newest line until enough rows have been gathered to fill the
            // viewport, then lay out forward from there.
            int first = lines.Count;
            int gathered = 0;
            while (first > 0 && gathered < wanted)
            {
                first--;
                gathered += _heights[first];
            }

            // The line we stopped on usually overshoots; its first few rows belong above the
            // viewport and are laid out but not kept.
            int skip = gathered - wanted;
            if (skip < 0) skip = 0;

            // Rendering starts at the top row either way. Scrolling back was already paid
            // for by gathering extra rows above and skipping into them, so the rows it
            // displaces simply fall off the bottom - subtracting it here as well would
            // scroll twice and land back where it started.
            int row = 0;
            for (int i = first; i < lines.Count; i++)
            {
                int consumed = Emit(lines[i], i, row, i == first ? skip : 0,
                                    i == caretLine ? caretColumn : -1);
                row += consumed;
                if (row >= Rows + 1) break;
            }
        }

        // ---- layout -------------------------------------------------------------------

        /// <summary>
        /// Write one logical line into the grid starting at <paramref name="row"/>, dropping
        /// its first <paramref name="skip"/> display rows. Returns how many display rows it
        /// occupied, counting the skipped ones, so the caller can advance.
        ///
        /// Rows outside the grid are still walked rather than skipped over: a line's wrapping
        /// determines where the next one starts, and the caret can sit on a row that is
        /// partly off the top.
        /// </summary>
        private int Emit(string line, int lineIndex, int row, int skip, int caretColumn)
        {
            _reader.Reset(line);

            int column = 0;
            int wrapped = 0;
            int visibleColumn = 0;

            while (_reader.Read())
            {
                int width = RuneWidth.Of(_reader.Rune);

                // A combining mark has nowhere to go in a grid of whole cells. Dropping it
                // keeps every column one glyph wide, which is the invariant the caret, the
                // selection and the column arithmetic all rest on.
                if (width == 0) continue;

                if (column + width > Columns)
                {
                    column = 0;
                    wrapped++;
                }

                int target = row + wrapped - skip;
                if (visibleColumn == caretColumn) Place(target, column);

                Note(target, lineIndex, visibleColumn);
                Write(target, column, _reader.Rune, width, _reader.Style);

                column += width;
                visibleColumn++;
            }

            // A caret sitting past the last character - which is where it is whenever you are
            // typing at the end of a line - has no rune to ride along with.
            if (visibleColumn == caretColumn)
            {
                if (column >= Columns) { column = 0; wrapped++; }
                Place(row + wrapped - skip, column);
            }

            return wrapped + 1 - skip;
        }

        private void Write(int row, int column, int rune, int width, Style style)
        {
            if (row < 0 || row >= Rows || column < 0 || column >= Columns) return;

            int at = row * Columns + column;
            _cells[at].Rune = rune;
            _cells[at].Foreground = style.Foreground;
            _cells[at].Background = style.Background;
            _cells[at].Flags = style.Flags;
            _cells[at].Width = (byte)width;

            // A wide glyph owns the column it spills into, which carries no rune of its own -
            // the renderer must not emit anything there or the row would come out too long.
            if (width == 2 && column + 1 < Columns)
            {
                int spill = at + 1;
                _cells[spill].Rune = 0;
                _cells[spill].Foreground = style.Foreground;
                _cells[spill].Background = style.Background;
                _cells[spill].Flags = style.Flags;
                _cells[spill].Width = 0;
            }
        }

        /// <summary>Record which line this row is showing, the first time anything lands on
        /// it.</summary>
        private void Note(int row, int lineIndex, int visibleColumn)
        {
            if (row < 0 || row >= Rows) return;

            if (_origins[row].Line != lineIndex)
            {
                _origins[row].Line = lineIndex;
                _origins[row].FirstRune = visibleColumn;
                _origins[row].RuneCount = 0;
            }

            _origins[row].RuneCount++;
        }

        private void Place(int row, int column)
        {
            if (row < 0 || row >= Rows) return;
            CaretRow = row;
            CaretColumn = column;
        }

        // ---- measurement ---------------------------------------------------------------

        private void Measure(IReadOnlyList<string> lines)
        {
            if (_heights.Count > lines.Count) _heights.RemoveRange(lines.Count, _heights.Count - lines.Count);

            // Clamped to the end of what has been measured: starting past it would append
            // heights at the wrong index and silently shift every line's position afterwards.
            int from = _dirtyFrom < 0 ? 0 : _dirtyFrom;
            if (from > _heights.Count) from = _heights.Count;

            for (int i = from; i < lines.Count; i++)
            {
                int height = Height(lines[i]);
                if (i < _heights.Count) _heights[i] = height;
                else _heights.Add(height);
            }

            _dirtyFrom = lines.Count;

            int total = 0;
            for (int i = 0; i < _heights.Count; i++) total += _heights[i];
            TotalRows = total;
        }

        private int Height(string line)
        {
            _reader.Reset(line);

            int column = 0;
            int rows = 1;
            while (_reader.Read())
            {
                int width = RuneWidth.Of(_reader.Rune);
                if (width == 0) continue;

                if (column + width > Columns)
                {
                    column = 0;
                    rows++;
                }
                column += width;
            }
            return rows;
        }

        private void Blank()
        {
            for (int i = 0; i < _cells.Length; i++) _cells[i] = Cell.Blank;

            for (int i = 0; i < _origins.Length; i++)
            {
                _origins[i].Line = -1;
                _origins[i].FirstRune = 0;
                _origins[i].RuneCount = 0;
            }
        }
    }
}
