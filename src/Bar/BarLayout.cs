namespace UwUTerm.Bar
{
    /// <summary>Where one thing in the bar goes, and how wide it is.</summary>
    public readonly struct Slot
    {
        public Slot(float x, float width)
        {
            X = x;
            Width = width;
        }

        public float X { get; }
        public float Width { get; }
        public float Right => X + Width;
    }

    /// <summary>
    /// Where everything in the bar goes, in pixels.
    ///
    /// Nothing here is a Unity type, and nothing here asks anything about the scene. A bar is
    /// a width, a few things pinned to each end, and a row of task buttons sharing what is
    /// left - which is arithmetic, and arithmetic can be checked without a game running. Every
    /// bug this replaces came from measuring a rect somebody else laid out and believing the
    /// answer; here the widths come in and the positions go out, and there is nothing in
    /// between to disagree with.
    ///
    /// One unit is one screen pixel. The bar it draws is our own canvas at a scale factor of
    /// one, so no reference resolution stands between a number here and a pixel on the screen.
    /// </summary>
    public sealed class BarLayout
    {
        /// <summary>The whole bar, in pixels.</summary>
        public float Width { get; set; } = 1920f;

        /// <summary>Between two things standing next to each other.</summary>
        public float Gap { get; set; } = 8f;

        /// <summary>Between the ends of the bar and the first thing in from them.</summary>
        public float Padding { get; set; } = 8f;

        /// <summary>The width a task button asks for when the row is not crowded.</summary>
        public float TaskWidth { get; set; } = 180f;

        /// <summary>How narrow a task button may be squeezed before the row is allowed to run
        /// out of the space it was given. A button below this is not a button any more.</summary>
        public float MinTaskWidth { get; set; } = 40f;

        /// <summary>
        /// Lay things out from the left edge inwards, in the order given, and answer where the
        /// last of them ends. A width of zero takes no room and no gap, so something switched
        /// off does not leave a hole where it used to be.
        /// </summary>
        public float PlaceLeft(float[] widths, int count, Slot[] into)
        {
            float x = Padding;

            for (int i = 0; i < count; i++)
            {
                if (widths[i] <= 0f) { into[i] = new Slot(x, 0f); continue; }

                into[i] = new Slot(x, widths[i]);
                x += widths[i] + Gap;
            }

            return x > Padding ? x - Gap : Padding;
        }

        /// <summary>
        /// The same from the right edge, in the order given: the first is the outermost, so a
        /// clock asked for first sits in the corner. Answers where the last of them starts.
        /// </summary>
        public float PlaceRight(float[] widths, int count, Slot[] into)
        {
            float right = Width - Padding;

            for (int i = 0; i < count; i++)
            {
                if (widths[i] <= 0f) { into[i] = new Slot(right, 0f); continue; }

                into[i] = new Slot(right - widths[i], widths[i]);
                right -= widths[i] + Gap;
            }

            return right < Width - Padding ? right + Gap : Width - Padding;
        }

        /// <summary>
        /// Share the space between the two ends out among the task buttons.
        ///
        /// Every button is the same width: the room divided by how many there are, capped at
        /// the width a button asks for so a bar with two windows open does not draw two
        /// enormous ones. The row therefore ends where the space ends, however many windows
        /// there are, which is the whole point of doing this ourselves.
        ///
        /// The floor is the one case where the row is allowed to be wider than its space -
        /// past it there is nothing useful to draw, and a caller that cares can compare the
        /// answer against <paramref name="to"/>.
        /// </summary>
        public float PlaceTasks(int count, float from, float to, Slot[] into)
        {
            if (count <= 0) return from;

            float room = to - from - Gap * (count - 1);
            float each = room / count;

            if (each > TaskWidth) each = TaskWidth;
            if (each < MinTaskWidth) each = MinTaskWidth;

            float x = from;
            for (int i = 0; i < count; i++)
            {
                into[i] = new Slot(x, each);
                x += each + Gap;
            }

            return x - Gap;
        }
    }
}
