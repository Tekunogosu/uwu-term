using System.Text;

namespace UwUTerm.Screen
{
    /// <summary>
    /// Turns the grid back into one string of TMP markup - the whole viewport, one mesh.
    ///
    /// The screen the game ships gives every line its own TMP_Text, its own layout element
    /// and its own measurement pass, so the cost of drawing scales with how much has been
    /// printed. Here it scales with how much is on screen: eighty columns by fifty rows is
    /// four thousand characters whether the history behind it is fifty lines or fifty
    /// thousand.
    ///
    /// Cells are emitted in runs of identical style so a row of ordinary text costs one tag
    /// pair rather than one per character, and a cell whose colour is Inherit gets no tag at
    /// all - the label's own colour shows through, which is how a theme change repaints
    /// everything that was printed before it.
    /// </summary>
    internal static class ScreenRenderer
    {
        private static readonly StringBuilder Builder = new StringBuilder(8192);

        internal static string Render(ScreenGrid grid)
        {
            Builder.Length = 0;

            for (int row = 0; row < grid.Rows; row++)
            {
                if (row > 0) Builder.Append('\n');
                RenderRow(grid, row);
            }

            return Builder.ToString();
        }

        private static void RenderRow(ScreenGrid grid, int row)
        {
            int last = LastInteresting(grid, row);
            if (last < 0) return;

            int column = 0;
            while (column <= last)
            {
                Cell cell = grid[row, column];
                if (cell.Width == 0) { column++; continue; }   // the far half of a wide glyph

                Style style = StyleOf(cell);

                int run = column;
                Open(style);
                do
                {
                    Cell current = grid[row, run];
                    if (current.Rune != 0) Append(current.Rune);
                    run++;
                }
                while (run <= last && Continues(grid, row, run, style));

                Close(style);
                column = run;
            }
        }

        private static bool Continues(ScreenGrid grid, int row, int column, Style style)
        {
            Cell cell = grid[row, column];
            if (cell.Width == 0) return true;   // spill column inherits by construction

            return cell.Foreground == style.Foreground
                && cell.Background == style.Background
                && cell.Flags == style.Flags;
        }

        /// <summary>The last column worth emitting. Trailing blanks with nothing on them are
        /// dropped, which is most of most rows.</summary>
        private static int LastInteresting(ScreenGrid grid, int row)
        {
            int last = -1;
            for (int column = 0; column < grid.Columns; column++)
            {
                Cell cell = grid[row, column];
                bool blank = cell.Rune == ' ' && cell.Background == Cell.Inherit && cell.Flags == CellFlags.None;
                if (!blank && cell.Width != 0) last = column;
            }
            return last;
        }

        private static Style StyleOf(Cell cell) => new Style
        {
            Foreground = cell.Foreground,
            Background = cell.Background,
            Flags = cell.Flags,
        };

        // ---- markup ---------------------------------------------------------------------

        private static void Open(Style style)
        {
            if (style.Background != Cell.Inherit) { Builder.Append("<mark=#"); Hex(style.Background); Builder.Append('>'); }
            if (style.Foreground != Cell.Inherit) { Builder.Append("<color=#"); Hex(style.Foreground); Builder.Append('>'); }
            if ((style.Flags & CellFlags.Bold) != 0) Builder.Append("<b>");
            if ((style.Flags & CellFlags.Italic) != 0) Builder.Append("<i>");
            if ((style.Flags & CellFlags.Underline) != 0) Builder.Append("<u>");
            if ((style.Flags & CellFlags.Strikethrough) != 0) Builder.Append("<s>");
        }

        private static void Close(Style style)
        {
            if ((style.Flags & CellFlags.Strikethrough) != 0) Builder.Append("</s>");
            if ((style.Flags & CellFlags.Underline) != 0) Builder.Append("</u>");
            if ((style.Flags & CellFlags.Italic) != 0) Builder.Append("</i>");
            if ((style.Flags & CellFlags.Bold) != 0) Builder.Append("</b>");
            if (style.Foreground != Cell.Inherit) Builder.Append("</color>");
            if (style.Background != Cell.Inherit) Builder.Append("</mark>");
        }

        /// <summary>
        /// A '&lt;' that reaches here never formed a tag - the reader consumed the ones that
        /// did - but TMP would try to parse it again on the way out. noparse is the only
        /// thing that stops it, and it is needed about as often as terminal output contains
        /// an unmatched angle bracket.
        /// </summary>
        private static void Append(int rune)
        {
            if (rune == '<') { Builder.Append("<noparse><</noparse>"); return; }

            if (rune < 0x10000) Builder.Append((char)rune);
            else Builder.Append(char.ConvertFromUtf32(rune));
        }

        private static void Hex(uint rgba)
        {
            const string digits = "0123456789ABCDEF";
            for (int shift = 28; shift >= 0; shift -= 4)
                Builder.Append(digits[(int)((rgba >> shift) & 0xF)]);
        }
    }
}
