namespace UwUTerm.Ui
{
    /// <summary>
    /// How much of the desktop the bar has taken, whichever bar is drawing it.
    ///
    /// Two of them can do the job - the mod's own, and the rearrangement of the game's - and
    /// everything that has to keep out from under the bar wants one answer rather than a test
    /// for each. A maximised window and a window snapped to fill both land on the rectangle
    /// this describes, so a bar that changes height moves them without either of them knowing
    /// which bar it was.
    /// </summary>
    internal static class DesktopArea
    {
        /// <summary>Whether the desktop's furniture is ours at all. When it is not, the game's
        /// own geometry still fits and nothing here should second-guess it.</summary>
        internal static bool Claimed => TopBar.Owns || DesktopBar.Active;

        /// <summary>What the bar takes off the top, in the units the desktop is laid out in.</summary>
        internal static float TopInset =>
            TopBar.Owns ? TopBar.TopInset
            : DesktopBar.Active ? DesktopBar.TopInset
            : 0f;
    }
}
