using System;
using UwUTerm.Bar;

namespace UwUTerm.Bar.Tests
{
    /// <summary>
    /// What the bar's arithmetic must do, stated as cases rather than as prose.
    ///
    /// The bug this whole layout exists to end was a row of task buttons that covered the
    /// widgets beside it, through six rounds of measuring a running game. The first case below
    /// is that bug: however many buttons there are, the row ends where its space ends.
    /// </summary>
    internal static class Program
    {
        private static int _failed;

        private static int Main()
        {
            RowNeverPassesItsSpace();
            ButtonsKeepTheirWidthWhileThereIsRoom();
            ButtonsShrinkEqually();
            TheFloorIsTheOnlyWayOut();
            EndsArePlacedInwards();
            SomethingSwitchedOffLeavesNoHole();
            AnEmptyRowTakesNothing();

            Console.WriteLine(_failed == 0 ? "bar layout: all good" : $"bar layout: {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>The one that matters: 1 window or 40, the row stops at the widgets.</summary>
        private static void RowNeverPassesItsSpace()
        {
            var bar = new BarLayout { Width = 3440f, TaskWidth = 180f, MinTaskWidth = 40f, Gap = 8f };
            var slots = new Slot[64];

            for (int count = 1; count <= 40; count++)
            {
                float end = bar.PlaceTasks(count, 120f, 2900f, slots);

                Is($"{count} buttons end by 2900", end <= 2900.01f);
                Is($"{count} buttons start at 120", Same(slots[0].X, 120f));
                Is($"{count} buttons are all inside", slots[count - 1].Right <= 2900.01f);
            }
        }

        private static void ButtonsKeepTheirWidthWhileThereIsRoom()
        {
            var bar = new BarLayout { TaskWidth = 180f, Gap = 8f };
            var slots = new Slot[8];

            bar.PlaceTasks(3, 0f, 3000f, slots);

            Is("a roomy bar draws buttons at their own width", Same(slots[0].Width, 180f));
            Is("and puts a gap between them", Same(slots[1].X, 188f));
        }

        private static void ButtonsShrinkEqually()
        {
            var bar = new BarLayout { TaskWidth = 180f, MinTaskWidth = 10f, Gap = 8f };
            var slots = new Slot[16];

            bar.PlaceTasks(10, 0f, 1000f, slots);

            float first = slots[0].Width;
            Is("a crowded row is narrower than a button asks for", first < 180f);

            for (int i = 1; i < 10; i++) Is($"button {i} matches the first", Same(slots[i].Width, first));

            Is("and the row still fills its space", Same(slots[9].Right, 1000f));
        }

        /// <summary>Past the floor the row is allowed to overrun, and the caller is told by the
        /// answer rather than by the row quietly drawing something unreadable.</summary>
        private static void TheFloorIsTheOnlyWayOut()
        {
            var bar = new BarLayout { TaskWidth = 180f, MinTaskWidth = 40f, Gap = 8f };
            var slots = new Slot[64];

            float end = bar.PlaceTasks(40, 0f, 500f, slots);

            Is("a button is never squeezed below the floor", Same(slots[0].Width, 40f));
            Is("and the overrun is in the answer", end > 500f);
        }

        private static void EndsArePlacedInwards()
        {
            var bar = new BarLayout { Width = 1000f, Gap = 10f, Padding = 5f };
            var left = new Slot[2];
            var right = new Slot[2];

            float leftEnd = bar.PlaceLeft(new[] { 40f, 50f }, 2, left);
            float rightStart = bar.PlaceRight(new[] { 100f, 20f }, 2, right);

            Is("the first thing sits at the padding", Same(left[0].X, 5f));
            Is("the second follows a gap later", Same(left[1].X, 55f));
            Is("and the left ends after it", Same(leftEnd, 105f));

            Is("the first thing on the right is in the corner", Same(right[0].Right, 995f));
            Is("the second sits inside it", Same(right[1].Right, 885f));
            Is("and the right starts at the last of them", Same(rightStart, 865f));
        }

        private static void SomethingSwitchedOffLeavesNoHole()
        {
            var bar = new BarLayout { Width = 1000f, Gap = 10f, Padding = 0f };
            var left = new Slot[3];

            float end = bar.PlaceLeft(new[] { 40f, 0f, 30f }, 3, left);

            Is("what is off takes no width", Same(left[1].Width, 0f));
            Is("and no gap either", Same(left[2].X, 50f));
            Is("so the row is as long as what is in it", Same(end, 80f));
        }

        private static void AnEmptyRowTakesNothing()
        {
            var bar = new BarLayout { Width = 1000f, Padding = 6f };
            var slots = new Slot[1];

            Is("no buttons end where they would have started", Same(bar.PlaceTasks(0, 120f, 900f, slots), 120f));
            Is("nothing on the left ends at the padding", Same(bar.PlaceLeft(new float[0], 0, slots), 6f));
            Is("nothing on the right starts at the padding", Same(bar.PlaceRight(new float[0], 0, slots), 994f));
        }

        private static bool Same(float a, float b) => Math.Abs(a - b) < 0.01f;

        private static void Is(string what, bool so)
        {
            if (so) return;

            _failed++;
            Console.WriteLine("  FAILED: " + what);
        }
    }
}
