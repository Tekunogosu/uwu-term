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

        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly Image _background;
        private readonly Outline _outline;
        private readonly TextMeshProUGUI _label;

        internal bool Visible => _root != null && _root.activeSelf;

        private Overlay(GameObject root, RectTransform rect, Image background, Outline outline, TextMeshProUGUI label)
        {
            _root = root;
            _rect = rect;
            _background = background;
            _outline = outline;
            _label = label;
        }

        internal static Overlay CreateTopRight(RectTransform parent, TMP_FontAsset font, float fontSize, Vector2 offset)
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

            var overlay = new Overlay(root, rect, background, outline, label);
            overlay.ApplyTheme();
            return overlay;
        }

        /// <summary>Colours come from the active UI_Theme so the panel tracks whatever theme
        /// the player is running.</summary>
        internal void ApplyTheme()
        {
            UI_Theme theme = OS.GetThemeFromFile();
            if (theme == null) return;

            Color32 background = theme.contextualBackground;
            background.a = 245;
            _background.color = background;
            _outline.effectColor = theme.outline;
            _label.color = theme.contextualText;
        }

        internal void SetText(string markup)
        {
            _label.text = markup;
            _label.ForceMeshUpdate();

            float width = Mathf.Max(120f, _label.preferredWidth + PadX * 2f);
            float height = Mathf.Max(22f, _label.preferredHeight + PadY * 2f);
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
