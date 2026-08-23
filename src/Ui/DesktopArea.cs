namespace UwUTerm.Ui
{
    /// <summary>
    /// How much of the desktop the bar has taken.
    ///
    /// Everything that has to keep out from under it wants one answer rather than a reading of
    /// <see cref="TopBar"/>'s internals. A maximised window and a window snapped to fill both
    /// land on the rectangle this describes, so a bar that changes height moves them without
    /// either of them knowing the bar exists.
    /// </summary>
    internal static class DesktopArea
    {
        /// <summary>Whether the desktop's furniture is ours at all. When it is not, the game's
        /// own geometry still fits and nothing here should second-guess it.</summary>
        internal static bool Claimed => TopBar.Owns;

        /// <summary>What the bar takes off the top, in the units the desktop is laid out in.</summary>
        internal static float TopInset => TopBar.Owns ? TopBar.TopInset : 0f;
    }
}
