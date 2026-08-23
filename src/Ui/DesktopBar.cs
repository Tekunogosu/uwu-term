using System.Collections.Generic;
using TMPro;
using UI.Dialogs;
using UnityEngine;
using UnityEngine.UI;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Collapses the desktop's two bars into one along the top.
    ///
    /// The game puts a 35px bar at the top - menu button, clock, notification icons, user
    /// name - and a 45px taskbar at the bottom holding the open windows. Both are pure scene
    /// layout: anchored rects with layout groups, and no code that assumes where they are.
    /// So the taskbar moves into the top bar, the clock joins the widgets on the right, and
    /// the 45px the bottom bar was using goes back to the desktop.
    ///
    /// Nothing here touches uDialog_TaskBar itself. It keeps building its task buttons the
    /// same way; they are simply built somewhere else.
    /// </summary>
    internal sealed class DesktopBar
    {
        private const string TopBar = "BarraSupDesktop";
        private const string BottomBar = "TaskBar";
        private const string Clock = "ButtonHora";
        private const string Widgets = "PanelRight";
        private const string MenuButton = "MainMenuButton";
        private const string CalendarPopup = "Calendar";
        private const string IconGrid = "Icons";
        private const string UserName = "UserNameDesktop";

        /// <summary>Between the start menu button and the user name beside it.</summary>
        private const float MenuGap = 10f;

        /// <summary>How narrow a task button is allowed to get. The game caps how many
        /// windows can be open, so the strip runs out of room long before this does; it is
        /// here so a bar squeezed to nothing cannot produce zero-width buttons.</summary>
        private const float MinTaskWidth = 24f;

        /// <summary>How often the right-hand items are looked up again, in frames.</summary>
        private const int RegatherEvery = 15;

        /// <summary>Room either side of the taskbar so its buttons never touch what is beside
        /// them. The gap before the window list is twice this, since that is the one that
        /// separates two groups rather than two neighbours.</summary>
        private static float Gutter => Mathf.Max(0f, UwUTermPlugin.BarSpacing.Value);

        private static DesktopBar _applied;
        private static readonly Vector3[] Corners = new Vector3[4];

        private RectTransform _top;
        private RectTransform _taskBar;
        private RectTransform _widgets;
        private RectTransform _clock;
        private RectTransform _calendar;
        private RectTransform _icons;
        private RectTransform _user;
        private RectTransform _userIcon;

        // What each piece looked like before, so switching the setting off puts it back
        // rather than requiring a restart.
        private Transform _userParent;
        private int _userOrder;
        private Vector2 _userAnchorMin, _userAnchorMax, _userPivot, _userPosition;
        private bool _iconShown;
        private Transform _clockParent;
        private int _clockOrder;
        private Vector2 _clockAnchorMin, _clockAnchorMax, _clockPivot, _clockPosition, _clockSize;
        private Vector2 _taskBarMin, _taskBarMax, _taskBarAnchorMin, _taskBarAnchorMax, _taskBarSize, _taskBarPivot;
        private Vector2 _calendarAnchorMin, _calendarAnchorMax, _calendarPosition;
        private Vector2 _iconsMin, _iconsMax;
        private bool _taskBarImage;
        private TextAnchor _taskBarAlignment;
        private int _taskBarPadLeft;
        private bool _taskBarExpand;
        private bool _taskBarControlWidth;
        private Vector2 _taskBarCell;

        /// <summary>Whatever sizes and places the task buttons. Which kind it is decides how a
        /// width is handed to a button, so it is asked for by the base every layout group
        /// shares rather than by the one kind that was expected.</summary>
        private LayoutGroup _taskLayout;

        /// <summary>The bar-level items on the right, and when they were last looked up.</summary>
        private readonly List<RectTransform> _items = new List<RectTransform>();
        private int _gathered = -RegatherEvery;

        /// <summary>Where the right-hand furniture started when the strip was last held clear of
        /// it, in the bar's own space.</summary>
        private float _widgetsEdge;

        /// <summary>The same edge in world space, for the watchdog - what is drawn is settled
        /// there, and a disagreement between the two spaces is itself the answer.</summary>
        private float _widgetsWorld;

        /// <summary>The width a task button is built at, read off the templates it is cloned
        /// from. 0 when there are none to read, which just means no upper bound: the buttons
        /// then always share the strip out between them.</summary>
        private float _taskNatural;
        private float _taskEach;

        internal static void Tick()
        {
            // Our own bar and the rearrangement of the game's cannot both have the furniture.
            bool wanted = UwUTermPlugin.FeatureDesktop.Value && !global::UwUTerm.Ui.TopBar.Owns
                          && !UwUTermPlugin.OwnTopBar.Value;

            if (!wanted)
            {
                if (_applied != null) { _applied.Undo(); _applied = null; }
                return;
            }

            if (_applied != null)
            {
                _applied.KeepTaskBarClear();

                return;
            }

            var bar = new DesktopBar();
            if (bar.Apply()) _applied = bar;
        }

        private bool Apply()
        {
            uDialog_TaskBar taskBar = uDialog_TaskBar.Singleton;
            if (taskBar == null) return false;

            _taskBar = taskBar.transform as RectTransform;
            Transform desktop = _taskBar != null ? _taskBar.parent : null;
            if (desktop == null) return false;

            _top = Find(desktop, TopBar);
            if (_top == null) return false;

            _widgets = Find(_top, Widgets);
            _clock = Find(_top, Clock);
            _calendar = Find(desktop, CalendarPopup);
            _icons = Find(desktop, IconGrid);
            if (_widgets == null || _clock == null) return false;

            _user = FindDeep(_top, UserName);

            var widget = _user != null ? _user.GetComponent<UserNameBar>() : null;
            _userIcon = widget != null && widget.iconImg != null ? widget.iconImg.rectTransform : null;

            _taskLayout = _taskBar.GetComponent<LayoutGroup>();
            MeasureTaskButton(taskBar);

            Remember();
            MoveUserNameToLeft();
            MoveClockToWidgets();
            AnchorTaskBarToTop();
            AnchorCalendarToClock();
            ReclaimBottomStrip();

            Report();
            UwUTermPlugin.Log.LogInfo(
                "desktop: bars merged into one along the top, task buttons laid out by " +
                (_taskLayout != null ? _taskLayout.GetType().Name : "nothing"));
            return true;
        }

        // ---- the rearrangement ----------------------------------------------------------

        /// <summary>
        /// Put the user name against the start menu, where a desktop usually keeps who you
        /// are, and let the window list start after it. The avatar that went with it is
        /// hidden - the name alone is what says who you are.
        ///
        /// Its width follows the name, so nothing here assumes a size - the taskbar's inset is
        /// measured from wherever its right edge ends up, every frame.
        /// </summary>
        private void MoveUserNameToLeft()
        {
            if (_user == null) return;

            // The icon is not a child of the label - UserNameBar holds a reference to one
            // living over with the notification icons - so hiding it is what keeps it from
            // being left behind on the far side of the bar. Deactivating rather than
            // clearing the image takes it out of that group's layout, so the widgets beside
            // it close the gap.
            var widget = _user.GetComponent<UserNameBar>();
            _userIcon = widget != null && widget.iconImg != null ? widget.iconImg.rectTransform : null;
            if (_userIcon != null) _userIcon.gameObject.SetActive(false);

            RectTransform menu = Find(_top, MenuButton);
            float x = menu != null ? RightEdgeIn(menu, _top) - _top.rect.xMin + MenuGap : MenuGap;

            _user.SetParent(_top, false);
            _user.anchorMin = _user.anchorMax = new Vector2(0f, 0.5f);
            _user.pivot = new Vector2(0f, 0.5f);
            _user.anchoredPosition = new Vector2(x, 0f);

            // A label sized by a layout group keeps whatever width the group gave it once it
            // leaves. Fitting to its own text is what makes a longer name take more room.
            var fitter = _user.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = _user.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
        }

        /// <summary>
        /// The clock sits centred in the top bar on its own. PanelRight beside it is a
        /// horizontal layout group that sizes itself to its contents, so dropping the clock in
        /// as the last child puts it in the corner and re-flows the rest without any maths.
        /// </summary>
        private void MoveClockToWidgets()
        {
            _clock.SetParent(_widgets, false);
            _clock.SetAsLastSibling();

            // Absolute placement means nothing inside a layout group, but a size does - the
            // group asks each child how big it wants to be.
            var element = _clock.GetComponent<LayoutElement>();
            if (element == null) element = _clock.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = _clock.rect.width;
            element.preferredHeight = _clock.rect.height;
        }

        /// <summary>
        /// Move the taskbar up into the top bar's strip - by re-anchoring it, never by
        /// reparenting it.
        ///
        /// The taskbar's parent is how the rest of the game finds the desktop. Ten places call
        /// uDialog.NewDialog(..., taskBar.transform.parent) to open a window, and the theme
        /// settings measure the desktop the same way. Reparenting it into the top bar makes
        /// every one of those resolve to a 35px strip, and new windows open into it - off
        /// screen.
        ///
        /// It is already a sibling of the top bar and drawn after it, so anchoring it over the
        /// bar's strip puts the task buttons in the right place with the parent left alone.
        /// Its own background is switched off so the bar shows through underneath.
        /// </summary>
        private void AnchorTaskBarToTop()
        {
            _taskBar.anchorMin = new Vector2(0f, 1f);
            _taskBar.anchorMax = new Vector2(1f, 1f);
            _taskBar.pivot = new Vector2(0.5f, 1f);

            var image = _taskBar.GetComponent<Image>();
            if (image != null) image.enabled = false;

            // The bar used to run the full width of the screen, so centring its buttons put
            // them in the middle of the desktop. In a strip that starts after the menu button
            // and stops before the widgets, centred leaves a gap on the left and packs the
            // buttons somewhere arbitrary. They should start where the strip starts.
            if (_taskLayout != null) _taskLayout.childAlignment = TextAnchor.MiddleLeft;

            KeepTaskBarClear();
        }

        /// <summary>
        /// Hold the taskbar's edges clear of the menu button and the widgets.
        ///
        /// Both of those change width as things happen - the widget group grows an icon when
        /// something needs attention, and shrinks again - so the insets are kept up rather
        /// than measured once.
        /// </summary>
        private void KeepTaskBarClear()
        {
            if (_taskBar == null || _top == null) return;

            Rect bar = _top.rect;
            float left = Gutter, right = Gutter;

            // Whichever sits furthest right of the things pinned to the left edge - the menu
            // button, and the user name beside it once it has been moved.
            RectTransform menu = Find(_top, MenuButton);
            if (menu != null) left = RightEdgeIn(menu, _top) - bar.xMin + Gutter;
            if (_user != null && _user.parent == _top)
                left = Mathf.Max(left, RightEdgeIn(_user, _top) - bar.xMin + Gutter * 2f);
            float widgets = _widgets != null ? WidgetsEdge() : bar.xMax;
            if (_widgets != null) right = bar.xMax - widgets + Gutter;

            _widgetsEdge = widgets;

            var min = new Vector2(left, -bar.height);
            var max = new Vector2(-right, 0f);

            if (_taskBar.offsetMin != min) _taskBar.offsetMin = min;
            if (_taskBar.offsetMax != max) _taskBar.offsetMax = max;

            // Re-asserted rather than set once: the taskbar rebuilds its buttons whenever a
            // window opens, closes or changes state, and the theme being applied rebuilds them
            // too, so anything that resets the group would otherwise win a frame later.
            if (_taskLayout == null) return;

            if (_taskLayout.childAlignment != TextAnchor.MiddleLeft)
                _taskLayout.childAlignment = TextAnchor.MiddleLeft;
            if (_taskLayout.padding.left != 0) _taskLayout.padding.left = 0;
            if (_taskLayout is HorizontalOrVerticalLayoutGroup line && line.childForceExpandWidth)
                line.childForceExpandWidth = false;

            FitTaskButtons();
        }

        /// <summary>
        /// Share the strip out between the task buttons so the row ends where the strip does.
        ///
        /// A horizontal layout group lays its children out at the width they ask for and runs
        /// past its own rect once they no longer fit, which is what puts the last window's
        /// button over the clock and the notification icons. Asking for a narrower button
        /// instead is what makes the row fit: every button is given the same width, the strip
        /// divided by how many there are, and keeps its built width while there is room for
        /// it.
        ///
        /// The buttons are destroyed and re-instantiated on every rebuild, so this is a
        /// per-frame pass over whatever is there now rather than something applied once.
        /// </summary>
        private void FitTaskButtons()
        {
            int count = 0;
            for (int i = 0; i < _taskBar.childCount; i++)
                if (IsTaskButton(_taskBar.GetChild(i))) count++;

            if (count == 0) return;

            float room = _taskBar.rect.width - _taskLayout.padding.left - _taskLayout.padding.right
                       - Spacing() * (count - 1);
            float each = room / count;
            if (_taskNatural > 0f) each = Mathf.Min(each, _taskNatural);
            each = Mathf.Max(each, MinTaskWidth);

            if (UwUTermPlugin.ScreenDebug.Value && !Mathf.Approximately(_taskEach, each))
                UwUTermPlugin.Log.LogInfo(
                    $"desktop: {count} task buttons at {each:F0}px in {room:F0}px " +
                    $"(built at {_taskNatural:F0}px)");
            _taskEach = each;

            // A grid hands every cell the same size, so there is one width to set and no
            // per-button work at all.
            if (_taskLayout is GridLayoutGroup grid)
            {
                if (!Mathf.Approximately(grid.cellSize.x, each))
                    grid.cellSize = new Vector2(each, grid.cellSize.y);

                for (int i = 0; i < _taskBar.childCount; i++)
                    FitTaskLabel(_taskBar.GetChild(i).GetComponent<uDialog_TaskBar_Task>());

                return;
            }

            // A line of buttons is sized one at a time, through the width each asks for - and
            // only when the group is told to control it. Left off, a preferred width is read
            // and ignored.
            if (_taskLayout is not HorizontalOrVerticalLayoutGroup line)
            {
                SizeTaskButtons(each);
                return;
            }

            if (!line.childControlWidth) line.childControlWidth = true;


            for (int i = 0; i < _taskBar.childCount; i++)
            {
                Transform child = _taskBar.GetChild(i);
                if (!IsTaskButton(child)) continue;

                FitTaskLabel(child.GetComponent<uDialog_TaskBar_Task>());

                var element = child.GetComponent<LayoutElement>();
                if (element == null) element = child.gameObject.AddComponent<LayoutElement>();

                // Writing the same width back dirties the layout, so a rebuild would be
                // queued every frame for a row that has not changed.
                if (!Mathf.Approximately(element.preferredWidth, each)) element.preferredWidth = each;

                // A fitter on the button sizes it from its own text, which is the one thing
                // that can overrule the width the group hands down.
                var fitter = child.GetComponent<ContentSizeFitter>();
                if (fitter != null && fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained)
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            }
        }

        /// <summary>
        /// Set the width on the buttons themselves.
        ///
        /// A preferred width is only read by Unity's own layout groups. Anything else placing
        /// the buttons is told nothing by one, so the size is written where every group looks:
        /// the button's own rect.
        /// </summary>
        private void SizeTaskButtons(float each)
        {
            for (int i = 0; i < _taskBar.childCount; i++)
            {
                Transform child = _taskBar.GetChild(i);
                if (!IsTaskButton(child)) continue;

                FitTaskLabel(child.GetComponent<uDialog_TaskBar_Task>());

                if (child is not RectTransform rect || Mathf.Approximately(rect.rect.width, each))
                    continue;

                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, each);
            }
        }

        /// <summary>The gap the group leaves between two buttons. Every kind of group has one;
        /// they disagree about whether it has a second dimension.</summary>
        private float Spacing() =>
            _taskLayout switch
            {
                HorizontalOrVerticalLayoutGroup line => line.spacing,
                GridLayoutGroup grid => grid.spacing.x,
                _ => 0f,
            };

        /// <summary>
        /// Keep a window's name inside the button that carries it: a name too long for the
        /// room the button now has is cut short rather than drawn past its edge and over the
        /// button beside it.
        ///
        /// The label follows the button on its own - it is either sized by the button's own
        /// layout or anchored to its edges - so the width is not set here. What it does with
        /// text that no longer fits is the part that has to be said.
        /// </summary>
        private static void FitTaskLabel(uDialog_TaskBar_Task task)
        {
            TMP_Text label = task != null ? task.GO_TextComponent : null;
            if (label == null) return;

            if (label.enableWordWrapping) label.enableWordWrapping = false;
            if (label.overflowMode != TextOverflowModes.Ellipsis)
                label.overflowMode = TextOverflowModes.Ellipsis;
        }

        /// <summary>A live button rather than one of the templates they are cloned from - those
        /// are children too, kept inactive.</summary>
        internal static bool IsTaskButton(Transform child)
        {
            if (child == null || !child.gameObject.activeSelf) return false;

            var task = child.GetComponent<uDialog_TaskBar_Task>();
            return task != null && !task.IsTemplate;
        }

        /// <summary>
        /// The width a button is built at, before anything is rearranged.
        ///
        /// Taken from the templates buttons are cloned from: a template is never laid out - a
        /// group skips inactive children - so it keeps the size the prefab was authored at,
        /// which is the size every button is instantiated with. A grid answers for itself,
        /// since its cell is what the button ends up as whatever it was built at.
        /// </summary>
        private void MeasureTaskButton(uDialog_TaskBar taskBar)
        {
            _taskNatural = 0f;
            foreach (uDialog_TaskBar_Task template in new[]
                     {
                         taskBar.TaskTemplate_ActiveTask,
                         taskBar.TaskTemplate_FocusedTask,
                         taskBar.TaskTemplate_InactiveTask,
                     })
            {
                var rect = template != null ? template.transform as RectTransform : null;
                if (rect != null) _taskNatural = Mathf.Max(_taskNatural, rect.rect.width);
            }

            // A grid overrules whatever the template was authored at - every button in one is
            // the size of a cell, so the cell is the width they are built at.
            if (_taskLayout is GridLayoutGroup cells && cells.cellSize.x > 1f)
                _taskNatural = cells.cellSize.x;
        }

        /// <summary>
        /// Where the right-hand furniture starts, taken from the items themselves rather than
        /// from the group they hang under.
        ///
        /// A group's rect describes its contents only as well as its own layout makes it, and
        /// nothing here can tell whether it does. Measuring the items removes the question: the
        /// group's own children, and the children of any group among them, which is the row of
        /// widgets as it is actually drawn.
        ///
        /// What hangs below them is left out. A widget's dropdown is several times the width of
        /// the widget and opens under the bar, so counting it would pull the strip back across
        /// the desktop for as long as somebody had a panel open.
        /// </summary>
        private float WidgetsEdge()
        {
            if (Time.frameCount - _gathered >= RegatherEvery) Gather();

            float edge = LeftEdgeIn(_widgets, _top);
            _widgetsWorld = LeftEdgeWorld(_widgets);
            foreach (RectTransform item in _items)
            {
                if (item == null || !item.gameObject.activeInHierarchy) continue;

                edge = Mathf.Min(edge, LeftEdgeIn(item, _top));
                _widgetsWorld = Mathf.Min(_widgetsWorld, LeftEdgeWorld(item));
            }

            return edge;
        }

        /// <summary>The bar-level items on the right. Re-taken on a slow tick rather than every
        /// frame: widgets come and go with what the machine is doing, and walking the group for
        /// each one costs more than the answer is worth at that rate.</summary>
        private void Gather()
        {
            _gathered = Time.frameCount;
            _items.Clear();
            if (_widgets == null) return;

            for (int i = 0; i < _widgets.childCount; i++)
            {
                if (_widgets.GetChild(i) is not RectTransform child) continue;

                _items.Add(child);
                if (child.GetComponent<LayoutGroup>() == null) continue;

                for (int j = 0; j < child.childCount; j++)
                    if (child.GetChild(j) is RectTransform inner) _items.Add(inner);
            }
        }

        /// <summary>
        /// An edge in another rect's space, taken from the corners rather than from the anchor.
        ///
        /// anchoredPosition is measured from a pivot that each object sets for itself, so
        /// adding half a width to it only finds the edge when the pivot happens to be centred.
        /// The corners are where the object actually is.
        /// </summary>
        internal static float RightEdgeIn(RectTransform child, RectTransform space)
        {
            child.GetWorldCorners(Corners);
            return space.InverseTransformPoint(Corners[2]).x;
        }

        /// <summary>An edge in world space. On an overlay canvas that is the pixel it is drawn
        /// at, which is the one measurement a scale factor cannot disagree with.</summary>
        internal static float LeftEdgeWorld(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Corners[0].x;
        }

        internal static float BottomEdgeWorld(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Corners[0].y;
        }

        internal static float TopEdgeWorld(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Corners[1].y;
        }

        internal static float RightEdgeWorld(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Corners[2].x;
        }

        internal static float LeftEdgeIn(RectTransform child, RectTransform space)
        {
            child.GetWorldCorners(Corners);
            return space.InverseTransformPoint(Corners[0]).x;
        }

        /// <summary>The calendar drops out of the clock, so it has to follow it to the corner
        /// rather than staying under the middle of the screen where the clock used to be.</summary>
        private void AnchorCalendarToClock()
        {
            if (_calendar == null) return;

            _calendar.anchorMin = _calendar.anchorMax = new Vector2(1f, 1f);
            _calendar.pivot = new Vector2(1f, 1f);
            _calendar.anchoredPosition = new Vector2(-Gutter, -_top.rect.height);
        }

        /// <summary>The icon grid was inset to clear both bars. With only one left, the strip
        /// at the bottom is desktop again.</summary>
        private void ReclaimBottomStrip()
        {
            if (_icons == null) return;
            _icons.offsetMin = new Vector2(_icons.offsetMin.x, 0f);
        }

        // ---- putting it back -------------------------------------------------------------

        private void Remember()
        {
            if (_userIcon != null) _iconShown = _userIcon.gameObject.activeSelf;

            if (_user != null)
            {
                _userParent = _user.parent;
                _userOrder = _user.GetSiblingIndex();
                _userAnchorMin = _user.anchorMin;
                _userAnchorMax = _user.anchorMax;
                _userPivot = _user.pivot;
                _userPosition = _user.anchoredPosition;
            }

            _clockParent = _clock.parent;
            _clockOrder = _clock.GetSiblingIndex();

            // Where it sat, not just what it hung from. A layout group positions its children,
            // so a clock that has been inside one comes back carrying whatever position the
            // group left it at - which on a bar with no layout group is wherever that happened
            // to be, rather than the middle where it belongs.
            _clockAnchorMin = _clock.anchorMin;
            _clockAnchorMax = _clock.anchorMax;
            _clockPivot = _clock.pivot;
            _clockPosition = _clock.anchoredPosition;
            _clockSize = _clock.sizeDelta;

            _taskBarAnchorMin = _taskBar.anchorMin;
            _taskBarAnchorMax = _taskBar.anchorMax;
            _taskBarMin = _taskBar.offsetMin;
            _taskBarMax = _taskBar.offsetMax;
            _taskBarSize = _taskBar.sizeDelta;
            _taskBarPivot = _taskBar.pivot;

            var image = _taskBar.GetComponent<Image>();
            _taskBarImage = image != null && image.enabled;

            var line = _taskLayout as HorizontalOrVerticalLayoutGroup;
            var cells = _taskLayout as GridLayoutGroup;
            _taskBarAlignment = _taskLayout != null ? _taskLayout.childAlignment : TextAnchor.MiddleCenter;
            _taskBarPadLeft = _taskLayout != null ? _taskLayout.padding.left : 0;
            _taskBarExpand = line != null && line.childForceExpandWidth;
            _taskBarControlWidth = line != null && line.childControlWidth;
            _taskBarCell = cells != null ? cells.cellSize : Vector2.zero;

            if (_calendar != null)
            {
                _calendarAnchorMin = _calendar.anchorMin;
                _calendarAnchorMax = _calendar.anchorMax;
                _calendarPosition = _calendar.anchoredPosition;
            }

            if (_icons != null)
            {
                _iconsMin = _icons.offsetMin;
                _iconsMax = _icons.offsetMax;
            }
        }

        private void Undo()
        {
            if (_userIcon != null) _userIcon.gameObject.SetActive(_iconShown);

            if (_user != null && _userParent != null)
            {
                var fitter = _user.GetComponent<ContentSizeFitter>();
                if (fitter != null) Object.Destroy(fitter);

                _user.SetParent(_userParent, false);
                _user.SetSiblingIndex(_userOrder);
                _user.anchorMin = _userAnchorMin;
                _user.anchorMax = _userAnchorMax;
                _user.pivot = _userPivot;
                _user.anchoredPosition = _userPosition;
            }

            if (_clock != null && _clockParent != null)
            {
                var element = _clock.GetComponent<LayoutElement>();
                if (element != null) Object.Destroy(element);

                _clock.SetParent(_clockParent, false);
                _clock.SetSiblingIndex(_clockOrder);
                _clock.anchorMin = _clockAnchorMin;
                _clock.anchorMax = _clockAnchorMax;
                _clock.pivot = _clockPivot;
                _clock.anchoredPosition = _clockPosition;
                _clock.sizeDelta = _clockSize;
            }

            if (_taskBar != null)
            {
                _taskBar.pivot = _taskBarPivot;
                _taskBar.anchorMin = _taskBarAnchorMin;
                _taskBar.anchorMax = _taskBarAnchorMax;
                _taskBar.offsetMin = _taskBarMin;
                _taskBar.offsetMax = _taskBarMax;
                _taskBar.sizeDelta = _taskBarSize;

                var image = _taskBar.GetComponent<Image>();
                if (image != null) image.enabled = _taskBarImage;

                if (_taskLayout != null)
                {
                    _taskLayout.childAlignment = _taskBarAlignment;
                    _taskLayout.padding.left = _taskBarPadLeft;
                }

                if (_taskLayout is HorizontalOrVerticalLayoutGroup line)
                {
                    line.childForceExpandWidth = _taskBarExpand;
                    line.childControlWidth = _taskBarControlWidth;
                }

                if (_taskLayout is GridLayoutGroup cells && _taskBarCell.x > 0f)
                    cells.cellSize = _taskBarCell;
            }

            if (_calendar != null)
            {
                _calendar.anchorMin = _calendarAnchorMin;
                _calendar.anchorMax = _calendarAnchorMax;
                _calendar.anchoredPosition = _calendarPosition;
            }

            if (_icons != null)
            {
                _icons.offsetMin = _iconsMin;
                _icons.offsetMax = _iconsMax;
            }

            UwUTermPlugin.Log.LogInfo("desktop: bars put back the way the game had them");
        }

        /// <summary>What the strip came out as, for the one path that still rearranges the
        /// game's own bar rather than drawing ours.</summary>
        private void Report()
        {
            if (!UwUTermPlugin.ScreenDebug.Value) return;

            UwUTermPlugin.Log.LogInfo(
                $"desktop: bar {_top.rect.width:F0}x{_top.rect.height:F0}, " +
                $"strip {_taskBar.rect.width:F0}px from {_taskBar.offsetMin.x:F0}, " +
                $"widgets from {_widgetsEdge:F0}");
        }

        private static RectTransform Find(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            return child as RectTransform;
        }

        /// <summary>By name anywhere beneath, since the widgets sit a level or two down.</summary>
        private static RectTransform FindDeep(Transform parent, string name)
        {
            foreach (RectTransform t in parent.GetComponentsInChildren<RectTransform>(true))
                if (t.name == name) return t;

            return null;
        }

        /// <summary>Height of whatever bar is at the top, for anything that has to keep out
        /// from under it.</summary>
        internal static float TopInset =>
            _applied != null && _applied._top != null ? _applied._top.rect.height : 0f;

        internal static bool Active => _applied != null;
    }
}
