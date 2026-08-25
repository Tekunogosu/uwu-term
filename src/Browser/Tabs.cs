using System.Collections.Generic;
using FeedBack;
using HarmonyLib;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Patches;
using UwUTerm.Ui;
using Util;

namespace UwUTerm.Browser
{
    /// <summary>
    /// Every browser window on the desktop, and which of them are tabs of the same window.
    ///
    /// A window is found rather than intercepted. The game builds a browser in
    /// <c>ProgramWindow.Procesar</c>, off the back of a command the server has already agreed to
    /// run, and patching that would put us in the way of every launch - including the ones meant to
    /// open a window of their own. Instead each browser dialog on the taskbar that has not been
    /// seen before is picked up on the next tick, so <c>Browser.exe</c> typed in a terminal, run
    /// from a script, or clicked on the desktop behaves exactly as it did.
    ///
    /// A tab is one of those launches that we asked for. <see cref="NewTab"/> runs
    /// <c>Browser.exe</c> through <c>InternalBash</c> - the same hidden shell <c>Stocks</c> uses -
    /// and marks the group it is for; the window that turns up while that mark is live joins the
    /// group instead of standing alone. The mark expires, so a launch that never arrives cannot
    /// swallow a window the player opened later.
    ///
    /// Closing is the game's own. <see cref="CloseTab"/> calls <c>HtmlBrowser.CloseTaskBar</c>,
    /// which ends the server-side process, tears the PowerUI document down and releases the
    /// cameras - none of which is reimplemented here. Our seam on that method is only there to ask
    /// first, and to take the window out of its group before it goes.
    /// </summary>
    internal static class Tabs
    {
        /// <summary>How long a browser window is given to lay itself out before its band is called
        /// unmeasurable, in seconds. A layout pass takes a frame; this is orders of magnitude more,
        /// because the only thing it has to separate is "not yet" from "never".</summary>
        private const float SettleWindow = 5f;

        /// <summary>How long after asking for a tab a new browser window is taken to be that tab,
        /// in seconds. Long enough for a round trip to the server, short enough that a launch which
        /// never arrives does not claim a window the player opened minutes later.</summary>
        private const float AdoptWindow = 15f;

        private static readonly List<TabGroup> Groups = new List<TabGroup>();
        private static readonly Dictionary<uDialog, TabStrip> Strips = new Dictionary<uDialog, TabStrip>();
        private static readonly List<uDialog> Scratch = new List<uDialog>();
        private static readonly List<string> Names = new List<string>();

        /// <summary>Browser windows seen but not yet laid out far enough to measure, and when each
        /// was first seen. A window that never becomes measurable is a prefab we no longer
        /// recognise, and saying so beats retrying quietly forever.</summary>
        private static readonly Dictionary<uDialog, float> Waiting = new Dictionary<uDialog, float>();

        private static TabGroup _pending;
        private static float _pendingUntil;
        private static UI_Theme _theme;
        private static string _watched;

        /// <summary>Set when a browser window turns out not to have the layout the row is fitted
        /// into. Nothing is drawn after that: half a feature applied to some windows and not others
        /// is worse than none, and the log says which it was.</summary>
        private static bool _broken;

        /// <summary>Set while we are the ones closing a window, so the seam on
        /// <c>CloseTaskBar</c> knows not to ask again about a decision already taken.</summary>
        private static bool _closing;

        /// <summary>How many closes the game is currently making itself, and until when it is
        /// working through a run of them. The question is for a player closing a window; every
        /// other way a browser is closed - killed, shut down, disconnected, cleared away by the
        /// tutorial - is a decision already taken somewhere else.
        ///
        /// Two counters because the paths come in two shapes. One returns when it is done and can
        /// be bracketed; the others hand a coroutine back and close a window every so many
        /// hundredths of a second, so what is held is a stretch of time long enough for the run
        /// they described.</summary>
        private static int _quietDepth;
        private static float _quietUntil;

        private static bool Quiet => _quietDepth > 0 || Time.unscaledTime < _quietUntil;

        /// <summary>Windows made ready to appear as a tab and not yet taken into one. They are on
        /// the taskbar for the frame between being shown and being adopted, and are not windows in
        /// their own right for any of it.</summary>
        private static readonly HashSet<uDialog> Prepared = new HashSet<uDialog>();

        /// <summary>Groups with a question already on screen. One close puts one question, however
        /// many of the group's windows are asked to close while it stands.</summary>
        private static readonly HashSet<TabGroup> Asking = new HashSet<TabGroup>();

        internal static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(HtmlBrowser), "CloseTaskBar", new System.Type[0]),
                  "HtmlBrowser.CloseTaskBar", nameof(OnClose), null);

            // kill on a tab's PID. The server decided; there is nothing left to ask about, and the
            // tab it names is the only one that should go.
            Patch(harmony, AccessTools.Method(typeof(PlayerClientMethods), "CloseProgramClientRpc",
                                              new[] { typeof(int), typeof(byte[]), typeof(bool) }),
                  "PlayerClientMethods.CloseProgramClientRpc", nameof(EnterQuiet), nameof(LeaveQuiet));

            // The game's own word for closing a window without ceremony. It says it here and
            // nowhere else, and every caller of it is clearing the desktop rather than shutting
            // one window.
            Patch(harmony, AccessTools.Method(typeof(Ventana), "CloseTaskBar", new[] { typeof(bool) }),
                  "Ventana.CloseTaskBar(bool)", nameof(EnterQuiet), nameof(LeaveQuiet));

            // Shutting down and rebooting both go through here, closing every window in turn.
            Patch(harmony, AccessTools.Method(typeof(Terminal), "ClosingPrograms", new[] { typeof(bool) }),
                  "Terminal.ClosingPrograms", nameof(GoingDown), null);

            // Leaving a machine takes every window opened on it with it, a tenth of a second apart.
            Patch(harmony, AccessTools.Method(typeof(PlayerClientMethods), "CloseWindowsClientRpc",
                                              new[] { typeof(System.Collections.Generic.List<int>), typeof(byte[]) }),
                  "PlayerClientMethods.CloseWindowsClientRpc", nameof(LettingGo), null);
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodInfo target, string name,
                                  string prefix, string postfix)
        {
            if (target == null)
            {
                UwUTermPlugin.Log.LogWarning($"browser tabs: {name} not found");
                return;
            }

            harmony.Patch(target,
                prefix: Method(prefix),
                postfix: postfix == null ? null : Method(postfix));
        }

        private static HarmonyMethod Method(string name) =>
            new HarmonyMethod(typeof(Tabs).GetMethod(name,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));

        // ---- what the rest of the mod asks -------------------------------------------------

        /// <summary>Whether this window is a tab of some other window that is on screen. The top
        /// bar draws one button per group rather than one per tab, and the window memory records
        /// one rectangle for the group rather than one per tab that shares it.</summary>
        internal static bool IsBackground(uDialog window)
        {
            if (window == null) return false;
            if (Prepared.Contains(window)) return true;

            for (int i = 0; i < Groups.Count; i++)
                if (Groups[i].IsBackground(window)) return true;
            return false;
        }

        /// <summary>The window that stands for another on the bar. A group of tabs is one thing
        /// there, so whatever the game says is focused - a tab about to open, or one held out of
        /// sight - answers as the tab actually on screen.</summary>
        internal static uDialog Represent(uDialog window)
        {
            if (window == null) return null;

            TabGroup group = GroupOf(window);
            if (group != null) return group.Active;

            return Prepared.Contains(window) && _pending != null ? _pending.Active : window;
        }

        /// <summary>Follow the theme the player has chosen, the same signal the top bar follows.</summary>
        internal static void Paint(UI_Theme theme)
        {
            if (theme == null) return;
            _theme = theme;

            for (int i = 0; i < Groups.Count; i++)
            {
                IList<uDialog> members = Groups[i].Members;
                for (int m = 0; m < members.Count; m++)
                    if (Strips.TryGetValue(members[m], out TabStrip strip))
                        strip.Repaint(theme, Groups[i].ActiveIndex);
            }
        }

        // ---- per-frame ---------------------------------------------------------------------

        internal static void Tick()
        {
            if (_broken || !UwUTermPlugin.FeatureBrowser.Value) return;

            uDialog_TaskBar bar = uDialog_TaskBar.Singleton;
            if (bar == null || bar.Tasks == null) return;

            Prune();
            Prepare(bar.transform.parent);
            Discover(bar.Tasks);

            if (_theme == null) _theme = OS.GetThemeFromFile();

            for (int i = 0; i < Groups.Count; i++)
            {
                TabGroup group = Groups[i];
                group.Hold();

                IList<uDialog> members = group.Members;

                Names.Clear();
                for (int m = 0; m < members.Count; m++) Names.Add(Title(members[m]));

                for (int m = 0; m < members.Count; m++)
                    if (Strips.TryGetValue(members[m], out TabStrip strip))
                        strip.Follow(Names, group.ActiveIndex, _theme);
            }

            Hotkeys();
            Watch();
        }

        /// <summary>Say which browser the game thinks is in front and which of them its pages will
        /// take a click through, whenever that changes.
        ///
        /// PowerUI picks the page a click lands in by raycasting and taking the last result, so
        /// two browsers offering their page at once is a click in whichever of them is behind.
        /// This is the line that says whether exactly one is offering.</summary>
        private static void Watch()
        {
            if (!UwUTermPlugin.BrowserDebug.Value) return;

            var sb = new System.Text.StringBuilder(256);
            for (int i = 0; i < Groups.Count; i++)
            {
                IList<uDialog> members = Groups[i].Members;
                for (int m = 0; m < members.Count; m++)
                {
                    uDialog member = members[m];
                    var browser = member != null ? member.GetComponentInChildren<HtmlBrowser>() : null;
                    if (browser == null) continue;

                    CanvasGroup group = member.CanvasGroup;
                    sb.Append($"[{m}{(m == Groups[i].ActiveIndex ? "*" : "")} ")
                      .Append($"front={member.IsFocused()} ")
                      .Append($"page={(browser.rawImage != null && browser.rawImage.raycastTarget)} ")
                      .Append($"blocks={(group != null && group.blocksRaycasts)} ")
                      .Append($"alpha={(group != null ? group.alpha : -1f):F0}] ");
                }
            }

            string now = sb.ToString();
            if (now == _watched) return;

            _watched = now;
            if (now.Length == 0) return;   // no browser open to describe

            UwUTermPlugin.Log.LogInfo("browser tabs: " + now);
        }

        /// <summary>What a tab is called: the page it is on, without the scheme in front of it. A
        /// browser sitting on the search page has not been anywhere yet.</summary>
        private static string Title(uDialog window)
        {
            var browser = window != null ? window.GetComponentInChildren<HtmlBrowser>() : null;
            string address = browser != null && browser.barAddress != null ? browser.barAddress.text : null;

            if (string.IsNullOrEmpty(address)) return "New Tab";

            if (address.StartsWith("http://")) address = address.Substring(7);
            else if (address.StartsWith("https://")) address = address.Substring(8);

            return address.Equals("gsearch.com") ? "New Tab" : address;
        }

        /// <summary>
        /// Make a window that is about to open take the group's place silently.
        ///
        /// A window is built hidden and shown a frame later, and left alone it arrives with a show
        /// animation, at whatever place the layout memory has for a browser - so a new tab announced
        /// itself as a window opening, somewhere else, and then jumped. All three are settled here,
        /// before the frame that shows it: the animation is dropped, the layout memory is told the
        /// window is already accounted for, and it is put exactly where the group is.
        ///
        /// Dropping the animation is also what keeps it invisible until we are ready. A window built
        /// hidden was closed rather than never shown, so its alpha is zero, and <c>Show</c> restores
        /// alpha only through the animation it plays - with none to play, the window is active and
        /// clear until <see cref="TabGroup.Adopt"/> raises it in the same instant as it hides the
        /// tab before it. What the player sees is the page changing, not a window arriving.
        ///
        /// Only while a tab has been asked for, and only for a window nothing else has claimed.
        /// </summary>
        private static void Prepare(Transform desktop)
        {
            if (desktop == null || _pending == null || Time.unscaledTime > _pendingUntil) return;

            uDialog into = _pending.Active;
            RectTransform parent = into != null ? into.RectTransform.parent as RectTransform : null;
            if (parent == null) return;

            for (int i = 0; i < desktop.childCount; i++)
            {
                var window = desktop.GetChild(i).GetComponent<uDialog>();
                if (window == null || Prepared.Contains(window) || Strips.ContainsKey(window)) continue;
                if (GroupOf(window) != null) continue;

                // Its own components have not woken yet, so the search has to reach inactive ones.
                if (window.GetComponentInChildren<HtmlBrowser>(true) == null) continue;

                Prepared.Add(window);
                window.ShowAnimation = eShowAnimation.None;
                WindowMemory.Release(window);

                Rect place = WindowGeometry.LocalRect(into.RectTransform, parent);
                WindowGeometry.Place(window.RectTransform, parent, place.size, place.center);

                if (UwUTermPlugin.BrowserDebug.Value)
                    UwUTermPlugin.Log.LogInfo($"browser tabs: prepared '{window.name}' at " +
                                              $"{place.width:F0}x{place.height:F0}");
                return;
            }
        }

        /// <summary>Browser windows nobody has claimed yet. One that arrived because we asked for a
        /// tab joins the group that asked; every other one is a window in its own right.</summary>
        private static void Discover(List<uDialog> open)
        {
            for (int i = 0; i < open.Count; i++)
            {
                uDialog window = open[i];
                if (window == null || Strips.ContainsKey(window)) continue;

                var browser = window.GetComponentInChildren<HtmlBrowser>();
                if (browser == null) continue;

                if (!TabStrip.Ready(browser))
                {
                    if (!Waiting.TryGetValue(window, out float since))
                        Waiting[window] = since = Time.unscaledTime;

                    if (Time.unscaledTime - since < SettleWindow) continue;
                }

                Waiting.Remove(window);

                TabGroup group = Claim();

                // Read before adopting, which is what makes the newcomer the group's active one.
                uDialog beside = group.Active;

                group.Adopt(window);
                Inherit(window, beside);

                // It is a tab now, so it stops being a window in its own right whatever happens
                // to the rest of this.
                Prepared.Remove(window);

                TabStrip strip = TabStrip.Attach(
                    window, browser,
                    () => NewTab(group),
                    index => group.Activate(index),
                    index => CloseTab(group, index < group.Count ? group.Members[index] : null));

                if (strip == null)
                {
                    _broken = true;
                    UwUTermPlugin.Log.LogError(
                        "browser tabs: the browser window has no tab band above its toolbar - tabs " +
                        "are off for this session. Turn BrowserDebug on to see what was found.");

                    group.Forget(window);
                    if (group.Count == 0) Groups.Remove(group);
                    return;
                }

                Strips[window] = strip;

                // A tab lands on the group's rectangle, which is not the one the layout memory has
                // been holding it to since it opened.
                if (group.Count > 1) WindowMemory.Release(window);

                if (UwUTermPlugin.BrowserDebug.Value)
                    UwUTermPlugin.Log.LogInfo($"browser tabs: adopted '{window.TitleText}' " +
                                              $"into a group of {group.Count}");
            }
        }

        /// <summary>Give a new tab the connection the tab it opened beside has - its user, its
        /// machine, and the title bar that says so. The window was built by a launch through the
        /// local shell, so left alone it describes that shell rather than the browser it belongs
        /// to. <c>UpdateConnectPc</c> is the game's own way of handing a window's connection to a
        /// window it spawns, and does the title and its colour along with it.</summary>
        private static void Inherit(uDialog window, uDialog beside)
        {
            if (window == null || beside == null) return;

            var tab = window.GetComponentInChildren<Ventana>();
            var from = beside.GetComponentInChildren<Ventana>();
            if (tab == null || from == null) return;

            tab.UpdateConnectPc(window, from);

            if (UwUTermPlugin.BrowserDebug.Value)
                UwUTermPlugin.Log.LogInfo($"browser tabs: tab runs as '{tab.GetActiveUser()}'" +
                                          (tab.IsRemoteConnection() ? " on a remote machine" : ""));
        }

        private static TabGroup Claim()
        {
            if (_pending != null && Time.unscaledTime <= _pendingUntil && Groups.Contains(_pending))
            {
                TabGroup group = _pending;
                _pending = null;
                return group;
            }

            _pending = null;
            var fresh = new TabGroup();
            Groups.Add(fresh);
            return fresh;
        }

        /// <summary>Windows destroyed without passing through our seam - torn down with the scene,
        /// or closed by a path we do not sit on. Their strips went with them, so there is nothing
        /// to take down here beyond the entries that named them.</summary>
        private static void Prune()
        {
            Forget(Strips);
            Forget(Waiting);
            Prepared.RemoveWhere(window => window == null);

            for (int i = Groups.Count - 1; i >= 0; i--)
            {
                Groups[i].Prune();
                if (Groups[i].Count > 0) continue;

                Asking.Remove(Groups[i]);
                Groups.RemoveAt(i);
            }
        }

        /// <summary>Drop entries whose window has been destroyed. The key has to be collected
        /// before it is removed: a destroyed object still answers as a key, and the dictionary
        /// cannot be written to while it is being read.</summary>
        private static void Forget<T>(Dictionary<uDialog, T> from)
        {
            Scratch.Clear();
            foreach (KeyValuePair<uDialog, T> pair in from)
                if (pair.Key == null) Scratch.Add(pair.Key);

            foreach (uDialog gone in Scratch) from.Remove(gone);
            Scratch.Clear();
        }

        // ---- opening and closing -----------------------------------------------------------

        /// <summary>Open a tab: a real <c>Browser.exe</c>, because a browser with no process behind
        /// it is one the server answers <c>ERROR_UNKPROC</c> to.</summary>
        internal static void NewTab(TabGroup group)
        {
            InternalBash bash = InternalBash.Singleton;
            if (bash == null)
            {
                UwUTermPlugin.Log.LogWarning("browser tabs: no InternalBash to launch Browser.exe with");
                return;
            }

            uDialog active = group != null ? group.Active : null;
            var source = active != null ? active.GetComponentInChildren<HtmlBrowser>() : null;
            if (source == null) return;

            _pending = group;
            _pendingUntil = Time.unscaledTime + AdoptWindow;
            Launch(bash, source);
        }

        /// <summary>
        /// Run the browser the way the tab beside it was run.
        ///
        /// <c>InternalBash.ProcesaLinea</c> calls <c>SetLocalInfo</c> first, which is what makes it a
        /// shell on this machine as the player's own user - so a tab opened beside a root browser
        /// came up as the ordinary user, and one opened beside a browser on a machine across the
        /// network came up here. Neither is the browser it was opened from.
        ///
        /// Two things carry that across. <c>propActiveUser</c> is the user the server checks the
        /// program's permissions against, and <c>parentPID</c> is the process it takes the machine
        /// from - <c>GreyScriptHelperServer.PrepareCommandServerRpc</c> reads
        /// <c>proceso.GetRemoteNetID()</c> off it - so naming the browser as the parent puts the tab
        /// wherever that browser is. The program is named by its full path for the same reason: the
        /// one this browser was launched from, on the machine it is running on, rather than whatever
        /// a search of this machine's <c>/bin</c> turns up.
        ///
        /// The shell is shared - the stock market and the start menu both launch through it - so
        /// what is borrowed is put back. Every other caller sets its own before using it, and this
        /// one does not get to be the exception that makes that a rule.
        /// </summary>
        private static void Launch(InternalBash bash, HtmlBrowser source)
        {
            string user = bash.activeUser;
            int parent = bash.parentLaunchPID;

            bash.activeUser = source.GetActiveUser();
            bash.parentLaunchPID = source.GetPID();

            string program = source.GetPathProgram();

            try { bash.ProcesaLineaUI(string.IsNullOrEmpty(program) ? "Browser.exe" : program); }
            finally
            {
                bash.activeUser = user;
                bash.parentLaunchPID = parent;
            }
        }

        internal static void CloseTab(TabGroup group, uDialog member)
        {
            if (group == null || member == null) return;

            var browser = member.GetComponentInChildren<HtmlBrowser>();
            if (browser == null) return;

            _closing = true;
            try { browser.CloseTaskBar(); }
            finally { _closing = false; }
        }

        private static void CloseGroup(TabGroup group)
        {
            if (group == null) return;

            // A copy of its own: closing each member takes it out of the group as it goes.
            var members = new List<uDialog>(group.Members);
            foreach (uDialog member in members) CloseTab(group, member);
        }

        /// <summary>
        /// A window carrying more than one tab asks before it takes them all with it.
        ///
        /// The seam is on <c>CloseTaskBar</c> rather than on <c>uDialog.Close</c> because that is
        /// the one method every way out reaches - the title bar's cross, the taskbar's menu, and
        /// the server closing a window from under the player - while <c>Close</c> is also how a
        /// window minimises, which must not cost the tabs anything.
        /// </summary>
        private static bool OnClose(HtmlBrowser __instance)
        {
            if (!UwUTermPlugin.FeatureBrowser.Value) return true;

            uDialog window = __instance.dialogo;
            TabGroup group = GroupOf(window);
            if (group == null) return true;

            // The game's own method turns a close during the tutorial into a question and closes
            // nothing, so the window must still be in its group when that answer comes back.
            if (__instance.tutorial != null) return true;

            if (!_closing && !Quiet && group.Count > 1 && UwUTermPlugin.ConfirmCloseTabs.Value && Ask(group))
                return false;

            Detach(group, window);
            return true;
        }

        /// <summary>Put the question to the player, and say whether it was actually put. A window
        /// that cannot raise the question closes rather than refusing to: an unanswerable question
        /// is a window with no way out of it.</summary>
        private static bool Ask(TabGroup group)
        {
            // Already on screen. The window stays shut against this close too, so the answer that
            // is coming decides for all of them rather than one question arriving per window.
            if (!Asking.Add(group)) return true;

            uDialog asked = OS.ShowQuestionWindow(
                $"This window has {group.Count} tabs open. Close them all?",
                new CloseAll(group), "Close all", "Cancel");

            if (asked == null)
            {
                Asking.Remove(group);
                UwUTermPlugin.Log.LogWarning("browser tabs: could not ask about closing, closing anyway");
                return false;
            }

            if (UwUTermPlugin.BrowserDebug.Value)
                UwUTermPlugin.Log.LogInfo($"browser tabs: asked about closing {group.Count} tabs");

            return true;
        }

        private static void Detach(TabGroup group, uDialog window)
        {
            if (Strips.TryGetValue(window, out TabStrip strip))
            {
                strip.Destroy();
                Strips.Remove(window);
            }

            group.Forget(window);
            if (group.Count > 0) return;

            Groups.Remove(group);
            Asking.Remove(group);
        }

        private static TabGroup GroupOf(uDialog window)
        {
            if (window == null) return null;

            for (int i = 0; i < Groups.Count; i++)
                if (Groups[i].Holds(window)) return Groups[i];
            return null;
        }

        /// <summary>The group the player is looking at, which is the one a keystroke is meant for.</summary>
        private static TabGroup Focused()
        {
            for (int i = 0; i < Groups.Count; i++)
            {
                uDialog active = Groups[i].Active;
                if (active != null && active.isVisible && active.IsFocused()) return Groups[i];
            }
            return null;
        }

        private static void Hotkeys()
        {
            if (!UwUTermPlugin.EnableTabHotkeys.Value) return;
            if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl)) return;

            // Not while the player is typing. A text field takes the keystroke either way - Ctrl+T
            // reaches TMP_InputField as a plain 't', which it appends - so acting on it as well
            // would switch tabs and edit the address bar at once.
            if (Typing()) return;

            TabGroup group = Focused();
            if (group == null) return;

            if (Input.GetKeyDown(KeyCode.T)) { NewTab(group); return; }
            if (Input.GetKeyDown(KeyCode.W)) { CloseTab(group, group.Active); return; }

            // Ctrl+9 is the last tab however many there are, as everywhere else that has this.
            for (int i = 0; i < 9; i++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha1 + i)) continue;
                group.Activate(i == 8 ? group.Count - 1 : i);
                return;
            }
        }

        /// <summary>Whether the keyboard belongs to a text field at the moment.</summary>
        private static bool Typing()
        {
            GameObject selected = UnityEngine.EventSystems.EventSystem.current != null
                ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject
                : null;

            return selected != null && selected.GetComponent<TMPro.TMP_InputField>() != null;
        }

        /// <summary>Shutting down or rebooting: every window on the desktop, a fifth of a second
        /// apart. Counted here rather than guessed at - it is the same list the method walks.</summary>
        private static void GoingDown(Terminal __instance) =>
            Hold(__instance.transform.root.GetComponentsInChildren<Ventana>().Length * 0.2f);

        /// <summary>A machine we were working on has gone, and with it every window opened on it,
        /// a tenth of a second apart.</summary>
        private static void LettingGo(System.Collections.Generic.List<int> PIDs) =>
            Hold((PIDs != null ? PIDs.Count : 0) * 0.1f);

        /// <summary>Stay quiet for a run of closes that hands back a coroutine rather than
        /// returning - there is no moment afterwards to be told about, so the length of the run is
        /// worked out from what the method itself is about to do, and a second on top for the
        /// frame it starts on.</summary>
        private static void Hold(float seconds) =>
            _quietUntil = Mathf.Max(_quietUntil, Time.unscaledTime + seconds + 1f);

        private static void EnterQuiet() => _quietDepth++;

        private static void LeaveQuiet() => _quietDepth--;

        /// <summary>Carries the answer to the question back. <c>ErrorWindow</c> hands it to whatever
        /// implements the interface, so this need not be a component of anything.</summary>
        private sealed class CloseAll : FeedBackInterface
        {
            private readonly TabGroup _group;

            internal CloseAll(TabGroup group) => _group = group;

            public void ResumeConfirmDialog(ConfirmationDialog confirmation, string inputUser = "",
                                            string password = "", string price = "")
            {
                Asking.Remove(_group);

                if (UwUTermPlugin.BrowserDebug.Value)
                    UwUTermPlugin.Log.LogInfo($"browser tabs: answered {confirmation}");

                if (confirmation == ConfirmationDialog.Yes) CloseGroup(_group);
            }
        }
    }
}
