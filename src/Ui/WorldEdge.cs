using UnityEngine;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Where a rect is actually drawn, in world space.
    ///
    /// anchoredPosition is measured from a pivot each object sets for itself, so adding half a
    /// width to it only finds an edge when the pivot happens to be centred. The corners are
    /// where the object is. On an overlay canvas they are the pixel it is drawn at, which is
    /// the one measurement a scale factor cannot disagree with - and the reason the bar is
    /// checked against these rather than against the numbers it wrote.
    /// </summary>
    internal static class WorldEdge
    {
        private static readonly Vector3[] Corners = new Vector3[4];

        internal static float Left(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Corners[0].x;
        }

        internal static float Right(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Corners[2].x;
        }

        internal static float Bottom(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Corners[0].y;
        }

        internal static float Top(RectTransform rect)
        {
            rect.GetWorldCorners(Corners);
            return Corners[1].y;
        }
    }
}
