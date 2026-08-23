using System.Text;
using TMPro;
using UI.Dialogs;
using UnityEngine;
using UnityEngine.UI;

namespace UwUTerm.Ui
{
    /// <summary>
    /// The whole top bar, laid out on one ruler.
    ///
    /// Everything in the bar is positioned against a different thing - the menu button against
    /// the left edge, the widget group against the right, the task strip against both, each
    /// child of a layout group against its siblings - and a rect is only as good a description
    /// of an object as its own contents make it. A group whose rect does not bound its children
    /// reads as narrow to anything measuring it while drawing something much wider, and that
    /// difference is invisible from either side.
    ///
    /// So every part is reported as a span in pixels from the left edge of the bar: one ruler,
    /// parents beside their children, whatever each is anchored to. What overlaps what is then
    /// a matter of reading two numbers rather than working out two coordinate spaces.
    ///
    /// Measured a couple of frames after the row changes shape, since a layout group applies at
    /// the end of a frame and reading it back in the same one describes the row before.
    /// </summary>
    internal static class TopBarReport
    {
        /// <summary>Frames to let the layout settle before believing what it says.</summary>
        private const int Settle = 2;

        /// <summary>How far under the bar to describe. Deep enough for the widget group's own
        /// contents, which is where the parts that get covered live.</summary>
        private const int Depth = 3;

        private static int _shape;
        private static float _width;
        private static int _measureAt;

        /// <summary>Describe the bar again when the row changes shape, once it has settled.
        /// Between changes this costs two comparisons - the dissection itself walks the bar and
        /// is not something to do while the row is still moving.</summary>
        internal static void WhenChanged(RectTransform top, RectTransform strip)
        {
            if (top == null || strip == null || !UwUTermPlugin.ScreenDebug.Value) return;

            int count = Buttons(strip);
            if (count != _shape || !Mathf.Approximately(strip.rect.width, _width))
            {
                _shape = count;
                _width = strip.rect.width;
                _measureAt = Time.frameCount + Settle;
                return;
            }

            if (Time.frameCount != _measureAt) return;

            Dissect(top, strip);
        }

        /// <summary>
        /// Catch an overlap whenever it happens, rather than when the row changes shape.
        ///
        /// The dissection samples a settled row, so anything that covers the widgets while the
        /// row is being rebuilt - a button at the width it was instantiated with, before it has
        /// been told a narrower one - is over before it is ever looked at. This runs every frame
        /// and reports the first overlap of each size, so a fault lasting one frame still leaves
        /// a record naming what did it.
        ///
        /// It walks the last button rather than all of them: the row is laid out left to right,
        /// so the rightmost thing drawn is in the last one.
        /// </summary>
        internal static void Watch(RectTransform top, RectTransform strip, float widgets, float world)
        {
            if (top == null || strip == null || !UwUTermPlugin.ScreenDebug.Value) return;

            int count = 0;
            for (int i = 0; i < strip.childCount; i++)
                if (DesktopBar.IsTaskButton(strip.GetChild(i))) count++;

            float drawn = float.NegativeInfinity, drawnWorld = float.NegativeInfinity;
            string worst = "nothing";

            // Everything the strip draws, not everything it draws that we recognise. Sizing a
            // row of buttons and measuring the same row are both filtered by what counts as a
            // task button, so a child that is neither would be missed twice over - laid out by
            // the group, drawn on the screen, and absent from both sides of our arithmetic.
            foreach (Graphic graphic in strip.GetComponentsInChildren<Graphic>(true))
            {
                if (!graphic.gameObject.activeInHierarchy) continue;

                float part = DrawnRight(graphic);
                if (part <= drawnWorld) continue;

                drawnWorld = part;
                drawn = Right(graphic.rectTransform, top);
                worst = graphic.transform.parent != null && graphic.transform.parent != strip
                    ? graphic.name + " of " + graphic.transform.parent.name
                    : graphic.name;
            }

            if (float.IsInfinity(drawnWorld)) return;

            float edge = widgets - top.rect.xMin;

            // Judged on what is drawn, not on what the rects work out to. The two agree only
            // while every object on both sides is scaled the same, and whether they are is the
            // question - so the pixels decide and the rects are reported beside them.
            int overlap = Mathf.RoundToInt(drawnWorld - world);
            if (overlap <= 1) return;
            if (_seen == overlap && _seenCount == count) return;

            _seen = overlap;
            _seenCount = count;

            UwUTermPlugin.Log.LogWarning(
                $"topbar: {count} buttons - '{worst}' is drawn to screen {drawnWorld:F0}, " +
                $"over the widgets at screen {world:F0} by {overlap}px. " +
                $"In bar space that is {drawn:F0} against {edge:F0}, " +
                $"a {Mathf.RoundToInt(drawn - edge)}px overlap. " +
                $"Strip {Span(strip, top)}, {count} buttons counted of " +
                $"{strip.childCount} children");
        }

        private static int _seen = -1;
        private static int _seenCount = -1;

        internal static void Dissect(RectTransform top, RectTransform strip, bool asked = false)
        {
            if (top == null || strip == null) return;
            if (!asked && !UwUTermPlugin.ScreenDebug.Value) return;

            var sb = new StringBuilder(8192);
            float ruler = top.rect.width;

            // Marked, so a dump taken while somebody is looking at the fault can be told apart
            // from the ones the log takes on its own.
            if (asked) sb.Append("topbar: ASKED FOR BY HAND - the bar as it is on screen now\n");

            sb.Append($"topbar: {ruler:F0}px wide, {UnityEngine.Screen.width}x{UnityEngine.Screen.height} screen, ")
              .Append($"drawn by {Scale(top)}\n")
              .Append("topbar: spans below are bar-space, then the screen pixels they land on\n");

            // The strip first: it is a sibling of the bar rather than a child, so it would not
            // be reached by walking the bar, and it is the thing everything else is compared to.
            sb.Append($"topbar: [strip] {Span(strip, top)} - the task buttons live here\n");
            Buttons(strip, top, sb);

            Children(strip, top, sb);

            sb.Append("topbar: [bar]\n");
            Describe(top, top, sb, 1);

            Verdict(top, strip, sb);

            UwUTermPlugin.Log.LogInfo(sb.ToString());
        }

        /// <summary>Where the row actually ends, against where it is allowed to end. The buttons
        /// and what they draw are reported apart: a label wider than its button is drawn past
        /// the button's edge while every button measures as fitting.</summary>
        private static void Buttons(RectTransform strip, RectTransform top, StringBuilder sb)
        {
            int count = 0;
            float buttons = float.NegativeInfinity, drawn = float.NegativeInfinity;
            float drawnWorld = float.NegativeInfinity;
            string worst = "nothing", inked = "nothing";

            for (int i = 0; i < strip.childCount; i++)
            {
                Transform child = strip.GetChild(i);
                if (!DesktopBar.IsTaskButton(child)) continue;

                count++;
                var rect = (RectTransform)child;
                buttons = Mathf.Max(buttons, Right(rect, top));

                foreach (Graphic graphic in child.GetComponentsInChildren<Graphic>(true))
                {
                    if (!graphic.gameObject.activeInHierarchy) continue;

                    float edge = Right(graphic.rectTransform, top);
                    if (edge > drawn) { drawn = edge; worst = graphic.name; }

                    float ink = DrawnRight(graphic);
                    if (ink <= drawnWorld) continue;

                    drawnWorld = ink;
                    inked = graphic is TMP_Text label
                        ? $"'{label.text}' in {graphic.name} ({label.overflowMode})"
                        : graphic.name;
                }
            }

            if (count == 0) { sb.Append("topbar:   no task buttons\n"); return; }

            var first = (RectTransform)null;
            for (int i = 0; i < strip.childCount && first == null; i++)
                if (DesktopBar.IsTaskButton(strip.GetChild(i))) first = (RectTransform)strip.GetChild(i);

            sb.Append($"topbar:   ink reaches screen {drawnWorld:F0} - {inked}\n")
              .Append($"topbar:   {count} buttons of {first.rect.width:F0}px, ")
              .Append($"first {Span(first, top)}, ")
              .Append($"buttons end at {buttons:F0}, drawing ends at {drawn:F0} ('{worst}')\n");
        }

        /// <summary>
        /// Everything in the strip, counted or not, and why.
        ///
        /// The layout group lays out every active child it has. Ours are picked out of those by
        /// a test, and anything the test rejects is still laid out and still drawn - so what the
        /// test rejected is exactly what a row that measures as fitting and does not fit would
        /// be made of.
        /// </summary>
        private static void Children(RectTransform strip, RectTransform top, StringBuilder sb)
        {
            sb.Append($"topbar: [strip children] {strip.childCount} in all\n");

            for (int i = 0; i < strip.childCount; i++)
            {
                if (strip.GetChild(i) is not RectTransform child) continue;

                var task = child.GetComponent<uDialog_TaskBar_Task>();
                string why = !child.gameObject.activeSelf ? "off"
                    : task == null ? "COUNTS FOR NOTHING - no task component"
                    : task.IsTemplate ? "template"
                    : "counted";

                sb.Append($"topbar:   [{i}] {child.name} - {why}  {Span(child, top)}")
                  .Append(task != null && task.Dialog != null ? $"  for '{task.Dialog.name}'" : "")
                  .Append('\n');
            }
        }

        private static int Buttons(RectTransform strip)
        {
            int count = 0;
            for (int i = 0; i < strip.childCount; i++)
                if (DesktopBar.IsTaskButton(strip.GetChild(i))) count++;

            return count;
        }

        /// <summary>Every part of the bar, with what places it and what sizes it - the levers
        /// anything moving it would have to pull.</summary>
        private static void Describe(Transform parent, RectTransform top, StringBuilder sb, int depth)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i) is not RectTransform rect) continue;

                sb.Append(' ', depth * 2 + 7).Append(rect.name);
                if (!rect.gameObject.activeSelf) sb.Append(" [off]");

                sb.Append($"  {Span(rect, top)}  ")
                  .Append($"anchor({rect.anchorMin.x:F2}-{rect.anchorMax.x:F2}) ")
                  .Append($"pivot({rect.pivot.x:F2}) ")
                  .Append($"pos({rect.anchoredPosition.x:F0})");

                string places = Places(rect);
                if (places.Length > 0) sb.Append("  ").Append(places);

                sb.Append('\n');

                if (depth < Depth) Describe(rect, top, sb, depth + 1);
            }
        }

        /// <summary>What decides this object's width and where it sits: the group laying its
        /// children out, a fitter sizing it to its own contents, and the numbers a parent group
        /// reads off it.</summary>
        private static string Places(RectTransform rect)
        {
            var parts = new StringBuilder();

            if (rect.GetComponent<HorizontalOrVerticalLayoutGroup>() is { } line)
                parts.Append($"[{line.GetType().Name} spacing {line.spacing} ")
                     .Append($"pad L{line.padding.left} R{line.padding.right} ")
                     .Append($"controlW {line.childControlWidth} expandW {line.childForceExpandWidth}] ");

            if (rect.GetComponent<ContentSizeFitter>() is { } fitter)
                parts.Append($"[fitter {fitter.horizontalFit}] ");

            if (rect.GetComponent<LayoutElement>() is { } element)
                parts.Append($"[element min {element.minWidth:F0} pref {element.preferredWidth:F0} ")
                     .Append($"flex {element.flexibleWidth:F1}] ");

            if (rect.parent != null && rect.parent.GetComponent<LayoutGroup>() != null)
                parts.Append($"[group reads min {LayoutUtility.GetMinWidth(rect):F0} ")
                     .Append($"pref {LayoutUtility.GetPreferredWidth(rect):F0} ")
                     .Append($"flex {LayoutUtility.GetFlexibleWidth(rect):F1}] ");

            return parts.ToString();
        }

        /// <summary>
        /// The one comparison the strip cannot make for itself: the leftmost thing drawn in the
        /// right-hand half of the bar, against where the row of buttons ends. The strip is held
        /// clear by measuring one rect, so anything drawn left of that rect's edge is covered
        /// without either side noticing.
        /// </summary>
        private static void Verdict(RectTransform top, RectTransform strip, StringBuilder sb)
        {
            float ends = Right(strip, top);
            float leftmost = float.PositiveInfinity;
            string owner = "nothing";

            foreach (Graphic graphic in top.GetComponentsInChildren<Graphic>(true))
            {
                if (!graphic.gameObject.activeInHierarchy) continue;

                RectTransform rect = graphic.rectTransform;

                // The bar's own background spans the whole width and is behind everything, so it
                // is not something the row can cover.
                if (rect == top) continue;

                float left = Left(rect, top);
                if (left < top.rect.width * 0.5f || left >= leftmost) continue;

                leftmost = left;
                owner = graphic.name;
            }

            if (float.IsInfinity(leftmost)) { sb.Append("topbar: nothing drawn in the right half\n"); return; }

            float slack = leftmost - ends;
            sb.Append($"topbar: strip ends at {ends:F0}, leftmost thing on the right is ")
              .Append($"'{owner}' at {leftmost:F0} - ")
              .Append(slack >= 0f ? $"{slack:F0}px of clearance" : $"OVERLAPPING by {-slack:F0}px");
        }

        /// <summary>
        /// The rightmost pixel an object actually puts on the screen.
        ///
        /// A rect is where an object is allowed to draw, not where it does. Text is the one
        /// that parts company with its rect: a glyph run wider than the box it was given is
        /// still drawn unless the overflow mode says otherwise, and no measurement of corners
        /// will ever see it. So for text the mesh is measured instead of the box around it.
        /// </summary>
        private static float DrawnRight(Graphic graphic)
        {
            float box = DesktopBar.RightEdgeWorld(graphic.rectTransform);
            if (graphic is not TMP_Text text) return box;

            text.ForceMeshUpdate();
            if (text.textInfo == null || text.textInfo.characterCount == 0) return box;

            return graphic.transform.TransformPoint(new Vector3(text.textBounds.max.x, 0f, 0f)).x;
        }

        private static string Span(RectTransform rect, RectTransform top) =>
            $"x {Left(rect, top):F0}..{Right(rect, top):F0} ({rect.rect.width:F0}px) " +
            $"screen {DesktopBar.LeftEdgeWorld(rect):F0}..{DesktopBar.RightEdgeWorld(rect):F0}";

        /// <summary>The canvas an object is drawn by, and what it scales by. Two objects under
        /// canvases with different factors sit where their own rects say and are drawn somewhere
        /// else entirely, which no amount of rect arithmetic will show.</summary>
        private static string Scale(RectTransform rect)
        {
            var canvas = rect.GetComponentInParent<Canvas>();
            return canvas != null
                ? $"{canvas.name} x{canvas.scaleFactor:F2} (lossy {rect.lossyScale.x:F2})"
                : $"no canvas (lossy {rect.lossyScale.x:F2})";
        }

        private static float Left(RectTransform rect, RectTransform top) =>
            DesktopBar.LeftEdgeIn(rect, top) - top.rect.xMin;

        private static float Right(RectTransform rect, RectTransform top) =>
            DesktopBar.RightEdgeIn(rect, top) - top.rect.xMin;
    }
}
