using System.Collections.Generic;
using TMPro;
using UI.Dialogs;
using UnityEngine;
using UnityEngine.UI;
using Util;
using UwUTerm.Bar;

namespace UwUTerm.Ui
{
    /// <summary>
    /// A top bar the mod owns.
    ///
    /// Owned rather than rearranged. The game's bar takes its widths from layout groups and
    /// content-size fitters nothing here controls, so holding a row of task buttons clear of the
    /// widgets by measuring a rect the game laid out is measuring a number that does not bound
    /// what it draws - and no care taken from outside makes it one.
    ///
    /// So nothing here is laid out by anything else. Positions come from <see cref="BarLayout"/>,
    /// which is arithmetic with no Unity in it and a test that runs without a game. What is left
    /// for this half is the scene: hosting the game's own widgets, drawing a button per window,
    /// and painting both in the player's theme.
    ///
    /// One unit is one screen pixel. The canvas maps a reference resolution onto the display -
    /// 1920x1080 at the game's "100%", which on a wider screen is not one pixel per unit - so
    /// the root is scaled by the inverse of that factor and everything inside it is in pixels.
    /// What the log says is then what the screen shows, which was not true of anything before.
    ///
    /// The game is not left drawing a bar underneath: <see cref="Patches.TopBarTakeover"/> stops
    /// uDialog_TaskBar building task buttons at all, so there is nothing hidden behind this - a
    /// window opening no longer destroys and rebuilds a row of GameObjects either, which is the
    /// per-frame cost the drag patch could only skip around.
    /// </summary>
    internal sealed class TopBar
    {
        private const string BarName = "BarraSupDesktop";
        private const string MenuButton = "MainMenuButton";
        private const string StartMenu = "MenuInicio";
        private const string Widgets = "PanelRight";
        private const string IconGrid = "IconsBar";
        private const string Clock = "ButtonHora";
        private const string UserName = "UserNameDesktop";
        private const string CalendarPopup = "Calendar";
        private const string IconGrid2 = "Icons";
        private const string RootName = "UwUTerm.TopBar";

        private static TopBar _applied;

        /// <summary>Whether this bar is drawing the desktop's furniture. False until it has
        /// taken the bar over, and for the whole session when the feature is off - the game's
        /// own bar is then left to draw itself as it always did.</summary>
        internal static bool Owns => _applied != null;

        private readonly BarLayout _layout = new BarLayout();

        private RectTransform _bar;      // the game's own top bar, kept as our background
        private RectTransform _root;     // everything of ours, in pixels
        private RectTransform _strip;    // the game's TaskBar object, which we do not move
        private RectTransform _rowRoot;  // ours, inside it, holding the buttons
        private Image _stripImage;       // the bottom bar's background, held out of sight
        private RectTransform _icons;    // the desktop's icon grid
        private uDialog_TaskBar _tasks;

        private RectTransform _menu, _startMenu, _user, _clock, _calendar;
        private string _reported = "";
        private string _userName;
        private readonly List<RectTransform> _widgets = new List<RectTransform>();

        private readonly Dictionary<uDialog, TaskButton> _buttons = new Dictionary<uDialog, TaskButton>();
        private readonly List<uDialog> _gone = new List<uDialog>();
        private readonly List<TaskButton> _row = new List<TaskButton>();

        private float _canvasScale = 1f;
        private float _barHeight;
        private UI_Theme _theme;

        // Reused every frame: a layout that allocates is a layout that runs once a frame and
        // hands the collector something to do for the rest of it.
        private float[] _leftWidths = new float[4];
        private Slot[] _leftSlots = new Slot[4];
        private float[] _rightWidths = new float[16];
        private Slot[] _rightSlots = new Slot[16];
        private Slot[] _taskSlots = new Slot[64];

        // ---- coming and going ---------------------------------------------------------------

        internal static void Tick()
        {
            if (_applied == null)
            {
                // Read once, at the only moment it can be acted on. Adopting reparents the
                // game's own widgets and switches its layout groups off, so a session that
                // has one bar keeps it: turning the setting off puts the bar back at the next
                // start, not mid-frame.
                if (!UwUTermPlugin.FeatureDesktop.Value) return;

                var bar = new TopBar();
                if (!bar.Adopt()) return;

                _applied = bar;
                UwUTermPlugin.Log.LogInfo("topbar: the mod's own bar is drawing the desktop");
            }

            _applied.Frame();

            // Asked for by hand, so it answers for the moment somebody is looking at the bar
            // rather than for a moment the log chose.
            if (UwUTermPlugin.DumpBar.Value.IsDown()) _applied.Dump();
        }

        /// <summary>
        /// Where every part of the bar is actually drawn, in screen pixels.
        ///
        /// The layout says where it put things; this says where they ended up. A row that is
        /// laid out at 154 and drawn at 0 is the difference between those two, and no amount of
        /// reading the first one would ever show it.
        /// </summary>
        private void Dump()
        {
            var sb = new System.Text.StringBuilder(2048);
            sb.Append($"topbar: asked for by hand - {UnityEngine.Screen.width}px wide at ")
              .Append($"x{_canvasScale:F2}, bar {_barHeight:F0}px\n");

            Say(sb, "menu", _menu);
            Say(sb, "user", _user);
            Say(sb, "strip", _strip);
            foreach (RectTransform widget in _widgets) Say(sb, "widget", widget);
            Say(sb, "clock", _clock);

            for (int i = 0; i < _row.Count; i++) _row[i].Say(sb, i);

            Crowd(sb);
            UwUTermPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// Everything drawn over the left end of the bar, whatever it belongs to.
        ///
        /// The parts above are the ones we place, and they can all be where they should be while
        /// something else is drawn on top of them. Naming what is actually in that space costs a
        /// walk of the desktop and settles which of the two it is - a button of ours in the wrong
        /// place, or an object nobody here has ever accounted for.
        /// </summary>
        private void Crowd(System.Text.StringBuilder sb)
        {
            Transform desktop = _bar.parent;
            if (desktop == null) return;

            float edge = _leftSlots[1].Right;
            sb.Append($"topbar:   drawn over the first {edge:F0}px, top {_barHeight:F0}px -\n");

            int found = 0;
            foreach (Graphic graphic in desktop.GetComponentsInChildren<Graphic>(true))
            {
                if (!graphic.gameObject.activeInHierarchy) continue;

                RectTransform rect = graphic.rectTransform;
                if (WorldEdge.Left(rect) >= edge) continue;
                if (WorldEdge.Right(rect) <= 0f) continue;

                // Only what is up here with the bar. The desktop below is full of things that
                // start left of this and have nothing to do with it.
                if (WorldEdge.Bottom(rect) >= UnityEngine.Screen.height) continue;
                if (WorldEdge.Top(rect) <= UnityEngine.Screen.height - _barHeight) continue;

                if (++found > 24) { sb.Append("topbar:     ... and more\n"); break; }

                sb.Append($"topbar:     '{Path(rect, desktop)}' <{graphic.GetType().Name}> ")
                  .Append($"x {WorldEdge.Left(rect):F0}..{WorldEdge.Right(rect):F0}")
                  .Append(graphic is TMP_Text text ? $" text '{text.text}'" : "")
                  .Append('\n');
            }

            if (found == 0) sb.Append("topbar:     nothing\n");
        }

        private static string Path(Transform part, Transform root)
        {
            string path = part.name;
            for (Transform t = part.parent; t != null && t != root; t = t.parent)
                path = t.name + "/" + path;

            return path;
        }

        private static void Say(System.Text.StringBuilder sb, string what, RectTransform item)
        {
            if (item == null) { sb.Append($"topbar:   {what} - missing\n"); return; }

            sb.Append($"topbar:   {what} '{item.name}' ")
              .Append(item.gameObject.activeSelf ? "" : "[off] ")
              .Append($"drawn {WorldEdge.Left(item):F0}..{WorldEdge.Right(item):F0}, ")
              .Append($"rect {item.rect.width:F0}x{item.rect.height:F0} ")
              .Append($"at ({item.anchoredPosition.x:F0},{item.anchoredPosition.y:F0}) ")
              .Append($"scale {item.lossyScale.x:F2}\n");
        }

        /// <summary>The theme the player chose, handed over whenever they change it.</summary>
        internal static void Paint(UI_Theme theme)
        {
            if (_applied == null || theme == null) return;

            _applied._theme = theme;
            foreach (TaskButton button in _applied._buttons.Values) button.Paint(theme);
        }

        /// <summary>What the bar takes off the top of the desktop, in the units the desktop is
        /// laid out in - a maximised window keeps out from under it by this much.</summary>
        internal static float TopInset =>
            _applied != null && _applied._canvasScale > 0.01f
                ? _applied._barHeight / _applied._canvasScale
                : 0f;

        private bool Adopt()
        {
            _tasks = uDialog_TaskBar.Singleton;
            if (_tasks == null) return false;

            _strip = _tasks.transform as RectTransform;
            Transform desktop = _strip != null ? _strip.parent : null;
            if (desktop == null) return false;

            _bar = Find(desktop, BarName);
            if (_bar == null) return false;

            var canvas = _bar.GetComponentInParent<Canvas>();
            if (canvas == null) return false;

            _canvasScale = canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f;

            // Our own space, inside the game's bar so that everything we host stays where the
            // game looks for it - DesktopFinder paints the clock and the user name by searching
            // its own children, and a bar hosted somewhere else would quietly break that.
            var root = new GameObject(RootName, typeof(RectTransform));
            _root = (RectTransform)root.transform;
            _root.SetParent(_bar, false);
            _root.anchorMin = _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 1f);
            _root.anchoredPosition = Vector2.zero;

            _calendar = Find(desktop, CalendarPopup);
            _menu = Find(_bar, MenuButton);
            _startMenu = Find(_bar, StartMenu);
            _clock = FindDeep(_bar, Clock);
            _user = FindDeep(_bar, UserName);

            RectTransform icons = FindDeep(_bar, IconGrid);
            if (icons != null)
                for (int i = icons.childCount - 1; i >= 0; i--)
                    if (icons.GetChild(i) is RectTransform widget)
                        _widgets.Insert(0, widget);

            HideUserIcon();
            Brand();

            Host(_menu);
            Host(_user);
            foreach (RectTransform widget in _widgets) Host(widget);
            Host(_clock);

            // The strip keeps being the game's own TaskBar object - the task switcher finds the
            // buttons by asking it for them, so ours have to be its children - and it stays
            // exactly where it was. Its parent is the game's word for "the desktop": ten places
            // spawn a window with uDialog_TaskBar.transform.parent, so an object moved out of the
            // desktop takes every window in the game with it, into whatever it was moved into.
            //
            // What it stops doing is laying its children out, and drawing a bar of its own.
            var group = _strip.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (group != null) group.enabled = false;

            _stripImage = _strip.GetComponent<Image>();

            // The strip stays where the game put it, along the bottom, because moving it is what
            // took every window in the game with it. Only the row inside it comes up to the top,
            // so what is left down there is a bar's background with nothing in it.
            _icons = Find(desktop, IconGrid2);
            Reclaim();

            // The strip's own transform is left exactly as the game has it. Something writes its
            // position after we do - our writes come back zeroed the next frame - and the
            // likeliest hand is an animator holding a clip's last frame, which this mod has met
            // once already in uDialog. Rather than argue with it every frame, the row lives in a
            // container of ours inside it, placed by measuring where the strip actually ended up.
            // GetComponentsInChildren is recursive, so the task switcher still finds the buttons.
            var row = new GameObject("UwUTerm.Row", typeof(RectTransform));
            _rowRoot = (RectTransform)row.transform;
            _rowRoot.SetParent(_strip, false);
            _rowRoot.anchorMin = _rowRoot.anchorMax = new Vector2(0f, 1f);
            _rowRoot.pivot = new Vector2(0f, 1f);

            SayWhoElseMoves(_strip);

            // The empty group the widgets came out of would otherwise keep sizing itself to
            // nothing in the corner of the screen.
            RectTransform panel = Find(_bar, Widgets);
            if (panel != null) panel.gameObject.SetActive(false);

            _theme = OS.GetThemeFromFile();
            return true;
        }

        /// <summary>
        /// The avatar beside the user's name is not drawn.
        ///
        /// It is not a child of the label - UserNameBar holds a reference to one living over
        /// with the notification icons - so a bar that moves the name and not the icon leaves it
        /// behind among the widgets, which is where it turns up next to the clock. Deactivating
        /// it takes it out of the row entirely: a widget with no width is given no slot and no
        /// gap, so the rest close up as though it had never been there.
        /// </summary>
        private void HideUserIcon()
        {
            var widget = _user != null ? _user.GetComponent<UserNameBar>() : null;
            if (widget == null || widget.iconImg == null) return;

            widget.iconImg.gameObject.SetActive(false);
        }

        /// <summary>
        /// Put the mod's own mark on the start menu button.
        ///
        /// The game's is a sprite the size the game draws it, so a bar taller than the one it was
        /// authored for enlarges it. Ours is carried inside the dll rather than installed beside
        /// it, at a size no bar is going to run out of.
        ///
        /// Which image on the button is the mark rather than its background is not something to
        /// assume: the smallest one with a sprite in it is, and the log says which object that
        /// turned out to be so a wrong guess is visible rather than merely wrong.
        /// </summary>
        private void Brand()
        {
            if (!UwUTermPlugin.MenuIcon.Value || _menu == null) return;

            Sprite mark = Mark();
            if (mark == null) return;

            Image smallest = null;
            foreach (Image image in _menu.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite == null) continue;
                if (smallest == null || Area(image) < Area(smallest)) smallest = image;
            }

            if (smallest == null) return;

            UwUTermPlugin.Log.LogInfo(
                $"topbar: the menu button's mark is '{smallest.name}', was '{smallest.sprite.name}'");

            smallest.sprite = mark;
            smallest.preserveAspect = true;
        }

        private static float Area(Image image) =>
            image.rectTransform.rect.width * image.rectTransform.rect.height;

        /// <summary>Our icon, read out of the assembly once.</summary>
        private static Sprite Mark()
        {
            if (_mark != null) return _mark;

            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (System.IO.Stream stream = assembly.GetManifestResourceStream("UwUTerm.Icon.png"))
                {
                    if (stream == null)
                    {
                        UwUTermPlugin.Log.LogWarning("topbar: the menu icon is not in the assembly");
                        return null;
                    }

                    var bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);

                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true);
                    if (!texture.LoadImage(bytes))
                    {
                        Object.Destroy(texture);
                        return null;
                    }

                    texture.name = "UwUTerm.Icon";
                    texture.wrapMode = TextureWrapMode.Clamp;
                    texture.filterMode = FilterMode.Trilinear;
                    texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);

                    _mark = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                                          new Vector2(0.5f, 0.5f));
                    _mark.name = "UwUTerm.Icon";
                }
            }
            catch (System.Exception e)
            {
                UwUTermPlugin.Log.LogWarning("topbar: could not read the menu icon - " + e.Message);
            }

            return _mark;
        }

        private static Sprite _mark;

        /// <summary>
        /// Take an object into our space without changing how big it looks.
        ///
        /// Inside the root a unit is a pixel, so an object authored in canvas units would come
        /// out smaller by exactly the factor the root undoes. Scaling it back by the same factor
        /// leaves it the size the player already knows, and leaves the game's own UI-size setting
        /// deciding that size.
        /// </summary>
        private void Host(RectTransform item)
        {
            if (item == null) return;

            item.SetParent(_root, false);
            item.anchorMin = item.anchorMax = new Vector2(0f, 1f);
            item.pivot = new Vector2(0f, 1f);
            item.localScale = Vector3.one * (_canvasScale * UwUTermPlugin.BarScale.Value);
        }

        // ---- every frame ----------------------------------------------------------------------

        private void Frame()
        {
            if (_root == null || _bar == null) return;

            Verify();
            Hide();
            Rescale();
            Sync();
            FitUserName();
            Place();
            KeepOnTop();
            Tint();
        }

        /// <summary>
        /// Keep the empty bar at the bottom out of sight.
        ///
        /// Re-asserted rather than switched off once: whatever writes the strip's position every
        /// frame is bound to the same object, and a thing that can be turned back on is worth
        /// checking rather than assuming. The check costs a comparison and the write happens only
        /// when something else has been at it.
        /// </summary>
        private void Hide()
        {
            if (_stripImage != null && _stripImage.enabled) _stripImage.enabled = false;
        }

        /// <summary>The icon grid was inset to clear a bar at each end of the desktop. With the
        /// windows listed along the top, the strip at the bottom is desktop again.</summary>
        private void Reclaim()
        {
            if (_icons == null) return;

            _icons.offsetMin = new Vector2(_icons.offsetMin.x, 0f);
        }

        /// <summary>
        /// Check last frame's work before doing this frame's.
        ///
        /// Everything written here is read back in the same tick, so a report of where the bar
        /// put something is really a report of what was written - and a write that is undone
        /// afterwards, by a layout rebuild at the end of the frame or by anything else that runs
        /// later, reads back as though it had held. Asking at the start of the next frame is the
        /// only moment the answer is the screen's rather than ours.
        /// </summary>
        private void Verify()
        {
            if (float.IsNaN(_placed)) return;

            float now = WorldEdge.Left(_rowRoot);
            if (Mathf.Abs(now - _placed) < 1f) { _drifted = false; return; }

            if (_drifted) return;

            _drifted = true;
            UwUTermPlugin.Log.LogWarning(
                $"topbar: the strip was put at {_placed:F0} and came back at {now:F0} - " +
                $"something moves it after we do. The row is at " +
                $"({_rowRoot.anchoredPosition.x:F0},{_rowRoot.anchoredPosition.y:F0}) " +
                $"scale {_rowRoot.localScale.x:F2}/{_rowRoot.lossyScale.x:F2}, inside a strip " +
                $"drawn from {WorldEdge.Left(_strip):F0} at " +
                $"({_strip.anchoredPosition.x:F0},{_strip.anchoredPosition.y:F0}) " +
                $"with {Layouts(_strip)}");
        }

        /// <summary>
        /// Anything up the chain that writes transforms of its own accord.
        ///
        /// An animator holding the last frame of a clip rewrites what it animates every frame,
        /// after everything else has run, and leaves no trace in code anybody can read. If one is
        /// bound to the bar it is named here once, so the next person meets it as a fact rather
        /// than as a mystery.
        /// </summary>
        private static void SayWhoElseMoves(RectTransform rect)
        {
            for (Transform t = rect; t != null; t = t.parent)
            {
                var animator = t.GetComponent<Animator>();
                if (animator == null) continue;

                UwUTermPlugin.Log.LogInfo(
                    $"topbar: '{t.name}' has an animator - enabled {animator.enabled}, controller " +
                    $"'{(animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "none")}'");
            }
        }

        /// <summary>Every layout component in play on an object and its parent, enabled or not -
        /// the one that moves something after we place it is in this list.</summary>
        private static string Layouts(RectTransform rect)
        {
            var found = new System.Text.StringBuilder();

            foreach (Behaviour part in rect.GetComponents<Behaviour>())
                if (part is LayoutGroup or ContentSizeFitter or LayoutElement)
                    found.Append($"{part.GetType().Name}({(part.enabled ? "on" : "off")}) ");

            if (rect.parent != null)
                foreach (Behaviour part in rect.parent.GetComponents<Behaviour>())
                    if (part is LayoutGroup or ContentSizeFitter or LayoutElement)
                        found.Append($"parent's {part.GetType().Name}({(part.enabled ? "on" : "off")}) ");

            return found.Length > 0 ? found.ToString() : "no layout components";
        }

        private float _placed = float.NaN;
        private bool _drifted;

        /// <summary>
        /// Paint each part of the bar a colour of its own.
        ///
        /// When a measurement and a screenshot disagree about where something is, one of them is
        /// describing an object the other one is not. Colouring the objects settles which: a
        /// picture of a yellow rectangle at the left edge of the screen is the first window
        /// button, wherever any rect claims it is, and a blue strip that starts somewhere other
        /// than where the row of buttons starts says the two came apart.
        /// </summary>
        private void Tint()
        {
            bool on = UwUTermPlugin.BarTint.Value;
            if (!on && !_tinted) return;

            _tinted = on;

            var strip = _strip.GetComponent<Image>();
            if (strip != null)
            {
                strip.enabled = on;
                if (on) strip.color = new Color(0.2f, 0.4f, 1f, 0.35f);
            }

            var menu = _menu != null ? _menu.GetComponent<Image>() : null;
            if (menu != null) menu.color = on ? new Color(1f, 0.2f, 0.2f, 1f) : Color.white;

            var user = _user != null ? _user.GetComponent<TMP_Text>() : null;
            if (user != null && on) user.color = new Color(0.2f, 1f, 0.3f, 1f);

            for (int i = 0; i < _row.Count; i++) _row[i].Tint(on && i == 0);
        }

        private bool _tinted;

        /// <summary>
        /// Keep the bar in front of the windows.
        ///
        /// Windows are siblings of the bar in the desktop and focusing one sends it to the end
        /// of that list, so any window drawn over the bar's own strip is simply a later sibling.
        /// The game could live with that - its bar was along the bottom, where a window that
        /// covered it was a window the player had dragged there - but a bar along the top is
        /// exactly where windows are, and a taskbar that a window can hide is not a taskbar.
        ///
        /// Only when something is actually above it. Re-ordering siblings dirties the canvas,
        /// which is the cost the drag patch exists to avoid, so this is a comparison every frame
        /// and a move only when a window has just been raised past it.
        /// </summary>
        private void KeepOnTop()
        {
            Transform desktop = _bar.parent;
            if (desktop == null) return;

            int top = Mathf.Max(_bar.GetSiblingIndex(), _strip.GetSiblingIndex());

            foreach (uDialog window in _tasks.Tasks)
            {
                if (window == null || !window.gameObject.activeInHierarchy) continue;
                if (window.transform.parent != desktop) continue;
                if (window.transform.GetSiblingIndex() <= top) continue;

                // The strip after the bar, so the buttons are drawn over its background rather
                // than under it.
                _bar.SetAsLastSibling();
                _strip.SetAsLastSibling();
                return;
            }
        }

        /// <summary>Follow the screen and the player's UI-size setting. Both can change while the
        /// game is running, and a bar measured once would be wrong the moment either did.</summary>
        private void Rescale()
        {
            var canvas = _bar.GetComponentInParent<Canvas>();
            float scale = canvas != null && canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f;
            float wanted = UwUTermPlugin.BarScale.Value;

            _barHeight = UwUTermPlugin.BarHeight.Value * wanted;

            if (!Mathf.Approximately(scale, _canvasScale))
            {
                _canvasScale = scale;
                Host(_menu);
                Host(_user);
                foreach (RectTransform widget in _widgets) Host(widget);
                Host(_clock);
            }

            _root.localScale = Vector3.one / _canvasScale;

            _root.sizeDelta = new Vector2(UnityEngine.Screen.width, _barHeight);

            // The game's bar is our background, and it is themed for us. It only has to be as
            // tall as we are, in the units it is authored in.
            float height = _barHeight / _canvasScale;
            if (!Mathf.Approximately(_bar.sizeDelta.y, height))
                _bar.sizeDelta = new Vector2(_bar.sizeDelta.x, height);

            _layout.Width = UnityEngine.Screen.width;
            _layout.Gap = Mathf.Max(0f, UwUTermPlugin.BarGap.Value) * wanted;
            _layout.Padding = _layout.Gap;
            _layout.MenuGap = Mathf.Max(0f, UwUTermPlugin.MenuGap.Value) * wanted;
            _layout.TaskWidth = UwUTermPlugin.TaskWidth.Value * wanted;
            _layout.MinTaskWidth = UwUTermPlugin.MinTaskWidth.Value * wanted;
        }

        /// <summary>
        /// One button per open window, kept rather than rebuilt.
        ///
        /// The game's bar destroyed every button and instantiated a fresh one whenever anything
        /// happened to any window, focus included - a GameObject per window per event. A button
        /// here is made when its window opens and destroyed when it closes, and in between it is
        /// told what changed.
        /// </summary>
        private void Sync()
        {
            List<uDialog> open = _tasks.Tasks;

            _gone.Clear();
            foreach (KeyValuePair<uDialog, TaskButton> pair in _buttons)
                if (pair.Key == null || !open.Contains(pair.Key)) _gone.Add(pair.Key);

            foreach (uDialog closed in _gone)
            {
                if (_buttons.TryGetValue(closed, out TaskButton button)) button.Destroy();
                _buttons.Remove(closed);
            }

            _row.Clear();
            foreach (uDialog window in open)
            {
                if (window == null) continue;

                if (!_buttons.TryGetValue(window, out TaskButton button))
                {
                    button = TaskButton.Make(_rowRoot, window, _tasks, Font(), _theme);
                    _buttons[window] = button;
                }

                button.Follow(window == _tasks.CurrentTask, _theme);
                _row.Add(button);
            }
        }

        private void Place()
        {
            Grow(ref _leftWidths, ref _leftSlots, 2);
            Grow(ref _rightWidths, ref _rightSlots, _widgets.Count + 1);
            if (_taskSlots.Length < _row.Count) _taskSlots = new Slot[Mathf.NextPowerOfTwo(_row.Count)];

            _leftWidths[0] = WidthOf(_menu);
            _leftWidths[1] = WidthOf(_user);
            float left = _layout.PlaceLeft(_leftWidths, 2, _leftSlots);

            // Outermost first, so the clock takes the corner and the widgets fill in beside it
            // in the order they are drawn.
            float pad = UwUTermPlugin.WidgetPadding.Value * UwUTermPlugin.BarScale.Value * 2f;

            _rightWidths[0] = WidthOf(_clock);
            for (int i = 0; i < _widgets.Count; i++)
            {
                float width = WidthOf(_widgets[_widgets.Count - 1 - i]);
                _rightWidths[i + 1] = width > 0f ? width + pad : 0f;
            }

            float right = _layout.PlaceRight(_rightWidths, _widgets.Count + 1, _rightSlots);

            Put(_menu, _leftSlots[0]);
            Put(_user, _leftSlots[1]);
            Put(_clock, _rightSlots[0]);
            for (int i = 0; i < _widgets.Count; i++)
                Put(_widgets[_widgets.Count - 1 - i], _rightSlots[i + 1]);

            // The start menu and the calendar drop out of things we have moved, and both are laid
            // out in the bar's own units rather than in pixels - they are not inside our root.
            // Their pivots are their own, so an edge is asked for and the pivot worked back from,
            // rather than a position being written as though every pivot were the same.
            Drop(_startMenu, _leftSlots[0].X, fromRight: false);
            Drop(_calendar, _rightSlots[0].Right, fromRight: true);

            float from = left + _layout.Gap;
            float to = right - _layout.Gap;

            // Measured rather than assumed: wherever the strip has been put, the row lands on
            // the pixel it should. Nothing here needs the strip's own position, size or scale to
            // be anything in particular, which is what makes it safe to leave alone.
            float scale = Mathf.Abs(_strip.lossyScale.x) > 0.001f ? _strip.lossyScale.x : 1f;
            float stripLeft = WorldEdge.Left(_strip);
            float stripTop = WorldEdge.Top(_strip);

            _rowRoot.localScale = Vector3.one / scale;
            _rowRoot.anchoredPosition = new Vector2(
                (from - stripLeft) / scale,
                (UnityEngine.Screen.height - stripTop) / scale);
            _rowRoot.sizeDelta = new Vector2(Mathf.Max(0f, to - from), _barHeight);
            _placed = from;

            _layout.PlaceTasks(_row.Count, 0f, Mathf.Max(0f, to - from), _taskSlots);
            for (int i = 0; i < _row.Count; i++) _row[i].Put(_taskSlots[i], _barHeight);

            Report(left, right, from, to);
        }

        /// <summary>
        /// What the bar worked out, in the pixels it is drawn at. Written when it changes rather
        /// than every frame, and in one line, because the whole point of laying the bar out in
        /// pixels is that reading it back should settle an argument about the screen.
        /// </summary>
        private void Report(float left, float right, float from, float to)
        {
            if (!UwUTermPlugin.ScreenDebug.Value) return;

            string now = $"topbar: {UnityEngine.Screen.width}px wide at x{_canvasScale:F2}, " +
                         $"bar {_barHeight:F0}px, gap {_layout.Gap:F0} - " +
                         $"menu {_leftSlots[0].X:F0}..{_leftSlots[0].Right:F0}, " +
                         $"user {_leftSlots[1].X:F0}..{_leftSlots[1].Right:F0} (ends {left:F0}), " +
                         $"strip {from:F0}..{to:F0} holding {_row.Count} of {_layout.TaskWidth:F0}px, " +
                         $"widgets from {right:F0}, clock {_rightSlots[0].X:F0}..{_rightSlots[0].Right:F0}";

            if (now == _reported) return;

            _reported = now;
            UwUTermPlugin.Log.LogInfo(now);
        }

        /// <summary>
        /// Hang a panel off the bar at a pixel position, in the units it is laid out in.
        ///
        /// A panel anchored to the bar's top-left is positioned by its own pivot, so writing a
        /// left edge into anchoredPosition puts a centre-pivoted panel half its width off to the
        /// side - which is a start menu drawn past the edge of the screen. The edge is asked for
        /// and the pivot worked back from it.
        /// </summary>
        private void Drop(RectTransform panel, float edge, bool fromRight)
        {
            if (panel == null) return;

            Rect area = panel.rect;
            float x = edge / _canvasScale;
            float top = _barHeight / _canvasScale;

            panel.anchorMin = panel.anchorMax = new Vector2(fromRight ? 1f : 0f, 1f);
            panel.anchoredPosition = new Vector2(
                fromRight
                    ? x - UnityEngine.Screen.width / _canvasScale - area.width * (1f - panel.pivot.x)
                    : x + area.width * panel.pivot.x,
                -top - area.height * (1f - panel.pivot.y));
        }

        /// <summary>An adopted object's width in pixels: what it was authored as, at the size it
        /// is actually drawn.</summary>
        private float WidthOf(RectTransform item) =>
            item == null || !item.gameObject.activeSelf
                ? 0f
                : item.rect.width * _canvasScale * UwUTermPlugin.BarScale.Value;

        /// <summary>Centre a thing in the room it was given, so a widget with padding around it
        /// sits in the middle of that padding rather than against one edge of it.</summary>
        private void Put(RectTransform item, Slot slot)
        {
            if (item == null || slot.Width <= 0f) return;

            float scale = _canvasScale * UwUTermPlugin.BarScale.Value;
            float width = item.rect.width * scale;
            float height = item.rect.height * scale;

            item.anchoredPosition = new Vector2(
                slot.X + (slot.Width - width) * 0.5f,
                -(_barHeight - height) * 0.5f);
        }

        /// <summary>
        /// The text object the user name is drawn by. <c>UserNameBar</c> names it, so that is what
        /// is asked rather than the object we happen to host - a name drawn by a child of it is a
        /// rect that would otherwise be measured and never sized.
        /// </summary>
        private TMP_Text UserLabel()
        {
            var widget = _user != null ? _user.GetComponent<UserNameBar>() : null;
            if (widget != null && widget.nameUser != null) return widget.nameUser;

            return _user != null ? _user.GetComponent<TMP_Text>() : null;
        }

        /// <summary>
        /// Give the user name the width of the name in it.
        ///
        /// The game sizes that label through the layout group it was authored in, and inside our
        /// root nothing does - so it keeps the width it had at the moment it was adopted, and a
        /// longer name than that one is drawn cut off. The width is the text's own, measured
        /// unconstrained: what the label needs, rather than what it is allowed.
        ///
        /// Measured when the name changes rather than every frame. Asking how wide text would be
        /// is the same work as laying it out, and the name changes about as often as the player
        /// does.
        /// </summary>
        private void FitUserName()
        {
            TMP_Text label = UserLabel();
            if (label == null || label.text == _userName) return;

            _userName = label.text;

            float width = label.GetPreferredValues(_userName, Mathf.Infinity, Mathf.Infinity).x;
            RectTransform rect = label.rectTransform;
            rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);

            // The slot is given to the object we host, which is the label itself here. Were the
            // game ever to draw the name through a child, the host has to grow with it or the
            // layout hands the name the room the old one needed.
            if (rect != _user && _user != null)
                _user.sizeDelta = new Vector2(width, _user.sizeDelta.y);
        }

        private TMP_FontAsset Font()
        {
            TMP_Text label = UserLabel();
            return label != null ? label.font : null;
        }

        private static void Grow(ref float[] widths, ref Slot[] slots, int needed)
        {
            if (widths.Length >= needed) return;

            widths = new float[needed];
            slots = new Slot[needed];
        }

        private static RectTransform Find(Transform parent, string name) => parent.Find(name) as RectTransform;

        private static RectTransform FindDeep(Transform parent, string name)
        {
            foreach (RectTransform t in parent.GetComponentsInChildren<RectTransform>(true))
                if (t.name == name) return t;

            return null;
        }
    }
}
