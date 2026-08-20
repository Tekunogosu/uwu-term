using UnityEngine;
using UnityEngine.UI;
using Util;

namespace UwUTerm.Ui
{
    /// <summary>
    /// The translucent rectangle showing where a dragged window will land.
    ///
    /// Parented to the desktop rather than to the window being dragged: the window is about
    /// to move and resize, and a preview that moved with it would be showing the wrong
    /// place. Kept as the last sibling so it draws over the windows, and with raycasts off
    /// so it never eats the drag it is describing.
    /// </summary>
    internal sealed class Ghost
    {
        private readonly GameObject _root;
        private readonly RectTransform _rect;

        private Ghost(GameObject root)
        {
            _root = root;
            _rect = (RectTransform)root.transform;
        }

        internal static Ghost Create(RectTransform parent)
        {
            var root = new GameObject("UwUTerm.SnapPreview", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Outline));
            var rect = (RectTransform)root.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);

            UI_Theme theme = OS.GetThemeFromFile();
            var image = root.GetComponent<Image>();
            image.raycastTarget = false;
            image.color = Tint(theme != null ? theme.buttonsHighlight : (Color32)Color.cyan, 46);

            var outline = root.GetComponent<Outline>();
            outline.effectColor = Tint(theme != null ? theme.outline : (Color32)Color.white, 190);
            outline.effectDistance = new Vector2(2f, -2f);

            root.SetActive(false);
            return new Ghost(root);
        }

        private static Color32 Tint(Color32 colour, byte alpha)
        {
            colour.a = alpha;
            return colour;
        }

        internal bool Owns(RectTransform parent) => _root != null && _rect.parent == parent;

        internal void Show(RectTransform parent, Vector2 size, Vector2 centre)
        {
            if (_root == null) return;

            _rect.sizeDelta = size;
            Vector3 world = parent.TransformPoint(new Vector3(centre.x, centre.y, 0f));
            _rect.position = new Vector3(world.x, world.y, _rect.position.z);

            // Only on the way in: reordering siblings dirties the whole canvas, and doing
            // it every frame of a drag is exactly the cost this preview is meant to sit
            // alongside, not add to.
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
                _rect.SetAsLastSibling();
            }
        }

        internal void Hide()
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
        }

        internal void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
