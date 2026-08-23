using TMPro;
using UI.Dialogs;
using UnityEngine;
using UnityEngine.UI;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Where a window is, and how to put it somewhere else.
    ///
    /// Three things move windows - snapping, maximising, and restoring the layout a session was
    /// left in - and a window moved three different ways lands in three slightly different
    /// places. They ask here instead, so a window snapped to fill, a window maximised and a
    /// window brought back all land on the same rectangle.
    ///
    /// The pivot is not to be trusted on the way in: uDialog moves it around mid-drag, so a rect
    /// is read from its corners rather than from anchoredPosition. It is set on the way out,
    /// because a size and a centre only mean one thing once it is.
    /// </summary>
    internal static class WindowGeometry
    {
        private static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>Size and centre in the parent's local space, whatever the pivot and anchors
        /// happen to be.</summary>
        internal static Rect LocalRect(RectTransform rt, RectTransform parent)
        {
            rt.GetWorldCorners(Corners);
            Vector3 bottomLeft = parent.InverseTransformPoint(Corners[0]);
            Vector3 topRight = parent.InverseTransformPoint(Corners[2]);
            return new Rect(bottomLeft.x, bottomLeft.y, topRight.x - bottomLeft.x, topRight.y - bottomLeft.y);
        }

        /// <summary>Size and place a window, always leaving it fully on the desktop.</summary>
        internal static void Place(RectTransform rt, RectTransform parent, Vector2 size, Vector2 centre,
                                   bool refreshText = false)
        {
            Rect area = parent.rect;
            centre.x = (size.x >= area.width)
                ? area.center.x
                : Mathf.Clamp(centre.x, area.xMin + size.x / 2f, area.xMax - size.x / 2f);
            centre.y = (size.y >= area.height)
                ? area.center.y
                : Mathf.Clamp(centre.y, area.yMin + size.y / 2f, area.yMax - size.y / 2f);

            uDialog dialog = rt.GetComponent<uDialog>();
            if (dialog != null) dialog.SetPivot(new Vector2(0.5f, 0.5f));

            rt.sizeDelta = size;
            Vector3 world = parent.TransformPoint(new Vector3(centre.x, centre.y, 0f));
            rt.position = new Vector3(world.x, world.y, rt.position.z);

            if (refreshText) RefreshText(rt);
        }

        /// <summary>
        /// A jump in size rebuilds TMP's mesh from the new layout, discarding any per-vertex
        /// colouring a component had applied on top - which is why the mail client loses its
        /// address highlighting until a word is hovered and re-tinted. Regenerating the text
        /// raises TMP's TEXT_CHANGED, giving those components the chance to reapply. A drag does
        /// not need this: it resizes a little each frame, so the effects never fall far behind.
        /// </summary>
        private static void RefreshText(RectTransform root)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(root);

            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                text.SetAllDirty();
                text.ForceMeshUpdate(true, true);
            }
        }
    }
}
