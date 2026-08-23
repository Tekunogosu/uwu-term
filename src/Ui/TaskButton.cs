using TMPro;
using UI.Dialogs;
using UnityEngine;
using UnityEngine.UI;
using UwUTerm.Bar;

namespace UwUTerm.Ui
{
    /// <summary>
    /// One window's button in the bar.
    ///
    /// Built once when its window opens and kept until the window closes. The game's own bar
    /// destroyed and re-instantiated every button whenever anything happened to any window, so
    /// a row of ten windows meant ten GameObjects thrown away and ten made on every focus
    /// change - which is what made dragging a window with several open so expensive. Here the
    /// only thing that happens on a focus change is a colour.
    ///
    /// It carries the game's own <see cref="uDialog_TaskBar_Task"/>, which is what makes the
    /// alt-tab switcher work unaltered: the switcher finds its list by asking the taskbar object
    /// for that component, and clicking is that component's own behaviour rather than a copy of
    /// it living here.
    /// </summary>
    internal sealed class TaskButton
    {
        /// <summary>Room for the icon at the left of a button, in pixels before scaling.</summary>
        private const float IconRoom = 26f;

        /// <summary>How long a button takes to light up under the pointer. Short enough to feel
        /// like a response and long enough not to flicker while the pointer crosses a row.</summary>
        private const float HoverFade = 0.08f;

        private readonly RectTransform _root;
        private readonly Image _background;
        private readonly RectTransform _icon;
        private readonly TMP_Text _label;
        private readonly uDialog_TaskBar_Task _task;

        private bool _focused;
        private UI_Theme _theme;

        private TaskButton(RectTransform root, Image background, RectTransform icon,
                           TMP_Text label, uDialog_TaskBar_Task task)
        {
            _root = root;
            _background = background;
            _icon = icon;
            _label = label;
            _task = task;
        }

        internal static TaskButton Make(RectTransform strip, uDialog window, uDialog_TaskBar bar,
                                        TMP_FontAsset font, UI_Theme theme)
        {
            var root = new GameObject("Task", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rect = (RectTransform)root.transform;
            rect.SetParent(strip, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);

            var background = root.GetComponent<Image>();

            var iconObject = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var icon = (RectTransform)iconObject.transform;
            icon.SetParent(rect, false);
            icon.anchorMin = icon.anchorMax = new Vector2(0f, 0.5f);
            icon.pivot = new Vector2(0f, 0.5f);

            var labelObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);

            var label = labelObject.AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font;
            label.enableWordWrapping = false;

            // A name too long for the button is cut short rather than drawn over its neighbour.
            // The row can be squeezed to a fraction of a name's width and this is what keeps it
            // inside the button it belongs to.
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.raycastTarget = false;

            var task = root.AddComponent<uDialog_TaskBar_Task>();
            task.IsTemplate = false;
            task.TaskBar = bar;
            task.GO_TextComponent = label;
            task.iconImg = iconObject.GetComponent<Image>();
            task.SetDialog(window);

            var button = root.AddComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(task.Clicked);

            var made = new TaskButton(rect, background, icon, label, task);
            made.Paint(theme);
            return made;
        }

        /// <summary>Keep up with a window that has been renamed, minimised or focused. All of it
        /// is a write to something that already exists.</summary>
        internal void Follow(bool focused, UI_Theme theme)
        {
            if (_task == null || _task.Dialog == null) return;

            if (_label != null && _label.text != _task.Dialog.TitleText)
                _label.text = _task.Dialog.TitleText;

            if (_focused == focused) return;

            _focused = focused;
            Paint(theme);
        }

        internal void Put(Slot slot, float height)
        {
            float scale = UwUTermPlugin.BarScale.Value;
            float room = IconRoom * scale;

            _root.anchoredPosition = new Vector2(slot.X, 0f);
            _root.sizeDelta = new Vector2(slot.Width, height);

            float icon = Mathf.Min(height * 0.6f, room);
            _icon.sizeDelta = new Vector2(icon, icon);
            _icon.anchoredPosition = new Vector2(_root.sizeDelta.x > room ? (room - icon) * 0.5f : 0f, 0f);

            // The label starts after the icon and ends at the button's edge, so a button squeezed
            // narrow gives its name less room rather than drawing it wider than itself.
            float text = _icon.sizeDelta.x > 0f && slot.Width > room ? room : 0f;
            _label.rectTransform.offsetMin = new Vector2(text, 0f);
            _label.rectTransform.offsetMax = new Vector2(-4f * scale, 0f);
            _label.fontSize = UwUTermPlugin.BarFontSize.Value * scale;
        }

        /// <summary>Where this button is drawn, for a dump taken while somebody is looking at
        /// the bar.</summary>
        internal void Say(System.Text.StringBuilder sb, int index)
        {
            sb.Append($"topbar:   [{index}] '{(_task != null && _task.Dialog != null ? _task.Dialog.name : "?")}' ")
              .Append($"drawn {DesktopBar.LeftEdgeWorld(_root):F0}..{DesktopBar.RightEdgeWorld(_root):F0}, ")
              .Append($"at ({_root.anchoredPosition.x:F0},{_root.anchoredPosition.y:F0}) ")
              .Append($"size {_root.sizeDelta.x:F0}x{_root.sizeDelta.y:F0}\n");
        }

        /// <summary>Stand out in a screenshot, so a picture can name this object without a
        /// measurement being believed first.</summary>
        internal void Tint(bool first)
        {
            if (_background == null) return;

            if (!first) { Paint(_theme); return; }

            _background.color = new Color(1f, 0.9f, 0.1f, 1f);

            // Plain white in the block, so what is seen is the tint rather than the tint through
            // the theme.
            var button = _root.GetComponent<Button>();
            if (button == null) return;

            ColorBlock colors = button.colors;
            colors.normalColor = colors.highlightedColor = colors.pressedColor = colors.selectedColor = Color.white;
            button.colors = colors;
        }

        /// <summary>
        /// The player's colours, put where Unity's own hover can use them.
        ///
        /// A button tints its graphic through its colour block, so the graphic is left white and
        /// the theme's colours go into the block instead. That is what makes hovering show: the
        /// same mechanism the game's own buttons use, rather than a colour written once that
        /// nothing can vary.
        /// </summary>
        internal void Paint(UI_Theme theme)
        {
            if (theme == null) return;

            _theme = theme;

            if (_background != null) _background.color = Color.white;
            if (_label != null) _label.color = theme.taskbar_text;
            if (_task != null && _task.iconImg != null) _task.iconImg.color = theme.taskbar_icons;

            var button = _root != null ? _root.GetComponent<Button>() : null;
            if (button == null) return;

            ColorBlock colors = button.colors;
            colors.normalColor = _focused ? theme.taskbar_focused : theme.taskbar_task;
            colors.highlightedColor = _focused ? theme.taskbar_focused_highlight : theme.taskbar_task_highlight;
            colors.pressedColor = colors.highlightedColor;
            colors.selectedColor = colors.normalColor;
            colors.fadeDuration = HoverFade;
            button.colors = colors;
        }

        internal void Destroy()
        {
            if (_root != null) Object.Destroy(_root.gameObject);
        }
    }
}
