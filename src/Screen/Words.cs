namespace UwUTerm.Screen
{
    /// <summary>
    /// What counts as one word.
    ///
    /// Konsole - and so yakuake - treats these as part of a word, which keeps a path or a flag
    /// moving as a single unit. Readline's own M-b is alphanumeric-only and stops at every
    /// slash, which is worse in a game where half of what you type is a path.
    ///
    /// One owner for the rule, because a word means the same thing whether Alt+B is stepping
    /// over it or a double-click is selecting it. Two copies of this set would drift the first
    /// time either was tuned, and the drift would show up as the keyboard and the mouse
    /// disagreeing about where a word ends.
    /// </summary>
    internal static class Words
    {
        private const string Extra = ":@-./_~";

        internal static bool IsWord(int rune) =>
            rune is < 0x110000 and >= 0 &&
            (char.IsLetterOrDigit((char)(rune > 0xFFFF ? 'a' : rune)) ||
             (rune <= 0xFFFF && Extra.IndexOf((char)rune) >= 0));

        internal static bool IsWord(char c) => char.IsLetterOrDigit(c) || Extra.IndexOf(c) >= 0;
    }
}
