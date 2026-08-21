using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TerminalPoolSystem;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Hands a terminal's drawing over to <see cref="ScreenView"/>.
    ///
    /// The game's own rows stay where they are, made invisible rather than removed: they are
    /// still the model Terminal.ProcesaLinea reads a submitted command out of, and still what
    /// Readline edits. Only the picture changes hands. That keeps password mode, script input
    /// polling, the bell, copy and paste, and the tutorial's command matching working
    /// untouched, and it means turning this off is a config edit rather than a restart.
    /// </summary>
    internal static class ScreenTakeover
    {
        private static readonly Dictionary<TerminalListAdapter, ScreenView> Views =
            new Dictionary<TerminalListAdapter, ScreenView>();

        private static readonly List<TerminalListAdapter> Dead = new List<TerminalListAdapter>();

        // Lists that exist but are not ready to be taken over yet. A terminal builds rows
        // before Unity has laid its window out, and one with no area cannot be measured into
        // a grid - nor should its rows be hidden, since nothing would be drawn in their place.
        private static readonly List<TerminalListAdapter> Waiting = new List<TerminalListAdapter>();

        internal static void Apply(Harmony harmony)
        {
            MethodInfo row = AccessTools.Method(typeof(TerminalListAdapter), "CreateViewsHolder");
            if (row == null)
            {
                UwUTermPlugin.Log.LogWarning("screen: TerminalListAdapter.CreateViewsHolder not found");
                return;
            }

            harmony.Patch(row, postfix: new HarmonyMethod(
                typeof(ScreenTakeover).GetMethod(nameof(OnRowCreated),
                    BindingFlags.Static | BindingFlags.NonPublic)));
        }

        /// <summary>A terminal builds rows as soon as it has anything to show, which makes
        /// this the earliest point its list is known to be alive and laid out.</summary>
        private static void OnRowCreated(TerminalListAdapter __instance, TerminalListItemViewsHolder __result)
        {
            if (__instance == null) return;

            // A row taken over already exists at full opacity for the rest of this frame.
            // Hiding it here rather than on the next tick is the difference between a clean
            // new line and a flash of doubled text every time you press Enter.
            if (Views.TryGetValue(__instance, out ScreenView view)) { view.HideHolder(__result); return; }

            if (!Waiting.Contains(__instance)) Waiting.Add(__instance);
        }

        /// <summary>Take over any list that has become measurable since the last frame.</summary>
        private static void Adopt()
        {
            for (int i = Waiting.Count - 1; i >= 0; i--)
            {
                TerminalListAdapter adapter = Waiting[i];
                if (adapter == null) { Waiting.RemoveAt(i); continue; }

                RectTransform viewport = adapter.Viewport;
                if (viewport == null) continue;

                Rect area = viewport.rect;
                if (area.width < 1f || area.height < 1f) continue;

                ScreenView view = ScreenView.Create(adapter);
                if (view == null) { Waiting.RemoveAt(i); continue; }

                Views[adapter] = view;
                Waiting.RemoveAt(i);

                if (UwUTermPlugin.ScreenDebug.Value)
                    UwUTermPlugin.Log.LogInfo($"screen: took over {Path(adapter.transform)}");
            }
        }

        /// <summary>Where a list sits in the scene, so two of them in one window can be told
        /// apart in the log.</summary>
        private static string Path(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        /// <summary>The screen drawing for a terminal, or null before it has been laid out.</summary>
        internal static ScreenView ViewFor(TerminalListAdapter adapter) =>
            adapter != null && Views.TryGetValue(adapter, out ScreenView view) ? view : null;

        /// <summary>
        /// A line changed somewhere other than at the end.
        ///
        /// The screen notices output arriving and typing by watching the last line, which is
        /// everything the terminal itself does. Search highlighting rewrites lines further up,
        /// and nothing about that is visible from the end of the list.
        /// </summary>
        internal static void NoteLineChanged(TerminalListAdapter adapter, int line) =>
            ViewFor(adapter)?.NoteLineChanged(line);

        /// <summary>Bring a line into view - what stepping to a search match needs.</summary>
        internal static void ScrollToLine(TerminalListAdapter adapter, int line) =>
            ViewFor(adapter)?.ScrollToLine(line);

        /// <summary>Whether the grid is drawing this terminal. Anything that would otherwise
        /// have to update the game's own rows asks first.</summary>
        internal static bool Owns(TerminalListAdapter adapter) => ViewFor(adapter) != null;

        private static bool _running;

        internal static void Tick()
        {
            if (!UwUTermPlugin.FeatureTerminal.Value)
            {
                if (_running) { TearDown(); _running = false; }
                return;
            }

            // Rows are pooled, so a terminal sitting idle builds none and the row hook would
            // never fire for it. Switching back on has to go and find what is already open.
            if (!_running)
            {
                _running = true;
                foreach (TerminalListAdapter adapter in Object.FindObjectsOfType<TerminalListAdapter>())
                    if (adapter != null && !Views.ContainsKey(adapter) && !Waiting.Contains(adapter))
                        Waiting.Add(adapter);
            }

            Reap();
            Adopt();

            foreach (KeyValuePair<TerminalListAdapter, ScreenView> pair in Views)
                pair.Value.Tick();
        }

        private static void Reap()
        {
            Dead.Clear();
            foreach (KeyValuePair<TerminalListAdapter, ScreenView> pair in Views)
                if (pair.Key == null) Dead.Add(pair.Key);

            foreach (TerminalListAdapter adapter in Dead)
            {
                Views[adapter].Destroy();
                Views.Remove(adapter);
            }
        }

        /// <summary>Give the game's own rows back, which is why they were only hidden.</summary>
        private static void TearDown()
        {
            foreach (KeyValuePair<TerminalListAdapter, ScreenView> pair in Views) pair.Value.Destroy();
            Views.Clear();
            Waiting.Clear();
            UwUTermPlugin.Log.LogInfo("screen: handed drawing back to the game");
        }

        internal static void Dismiss(uDialog dialog)
        {
            if (dialog == null) return;

            Dead.Clear();
            foreach (KeyValuePair<TerminalListAdapter, ScreenView> pair in Views)
            {
                if (pair.Key == null) { Dead.Add(pair.Key); continue; }

                Terminal owner = pair.Key.GetComponentInParent<Terminal>();
                if (owner != null && ReferenceEquals(owner.dialogo, dialog)) Dead.Add(pair.Key);
            }

            foreach (TerminalListAdapter adapter in Dead)
            {
                Views[adapter].Destroy();
                Views.Remove(adapter);
            }
        }
    }
}
