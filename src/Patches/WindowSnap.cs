using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UI.Dialogs;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace UwUTerm.Patches
{
    /// <summary>
    /// Drag-to-edge window snapping, plus modifier+drag to move a window from anywhere.
    ///
    /// Windows are uDialog instances. Moving one runs through uDialog.OnTitleDrag /
    /// OnDialogDrag; resizing runs through uDialog_ResizeListener - separate paths, which
    /// is what lets this snap on move without firing on resize. The release is read from
    /// the mouse button in Update, because uDialog has no drag-end of its own and
    /// Ventana.OnEndDrag turned out not to be wired to anything that reaches us.
    ///
    /// Geometry follows uDialog.Maximize: centre pivot, size via sizeDelta, world-space
    /// position. It is computed in the parent's local rect and converted once, so a
    /// scaled canvas cannot skew it.
    /// </summary>
    internal static class WindowSnap
    {
        private enum Zone { None, Full, Left, Right, TopLeft, TopRight, BottomLeft, BottomRight }

        // Matches uDialog.Maximize's insets, so a snapped window lines up with a maximized one.
        private const float WorkAreaX = 0.99f;
        private const float WorkAreaY = 0.92f;

        private static uDialog _moving;
        private static bool _loggedThisDrag;

        private static uDialog _modifierDrag;
        private static Vector2 _grabOffset;

        private static readonly Dictionary<uDialog, Vector2> PreSnapSize = new Dictionary<uDialog, Vector2>();
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        internal static void Apply(Harmony harmony)
        {
            var onMove = new HarmonyMethod(
                typeof(WindowSnap).GetMethod(nameof(OnMove), BindingFlags.Static | BindingFlags.NonPublic));

            // Two [HarmonyPatch] attributes on one method collapse into a single target,
            // so these are registered by hand.
            foreach (string name in new[] { "OnTitleDrag", "OnDialogDrag" })
            {
                MethodInfo target = AccessTools.Method(typeof(uDialog), name);
                if (target == null)
                {
                    UwUTermPlugin.Log.LogWarning($"snap: uDialog.{name} not found");
                    continue;
                }
                harmony.Patch(target, postfix: onMove);
            }
        }

        internal static void Tick()
        {
            if (!UwUTermPlugin.EnableWindowSnap.Value && !UwUTermPlugin.EnableModifierDrag.Value) return;

            if (Input.GetMouseButtonUp(0))
            {
                uDialog released = _modifierDrag ?? _moving;
                _modifierDrag = null;
                _moving = null;
                _loggedThisDrag = false;
                if (released != null && UwUTermPlugin.EnableWindowSnap.Value) Snap(released);
                return;
            }

            if (!UwUTermPlugin.EnableModifierDrag.Value) return;

            if (_modifierDrag == null)
            {
                if (Input.GetMouseButtonDown(0) && ModifierHeld()) BeginModifierDrag();
            }
            else if (Input.GetMouseButton(0))
            {
                ContinueModifierDrag();
            }
        }

        // ---- moving ----------------------------------------------------------------

        private static void OnMove(uDialog __instance)
        {
            if (!UwUTermPlugin.EnableWindowSnap.Value) return;
            if (ReferenceEquals(_moving, __instance)) return;

            _moving = __instance;
            if (!_loggedThisDrag)
            {
                _loggedThisDrag = true;
                Debug("move: dragging " + __instance.name);
            }

            Unsnap(__instance);
        }

        private static bool ModifierHeld()
        {
            switch (UwUTermPlugin.ModifierDragKey.Value)
            {
                case "alt":   return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                case "shift": return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                default:      return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            }
        }

        private static void BeginModifierDrag()
        {
            uDialog dialog = DialogUnderPointer();
            if (dialog == null) return;

            RectTransform parent = dialog.RectTransform.parent as RectTransform;
            if (parent == null) return;
            if (!TryGetPointer(parent, out Vector2 pointer)) return;

            Unsnap(dialog);

            Rect current = LocalRect(dialog.RectTransform, parent);
            _grabOffset = current.center - pointer;
            _modifierDrag = dialog;
            _moving = dialog;
            dialog.Focus();
            Debug("modifier drag: " + dialog.name);
        }

        /// <summary>
        /// Positioned absolutely from the grab offset rather than accumulated from deltas,
        /// so if uDialog's own drag is moving the same window on the same frame the result
        /// is still exactly one window-width of movement, not two.
        /// </summary>
        private static void ContinueModifierDrag()
        {
            RectTransform rt = _modifierDrag.RectTransform;
            RectTransform parent = rt.parent as RectTransform;
            if (parent == null) { _modifierDrag = null; return; }
            if (!TryGetPointer(parent, out Vector2 pointer)) return;

            Rect current = LocalRect(rt, parent);
            Place(rt, parent, current.size, pointer + _grabOffset);
        }

        // ---- snapping --------------------------------------------------------------

        private static void Snap(uDialog dialog)
        {
            RectTransform rt = dialog.RectTransform;
            RectTransform parent = rt.parent as RectTransform;
            if (parent == null) return;

            if (!TryGetPointer(parent, out Vector2 pointer))
            {
                Debug("release: pointer could not be mapped into " + parent.name);
                return;
            }

            Rect area = parent.rect;
            Zone zone = ZoneFor(area, pointer);
            Debug($"release: area={area} pointer={pointer} zone={zone}");
            if (zone == Zone.None) return;

            if (!PreSnapSize.ContainsKey(dialog)) PreSnapSize[dialog] = rt.sizeDelta;

            Rect target = TargetFor(WorkArea(area), zone);
            Place(rt, parent, target.size, target.center, refreshText: true);
        }

        /// <summary>
        /// Restore a snapped window's old size while keeping it under the cursor: the
        /// pointer holds its horizontal position along the titlebar and the top edge stays
        /// put. Resizing around the centre instead is what threw windows off-screen, out
        /// of reach of their own close button.
        /// </summary>
        private static void Unsnap(uDialog dialog)
        {
            if (!PreSnapSize.TryGetValue(dialog, out Vector2 original)) return;
            PreSnapSize.Remove(dialog);

            RectTransform rt = dialog.RectTransform;
            RectTransform parent = rt.parent as RectTransform;
            if (parent == null) return;

            Rect current = LocalRect(rt, parent);

            if (!TryGetPointer(parent, out Vector2 pointer))
            {
                Place(rt, parent, original, current.center, refreshText: true);
                return;
            }

            float grip = Mathf.Clamp01((pointer.x - current.xMin) / Mathf.Max(1f, current.width));
            grip = Mathf.Clamp(grip, 0.05f, 0.95f);

            float left = pointer.x - grip * original.x;
            float top = current.yMax;
            Place(rt, parent, original, new Vector2(left + original.x / 2f, top - original.y / 2f), refreshText: true);
        }

        // ---- geometry --------------------------------------------------------------

        /// <summary>Size and centre in the parent's local space, whatever the pivot and
        /// anchors happen to be - uDialog moves the pivot around mid-drag.</summary>
        private static Rect LocalRect(RectTransform rt, RectTransform parent)
        {
            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector3 bottomLeft = parent.InverseTransformPoint(corners[0]);
            Vector3 topRight = parent.InverseTransformPoint(corners[2]);
            return new Rect(bottomLeft.x, bottomLeft.y, topRight.x - bottomLeft.x, topRight.y - bottomLeft.y);
        }

        /// <summary>Size and place a window, always leaving it fully on the desktop.</summary>
        private static void Place(RectTransform rt, RectTransform parent, Vector2 size, Vector2 centre, bool refreshText = false)
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
        /// A snap changes a window's size in one jump, and TMP rebuilds its mesh from the
        /// new layout - discarding any per-vertex colouring a component had applied on top,
        /// which is why the mail client loses its address highlighting until you hover a
        /// word and it re-tints. Regenerating the text raises TMP's TEXT_CHANGED, giving
        /// those components the chance to reapply. A drag does not need this: it resizes a
        /// little each frame, so the effects never fall far behind.
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

        private static uDialog DialogUnderPointer()
        {
            if (EventSystem.current == null) return null;

            var pointer = new PointerEventData(EventSystem.current) { position = Input.mousePosition };
            Hits.Clear();
            EventSystem.current.RaycastAll(pointer, Hits);

            for (int i = 0; i < Hits.Count; i++)
            {
                uDialog dialog = Hits[i].gameObject.GetComponentInParent<uDialog>();
                if (dialog != null) return dialog;
            }
            return null;
        }

        private static bool TryGetPointer(RectTransform parent, out Vector2 local)
        {
            Canvas canvas = parent.GetComponentInParent<Canvas>();
            Camera camera = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? canvas.worldCamera
                : null;

            return RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent, Input.mousePosition, camera, out local);
        }

        private static Zone ZoneFor(Rect area, Vector2 p)
        {
            float marginX = Mathf.Max(16f, area.width * UwUTermPlugin.SnapEdgeMargin.Value);
            float marginY = Mathf.Max(16f, area.height * UwUTermPlugin.SnapEdgeMargin.Value);
            float corner = area.height * UwUTermPlugin.SnapCornerBand.Value;

            bool nearLeft = p.x <= area.xMin + marginX;
            bool nearRight = p.x >= area.xMax - marginX;

            if (nearLeft || nearRight)
            {
                bool top = p.y >= area.yMax - corner;
                bool bottom = p.y <= area.yMin + corner;

                if (nearLeft) return top ? Zone.TopLeft : (bottom ? Zone.BottomLeft : Zone.Left);
                return top ? Zone.TopRight : (bottom ? Zone.BottomRight : Zone.Right);
            }

            if (p.y >= area.yMax - marginY && UwUTermPlugin.SnapTopMaximizes.Value) return Zone.Full;

            return Zone.None;
        }

        private static Rect WorkArea(Rect area)
        {
            float w = area.width * WorkAreaX;
            float h = area.height * WorkAreaY;
            return new Rect(area.center.x - w / 2f, area.center.y - h / 2f, w, h);
        }

        private static Rect TargetFor(Rect work, Zone zone)
        {
            float halfW = work.width / 2f;
            float halfH = work.height / 2f;

            switch (zone)
            {
                case Zone.Left:        return new Rect(work.xMin, work.yMin, halfW, work.height);
                case Zone.Right:       return new Rect(work.xMin + halfW, work.yMin, halfW, work.height);
                case Zone.TopLeft:     return new Rect(work.xMin, work.yMin + halfH, halfW, halfH);
                case Zone.TopRight:    return new Rect(work.xMin + halfW, work.yMin + halfH, halfW, halfH);
                case Zone.BottomLeft:  return new Rect(work.xMin, work.yMin, halfW, halfH);
                case Zone.BottomRight: return new Rect(work.xMin + halfW, work.yMin, halfW, halfH);
                default:               return work;
            }
        }

        private static void Debug(string message)
        {
            if (UwUTermPlugin.SnapDebug.Value) UwUTermPlugin.Log.LogInfo("snap " + message);
        }
    }
}
