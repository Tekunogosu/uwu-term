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

        /// <summary>Between the user icon and the name it belongs to.</summary>
        private const float IconGap = 6f;

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
        private Transform _iconParent;
        private int _iconOrder;
        private Vector2 _iconAnchorMin, _iconAnchorMax, _iconPivot, _iconPosition, _iconSize;
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

        internal static void Tick()
        {
            bool wanted = UwUTermPlugin.FeatureDesktop.Value;

            if (!wanted)
            {
                if (_applied != null) { _applied.Undo(); _applied = null; }
                return;
            }

            if (_applied != null) { _applied.KeepTaskBarClear(); return; }

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

            Remember();
            MoveUserNameToLeft();
            MoveClockToWidgets();
            AnchorTaskBarToTop();
            AnchorCalendarToClock();
            ReclaimBottomStrip();

            Report();
            UwUTermPlugin.Log.LogInfo("desktop: bars merged into one along the top");
            return true;
        }

        // ---- the rearrangement ----------------------------------------------------------

        /// <summary>
        /// Put the user name against the start menu, where a desktop usually keeps who you
        /// are, and let the window list start after it.
        ///
        /// Its width follows the name, so nothing here assumes a size - the taskbar's inset is
        /// measured from wherever its right edge ends up, every frame.
        /// </summary>
        private void MoveUserNameToLeft()
        {
            if (_user == null) return;

            // The icon is not a child of the label - UserNameBar holds a reference to one
            // living over with the notification icons - so moving the name alone leaves it
            // behind on the far side of the bar.
            var widget = _user.GetComponent<UserNameBar>();
            _userIcon = widget != null && widget.iconImg != null ? widget.iconImg.rectTransform : null;

            RectTransform menu = Find(_top, MenuButton);
            float x = menu != null ? RightEdgeIn(menu, _top) - _top.rect.xMin + Gutter : Gutter;

            if (_userIcon != null)
            {
                // Taken before the reparent: a layout group sized it, and leaving one drops
                // whatever width it was given.
                Vector2 size = _userIcon.rect.size;

                _userIcon.SetParent(_top, false);
                _userIcon.anchorMin = _userIcon.anchorMax = new Vector2(0f, 0.5f);
                _userIcon.pivot = new Vector2(0f, 0.5f);
                _userIcon.sizeDelta = size;
                _userIcon.anchoredPosition = new Vector2(x, 0f);

                x += size.x + IconGap;
            }

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
            var layout = _taskBar.GetComponent<HorizontalLayoutGroup>();
            if (layout != null) layout.childAlignment = TextAnchor.MiddleLeft;

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
            if (_widgets != null) right = bar.xMax - LeftEdgeIn(_widgets, _top) + Gutter;

            var min = new Vector2(left, -bar.height);
            var max = new Vector2(-right, 0f);

            if (_taskBar.offsetMin != min) _taskBar.offsetMin = min;
            if (_taskBar.offsetMax != max) _taskBar.offsetMax = max;

            // Re-asserted rather than set once: the taskbar rebuilds its buttons whenever a
            // window opens, closes or changes state, and the theme being applied rebuilds them
            // too, so anything that resets the group would otherwise win a frame later.
            var layout = _taskBar.GetComponent<HorizontalLayoutGroup>();
            if (layout == null) return;

            if (layout.childAlignment != TextAnchor.MiddleLeft) layout.childAlignment = TextAnchor.MiddleLeft;
            if (layout.padding.left != 0) layout.padding.left = 0;
            if (layout.childForceExpandWidth) layout.childForceExpandWidth = false;
        }

        /// <summary>
        /// An edge in another rect's space, taken from the corners rather than from the anchor.
        ///
        /// anchoredPosition is measured from a pivot that each object sets for itself, so
        /// adding half a width to it only finds the edge when the pivot happens to be centred.
        /// The corners are where the object actually is.
        /// </summary>
        private static float RightEdgeIn(RectTransform child, RectTransform space)
        {
            child.GetWorldCorners(Corners);
            return space.InverseTransformPoint(Corners[2]).x;
        }

        private static float LeftEdgeIn(RectTransform child, RectTransform space)
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
            if (_userIcon != null)
            {
                _iconParent = _userIcon.parent;
                _iconOrder = _userIcon.GetSiblingIndex();
                _iconAnchorMin = _userIcon.anchorMin;
                _iconAnchorMax = _userIcon.anchorMax;
                _iconPivot = _userIcon.pivot;
                _iconPosition = _userIcon.anchoredPosition;
                _iconSize = _userIcon.sizeDelta;
            }

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

            var layout = _taskBar.GetComponent<HorizontalLayoutGroup>();
            _taskBarAlignment = layout != null ? layout.childAlignment : TextAnchor.MiddleCenter;
            _taskBarPadLeft = layout != null ? layout.padding.left : 0;
            _taskBarExpand = layout != null && layout.childForceExpandWidth;

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
            if (_userIcon != null && _iconParent != null)
            {
                _userIcon.SetParent(_iconParent, false);
                _userIcon.SetSiblingIndex(_iconOrder);
                _userIcon.anchorMin = _iconAnchorMin;
                _userIcon.anchorMax = _iconAnchorMax;
                _userIcon.pivot = _iconPivot;
                _userIcon.anchoredPosition = _iconPosition;
                _userIcon.sizeDelta = _iconSize;
            }

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

                var layout = _taskBar.GetComponent<HorizontalLayoutGroup>();
                if (layout != null)
                {
                    layout.childAlignment = _taskBarAlignment;
                    layout.padding.left = _taskBarPadLeft;
                    layout.childForceExpandWidth = _taskBarExpand;
                }
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

        /// <summary>What the taskbar strip actually came out as, and what its layout group is
        /// doing with it. Logged once, because two attempts at this were guesses.</summary>
        private void Report()
        {
            if (!UwUTermPlugin.ScreenDebug.Value) return;

            var layout = _taskBar.GetComponent<HorizontalLayoutGroup>();
            Rect bar = _top.rect;

            UwUTermPlugin.Log.LogInfo(
                $"desktop: bar {bar.width:F0}x{bar.height:F0} " +
                $"taskbar offsets L{_taskBar.offsetMin.x:F0} R{_taskBar.offsetMax.x:F0} " +
                $"rect {_taskBar.rect.width:F0}x{_taskBar.rect.height:F0}");

            if (layout != null)
                UwUTermPlugin.Log.LogInfo(
                    $"desktop: layout align={layout.childAlignment} spacing={layout.spacing} " +
                    $"pad L{layout.padding.left} R{layout.padding.right} " +
                    $"expandW={layout.childForceExpandWidth} controlW={layout.childControlWidth} " +
                    $"reverse={layout.reverseArrangement}");
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
