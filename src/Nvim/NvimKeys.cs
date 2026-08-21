using UnityEngine;

namespace UwUTerm.Nvim
{
    /// <summary>
    /// Unity keystrokes in the notation neovim reads.
    ///
    /// nvim_input takes what a mapping would be written as - "a", "&lt;Esc&gt;", "&lt;C-w&gt;",
    /// "&lt;S-Tab&gt;" - so a keystroke has to be described rather than delivered. Printable
    /// characters go as themselves and everything else is named, which is why this is a table
    /// and not a cast.
    /// </summary>
    internal static class NvimKeys
    {
        /// <summary>What to send for this event, or null when it says nothing - a modifier on
        /// its own, or a repeat of a character already handled.</summary>
        internal static string From(Event e)
        {
            if (e == null || e.type != EventType.KeyDown) return null;

            bool control = e.control || e.command;
            string named = Named(e.keyCode);

            if (named != null) return Wrap(named, control, e.alt, e.shift);

            char typed = e.character;

            // Ctrl with a letter arrives as a control code rather than the letter, and neovim
            // wants the letter back with the modifier named.
            if (control && e.keyCode >= KeyCode.A && e.keyCode <= KeyCode.Z)
                return Wrap(((char)('a' + (e.keyCode - KeyCode.A))).ToString(), true, e.alt, e.shift);

            if (typed == '\0' || typed == '￿') return null;
            if (typed < ' ' && typed != '\t') return null;

            if (e.alt) return Wrap(typed.ToString(), false, true, false);

            // "<" is the one printable character that cannot be sent as itself: neovim would
            // read it as the start of a key name.
            return typed == '<' ? "<lt>" : typed.ToString();
        }

        private static string Named(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Escape: return "Esc";
                case KeyCode.Return: case KeyCode.KeypadEnter: return "CR";
                case KeyCode.Backspace: return "BS";
                case KeyCode.Tab: return "Tab";
                case KeyCode.Delete: return "Del";
                case KeyCode.Insert: return "Insert";
                case KeyCode.Home: return "Home";
                case KeyCode.End: return "End";
                case KeyCode.PageUp: return "PageUp";
                case KeyCode.PageDown: return "PageDown";
                case KeyCode.UpArrow: return "Up";
                case KeyCode.DownArrow: return "Down";
                case KeyCode.LeftArrow: return "Left";
                case KeyCode.RightArrow: return "Right";
                case KeyCode.Space: return "Space";
                case KeyCode.F1: return "F1";
                case KeyCode.F2: return "F2";
                case KeyCode.F3: return "F3";
                case KeyCode.F4: return "F4";
                case KeyCode.F5: return "F5";
                case KeyCode.F6: return "F6";
                case KeyCode.F7: return "F7";
                case KeyCode.F8: return "F8";
                case KeyCode.F9: return "F9";
                case KeyCode.F10: return "F10";
                case KeyCode.F11: return "F11";
                case KeyCode.F12: return "F12";
                default: return null;
            }
        }

        /// <summary>
        /// Names a key with its modifiers, in the order neovim expects.
        ///
        /// Shift is only named for keys that have no shifted character of their own: a capital
        /// arrives as "A" already, and asking for &lt;S-A&gt; on top would be a different key.
        /// </summary>
        private static string Wrap(string key, bool control, bool alt, bool shift)
        {
            bool bare = !control && !alt && !shift;
            if (bare && key.Length == 1) return key;

            string prefix = "";
            if (control) prefix += "C-";
            if (alt) prefix += "A-";
            if (shift && key.Length > 1) prefix += "S-";

            return prefix.Length == 0 && key.Length == 1 ? key : "<" + prefix + key + ">";
        }
    }
}
