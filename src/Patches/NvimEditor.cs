using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NotepadPoolSystem;
using TMPro;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Nvim;
using UwUTerm.Ui;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Puts a real neovim in the code editor window.
    ///
    /// The window keeps doing everything else it does - opening files, saving, and handing the
    /// source to the server to be compiled - because none of that knows where the text comes
    /// from. Notepad.GetSource and SetSource are the only two places it asks, so those are the
    /// only two places that change.
    ///
    /// Keys go to neovim instead of the game's editor, which is what makes it neovim rather
    /// than a picture of one. Without a binary to run, none of this engages and the window
    /// opens exactly as it always did.
    /// </summary>
    internal static class NvimEditor
    {
        private static readonly Dictionary<Notepad, NvimView> Editors = new Dictionary<Notepad, NvimView>();
        private static readonly List<Notepad> Dead = new List<Notepad>();

        /// <summary>How long to keep asking for a session that has just gone, before settling
        /// for an editor of our own.</summary>
        private const float RejoinGrace = 15f;

        // Set when a joined session ends. A daemon that restarts itself is back within a second
        // or two, and swapping the player's whole setup for a bare editor in the meantime would
        // undo the thing they started the daemon for.
        private static float _rejoinUntil;

        internal static void Apply(Harmony harmony)
        {
            Patch(harmony, typeof(NotepadListAdapter), "OnGUI", nameof(OnKey), prefix: true);
            Patch(harmony, typeof(Notepad), "GetSource", nameof(OnGetSource), prefix: true);
            Patch(harmony, typeof(Notepad), "SetSource", nameof(OnSetSource), prefix: true);
        }

        private static void Patch(Harmony harmony, System.Type type, string method, string handler, bool prefix)
        {
            MethodInfo target = AccessTools.Method(type, method);
            if (target == null)
            {
                UwUTermPlugin.Log.LogWarning($"nvim: {type.Name}.{method} not found");
                return;
            }

            var patch = new HarmonyMethod(typeof(NvimEditor).GetMethod(handler,
                BindingFlags.Static | BindingFlags.NonPublic));

            harmony.Patch(target, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
        }

        // ---- taking over ------------------------------------------------------------------

        internal static void Tick()
        {
            if (!UwUTermPlugin.FeatureEditor.Value)
            {
                if (Editors.Count > 0) CloseAll();
                return;
            }

            if (UwUTermPlugin.NvimDownload.Value) NvimInstall.FetchInBackground();
            if (!NvimInstall.Usable) return;

            Adopt();

            Dead.Clear();
            foreach (KeyValuePair<Notepad, NvimView> pair in Editors)
            {
                if (pair.Key == null || !pair.Value.Running) { Dead.Add(pair.Key); continue; }

                Hide(pair.Key.listAdapter, true);

                NotepadListAdapter adapter = pair.Key.listAdapter;
                pair.Value.Tick(adapter != null && adapter.isFocus);
            }

            foreach (Notepad closed in Dead) Close(closed);
        }

        /// <summary>Find editor windows that have opened since the last look.</summary>
        private static void Adopt()
        {
            foreach (Notepad window in Object.FindObjectsOfType<Notepad>())
            {
                if (window == null || Editors.ContainsKey(window)) continue;

                NotepadListAdapter adapter = window.listAdapter;
                RectTransform viewport = adapter != null ? adapter.Viewport : null;
                if (viewport == null) continue;

                TMP_Text sample = viewport.GetComponentInChildren<TMP_Text>(true);
                TMP_FontAsset font = sample != null ? sample.font : null;
                float size = sample != null ? sample.fontSize : 14f;

                // A session has one screen - every UI attached to it renders the same grid,
                // sized to the smallest of them - so only the first window is offered it. The
                // rest run an editor of their own rather than mirror it.
                bool free = Holder(out Notepad _) == null;
                bool waiting = free && NvimInstall.Address != null
                                    && Time.realtimeSinceStartup < _rejoinUntil;

                NvimView view = NvimView.Create(viewport, TerminalFont.For(font) ?? font, size, null,
                                                joinSession: free, allowSpawn: !waiting);

                // Nothing yet, and the session is still expected back - ask again next frame
                // rather than settle for less.
                if (view == null) continue;

                // The game's rows stay where they are and stop drawing - the window still reads
                // its own model for things like the title's modified marker.
                Hide(adapter, true);

                Notepad owner = window;
                bool joined = view.Attached;
                view.Ended += why =>
                {
                    UwUTermPlugin.Log.LogWarning("nvim: " + why);
                    if (joined) _rejoinUntil = Time.realtimeSinceStartup + RejoinGrace;
                    Close(owner);
                };

                Editors[window] = view;

                Notepad following = window;
                view.BufferEntered += buffer => Retarget(following, view.PathFor(buffer));

                view.Open(FileName(window), window.GetSource(), UwUTermPlugin.NvimFiletype.Value,
                          window.rutaArchivo);
                Report(viewport, view);

                UwUTermPlugin.Log.LogInfo("nvim: editing in neovim");
            }
        }

        /// <summary>
        /// Everything drawing text inside the editor window, and how opaque it is.
        ///
        /// Two things painting the same characters look like one thing painting them badly, so
        /// this says which objects are actually on screen rather than which ones were meant to
        /// be hidden.
        /// </summary>
        private static void Report(RectTransform viewport, NvimView view)
        {
            if (!UwUTermPlugin.ScreenDebug.Value) return;

            Transform root = viewport.parent != null ? viewport.parent : viewport;

            // Every graphic, not just the text ones - a caret and a selection are images, and
            // listing only text is how they stayed invisible in a report about what is visible.
            foreach (UnityEngine.UI.Graphic graphic in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            {
                if (!graphic.gameObject.activeInHierarchy || graphic.color.a <= 0.01f) continue;

                string path = graphic.name;
                for (Transform t = graphic.transform.parent; t != null && t != root.parent; t = t.parent)
                    path = t.name + "/" + path;

                UwUTermPlugin.Log.LogInfo(
                    $"nvim:   drawing '{path}' <{graphic.GetType().Name}> alpha={graphic.color.a:F2}");
            }
        }

        private static void Close(Notepad window)
        {
            if (window != null && Editors.TryGetValue(window, out NvimView view))
            {
                view.Destroy();
                if (window.listAdapter != null) Hide(window.listAdapter, false);
            }

            Editors.Remove(window);
        }

        /// <summary>The game is going away. Take the editors with it.</summary>
        internal static void Shutdown() => CloseAll();

        private static void CloseAll()
        {
            var open = new List<Notepad>(Editors.Keys);
            foreach (Notepad window in open) Close(window);
        }

        /// <summary>
        /// Make the game's own editor invisible without disturbing it - it is still the model
        /// the window reads for things like whether the file has unsaved changes.
        ///
        /// One group over the content OSA fills, rather than a hunt through its parts. Rows,
        /// line numbers, the syntax overlay, the caret and the selection are all under there
        /// and all reappear on their own schedule - the caret whenever the window takes focus,
        /// a row whenever one is recycled - so fading them one at a time is a list that is
        /// never quite finished. The picture drawn in their place is a sibling of the content
        /// rather than a child, so it is not caught by this.
        /// </summary>
        private static void Hide(NotepadListAdapter adapter, bool hidden)
        {
            if (adapter == null) return;

            RectTransform content = adapter.Content;
            if (content == null) return;

            var group = content.GetComponent<CanvasGroup>();
            if (group == null)
            {
                if (!hidden) return;
                group = content.gameObject.AddComponent<CanvasGroup>();
            }

            float wanted = hidden ? 0f : 1f;
            if (!Mathf.Approximately(group.alpha, wanted)) group.alpha = wanted;
        }

        private static NvimView For(Notepad window) =>
            window != null && Editors.TryGetValue(window, out NvimView view) ? view : null;

        // ---- the seams --------------------------------------------------------------------

        /// <summary>Keys reach neovim instead of the game's editor.</summary>
        private static bool OnKey(NotepadListAdapter __instance)
        {
            if (Event.current == null || Event.current.type != EventType.KeyDown) return true;
            if (!__instance.isFocus) return true;

            Notepad window = __instance.GetComponentInParent<Notepad>();
            NvimView view = For(window);
            if (view == null) return true;

            string keys = NvimKeys.From(Event.current);
            if (keys == null) return true;

            view.Send(keys);
            Event.current.Use();
            return false;
        }

        /// <summary>The source the window compiles is neovim's buffer.</summary>
        private static bool OnGetSource(Notepad __instance, ref string __result)
        {
            NvimView view = For(__instance);
            if (view == null) return true;

            __result = view.Source;
            return false;
        }

        /// <summary>Opening a file puts it into neovim rather than the game's rows.</summary>
        private static bool OnSetSource(Notepad __instance, string source)
        {
            NvimView view = For(__instance);
            if (view == null) return true;

            // Loading a file is also when its name becomes known, so the buffer is renamed
            // here rather than left as whatever the last one was called.
            view.Open(FileName(__instance), source, UwUTermPlugin.NvimFiletype.Value,
                      __instance.rutaArchivo);

            // Opening a file through the folder dialog leaves focus with the dialog that just
            // closed, so the first keystroke afterwards goes nowhere and the editor has to be
            // clicked before it will take any. Loading a file is a request to edit it.
            Focus(__instance);
            return false;
        }

        /// <summary>
        /// Put the keyboard back on the editor, the same way the window does when it is
        /// restored - selecting its own selectable is what marks the list focused, and the list
        /// being focused is what lets keys through to neovim.
        /// </summary>
        private static void Focus(Notepad window)
        {
            NotepadListAdapter adapter = window != null ? window.listAdapter : null;
            if (adapter == null) return;

            if (adapter.selectableNotepad != null) adapter.selectableNotepad.Select();
            adapter.SetFocus(true);
        }

        /// <summary>The window holding the shared session, if one is. An editor started for a
        /// window is not one - those are per-window, and a second gets its own.</summary>
        private static NvimView Holder(out Notepad owner)
        {
            owner = null;

            foreach (KeyValuePair<Notepad, NvimView> pair in Editors)
            {
                if (pair.Key == null || !pair.Value.Running || !pair.Value.Attached) continue;

                owner = pair.Key;
                return pair.Value;
            }

            return null;
        }

        /// <summary>
        /// Point a window at the file whose buffer is on screen.
        ///
        /// One window, many buffers, one save button - so which file that button writes has to
        /// follow what is being looked at. A buffer the game did not open has no path of its
        /// own, and the empty one left here is what makes the game ask where to put it instead
        /// of writing it over the last script that was open.
        /// </summary>
        private static void Retarget(Notepad window, string path)
        {
            if (window == null) return;

            string wanted = path ?? "";
            if (window.rutaArchivo == wanted) return;

            window.rutaArchivo = wanted;
            window.SetWindowTitleText(window.nombreVentana + (wanted.Length > 0 ? " - " + wanted : ""));
        }

        /// <summary>
        /// The file's name as the game knows it, without the path.
        ///
        /// Only the name: the path is a place on the game's server, and the workspace is a
        /// folder on this machine. Writing "/home/someone/scripts/thing.src" here would build
        /// that whole tree locally to hold one file.
        /// </summary>
        private static string FileName(Notepad window)
        {
            string path = window == null ? null : window.rutaArchivo;
            return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path);
        }
    }
}
