using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Windows open where they were left, across closing one and across closing the game.
    ///
    /// A window's kind is its prefab's name - <c>uDialog_Utilities.InstantiatePrefab</c> copies it
    /// onto the instance, so a Terminal is called Terminal for as long as it exists. The title is
    /// not that: it follows the folder a file explorer is in and the host a terminal is on.
    ///
    /// A kind holds a list rather than one rectangle, because five terminals are five windows and
    /// each of them wants its own place. The list is the arrangement as it stands: the windows of
    /// a kind are written down in the order the taskbar holds them, so closing the middle one of
    /// three leaves two, and the one that opens next takes the second place rather than the third.
    ///
    /// Nothing is patched. Where a window is is read from its transform and written back to it,
    /// which the drag, the snap, the resize and the maximise all go through already - hooking each
    /// of those instead would be four seams to keep in step with a fifth that gets added later.
    ///
    /// A restored window is held to its place for a moment after it opens. uDialog plays a show
    /// animation and leaves its animator enabled afterwards, and an enabled animator rewrites what
    /// its clip animates every frame - so a position written once, before that has finished, is a
    /// position that may not survive the frame it was written in.
    /// </summary>
    internal static class WindowMemory
    {
        /// <summary>How long a window is held to the place it was given, in seconds. The show
        /// animation runs for half of it.</summary>
        private const float Settle = 0.6f;

        /// <summary>Movement below this is the same place: floating point, not the player.</summary>
        private const float Same = 0.5f;

        private const float WriteEvery = 3f;

        private static readonly Dictionary<string, List<Rect>> Saved =
            new Dictionary<string, List<Rect>>();

        /// <summary>Windows opened this session, and until when each is still being held to the
        /// place it was given.</summary>
        private static readonly Dictionary<uDialog, float> Holding = new Dictionary<uDialog, float>();
        private static readonly Dictionary<uDialog, Rect> Wanted = new Dictionary<uDialog, Rect>();
        private static readonly List<uDialog> Gone = new List<uDialog>();
        private static readonly Dictionary<string, int> Counted = new Dictionary<string, int>();

        private static bool _loaded;
        private static bool _dirty;
        private static float _nextWrite;

        private static string Path =>
            System.IO.Path.Combine(BepInEx.Paths.ConfigPath, UwUTermPlugin.Guid + ".windows");

        internal static void Tick()
        {
            if (!UwUTermPlugin.PersistWindows.Value) return;

            uDialog_TaskBar bar = uDialog_TaskBar.Singleton;
            if (bar == null || bar.Tasks == null) return;

            Load();
            Forget(bar.Tasks);

            Counted.Clear();
            foreach (uDialog window in bar.Tasks)
            {
                if (window == null) continue;

                RectTransform rect = window.RectTransform;
                RectTransform parent = rect != null ? rect.parent as RectTransform : null;
                if (parent == null) continue;

                string kind = window.gameObject.name;
                int slot = Counted.TryGetValue(kind, out int n) ? n : 0;
                Counted[kind] = slot + 1;

                if (!Holding.ContainsKey(window)) Restore(window, rect, parent, kind, slot);
                else if (!Hold(window, rect, parent)) Record(rect, parent, kind, slot);
            }

            Write();
        }

        /// <summary>Put a window that has just opened where its kind's slot says, if that slot has
        /// ever been written down.</summary>
        private static void Restore(uDialog window, RectTransform rect, RectTransform parent,
                                    string kind, int slot)
        {
            Holding[window] = Time.unscaledTime + Settle;

            if (!Saved.TryGetValue(kind, out List<Rect> places) || slot >= places.Count) return;

            Rect want = Fit(places[slot], WindowArea.Area(parent.rect));
            Wanted[window] = want;
            WindowGeometry.Place(rect, parent, want.size, want.center, refreshText: true);
        }

        /// <summary>Whether a window is still being held to the place it was given. Re-asserted
        /// every frame it drifts, because whatever moved it will move it again next frame too.</summary>
        private static bool Hold(uDialog window, RectTransform rect, RectTransform parent)
        {
            if (Time.unscaledTime >= Holding[window]) return false;
            if (!Wanted.TryGetValue(window, out Rect want)) return true;

            Rect now = WindowGeometry.LocalRect(rect, parent);
            if (Vector2.Distance(now.center, want.center) > Same || Vector2.Distance(now.size, want.size) > Same)
                WindowGeometry.Place(rect, parent, want.size, want.center);

            return true;
        }

        /// <summary>Write down where a window is now. A window being minimised is on its way
        /// somewhere it is not staying, so it keeps the place it had while it was visible.</summary>
        private static void Record(RectTransform rect, RectTransform parent, string kind, int slot)
        {
            uDialog dialog = rect.GetComponent<uDialog>();
            if (dialog != null && !dialog.isVisible) return;

            if (!Saved.TryGetValue(kind, out List<Rect> places))
                Saved[kind] = places = new List<Rect>();

            Rect now = WindowGeometry.LocalRect(rect, parent);
            while (places.Count <= slot) places.Add(now);

            if (Vector2.Distance(places[slot].center, now.center) <= Same &&
                Vector2.Distance(places[slot].size, now.size) <= Same) return;

            places[slot] = now;
            _dirty = true;
        }

        /// <summary>A remembered rectangle on a desktop that may be a different size than the one
        /// it was written on - a window off the edge of this screen is a window that is gone.</summary>
        private static Rect Fit(Rect want, Rect area)
        {
            var size = new Vector2(
                Mathf.Min(want.width, area.width),
                Mathf.Min(want.height, area.height));

            var centre = new Vector2(
                Mathf.Clamp(want.center.x, area.xMin + size.x / 2f, area.xMax - size.x / 2f),
                Mathf.Clamp(want.center.y, area.yMin + size.y / 2f, area.yMax - size.y / 2f));

            return new Rect(centre - size / 2f, size);
        }

        private static void Forget(List<uDialog> open)
        {
            Gone.Clear();
            foreach (KeyValuePair<uDialog, float> pair in Holding)
                if (pair.Key == null || !open.Contains(pair.Key)) Gone.Add(pair.Key);

            foreach (uDialog closed in Gone)
            {
                Holding.Remove(closed);
                Wanted.Remove(closed);
            }
        }

        // ---- the file ---------------------------------------------------------------------

        /// <summary>Written on a timer rather than on every move: a drag changes the answer every
        /// frame, and the answer is only ever read at startup.</summary>
        private static void Write()
        {
            if (!_dirty || Time.unscaledTime < _nextWrite) return;

            _nextWrite = Time.unscaledTime + WriteEvery;
            Flush();
        }

        internal static void Flush()
        {
            if (!_dirty) return;
            _dirty = false;

            var lines = new List<string>();
            foreach (KeyValuePair<string, List<Rect>> kind in Saved)
                for (int i = 0; i < kind.Value.Count; i++)
                {
                    Rect r = kind.Value[i];
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2:F1}|{3:F1}|{4:F1}|{5:F1}",
                        kind.Key, i, r.x, r.y, r.width, r.height));
                }

            try
            {
                File.WriteAllLines(Path, lines.ToArray());
            }
            catch (Exception e)
            {
                UwUTermPlugin.Log.LogWarning("windows: could not write - " + e.Message);
            }
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(Path)) return;

                int read = 0;
                foreach (string line in File.ReadAllLines(Path))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length != 6) continue;

                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
                        continue;
                    if (!Number(parts[2], out float x) || !Number(parts[3], out float y) ||
                        !Number(parts[4], out float w) || !Number(parts[5], out float h)) continue;

                    if (!Saved.TryGetValue(parts[0], out List<Rect> places))
                        Saved[parts[0]] = places = new List<Rect>();

                    while (places.Count <= slot) places.Add(new Rect(x, y, w, h));
                    places[slot] = new Rect(x, y, w, h);
                    read++;
                }

                UwUTermPlugin.Log.LogInfo($"windows: {read} remembered places for {Saved.Count} kinds");
            }
            catch (Exception e)
            {
                UwUTermPlugin.Log.LogWarning("windows: could not read - " + e.Message);
            }
        }

        private static bool Number(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
