using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Two corrections to the desktop clock and its calendar.
    ///
    /// The calendar lays a month out by first working out how many blank cells come before
    /// the 1st, and it takes that from the weekday of <em>today</em> rather than of the 1st.
    /// Every date in the grid is then shifted by the difference, so the number for today sits
    /// under the wrong weekday - which is what makes the calendar disagree with the weekday
    /// the clock beside it is showing. It is only right on the 1st of a month.
    ///
    /// The clock's own label caches the weekday and recomputes it when the day of the month
    /// changes. That is nearly always the same thing as the date changing, but not when a
    /// month or a year rolls over onto the same number, so it is keyed on the date itself.
    /// </summary>
    internal static class DesktopClock
    {
        internal static void Apply(Harmony harmony)
        {
            Patch(harmony, typeof(Calendar), "UpdateCalendar", nameof(OnUpdateCalendar));
            Patch(harmony, typeof(FechaHora), "SyncReloj", nameof(OnSyncClock));
        }

        private static void Patch(Harmony harmony, Type type, string method, string handler)
        {
            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                UwUTermPlugin.Log.LogWarning($"clock: {type.Name}.{method} not found");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(
                typeof(DesktopClock).GetMethod(handler,
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        /// <summary>Lay the month out from the weekday the 1st actually falls on.</summary>
        private static bool OnUpdateCalendar(Calendar __instance)
        {
            Clock clock = __instance.clock;
            if (clock == null || __instance.dias == null || __instance.dias.Count == 0) return true;

            DateTime now = clock.GetDateTime();
            __instance.date.text = clock.GetMesFormat() + " " + clock.GetDia() + ", " + clock.GetYear();

            // Monday-first, so Sunday is the last column rather than the first.
            var first = new DateTime(now.Year, now.Month, 1);
            int lead = ((int)first.DayOfWeek + 6) % 7;
            int days = DateTime.DaysInMonth(now.Year, now.Month);
            int cells = __instance.dias.Count;

            for (int i = 0; i < cells; i++)
            {
                int day = i - lead + 1;
                bool inMonth = day >= 1 && day <= days;

                Show(__instance, i, inMonth);
                if (inMonth) __instance.dias[i].text = day.ToString();
            }

            int today = lead + now.Day - 1;
            if (today >= 0 && today < cells)
            {
                Image cell = Backing(__instance.dias[today]);
                if (cell != null) cell.color = __instance.colorCurrentDay;
            }

            return false;
        }

        private static void Show(Calendar calendar, int index, bool visible)
        {
            TMP_Text text = calendar.dias[index];
            if (text == null) return;

            text.enabled = visible;

            Image cell = Backing(text);
            if (cell != null) cell.enabled = visible;
        }

        /// <summary>The panel behind a day, which is what carries the highlight.</summary>
        private static Image Backing(TMP_Text day) =>
            day == null || day.transform.parent == null ? null : day.transform.parent.GetComponent<Image>();

        /// <summary>
        /// Keep the weekday in step with the date rather than with the day number.
        ///
        /// The two only differ where a month or a year turns over onto the same day number,
        /// which is rare and lasts until the next one - long enough to be seen.
        /// </summary>
        private static bool OnSyncClock(FechaHora __instance)
        {
            Clock clock = __instance.clock;
            TMP_Text label = __instance.horaFecha;
            if (clock == null || label == null) return true;

            DateTime now = clock.GetDateTime();
            label.text = now.ToString("ddd") + " " + clock.GetFormatHora();
            return false;
        }
    }
}
