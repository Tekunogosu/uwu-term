using System.Collections.Generic;

namespace UwUTerm.Screen
{
    internal struct Style
    {
        internal uint Foreground;
        internal uint Background;
        internal CellFlags Flags;
    }

    /// <summary>
    /// Reads a line of the game's output one rune at a time, carrying the style in force at
    /// that point.
    ///
    /// This is the seam an escape-sequence parser would occupy in a real emulator. Grey Hack
    /// never sends escapes - the server writes TMP markup directly into the text, and this
    /// mod's own prompt renderer adds more of it - so what has to be decoded is a tag
    /// language, not a byte protocol. Same job either way: turn a stream carrying formatting
    /// inline into runes plus the state that applies to them.
    ///
    /// Tags nest, so colours are kept on stacks and a closing tag pops. An unrecognised tag
    /// is consumed and dropped rather than drawn, which is what the rest of the mod already
    /// does when it needs the visible text of a line. A lone '&lt;' that opens nothing is
    /// text, and prints.
    /// </summary>
    internal sealed class MarkupReader
    {
        private const int MaxNesting = 16;

        private string _source = "";
        private int _index;

        private readonly uint[] _foreground = new uint[MaxNesting];
        private readonly uint[] _background = new uint[MaxNesting];
        private int _foregroundDepth;
        private int _backgroundDepth;
        private CellFlags _flags;

        /// <summary>The rune just read. Valid only after Read returned true.</summary>
        internal int Rune;

        internal Style Style => new Style
        {
            Foreground = _foregroundDepth > 0 ? _foreground[_foregroundDepth - 1] : Cell.Inherit,
            Background = _backgroundDepth > 0 ? _background[_backgroundDepth - 1] : Cell.Inherit,
            Flags = _flags,
        };

        internal void Reset(string source)
        {
            _source = source ?? "";
            _index = 0;
            _foregroundDepth = 0;
            _backgroundDepth = 0;
            _flags = CellFlags.None;
            Rune = 0;
        }

        /// <summary>Advance to the next rune, applying any tags passed on the way. False at
        /// the end of the line.</summary>
        internal bool Read()
        {
            while (_index < _source.Length)
            {
                char c = _source[_index];

                if (c == '<')
                {
                    int close = _source.IndexOf('>', _index + 1);
                    if (close < 0)
                    {
                        // Nothing closes it, so it was never a tag.
                        Rune = c;
                        _index++;
                        return true;
                    }

                    Apply(_source.Substring(_index + 1, close - _index - 1));
                    _index = close + 1;
                    continue;
                }

                // Astral characters arrive as a surrogate pair and are one rune, not two.
                if (char.IsHighSurrogate(c) && _index + 1 < _source.Length &&
                    char.IsLowSurrogate(_source[_index + 1]))
                {
                    Rune = char.ConvertToUtf32(c, _source[_index + 1]);
                    _index += 2;
                    return true;
                }

                Rune = c;
                _index++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// How many drawn runes precede <paramref name="rawIndex"/> in the source.
        ///
        /// The game measures the caret in raw string offsets, which count every character of
        /// every tag; the grid measures it in runes that reach the screen. Between those two
        /// is this, and getting it wrong puts the caret somewhere inside a colour tag.
        /// </summary>
        internal static int RunesBefore(string source, int rawIndex)
        {
            if (string.IsNullOrEmpty(source) || rawIndex <= 0) return 0;

            var reader = new MarkupReader();
            reader.Reset(source);

            int runes = 0;
            while (reader.Read())
            {
                if (reader._index > rawIndex) break;
                runes++;
            }
            return runes;
        }

        /// <summary>
        /// The drawn text between two rune offsets, tags dropped.
        ///
        /// Rune offsets rather than character offsets, because that is what a column on screen
        /// counts and what a selection is therefore held in. The two differ wherever an astral
        /// character turns up, and slicing a string by the wrong one splits a surrogate pair.
        /// </summary>
        internal static string Slice(string source, int firstRune, int lastRune)
        {
            if (string.IsNullOrEmpty(source) || lastRune <= firstRune) return "";

            var reader = new MarkupReader();
            reader.Reset(source);

            var sb = new System.Text.StringBuilder();
            int rune = 0;
            while (reader.Read())
            {
                if (rune >= lastRune) break;
                if (rune >= firstRune) sb.Append(char.ConvertFromUtf32(reader.Rune));
                rune++;
            }
            return sb.ToString();
        }

        /// <summary>Every drawn rune of a line, in order. What a word boundary is measured
        /// against.</summary>
        internal static int[] Runes(string source)
        {
            var reader = new MarkupReader();
            reader.Reset(source);

            var runes = new List<int>(source == null ? 0 : source.Length);
            while (reader.Read()) runes.Add(reader.Rune);
            return runes.ToArray();
        }

        /// <summary>How many runes a line draws. The end of a selection on that line.</summary>
        internal static int RuneCount(string source)
        {
            var reader = new MarkupReader();
            reader.Reset(source);

            int runes = 0;
            while (reader.Read()) runes++;
            return runes;
        }

        /// <summary>The visible text of a line, with every tag removed. What a search matches
        /// against, and what a column index counts.</summary>
        internal static string Visible(string source)
        {
            var reader = new MarkupReader();
            reader.Reset(source);

            var sb = new System.Text.StringBuilder(source == null ? 0 : source.Length);
            while (reader.Read()) sb.Append(char.ConvertFromUtf32(reader.Rune));
            return sb.ToString();
        }

        // ---- tags --------------------------------------------------------------------

        private void Apply(string tag)
        {
            if (tag.Length == 0) return;

            if (tag[0] == '/')
            {
                switch (Name(tag.Substring(1)))
                {
                    case "color": if (_foregroundDepth > 0) _foregroundDepth--; break;
                    case "mark": if (_backgroundDepth > 0) _backgroundDepth--; break;
                    case "b": _flags &= ~CellFlags.Bold; break;
                    case "i": _flags &= ~CellFlags.Italic; break;
                    case "u": _flags &= ~CellFlags.Underline; break;
                    case "s": _flags &= ~CellFlags.Strikethrough; break;
                }
                return;
            }

            // <#rrggbb>, TMP's shorthand for a colour with no tag name.
            if (tag[0] == '#')
            {
                if (TryParseColour(tag, out uint shorthand)) Push(_foreground, ref _foregroundDepth, shorthand);
                return;
            }

            int equals = tag.IndexOf('=');
            string name = Name(equals < 0 ? tag : tag.Substring(0, equals));
            string value = equals < 0 ? "" : tag.Substring(equals + 1).Trim('"', '\'');

            switch (name)
            {
                case "color":
                    Push(_foreground, ref _foregroundDepth,
                         TryParseColour(value, out uint fg) ? fg : Cell.Inherit);
                    break;

                case "mark":
                    // A bare <mark> is TMP's default highlight, a translucent yellow.
                    Push(_background, ref _backgroundDepth,
                         TryParseColour(value, out uint bg) ? bg : 0xFFFF00FFu & 0xFFFFFF80u);
                    break;

                case "b": _flags |= CellFlags.Bold; break;
                case "i": _flags |= CellFlags.Italic; break;
                case "u": _flags |= CellFlags.Underline; break;
                case "s": _flags |= CellFlags.Strikethrough; break;

                // Everything else - <size>, <align>, <sprite>, <voffset> - is a tag this
                // screen has no cell-level equivalent for. Dropping it is deliberate: a grid
                // where one row is a different height or a different width is not a grid.
            }
        }

        private static string Name(string raw) => raw.Trim().ToLowerInvariant();

        private static void Push(uint[] stack, ref int depth, uint value)
        {
            // A malformed line with no closing tags must not walk off the end. Holding the
            // top entry loses the innermost colour rather than the whole line.
            if (depth >= stack.Length) depth = stack.Length - 1;
            stack[depth++] = value;
        }

        /// <summary>TMP accepts #RGB, #RGBA, #RRGGBB and #RRGGBBAA, plus a handful of names.
        /// Everything is widened to 0xRRGGBBAA.</summary>
        internal static bool TryParseColour(string text, out uint colour)
        {
            colour = Cell.Inherit;
            if (string.IsNullOrEmpty(text)) return false;

            string value = text.Trim();
            if (value.Length > 0 && value[0] == '#') value = value.Substring(1);

            if (Names.TryGetValue(value.ToLowerInvariant(), out uint named))
            {
                colour = named;
                return true;
            }

            if (!IsHex(value)) return false;

            uint r, g, b, a = 0xFF;
            switch (value.Length)
            {
                case 3:
                case 4:
                    r = Nibble(value[0]) * 0x11;
                    g = Nibble(value[1]) * 0x11;
                    b = Nibble(value[2]) * 0x11;
                    if (value.Length == 4) a = Nibble(value[3]) * 0x11;
                    break;

                case 6:
                case 8:
                    r = Nibble(value[0]) * 16 + Nibble(value[1]);
                    g = Nibble(value[2]) * 16 + Nibble(value[3]);
                    b = Nibble(value[4]) * 16 + Nibble(value[5]);
                    if (value.Length == 8) a = Nibble(value[6]) * 16 + Nibble(value[7]);
                    break;

                default:
                    return false;
            }

            colour = (r << 24) | (g << 16) | (b << 8) | a;

            // Inherit is spelled as a fully transparent colour, so a literal one has to be
            // nudged to stay a colour rather than becoming "whatever the theme says".
            if (colour == Cell.Inherit) colour = 1u;
            return true;
        }

        private static bool IsHex(string value)
        {
            if (value.Length == 0) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        private static uint Nibble(char c) =>
            c <= '9' ? (uint)(c - '0') : (uint)((c | 0x20) - 'a' + 10);

        private static readonly Dictionary<string, uint> Names = new Dictionary<string, uint>
        {
            { "black",   0x000000FF }, { "white",  0xFFFFFFFF },
            { "red",     0xFF0000FF }, { "green",  0x00FF00FF },
            { "blue",    0x0000FFFF }, { "yellow", 0xFFFF00FF },
            { "orange",  0xFFA500FF }, { "purple", 0x800080FF },
            { "cyan",    0x00FFFFFF }, { "magenta",0xFF00FFFF },
            { "grey",    0x808080FF }, { "gray",   0x808080FF },
            { "brown",   0xA52A2AFF }, { "pink",   0xFFC0CBFF },
            { "lightblue", 0xADD8E6FF },
        };
    }
}
