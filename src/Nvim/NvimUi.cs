using System;
using System.Collections.Generic;
using UwUTerm.Screen;

namespace UwUTerm.Nvim
{
    /// <summary>
    /// Turns neovim's redraw notifications into cells.
    ///
    /// With ext_linegrid on, neovim describes the screen rather than the text: a run of cells
    /// at a position, a scrolled rectangle, a cleared grid, where the cursor went. That is a
    /// picture already, which is why an editor can be embedded this way without a terminal
    /// anywhere in the arrangement.
    ///
    /// Highlight groups arrive separately from the cells that use them - hl_attr_define hands
    /// over an id and its colours, and every cell afterwards refers to it by number. A cell
    /// with no id of its own keeps whichever was last used in that run, which is why the
    /// current highlight is carried between cells rather than reset.
    /// </summary>
    internal sealed class NvimUi
    {
        private readonly ScreenGrid _grid;
        private readonly Dictionary<long, Style> _highlights = new Dictionary<long, Style>();

        private Style _default = new Style { Foreground = Cell.Inherit, Background = Cell.Inherit };

        // The colour neovim considers "no background". Kept so cells carrying it can be left
        // unpainted rather than covering the window in a flat sheet of it.
        private uint _defaultBackground = Cell.Inherit;

        /// <summary>Raised once a batch of redraws has been applied, which is neovim saying the
        /// picture is consistent again and worth showing.</summary>
        internal event Action Flushed;

        /// <summary>Asked for when neovim wants a different grid size than it has.</summary>
        internal event Action<int, int> Resized;

        internal NvimUi(ScreenGrid grid) => _grid = grid;

        internal void Handle(RpcClient.Notification notification)
        {
            if (notification.Method != "redraw") return;

            foreach (object batch in notification.Arguments)
            {
                if (!(batch is object[] parts) || parts.Length == 0) continue;

                string name = parts[0] as string;
                if (name == null) continue;

                // Every event but the name is a list of argument tuples - one event can carry
                // many, and a grid_line batch usually carries a screenful.
                for (int i = 1; i < parts.Length; i++)
                    if (parts[i] is object[] arguments) Apply(name, arguments);

                if (parts.Length == 1) Apply(name, new object[0]);
            }
        }

        private void Apply(string name, object[] arguments)
        {
            switch (name)
            {
                case "grid_resize": GridResize(arguments); return;
                case "grid_clear": _grid.Fill(_default); return;
                case "grid_line": GridLine(arguments); return;
                case "grid_scroll": GridScroll(arguments); return;
                case "grid_cursor_goto": CursorGoto(arguments); return;
                case "default_colors_set": DefaultColours(arguments); return;
                case "hl_attr_define": DefineHighlight(arguments); return;
                case "flush": Flushed?.Invoke(); return;
            }
        }

        // ---- the screen ------------------------------------------------------------------

        private void GridResize(object[] arguments)
        {
            if (arguments.Length < 3) return;

            int columns = (int)Number(arguments[1]);
            int rows = (int)Number(arguments[2]);
            Resized?.Invoke(columns, rows);
        }

        /// <summary>
        /// A run of cells starting at one position: [grid, row, col, cells], where each cell is
        /// [text], [text, hl_id] or [text, hl_id, repeat].
        /// </summary>
        private void GridLine(object[] arguments)
        {
            if (arguments.Length < 4) return;

            int row = (int)Number(arguments[1]);
            int column = (int)Number(arguments[2]);
            if (!(arguments[3] is object[] cells)) return;

            Style style = _default;

            foreach (object entry in cells)
            {
                if (!(entry is object[] cell) || cell.Length == 0) continue;

                string text = cell[0] as string ?? "";
                if (cell.Length >= 2) style = Highlight(Number(cell[1]));

                int repeat = cell.Length >= 3 ? (int)Number(cell[2]) : 1;
                int rune = text.Length == 0 ? ' ' : char.ConvertToUtf32(text, 0);
                int width = RuneWidth.Of(rune);
                if (width == 0) width = 1;

                for (int i = 0; i < repeat; i++)
                {
                    _grid.Put(row, column, rune, width, style);
                    column += width;
                }
            }
        }

        private void GridScroll(object[] arguments)
        {
            if (arguments.Length < 7) return;

            _grid.ScrollRegion(
                (int)Number(arguments[1]), (int)Number(arguments[2]),
                (int)Number(arguments[3]), (int)Number(arguments[4]),
                (int)Number(arguments[5]));
        }

        private void CursorGoto(object[] arguments)
        {
            if (arguments.Length < 3) return;
            _grid.PlaceCaret((int)Number(arguments[1]), (int)Number(arguments[2]));
        }

        // ---- colour ----------------------------------------------------------------------

        private void DefaultColours(object[] arguments)
        {
            if (arguments.Length < 2) return;

            _defaultBackground = Rgb(arguments[1]);

            // Inherit, not the colour itself: ordinary text should let the window show through,
            // the same way the terminal does. Painting neovim's default background over every
            // cell would replace the editor's own backdrop with a flat rectangle.
            _default = new Style
            {
                Foreground = Rgb(arguments[0]),
                Background = Cell.Inherit,
            };
        }

        /// <summary>
        /// hl_attr_define is [id, rgb_attrs, cterm_attrs, info]. Only the rgb half is read -
        /// the terminal colours beside it exist for UIs that cannot do better, and a cell here
        /// holds a full colour.
        /// </summary>
        private void DefineHighlight(object[] arguments)
        {
            if (arguments.Length < 2) return;
            if (!(arguments[1] is Dictionary<string, object> attributes)) return;

            long id = Number(arguments[0]);
            var style = new Style
            {
                Foreground = attributes.TryGetValue("foreground", out object fg) ? Rgb(fg) : _default.Foreground,
                Background = attributes.TryGetValue("background", out object bg) ? Background(Rgb(bg)) : _default.Background,
                Flags = Flags(attributes),
            };

            // Reverse is how a cursor line, a selection and the mode line are all drawn, so it
            // has to be resolved here rather than left to the renderer.
            if (attributes.ContainsKey("reverse"))
            {
                uint swap = style.Foreground;
                style.Foreground = style.Background == Cell.Inherit ? _defaultBackground : style.Background;
                style.Background = swap == Cell.Inherit ? _default.Foreground : swap;
            }

            _highlights[id] = style;
        }

        private static CellFlags Flags(Dictionary<string, object> attributes)
        {
            CellFlags flags = CellFlags.None;

            if (attributes.ContainsKey("bold")) flags |= CellFlags.Bold;
            if (attributes.ContainsKey("italic")) flags |= CellFlags.Italic;
            if (attributes.ContainsKey("underline")) flags |= CellFlags.Underline;
            if (attributes.ContainsKey("strikethrough")) flags |= CellFlags.Strikethrough;

            return flags;
        }

        /// <summary>A background that is only the default one is no background at all - the
        /// window behind is what should show. Anything else is a real highlight and gets
        /// painted: a statusline, a visual selection, a cursor line.</summary>
        private uint Background(uint colour) =>
            colour == _defaultBackground ? Cell.Inherit : colour;

        private Style Highlight(long id) =>
            id == 0 || !_highlights.TryGetValue(id, out Style style) ? _default : style;

        /// <summary>Neovim sends colour as one integer, 0xRRGGBB. A cell holds RGBA, and zero
        /// there means "whatever the theme says" - so an absent colour stays absent.</summary>
        private static uint Rgb(object value)
        {
            if (value == null) return Cell.Inherit;

            long packed = Number(value);
            if (packed < 0) return Cell.Inherit;

            return ((uint)packed << 8) | 0xFFu;
        }

        private static long Number(object value) =>
            value == null ? 0 : Convert.ToInt64(value);
    }
}
