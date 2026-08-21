using UnityEngine;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Two buffers, the way X11 has two.
    ///
    /// Selecting text fills Primary and nothing else; middle-click pastes it back. Explicit
    /// copying - Ctrl+Shift+C - uses the system clipboard, which is shared with every other
    /// program on the machine. Keeping them apart is the whole point: dragging across some
    /// output should not throw away whatever was copied to paste a moment later.
    ///
    /// Primary lives in this process, so unlike the real thing it does not reach other
    /// applications. Reaching them would mean spawning xclip or wl-copy on every drag, with a
    /// branch for X11 against Wayland and a dependency on tools that may not be installed -
    /// a lot of machinery for a case the system clipboard already covers.
    /// </summary>
    /// <remarks>
    /// Named for the game rather than for us: Grey Hack has its own Clipboard in the global
    /// namespace, which wins name lookup over anything imported, so a type called Clipboard
    /// here would silently resolve to theirs at every use site.
    /// </remarks>
    internal static class TerminalClipboard
    {
        private static string _primary = "";

        internal static string Primary
        {
            get => _primary;
            set => _primary = value ?? "";
        }

        /// <summary>The machine's clipboard, shared with every other program on it.</summary>
        internal static string Shared
        {
            get => GUIUtility.systemCopyBuffer ?? "";
            set => GUIUtility.systemCopyBuffer = value ?? "";
        }

        /// <summary>
        /// Flatten text on its way to the input line.
        ///
        /// A newline in a pasted string would read as Enter and run whatever came before it,
        /// so a stray copied line break becomes a command. Terminals guard against this with
        /// bracketed paste, which needs a shell that understands it; there is no such shell
        /// here, so the breaks become spaces and nothing runs until you press Enter yourself.
        /// </summary>
        internal static string ForInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        }
    }
}
