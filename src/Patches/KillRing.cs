using System.Collections.Generic;
using UnityEngine;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Readline's kill ring.
    ///
    /// Two behaviours from the spec that are easy to get wrong: consecutive kills
    /// accumulate into a single entry rather than pushing several, and they accumulate
    /// *directionally* - a backward kill prepends to the entry, a forward kill appends -
    /// so C-w C-w yanks back as "one two", not "twoone". Anything that is not a kill
    /// breaks the run and the next kill starts a fresh entry.
    ///
    /// The ring is shared by every terminal window, which readline would not do (a ring
    /// is per-shell) but which is strictly more useful here: kill in one window, yank in
    /// another.
    /// </summary>
    internal static class KillRing
    {
        private static readonly List<string> Ring = new List<string>();
        private static int _index;

        internal static bool LastWasKill;
        internal static bool LastWasYank;

        internal static void Kill(string text, bool backward)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (LastWasKill && Ring.Count > 0)
            {
                Ring[0] = backward ? text + Ring[0] : Ring[0] + text;
            }
            else
            {
                Ring.Insert(0, text);
                int max = Mathf.Max(1, UwUTermPlugin.KillRingSize.Value);
                while (Ring.Count > max) Ring.RemoveAt(Ring.Count - 1);
            }

            _index = 0;
            LastWasKill = true;
        }

        internal static string Current => Ring.Count == 0 ? null : Ring[_index];

        /// <summary>M-y: rotate to the next entry and report it. Only meaningful right
        /// after a yank, which the caller enforces.</summary>
        internal static string Rotate()
        {
            if (Ring.Count == 0) return null;
            _index = (_index + 1) % Ring.Count;
            return Ring[_index];
        }

        internal static void Clear()
        {
            Ring.Clear();
            _index = 0;
        }
    }
}
