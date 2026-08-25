using System;
using System.Collections.Generic;
using TMPro;
using UI.Dialogs;
using UnityEngine;
using UnityEngine.UI;
using UwUTerm.Bar;

namespace UwUTerm.Ui
{
    /// <summary>
    /// The row of tabs across the top of one browser window.
    ///
    /// It knows names and an index, and answers clicks by calling back. What a tab is, which
    /// windows are in a group and what closing one costs are all decided elsewhere; the same
    /// split the terminal and the top bar already have, and what keeps the dependency running one
    /// way - the browser code draws through this, and this asks the browser code nothing.
    ///
    /// The band it draws in is the game's own. The browser prefab carries a tab-shaped <c>Tab</c>
    /// reading "Main Page" and an <c>AddTab</c> button left switched off, in a strip above the
    /// toolbar that nothing else uses - a row of tabs the game stopped short of finishing. Ours
    /// goes there rather than taking a band of its own, so the page keeps the height it had and no
    /// rectangle of ours has to survive a layout group that owns it.
    ///
    /// The stub's rectangle is copied, not computed: its anchors, pivot, height and offset are
    /// taken as they stand and only the width is changed. An earlier version worked the band out
    /// from the toolbar's rectangle instead and switched off whatever fell inside it. It measured
    /// 294.5px where the row is 30 and took the page, the toolbar and the bookmark button with it,
    /// which then made the window refuse to close - <c>CloseTaskBar</c> destroys PowerUI's panel
    /// before anything else and there was no longer one to destroy. So the two objects it replaces
    /// are found by name and nothing is chosen by where it happens to be: a renamed object costs a
    /// feature, and a wrong rectangle costs the window.
    ///
    /// Widths come from <see cref="BarLayout"/>, the same arithmetic the top bar's task buttons use:
    /// however many tabs are open, the row ends where its space ends.
    /// </summary>
    internal sealed class TabStrip
    {
        /// <summary>The size the strip's proportions are authored at, in pixels. A taller row scales
        /// its padding and its cross with it rather than keeping the same gap at any height.</summary>
        private const float AuthoredHeight = 30f;

        /// <summary>What the prefab calls the single tab it ships with, and the button beside it
        /// that was left switched off. These two objects are the row this replaces.</summary>
        private const string StubName = "Tab";
        private const string AddName = "AddTab";

        /// <summary>A row shorter than this is not the one we are looking for, and drawing tabs a
        /// few pixels tall says so less clearly than refusing to.</summary>
        private const float MinBand = 12f;

        /// <summary>How much of a tab the close cross takes, in pixels before scaling. A tab too
        /// narrow to hold one and still show a name drops the cross rather than the name.</summary>
        private const float CrossRoom = 18f;

        private const float AddRoom = 26f;

        private readonly RectTransform _root;
        private readonly Image _background;
        private readonly TMP_FontAsset _font;
        private readonly BarLayout _layout = new BarLayout();

        private readonly Action _onAdd;
        private readonly Action<int> _onSelect;
        private readonly Action<int> _onClose;

        private readonly List<GameObject> _hidden;
        private readonly List<Tab> _tabs = new List<Tab>();
        private RectTransform _add;
        private Image _addImage;
        private TMP_Text _addLabel;

        private Slot[] _slots = new Slot[8];
        private readonly float _height;
        private int _built = -1;
        private int _painted = -1;

        private TabStrip(Image background, RectTransform root, TMP_FontAsset font, float height,
                         List<GameObject> hidden, Action onAdd, Action<int> onSelect, Action<int> onClose)
        {
            _root = root;
            _background = background;
            _font = font;
            _height = height;
            _hidden = hidden;
            _onAdd = onAdd;
            _onSelect = onSelect;
            _onClose = onClose;
        }

        /// <summary>Whether the window has the row this replaces. Nothing here depends on a
        /// layout pass having run: the stub is a fixed size, so it measures the same on the frame
        /// the window is built as on any later one.</summary>
        internal static bool Ready(HtmlBrowser browser) => Stub(browser, out _, out _);

        /// <summary>Put a row of tabs where the browser window already keeps one. Answers null when
        /// that row is not there, leaving the window exactly as it was.</summary>
        internal static TabStrip Attach(uDialog window, HtmlBrowser browser,
                                        Action onAdd, Action<int> onSelect, Action<int> onClose)
        {
            if (!Stub(browser, out RectTransform area, out RectTransform stub))
            {
                UwUTermPlugin.Log.LogWarning("browser tabs: the window has no tab row to replace");
                return null;
            }

            var root = new GameObject("UwUTerm.Tabs", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)root.transform;
            rect.SetParent(area, false);

            // The stub's own band, copied rather than worked out - its anchors, pivot, height and
            // offset are taken as they stand, and only the width is changed, to the full row. There
            // is no arithmetic here to be wrong about a coordinate space.
            rect.anchorMin = new Vector2(0f, stub.anchorMin.y);
            rect.anchorMax = new Vector2(1f, stub.anchorMax.y);
            rect.pivot = new Vector2(0f, stub.pivot.y);
            rect.sizeDelta = new Vector2(0f, stub.sizeDelta.y);
            rect.anchoredPosition = new Vector2(0f, stub.anchoredPosition.y);

            // Last, because the panel holding the pages covers the whole area including this band
            // and is drawn after the row the game left in it.
            rect.SetAsLastSibling();

            List<GameObject> hidden = Hide(stub, area.Find(AddName));

            TMP_FontAsset font = browser.barAddress != null && browser.barAddress.textComponent != null
                ? browser.barAddress.textComponent.font
                : null;

            if (UwUTermPlugin.BrowserDebug.Value) Report(window, area, stub, hidden);

            return new TabStrip(root.GetComponent<Image>(), rect, font, stub.rect.height, hidden,
                                onAdd, onSelect, onClose);
        }

        /// <summary>
        /// The single tab the browser prefab ships with, and the area holding it.
        ///
        /// Found by name, and only ever these two objects. An earlier version worked the band out
        /// from the toolbar's rectangle and switched off whatever fell inside it, which measured
        /// 294.5px where the row is 30 and took the page, the toolbar and the bookmark button with
        /// it. A name that changes costs a feature; a rectangle that is wrong costs the window.
        /// </summary>
        private static bool Stub(HtmlBrowser browser, out RectTransform area, out RectTransform stub)
        {
            area = null;
            stub = null;

            if (browser == null || browser.panelGroup == null) return false;

            area = browser.panelGroup.transform.parent as RectTransform;
            if (area == null) return false;

            stub = area.Find(StubName) as RectTransform;
            return stub != null && stub.rect.height >= MinBand;
        }

        /// <summary>Switch the row the game left in the band off, and answer what was actually
        /// switched off so it can be put back. Only ever the objects named above: nothing is
        /// chosen by where it happens to be.</summary>
        private static List<GameObject> Hide(params Transform[] objects)
        {
            var hidden = new List<GameObject>();

            foreach (Transform t in objects)
            {
                if (t == null || !t.gameObject.activeSelf) continue;

                t.gameObject.SetActive(false);
                hidden.Add(t.gameObject);
            }

            return hidden;
        }

        /// <summary>What the row was fitted to and what was switched off, beside the hierarchy it
        /// was read from.</summary>
        private static void Report(uDialog window, RectTransform area, RectTransform stub,
                                   List<GameObject> hidden)
        {
            var sb = new System.Text.StringBuilder(2048);
            sb.Append($"browser tabs: row on '{stub.name}' in '{area.name}', ")
              .Append($"{stub.rect.width:F0}x{stub.rect.height:F0} at ")
              .Append($"({stub.anchoredPosition.x:F0},{stub.anchoredPosition.y:F0}), ")
              .Append(hidden.Count == 0 ? "nothing switched off" : "switched off " + Named(hidden))
              .Append('\n');

            Hierarchy.Describe(window.transform, sb, 0, 6);
            UwUTermPlugin.Log.LogInfo(sb.ToString());
        }

        private static string Named(List<GameObject> objects)
        {
            var names = new List<string>();
            foreach (GameObject o in objects) names.Add(o.name);
            return string.Join(", ", names.ToArray());
        }

        // ---- per-frame --------------------------------------------------------------------

        internal void Follow(IList<string> names, int active, UI_Theme theme)
        {
            if (_root == null) return;

            if (_built != names.Count) Rebuild(names.Count);

            for (int i = 0; i < _tabs.Count && i < names.Count; i++) _tabs[i].Say(names[i]);
            Place();

            if (_painted != active) Paint(theme, active);
        }

        internal void Repaint(UI_Theme theme, int active) => Paint(theme, active);

        private void Rebuild(int count)
        {
            foreach (Tab tab in _tabs) tab.Destroy();
            _tabs.Clear();

            for (int i = 0; i < count; i++) _tabs.Add(Tab.Make(_root, i, _font, _onSelect, _onClose));

            if (_add == null) MakeAddButton();
            _add.SetAsLastSibling();

            _built = count;
            _painted = -1;
        }

        private void MakeAddButton()
        {
            var obj = new GameObject("New", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            _add = (RectTransform)obj.transform;
            _add.SetParent(_root, false);
            _add.anchorMin = _add.anchorMax = new Vector2(0f, 1f);
            _add.pivot = new Vector2(0f, 1f);

            _addImage = obj.GetComponent<Image>();

            var labelObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.SetParent(_add, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

            _addLabel = labelObject.AddComponent<TextMeshProUGUI>();
            if (_font != null) _addLabel.font = _font;
            _addLabel.text = "+";
            _addLabel.alignment = TextAlignmentOptions.Center;
            _addLabel.raycastTarget = false;

            var button = obj.GetComponent<Button>();
            button.targetGraphic = _addImage;
            button.onClick.AddListener(() => _onAdd());
        }

        private void Place()
        {
            float width = _root.rect.width;
            float scale = Mathf.Max(0.1f, _height / AuthoredHeight);
            float room = AddRoom * scale;

            _layout.Width = width;
            _layout.Padding = 2f * scale;
            _layout.Gap = 2f * scale;
            _layout.TaskWidth = UwUTermPlugin.TabWidth.Value;
            _layout.MinTaskWidth = UwUTermPlugin.MinTabWidth.Value;

            if (_slots.Length < _tabs.Count) _slots = new Slot[Mathf.NextPowerOfTwo(_tabs.Count)];

            float end = _layout.PlaceTasks(_tabs.Count, _layout.Padding,
                                           width - _layout.Padding - room - _layout.Gap, _slots);

            for (int i = 0; i < _tabs.Count; i++)
                _tabs[i].Put(_slots[i], _height, CrossRoom * scale, UwUTermPlugin.TabFontSize.Value);

            // The button that opens a tab stands just past the last one, and stops at the edge
            // when the row has run out of room rather than being pushed off it.
            _add.anchoredPosition = new Vector2(Mathf.Min(end + _layout.Gap, width - _layout.Padding - room), 0f);
            _add.sizeDelta = new Vector2(room, _height);
            _addLabel.fontSize = UwUTermPlugin.TabFontSize.Value * 1.2f;
        }

        private void Paint(UI_Theme theme, int active)
        {
            if (theme == null) return;

            _painted = active;
            _background.color = theme.window_background;

            for (int i = 0; i < _tabs.Count; i++) _tabs[i].Paint(theme, i == active);

            if (_addImage == null) return;

            _addImage.color = Color.white;
            _addLabel.color = theme.buttons_text;

            var button = _add.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = theme.buttons_background;
            colors.highlightedColor = theme.buttonsHighlight;
            colors.pressedColor = colors.highlightedColor;
            colors.selectedColor = colors.normalColor;
            button.colors = colors;
        }

        /// <summary>Put the band back the way it was found, so a window that stops carrying tabs
        /// shows the row the game drew there.</summary>
        internal void Destroy()
        {
            foreach (GameObject o in _hidden)
                if (o != null) o.SetActive(true);

            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }

        // ---- one tab ----------------------------------------------------------------------

        private sealed class Tab
        {
            private readonly RectTransform _root;
            private readonly Image _background;
            private readonly TMP_Text _label;
            private readonly RectTransform _cross;
            private readonly TMP_Text _crossLabel;

            private Tab(RectTransform root, Image background, TMP_Text label,
                        RectTransform cross, TMP_Text crossLabel)
            {
                _root = root;
                _background = background;
                _label = label;
                _cross = cross;
                _crossLabel = crossLabel;
            }

            internal static Tab Make(RectTransform strip, int index, TMP_FontAsset font,
                                     Action<int> onSelect, Action<int> onClose)
            {
                var obj = new GameObject("Tab", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                var rect = (RectTransform)obj.transform;
                rect.SetParent(strip, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);

                var background = obj.GetComponent<Image>();

                var labelObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
                var labelRect = (RectTransform)labelObject.transform;
                labelRect.SetParent(rect, false);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;

                var label = labelObject.AddComponent<TextMeshProUGUI>();
                if (font != null) label.font = font;
                label.enableWordWrapping = false;
                label.overflowMode = TextOverflowModes.Ellipsis;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.raycastTarget = false;

                var select = obj.GetComponent<Button>();
                select.targetGraphic = background;
                select.onClick.AddListener(() => onSelect(index));
                MiddleClick.On(obj, () => onClose(index));

                var crossObject = new GameObject("Close", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                var cross = (RectTransform)crossObject.transform;
                cross.SetParent(rect, false);
                cross.anchorMin = cross.anchorMax = new Vector2(1f, 0.5f);
                cross.pivot = new Vector2(1f, 0.5f);

                // The cross is its own click target on top of the tab's, so hitting it closes the
                // tab rather than selecting it. Its image carries the click and stays clear.
                Image crossImage = crossObject.GetComponent<Image>();
                crossImage.color = new Color(1f, 1f, 1f, 0f);

                var crossText = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
                var crossTextRect = (RectTransform)crossText.transform;
                crossTextRect.SetParent(cross, false);
                crossTextRect.anchorMin = Vector2.zero;
                crossTextRect.anchorMax = Vector2.one;
                crossTextRect.offsetMin = crossTextRect.offsetMax = Vector2.zero;

                var crossLabel = crossText.AddComponent<TextMeshProUGUI>();
                if (font != null) crossLabel.font = font;
                crossLabel.text = "×";
                crossLabel.alignment = TextAlignmentOptions.Center;
                crossLabel.raycastTarget = false;

                var close = crossObject.GetComponent<Button>();
                close.targetGraphic = crossImage;
                close.onClick.AddListener(() => onClose(index));

                // The cross has a button of its own, so a middle click landing on it stops there
                // rather than reaching the tab underneath.
                MiddleClick.On(crossObject, () => onClose(index));

                return new Tab(rect, background, label, cross, crossLabel);
            }

            internal void Say(string title)
            {
                if (_label != null && _label.text != title) _label.text = title;
            }

            internal void Put(Slot slot, float height, float crossRoom, float fontSize)
            {
                _root.anchoredPosition = new Vector2(slot.X, 0f);
                _root.sizeDelta = new Vector2(slot.Width, height);

                bool room = slot.Width > crossRoom * 2f;
                if (_cross.gameObject.activeSelf != room) _cross.gameObject.SetActive(room);

                _cross.sizeDelta = new Vector2(crossRoom, height);
                _label.rectTransform.offsetMin = new Vector2(crossRoom * 0.4f, 0f);
                _label.rectTransform.offsetMax = new Vector2(room ? -crossRoom : -crossRoom * 0.4f, 0f);
                _label.fontSize = fontSize;
                _crossLabel.fontSize = fontSize;
            }

            internal void Paint(UI_Theme theme, bool active)
            {
                _background.color = Color.white;
                _label.color = active ? theme.titleText : theme.buttons_text;
                _crossLabel.color = _label.color;

                var button = _root.GetComponent<Button>();
                ColorBlock colors = button.colors;
                colors.normalColor = active ? theme.title : theme.buttons_background;
                colors.highlightedColor = active ? theme.title : theme.buttonsHighlight;
                colors.pressedColor = colors.highlightedColor;
                colors.selectedColor = colors.normalColor;
                button.colors = colors;

                var closeButton = _cross.GetComponent<Button>();
                ColorBlock closeColors = closeButton.colors;
                closeColors.normalColor = new Color(1f, 1f, 1f, 0f);
                closeColors.highlightedColor = new Color32(theme.buttonsHighlight.r, theme.buttonsHighlight.g,
                                                           theme.buttonsHighlight.b, 200);
                closeColors.pressedColor = closeColors.highlightedColor;
                closeColors.selectedColor = closeColors.normalColor;
                closeButton.colors = closeColors;
            }

            internal void Destroy()
            {
                if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
            }
        }
    }
}
