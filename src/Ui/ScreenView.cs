using System.Collections.Generic;
using TerminalPoolSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using frame8.Logic.Misc.Visual.UI.MonoBehaviours;
using UwUTerm.Patches;
using UwUTerm.Screen;
using Util;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Draws one terminal's screen as a single mesh.
    ///
    /// The game gives every line its own TMP_Text, its own ContentSizeFitter and its own
    /// measurement pass, so what it costs to draw grows with how much has been printed - and
    /// a row's height, once measured, is cached on the line forever, which is why text that
    /// changes size afterwards stays in a box built for the old one.
    ///
    /// Here the height of a row is arithmetic: the font's own line height times the point
    /// size. Nothing measures, so nothing can be stale, and only the rows on screen are ever
    /// built. The scrollback stays as the strings the server sent.
    /// </summary>
    internal sealed class ScreenView
    {
        private const string ObjectName = "UwUTerm.Screen";
        private const int MinimumColumns = 20;
        private const float MultiClickSeconds = 0.4f;
        private const float AutoScrollSeconds = 0.05f;

        private readonly TerminalListAdapter _adapter;
        private readonly ScreenGrid _grid = new ScreenGrid();
        private readonly LineSource _lines;

        private GameObject _root;
        private RectTransform _rect;
        private TextMeshProUGUI _label;

        private int _scrollBack;
        private int _lastCount = -1;
        private string _lastLine;
        private int _lastCaret = -1;
        private float _lastWidth, _lastHeight, _lastFontSize;
        private float _advance = 1f, _lineHeight = 1f;
        private bool _dirty = true;

        internal int Columns => _grid.Columns;
        internal int Rows => _grid.Rows;

        private ScreenView(TerminalListAdapter adapter)
        {
            _adapter = adapter;
            _lines = new LineSource(adapter);
        }

        /// <summary>
        /// Build a screen over a terminal's scroll viewport. The label is parented to the
        /// viewport rather than to the content OSA scrolls, so the game's scrolling cannot
        /// drag it away, and the viewport's mask still clips it.
        /// </summary>
        internal static ScreenView Create(TerminalListAdapter adapter)
        {
            RectTransform viewport = adapter != null ? adapter.Viewport : null;
            if (viewport == null) return null;

            var view = new ScreenView(adapter);

            view._root = new GameObject(ObjectName, typeof(RectTransform), typeof(CanvasRenderer));
            view._rect = (RectTransform)view._root.transform;
            view._rect.SetParent(viewport, false);
            view._rect.anchorMin = Vector2.zero;
            view._rect.anchorMax = Vector2.one;

            // Inset the label rather than setting a TMP margin. Everything downstream - the
            // column count, the row count, the caret's position - is measured from this rect,
            // so insetting it once means none of them need to know about the padding.
            float pad = Mathf.Max(0f, UwUTermPlugin.ScreenPadding.Value);
            view._rect.offsetMin = new Vector2(pad, pad);
            view._rect.offsetMax = new Vector2(-pad, -pad);

            view._label = view._root.AddComponent<TextMeshProUGUI>();
            view._label.raycastTarget = false;
            view._label.richText = true;
            view._label.enableWordWrapping = false;
            view._label.overflowMode = TextOverflowModes.Overflow;
            view._label.alignment = TextAlignmentOptions.TopLeft;
            view._label.margin = Vector4.zero;

            view.AdoptFontFrom(adapter);
            view.ApplyTheme();

            // Sits on the viewport, between the rows and OSA, so the wheel stops here.
            view._scroll = viewport.gameObject.AddComponent<ScreenScroll>();
            view._scroll.View = view;

            // OSA does not use a ScrollRect. It implements IScrollRectProxy, and a separate
            // ScrollbarFixer8 - which lives on the scrollbar itself - reads that proxy and
            // writes the handle's value and size every frame. With the wheel intercepted the
            // proxy never moves, so the fixer was writing the handle back to rest as fast as
            // anything could set it: the wheel could not move it, and a drag snapped home.
            //
            // Turning the fixer off hands the scrollbar over completely. Nothing else writes
            // it, so this can drive it and hear drags through its own event.
            view._fixer = FindFixer(viewport);
            if (view._fixer != null)
            {
                view._bar = view._fixer.GetComponent<Scrollbar>();
                view._fixer.enabled = false;
                Reveal(view._fixer.gameObject);
            }

            if (view._bar != null) view._bar.onValueChanged.AddListener(view.OnBarDragged);

            if (UwUTermPlugin.ScreenDebug.Value)
                UwUTermPlugin.Log.LogInfo(
                    $"screen: scrollbar {(view._bar == null ? "not found" : view._bar.name)}, " +
                    $"fixer {(view._fixer == null ? "not found" : "disabled")}");

            if (UwUTermPlugin.ScreenDebug.Value) view.ReportTextObjects(viewport);
            return view;
        }

        /// <summary>
        /// Match whatever the terminal's own rows are using, so the screen follows both the
        /// game's font and TerminalFont without knowing about either.
        ///
        /// Checked every tick rather than once: the first row may not exist yet when a screen
        /// is built, and TerminalFont can swap the face underneath at any time. Returns true
        /// when something changed, because the grid is sized in glyphs and a new face means a
        /// different number of them.
        /// </summary>
        private bool AdoptFontFrom(TerminalListAdapter adapter)
        {
            TMP_Text sample = adapter.GetLastViewLine()?.lineText;
            if (sample == null || sample.font == null) return false;

            if (ReferenceEquals(_label.font, sample.font) &&
                Mathf.Approximately(_label.fontSize, sample.fontSize)) return false;

            _label.font = sample.font;
            _label.fontSize = sample.fontSize;
            return true;
        }

        /// <summary>
        /// Every text object that could be drawing inside this terminal, with the alpha it
        /// actually renders at once its CanvasGroups are taken into account. Two things
        /// drawing the same characters look exactly like one thing drawing them twice, and
        /// this is the only way to tell those apart from outside the game.
        /// </summary>
        private void ReportTextObjects(RectTransform viewport)
        {
            Transform root = viewport.parent != null ? viewport.parent : viewport;

            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (ReferenceEquals(text, _label)) continue;

                string path = text.name;
                for (Transform p = text.transform.parent; p != null && p != root.parent; p = p.parent)
                    path = p.name + "/" + path;

                // A nested Canvas starts its own batch and does not inherit the CanvasGroup
                // above it, so one sitting between a row and the group we set would leave the
                // row drawing at full opacity while the arithmetic below still says zero.
                string nested = NestedCanvas(text.transform, root.parent);

                UwUTermPlugin.Log.LogInfo(
                    $"screen:   text '{path}' active={text.gameObject.activeInHierarchy} " +
                    $"enabled={text.enabled} alpha={EffectiveAlpha(text.transform, root.parent):F2} " +
                    $"canvasBetween={nested} colourAlpha={text.color.a:F2} " +
                    $"chars={(text.text == null ? 0 : text.text.Length)}");
            }
        }

        private static string NestedCanvas(Transform from, Transform stopAt)
        {
            for (Transform t = from; t != null && t != stopAt; t = t.parent)
                if (t.GetComponent<Canvas>() != null) return t.name;

            return "none";
        }

        private static float EffectiveAlpha(Transform from, Transform stopAt)
        {
            float alpha = 1f;
            for (Transform t = from; t != null && t != stopAt; t = t.parent)
            {
                CanvasGroup group = t.GetComponent<CanvasGroup>();
                if (group != null) alpha *= group.alpha;
            }
            return alpha;
        }

        /// <summary>The scrollbar hangs off the same scroll view as the rows, so the search
        /// starts from the object above the viewport rather than inside it.</summary>
        private static ScrollbarFixer8 FindFixer(RectTransform viewport)
        {
            Transform root = viewport.parent != null ? viewport.parent : viewport;
            return root.GetComponentInChildren<ScrollbarFixer8>(true);
        }

        /// <summary>The fixer fades and shrinks an idle scrollbar itself, so one caught
        /// mid-hide would stay that way once it stops running.</summary>
        private static void Reveal(GameObject scrollbar)
        {
            var group = scrollbar.GetComponent<CanvasGroup>();
            if (group != null) group.alpha = 1f;

            scrollbar.transform.localScale = Vector3.one;
        }

        internal void ApplyTheme()
        {
            UI_Theme theme = OS.GetThemeFromFile();
            if (theme != null) _label.color = theme.terminalText;
        }

        internal void Destroy()
        {
            ShowRows();
            if (_scroll != null) Object.Destroy(_scroll);
            if (_bar != null) _bar.onValueChanged.RemoveListener(OnBarDragged);
            if (_fixer != null) _fixer.enabled = true;
            if (_root != null) Object.Destroy(_root);
            _root = null;
        }

        /// <summary>
        /// Make the game's own rows transparent, leaving everything else about them alone.
        ///
        /// Hiding an ancestor instead was the obvious move and the wrong one: a CanvasGroup
        /// above these rows is shared with whatever else the game does to that object - and
        /// uDialog animates windows by driving exactly that property - so the value is
        /// overwritten and the rows come back. Colour is per graphic and nothing else writes
        /// it, so it stays put.
        ///
        /// Transparency rather than disabling, because a disabled graphic contributes no
        /// preferred size, and OSA decides whether a row is worth keeping a views holder for
        /// from its size. Losing the holders would take GetLastViewLine with them, and with it
        /// the font this screen draws in.
        /// </summary>
        /// <summary>
        /// Hide the rows OSA is actually showing.
        ///
        /// Walking every line of the scrollback instead - asking whether each one happened to
        /// be visible - costs the length of the history on every frame, for every terminal
        /// open, and the lookup it calls is itself a scan of the visible set. A few thousand
        /// lines across a few windows turns that into hundreds of thousands of steps a frame,
        /// which is felt as a delay whenever anything else wants the main thread.
        ///
        /// OSA already keeps the visible rows in a list, and there are only ever a screenful.
        /// </summary>
        private void HideRows()
        {
            int visible = _adapter.VisibleItemsCount;
            for (int i = 0; i < visible; i++)
                HideHolder(_adapter.GetItemViewsHolder(i));
        }

        /// <summary>
        /// Hide one row, called the moment it is built as well as every tick.
        ///
        /// Waiting for the next tick leaves a freshly created row drawing at full opacity for
        /// one frame, which is a visible flash on the line every time you press Enter - the
        /// row exists before anything of ours has had a chance to run.
        /// </summary>
        internal void HideHolder(TerminalListItemViewsHolder holder)
        {
            if (holder == null) return;

            Hide(holder.lineText);
            Hide(holder.selection);
            Hide(holder.subSelectionMid);
            Hide(holder.subSelectionLast);
        }

        private void Hide(Component target)
        {
            Graphic graphic = Graphic(target);
            if (graphic == null) return;

            Color colour = graphic.color;
            if (colour.a == 0f) return;

            // Remembered the first time each graphic is faded. The row text and the three
            // selection rectangles are set to different alphas by the game, so restoring them
            // all to one value - the theme's, say - would quietly change how selection looks.
            if (!_alphas.ContainsKey(graphic)) _alphas[graphic] = colour.a;

            graphic.color = new Color(colour.r, colour.g, colour.b, 0f);
        }

        private void ShowRows()
        {
            foreach (KeyValuePair<Graphic, float> entry in _alphas)
            {
                if (entry.Key == null) continue;

                Color colour = entry.Key.color;
                entry.Key.color = new Color(colour.r, colour.g, colour.b, entry.Value);
            }
            _alphas.Clear();
        }

        private static Graphic Graphic(Component target) =>
            target == null ? null : target.GetComponent<Graphic>();

        internal void MarkDirty() => _dirty = true;

        /// <summary>Scroll by whole display rows, clamped so there is always something on
        /// screen and so the newest line cannot be scrolled past.</summary>
        internal void Scroll(int rows)
        {
            int limit = Mathf.Max(0, _grid.TotalRows - _grid.Rows);
            int next = Mathf.Clamp(_scrollBack + rows, 0, limit);

            if (UwUTermPlugin.ScreenDebug.Value)
                UwUTermPlugin.Log.LogInfo(
                    $"screen: scroll {rows:+#;-#;0} back={_scrollBack}->{next} limit={limit} " +
                    $"total={_grid.TotalRows} rows={_grid.Rows} lines={_lines.Count}");

            if (next == _scrollBack) return;

            _scrollBack = next;
            _dirty = true;
        }

        /// <summary>
        /// Show where the view sits and how much of the scrollback it covers.
        ///
        /// The handle's value is guarded while it is being written, because writing it raises
        /// the same change event that dragging does - without that, every frame would feed its
        /// own position back in as if the player had moved it.
        /// </summary>
        private void DriveBar()
        {
            if (_bar == null) return;

            int limit = Mathf.Max(0, _grid.TotalRows - _grid.Rows);

            _drivingBar = true;
            _bar.size = _grid.TotalRows > 0 ? Mathf.Clamp01((float)_grid.Rows / _grid.TotalRows) : 1f;
            _bar.value = limit > 0 ? Mathf.Clamp01((float)_scrollBack / limit) : 0f;
            _drivingBar = false;
        }

        /// <summary>Bottom of the bar is the newest line, matching what the scroll rect meant
        /// by the same position before the grid took the bar over.</summary>
        private void OnBarDragged(float value)
        {
            if (_drivingBar) return;

            int limit = Mathf.Max(0, _grid.TotalRows - _grid.Rows);
            int next = Mathf.Clamp(Mathf.RoundToInt(value * limit), 0, limit);
            if (next == _scrollBack) return;

            _scrollBack = next;
            _dirty = true;
        }

        internal void ScrollToEnd()
        {
            if (_scrollBack == 0) return;
            _scrollBack = 0;
            _dirty = true;
        }

        private readonly Dictionary<Graphic, float> _alphas = new Dictionary<Graphic, float>();
        private Image _caret;
        private readonly List<Image> _outline = new List<Image>();
        private readonly List<Image> _highlights = new List<Image>();
        private ScreenSpan _selection;
        private bool _dragging;
        private int _clicks;
        private float _lastClickAt;
        private ScreenPoint _lastClickWhere = ScreenPoint.None;

        /// <summary>What a drag moves by: single characters, whole words after a double click,
        /// whole lines after a triple.</summary>
        private enum Grain { Character, Word, Line }

        private Grain _grain;
        private ScreenSpan _origin;
        private float _nextAutoScroll;
        private ScreenScroll _scroll;
        private Scrollbar _bar;
        private ScrollbarFixer8 _fixer;
        private bool _drivingBar;
        private bool _warnedAboutFont;
        private const int MaxDumps = 4;
        private int _dumps;
        private string _lastDump;

        internal void Tick()
        {
            if (_root == null || _adapter == null) return;

            if (AdoptFontFrom(_adapter)) _dirty = true;

            // Without a face there is nothing to draw with, and the screen would come up
            // blank with no other symptom. Say so rather than letting it look like the grid
            // is empty.
            if (_label.font == null && !_warnedAboutFont)
            {
                _warnedAboutFont = true;
                UwUTermPlugin.Log.LogWarning(
                    "screen: no font could be taken from the terminal's rows, so nothing will draw");
            }

            HideRows();

            if (Resized() || ContentChanged()) _dirty = true;
            if (_dirty)
            {
                _dirty = false;
                Compose();
            }

            Mouse();
            PositionCaret();
            PaintSelection();
            DriveBar();
        }

        // ---- change detection ----------------------------------------------------------

        /// <summary>
        /// Polled rather than hooked. Every path that changes the screen - output arriving,
        /// a keystroke, history recall, a completion - ends up writing the model, so watching
        /// the model catches all of them; hooking each writer would be a patch per path and a
        /// new way to break on every game update.
        /// </summary>
        private bool ContentChanged()
        {
            var data = _adapter.Data;
            if (data == null) return false;

            int count = data.Count;
            string last = count > 0 ? data[count - 1].line : null;
            int caret = _adapter.charIndexInput;

            if (count == _lastCount && ReferenceEquals(last, _lastLine) && caret == _lastCaret)
                return false;

            // Whichever lines could have changed have to be measured again. Appending leaves
            // everything before the old end alone; editing touches only the line being typed.
            if (count != _lastCount) _grid.Invalidate(Mathf.Max(0, Mathf.Min(count, _lastCount) - 1));
            else _grid.Invalidate(count - 1);

            // New output means the user wants to see it, the same way a real terminal snaps
            // back to the bottom when something prints.
            if (count != _lastCount) _scrollBack = 0;

            _lastCount = count;
            _lastLine = last;
            _lastCaret = caret;
            return true;
        }

        private bool Resized()
        {
            float pad = Mathf.Max(0f, UwUTermPlugin.ScreenPadding.Value);
            if (!Mathf.Approximately(pad, _rect.offsetMin.x))
            {
                _rect.offsetMin = new Vector2(pad, pad);
                _rect.offsetMax = new Vector2(-pad, -pad);
            }

            Rect area = _rect.rect;
            if (Mathf.Approximately(area.width, _lastWidth) &&
                Mathf.Approximately(area.height, _lastHeight) &&
                Mathf.Approximately(_label.fontSize, _lastFontSize))
                return false;

            _lastWidth = area.width;
            _lastHeight = area.height;
            _lastFontSize = _label.fontSize;

            _advance = Advance();
            _lineHeight = LineHeight();

            _grid.Resize(
                Mathf.Max(MinimumColumns, Mathf.FloorToInt(area.width / _advance)),
                Mathf.Max(1, Mathf.FloorToInt(area.height / _lineHeight)));
            _grid.InvalidateAll();

            if (UwUTermPlugin.ScreenDebug.Value)
                UwUTermPlugin.Log.LogInfo(
                    $"screen: {_grid.Columns}x{_grid.Rows} in {area.width:F0}x{area.height:F0}px, " +
                    $"line height {_lineHeight:F1}px advance {_advance:F1}px");

            return true;
        }

        // ---- metrics --------------------------------------------------------------------

        /// <summary>
        /// A row is exactly the font's line height at this point size - computed, never
        /// measured, so it cannot disagree with the text sitting in it.
        /// </summary>
        internal float LineHeight()
        {
            TMP_FontAsset asset = _label.font;
            if (asset == null || asset.faceInfo.pointSize <= 0) return Mathf.Max(1f, _label.fontSize * 1.2f);

            float scale = _label.fontSize / asset.faceInfo.pointSize * asset.faceInfo.scale;
            float height = asset.faceInfo.lineHeight * scale;
            return height > 0f ? height : Mathf.Max(1f, _label.fontSize * 1.2f);
        }

        /// <summary>The font is monospace, so one glyph's advance divides the width evenly.
        /// Measured over ten so a rounded advance cannot compound.</summary>
        private float Advance()
        {
            float width = _label.GetPreferredValues("MMMMMMMMMM").x / 10f;
            return width > 0f ? width : Mathf.Max(1f, _label.fontSize * 0.6f);
        }

        // ---- drawing --------------------------------------------------------------------

        private void Compose()
        {
            int caretLine = -1, caretColumn = -1;
            var data = _adapter.Data;

            if (data != null && data.Count > 0)
            {
                caretLine = data.Count - 1;
                string line = data[caretLine].line ?? "";

                // charIndexInput names the character the caret follows, so the caret's raw
                // offset is one past it.
                caretColumn = MarkupReader.RunesBefore(line, _adapter.charIndexInput + 1);
            }

            _grid.Compose(_lines, _scrollBack, caretLine, caretColumn);

            _label.lineSpacing = 0f;
            string markup = ScreenRenderer.Render(_grid, 0u, caretVisible: false);
            _label.text = markup;

            if (UwUTermPlugin.ScreenDebug.Value && _dumps < MaxDumps && markup != _lastDump)
            {
                _dumps++;
                _lastDump = markup;
                UwUTermPlugin.Log.LogInfo(
                    $"screen: compose lines={_lines.Count} total={_grid.TotalRows} grid={_grid.Columns}x{_grid.Rows} " +
                    $"caret={_grid.CaretRow},{_grid.CaretColumn}");
                UwUTermPlugin.Log.LogInfo("screen: text |" + markup.Replace("\n", "\\n") + "|");
            }
        }

        /// <summary>
        /// isCaret says the line is the one being edited, and stays set whether or not anyone
        /// is looking - so on its own it puts a blinking caret in every terminal on the desktop
        /// at once. Focus is what separates the terminal being typed into from the rest.
        /// </summary>
        private bool CaretWanted()
        {
            var data = _adapter.Data;
            return data != null && data.Count > 0 && data[data.Count - 1].isCaret;
        }

        /// <summary>
        /// The caret is its own quad rather than a &lt;mark&gt; on the character under it.
        ///
        /// TMP builds a mark as extra geometry sampling the font's own atlas, which for an
        /// asset created at runtime is populated on demand and carries no guarantee of holding
        /// what that draw needs - so the tag can come out to nothing at all, which is exactly
        /// what it did. A quad of our own depends on none of that, blinks without regenerating
        /// the text, and is the same technique selection highlighting will want.
        /// </summary>
        // ---- selection -------------------------------------------------------------------

        /// <summary>
        /// Polled rather than taken as pointer events.
        ///
        /// Implementing IPointerDownHandler here would put this object first in line for the
        /// click, ahead of everything the game already routes through it - focusing the window
        /// among them. Watching the mouse instead leaves that routing alone.
        /// </summary>
        private void Mouse()
        {
            if (_adapter == null) { _dragging = false; return; }

            // A drag, once started, is not given up because the pointer wandered off the
            // terminal or the window stopped being the focused one. Letting go of the button
            // is the only thing that ends it - which is what makes selecting a word against
            // the very edge of the window possible at all.
            if (_dragging)
            {
                if (Input.GetMouseButton(0))
                {
                    if (Over(out ScreenPoint head, clamp: true)) ExtendTo(head);
                    AutoScroll();
                    return;
                }

                _dragging = false;

                // Selecting fills the primary buffer, the way it does on a Linux desktop, and
                // leaves the system clipboard for an explicit copy.
                if (_selection.Exists) TerminalClipboard.Primary = SelectionText.Extract(_lines, _selection);
                return;
            }

            if (!_adapter.IsFocus()) return;

            if (Input.GetMouseButtonDown(2)) { Paste(TerminalClipboard.Primary); return; }

            if (Input.GetMouseButtonDown(0) && Over(out ScreenPoint start, clamp: false)) Click(start);
        }

        /// <summary>
        /// Walk the view when a drag reaches past the top or bottom edge, so a selection can
        /// run further than one screenful. Stepped on a timer rather than per frame, or the
        /// scrollback would go by far too fast to aim at.
        /// </summary>
        private void AutoScroll()
        {
            if (!PointerIn(out Vector2 local)) return;

            Rect area = _rect.rect;
            int direction = local.y > area.yMax ? 1 : (local.y < area.yMin ? -1 : 0);
            if (direction == 0) return;

            if (Time.unscaledTime < _nextAutoScroll) return;
            _nextAutoScroll = Time.unscaledTime + AutoScrollSeconds;

            Scroll(direction);
        }

        /// <summary>
        /// One click starts a drag, two take the word, three take the line - the same
        /// escalation every terminal uses.
        ///
        /// The run has to break when the pointer moves, or two clicks on opposite sides of the
        /// screen inside the interval would count as a double click. A cell either side is
        /// allowed, since a hand rarely holds still enough to hit the same one twice.
        /// </summary>
        private void Click(ScreenPoint at)
        {
            bool sameSpot = _lastClickWhere.Exists && _lastClickWhere.Line == at.Line &&
                            Mathf.Abs(_lastClickWhere.Rune - at.Rune) <= 1;

            _clicks = (Time.unscaledTime - _lastClickAt <= MultiClickSeconds && sameSpot) ? _clicks + 1 : 1;
            if (_clicks > 3) _clicks = 1;

            _lastClickAt = Time.unscaledTime;
            _lastClickWhere = at;

            switch (_clicks)
            {
                case 2:
                    _grain = Grain.Word;
                    _selection = SelectionText.Word(_lines, at);
                    break;

                case 3:
                    _grain = Grain.Line;
                    _selection = SelectionText.Line(_lines, at.Line);
                    break;

                default:
                    _grain = Grain.Character;
                    _selection = new ScreenSpan { Anchor = at, Head = at };
                    break;
            }

            // Dragging carries on from a double or triple click rather than starting again, so
            // the selection grows a word or a line at a time.
            _origin = _selection;
            _dragging = true;
        }

        /// <summary>
        /// Grow the selection to wherever the pointer is, in whatever unit the click chose.
        ///
        /// The unit first clicked always stays selected whole: dragging left from the middle of
        /// a word does not cut that word in half, it keeps it and adds what comes before. That
        /// is why the original span is kept rather than just its starting point.
        /// </summary>
        private void ExtendTo(ScreenPoint head)
        {
            if (_grain == Grain.Character)
            {
                _selection.Head = head;
                return;
            }

            ScreenSpan reached = _grain == Grain.Word
                ? SelectionText.Word(_lines, head)
                : SelectionText.Line(_lines, head.Line);

            if (!reached.Exists) { _selection.Head = head; return; }

            _origin.Ordered(out ScreenPoint originFrom, out ScreenPoint originTo);
            reached.Ordered(out ScreenPoint reachedFrom, out ScreenPoint reachedTo);

            // Anchor at the far end of the original unit so the drag reads in the direction it
            // is going.
            _selection = reachedFrom.Before(originFrom)
                ? new ScreenSpan { Anchor = originTo, Head = reachedFrom }
                : new ScreenSpan { Anchor = originFrom, Head = reachedTo };
        }

        /// <summary>The cell under the pointer, as a place in the scrollback. Clamped to the
        /// nearest real cell when a drag has taken the pointer outside.</summary>
        private bool Over(out ScreenPoint point, bool clamp)
        {
            point = ScreenPoint.None;
            if (!PointerIn(out Vector2 local)) return false;

            Rect area = _rect.rect;
            if (!clamp && !area.Contains(local)) return false;

            int row = Mathf.FloorToInt((area.yMax - Mathf.Clamp(local.y, area.yMin, area.yMax)) / _lineHeight);
            int column = Mathf.FloorToInt((Mathf.Clamp(local.x, area.xMin, area.xMax) - area.xMin) / _advance);

            row = _grid.NearestRow(Mathf.Clamp(row, 0, Mathf.Max(0, _grid.Rows - 1)));
            if (row < 0) return false;

            if (!_grid.TryLocate(row, Mathf.Clamp(column, 0, Mathf.Max(0, _grid.Columns - 1)),
                                 out int line, out int rune)) return false;

            point = new ScreenPoint { Line = line, Rune = rune };
            return true;
        }

        private bool PointerIn(out Vector2 local)
        {
            Canvas canvas = _rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;

            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _rect, Input.mousePosition, camera, out local);
        }

        internal string SelectedText() =>
            _selection.Exists ? SelectionText.Extract(_lines, _selection) : "";

        internal void Paste(string text)
        {
            string flat = TerminalClipboard.ForInput(text);
            if (flat.Length == 0) return;
            if (!Readline.ReadInput(_adapter, out string input, out int point)) return;

            Readline.WriteInput(_adapter, input.Insert(point, flat), point + flat.Length);
        }

        /// <summary>
        /// One quad per row the selection touches, sized in whole cells.
        ///
        /// The game draws selection as three rectangles per row - a first line, a middle and a
        /// last - because it is highlighting proportional runs inside a text object it does
        /// not control. On a grid every cell is the same width, so a run is a rectangle and
        /// the arithmetic is two multiplications.
        /// </summary>
        private void PaintSelection()
        {
            int used = 0;

            if (_selection.Exists)
            {
                UI_Theme theme = OS.GetThemeFromFile();
                Color32 colour = theme != null ? theme.terminalText : (Color32)Color.white;

                for (int row = 0; row < _grid.Rows; row++)
                {
                    if (!RowHighlight(row, out int first, out int last)) continue;

                    Image quad = Quad(used++);
                    var rect = (RectTransform)quad.transform;
                    rect.sizeDelta = new Vector2((last - first) * _advance, _lineHeight);
                    rect.anchoredPosition = new Vector2(first * _advance, -row * _lineHeight);
                    quad.color = new Color32(colour.r, colour.g, colour.b, 90);
                    quad.enabled = true;
                }
            }

            for (int i = used; i < _highlights.Count; i++) _highlights[i].enabled = false;
        }

        /// <summary>Which columns of a row the selection covers. Runes and columns part company
        /// wherever a wide glyph sits, so the row's own cells are walked rather than assumed.</summary>
        private bool RowHighlight(int row, out int firstColumn, out int lastColumn)
        {
            firstColumn = 0;
            lastColumn = 0;

            ScreenGrid.RowOrigin origin = _grid.OriginOf(row);
            if (origin.Line < 0) return false;
            if (!_selection.RunesOn(origin.Line, out int firstRune, out int lastRune)) return false;

            int rune = origin.FirstRune;
            bool started = false;

            for (int column = 0; column < _grid.Columns; column++)
            {
                if (_grid[row, column].Width == 0) { if (started) lastColumn = column + 1; continue; }
                if (rune >= origin.FirstRune + origin.RuneCount) break;

                if (rune >= firstRune && rune < lastRune)
                {
                    if (!started) { firstColumn = column; started = true; }
                    lastColumn = column + 1;
                }
                rune++;
            }

            return started && lastColumn > firstColumn;
        }

        private Image Quad(int index)
        {
            while (_highlights.Count <= index)
            {
                var go = new GameObject("Selection", typeof(RectTransform), typeof(CanvasRenderer));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_rect, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);

                var image = go.AddComponent<Image>();
                image.raycastTarget = false;

                // Behind the glyphs, so highlighted text stays readable - a quad drawn over
                // them would hide exactly what was selected.
                rt.SetAsFirstSibling();
                _highlights.Add(image);
            }
            return _highlights[index];
        }

        // ---- caret -----------------------------------------------------------------------

        private void PositionCaret()
        {
            bool onScreen = _scrollBack == 0 && CaretWanted() && _grid.CaretRow >= 0;
            if (!onScreen)
            {
                Light(_caret, false);
                Outline(false);
                return;
            }

            var cell = new Vector2(_grid.CaretColumn * _advance, -_grid.CaretRow * _lineHeight);

            UI_Theme theme = OS.GetThemeFromFile();
            Color32 colour = theme != null ? theme.terminalText : (Color32)Color.white;

            // An unfocused terminal draws the outline of a block and holds still. Terminals
            // have shown an idle window this way for decades, and it says which one has the
            // keyboard without a second one blinking for attention it cannot act on. The shape
            // setting is deliberately ignored: a hollow bar is a bar, and unreadable as a hint.
            if (!_adapter.IsFocus())
            {
                Light(_caret, false);
                DrawOutline(cell, colour);
                return;
            }

            Outline(false);

            if (_caret == null) BuildCaret();

            bool lit = !UwUTermPlugin.CursorBlink.Value || ((int)(Time.unscaledTime / 0.5f) & 1) == 0;
            Light(_caret, lit);
            if (!lit) return;

            Shape(out Vector2 size, out Vector2 offset, out byte alpha);

            var rect = (RectTransform)_caret.transform;
            rect.sizeDelta = size;
            rect.anchoredPosition = cell + offset;
            _caret.color = new Color32(colour.r, colour.g, colour.b, alpha);
        }

        /// <summary>
        /// The four sides of a block, as separate quads.
        ///
        /// Unity's Outline effect draws offset copies of what it decorates, which for a solid
        /// rectangle is a bigger solid rectangle rather than a hollow one. Four thin quads are
        /// the shape actually wanted, and cost no more than the one they replace.
        /// </summary>
        private void DrawOutline(Vector2 cell, Color32 colour)
        {
            float thickness = Mathf.Max(1f, _lineHeight * 0.08f);
            float w = _advance;
            float h = _lineHeight;

            Place(0, cell, new Vector2(w, thickness), colour);
            Place(1, cell + new Vector2(0f, -(h - thickness)), new Vector2(w, thickness), colour);
            Place(2, cell + new Vector2(0f, -thickness), new Vector2(thickness, h - thickness * 2f), colour);
            Place(3, cell + new Vector2(w - thickness, -thickness), new Vector2(thickness, h - thickness * 2f), colour);
        }

        private void Place(int index, Vector2 position, Vector2 size, Color32 colour)
        {
            while (_outline.Count <= index)
            {
                var go = new GameObject("CaretOutline", typeof(RectTransform), typeof(CanvasRenderer));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_rect, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);

                var image = go.AddComponent<Image>();
                image.raycastTarget = false;
                _outline.Add(image);
            }

            Image edge = _outline[index];
            var rect = (RectTransform)edge.transform;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            edge.color = new Color32(colour.r, colour.g, colour.b, 0xC0);
            edge.enabled = true;
        }

        private void Outline(bool visible)
        {
            for (int i = 0; i < _outline.Count; i++) Light(_outline[i], visible);
        }

        private static void Light(Image image, bool visible)
        {
            if (image != null) image.enabled = visible;
        }

        /// <summary>
        /// Block, bar or underline, sized from the cell it sits in so it follows the font.
        ///
        /// A block covers the character, so it is drawn translucent and the glyph reads
        /// through it. A bar and an underline sit beside the glyph rather than over it, so
        /// they are drawn solid - a thin translucent line is barely there at all.
        /// </summary>
        private void Shape(out Vector2 size, out Vector2 offset, out byte alpha)
        {
            switch (UwUTermPlugin.CursorStyle.Value.Trim().ToLowerInvariant())
            {
                case "bar":
                case "beam":
                case "line":
                    size = new Vector2(Mathf.Max(1f, _advance * 0.15f), _lineHeight);
                    offset = Vector2.zero;
                    alpha = 0xFF;
                    return;

                case "underline":
                case "underscore":
                    float thickness = Mathf.Max(1f, _lineHeight * 0.12f);
                    size = new Vector2(_advance, thickness);
                    offset = new Vector2(0f, -(_lineHeight - thickness));
                    alpha = 0xFF;
                    return;

                default:
                    size = new Vector2(_advance, _lineHeight);
                    offset = Vector2.zero;
                    alpha = 0xB0;
                    return;
            }
        }

        private void BuildCaret()
        {
            var caret = new GameObject("Caret", typeof(RectTransform), typeof(CanvasRenderer));
            var rect = (RectTransform)caret.transform;
            rect.SetParent(_rect, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);

            _caret = caret.AddComponent<Image>();
            _caret.raycastTarget = false;
        }

        /// <summary>
        /// The game's line models, seen as the plain strings the grid wants. A view rather
        /// than a copy: the scrollback can run to thousands of lines and rebuilding a list of
        /// them on every keystroke would cost more than drawing.
        /// </summary>
        private sealed class LineSource : IReadOnlyList<string>
        {
            private readonly TerminalListAdapter _adapter;

            internal LineSource(TerminalListAdapter adapter) => _adapter = adapter;

            public int Count => _adapter.Data == null ? 0 : _adapter.Data.Count;

            /// <summary>
            /// A password line is stored in the clear and masked only when the game draws it,
            /// so reading the model straight - which is what this screen does everywhere else -
            /// puts the typed password on screen. The game's own masking is reused rather than
            /// reimplemented, so the two cannot drift over what counts as the editable part.
            /// </summary>
            public string this[int index]
            {
                get
                {
                    TerminalListItemModel model = _adapter.Data[index];
                    if (model == null) return "";
                    if (!model.isPassword) return model.line ?? "";

                    return _adapter.GetPasswordBuildLine(index) ?? "";
                }
            }

            public IEnumerator<string> GetEnumerator()
            {
                for (int i = 0; i < Count; i++) yield return this[i];
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
