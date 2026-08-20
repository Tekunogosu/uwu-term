using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Util;

namespace UwUTerm.Ui
{
    /// <summary>
    /// A small floating panel pinned inside a window, built from bare GameObjects rather
    /// than one of the game's prefabs.
    ///
    /// PoolPrefabs would hand us native prefabs by name, but everything it offers is a
    /// full modal dialog - the wrong shape for a corner strip - and depending on prefab
    /// names means a game patch can rename our UI out from under us. Building it here and
    /// pulling the colours from UI_Theme and the font from the terminal gets the native
    /// look without the coupling, and follows the player's theme when they change it.
    /// </summary>
    internal sealed class Overlay
    {
        private const float PadX = 10f;
        private const float PadY = 5f;
        private const float MaxStretchHeight = 110f;

        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly Image _background;
        private readonly Outline _outline;
        private readonly TextMeshProUGUI _label;
        private bool _stretch;
        private int _alpha = 128;

        internal bool Visible => _root != null && _root.activeSelf;

        private Overlay(GameObject root, RectTransform rect, Image background, Outline outline, TextMeshProUGUI label)
        {
            _root = root;
            _rect = rect;
            _background = background;
            _outline = outline;
            _label = label;
        }

        /// <summary>Full-width strip along the bottom of the window, wrapping so a long
        /// list stays inside the text area instead of running off the side.</summary>
        internal static Overlay CreateBottom(RectTransform parent, TMP_FontAsset font, float fontSize, float margin, float height, int alpha)
        {
            Overlay overlay = Build(parent, font, fontSize, alpha);
            overlay._stretch = true;

            RectTransform rect = overlay._rect;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.offsetMin = new Vector2(margin, 0f);
            rect.offsetMax = new Vector2(-margin, height);

            // One line, truncated: the counter says how many candidates there are, so
            // wrapping to a second row costs terminal space to repeat what is already known.
            overlay._label.enableWordWrapping = false;
            overlay._label.overflowMode = TextOverflowModes.Truncate;
            overlay._label.alignment = TextAlignmentOptions.MidlineLeft;
            return overlay;
        }

        internal static Overlay CreateTopRight(RectTransform parent, TMP_FontAsset font, float fontSize, Vector2 offset, int alpha)
        {
            var root = new GameObject("UwUTerm.Overlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Outline));
            var rect = (RectTransform)root.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(160f, 24f);
            root.transform.SetAsLastSibling();

            var background = root.GetComponent<Image>();
            background.raycastTarget = false;

            var outline = root.GetComponent<Outline>();
            outline.effectDistance = new Vector2(1f, -1f);

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(PadX, PadY);
            labelRect.offsetMax = new Vector2(-PadX, -PadY);

            var label = labelObject.AddComponent<TextMeshProUGUI>();
            label.raycastTarget = false;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            if (font != null) label.font = font;
            label.fontSize = fontSize;

            var overlay = new Overlay(root, rect, background, outline, label) { _alpha = alpha };
            overlay.ApplyTheme();
            return overlay;
        }

        private static Overlay Build(RectTransform parent, TMP_FontAsset font, float fontSize, int alpha) =>
            CreateTopRight(parent, font, fontSize, Vector2.zero, alpha);

        /// <summary>Colours come from the active UI_Theme so the panel tracks whatever theme
        /// the player is running.</summary>
        internal void ApplyTheme()
        {
            UI_Theme theme = OS.GetThemeFromFile();
            if (theme == null) return;

            Color32 background = theme.contextualBackground;
            background.a = (byte)Mathf.Clamp(_alpha, 0, 255);
            _background.color = background;

            Color32 edge = theme.outline;
            edge.a = background.a == 0 ? (byte)0 : edge.a;
            _outline.effectColor = edge;

            _label.color = theme.contextualText;
        }

        internal void SetText(string markup)
        {
            _label.text = markup;
            _label.ForceMeshUpdate();

            float height = Mathf.Max(22f, _label.preferredHeight + PadY * 2f);

            // A bottom strip keeps the size it was given - the terminal above it has been
            // shortened to match, and resizing per keystroke would fight that.
            if (_stretch) return;

            float width = Mathf.Max(120f, _label.preferredWidth + PadX * 2f);
            _rect.sizeDelta = new Vector2(width, height);
        }

        internal void Show()
        {
            if (_root == null) return;
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
        }

        internal void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        internal void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
