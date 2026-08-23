using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UI.Dialogs;
using UnityEngine;
using UnityEngine.EventSystems;
using UwUTerm.Ui;
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

        private static Ghost _ghost;

        private static float _dragStarted;
        private static int _dragFrames;

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

            MethodInfo focus = AccessTools.Method(typeof(uDialog), "Focus");
            if (focus != null)
                harmony.Patch(focus, prefix: new HarmonyMethod(
                    typeof(WindowSnap).GetMethod(nameof(SkipRedundantFocus),
                        BindingFlags.Static | BindingFlags.NonPublic)));
            else
                UwUTermPlugin.Log.LogWarning("snap: uDialog.Focus not found");
        }

        /// <summary>
        /// uDialog.DragUpdate calls Focus() on every frame of a drag, and Focus() does
        /// SetAsLastSibling() plus a taskbar update, a context-menu clear and an input
        /// re-activation. Reordering siblings dirties the entire canvas, so each frame of a
        /// drag rebuilds the batches for every window, the desktop and the taskbar - which
        /// is why the cost grows with the number of windows open.
        ///
        /// The window is already frontmost after the first frame, so the rest are pure
        /// waste. Only redundant calls during a drag are skipped: a real focus change, or a
        /// click on an unfocused window, still runs the original untouched.
        /// </summary>
        private static bool SkipRedundantFocus(uDialog __instance)
        {
            if (!UwUTermPlugin.SkipDragFocus.Value) return true;
            if (!ReferenceEquals(_moving, __instance) && !ReferenceEquals(_modifierDrag, __instance)) return true;

            return !__instance.IsFocused();
        }

        internal static void Tick()
        {
            Hotkeys();

            // Not gated on either feature: the release is what clears _moving, and a drag
            // left marked as still running would keep Focus() skipped for good.
            if (Input.GetMouseButtonUp(0))
            {
                _ghost?.Hide();
                EndDragTiming();
                uDialog released = _modifierDrag ?? _moving;
                _modifierDrag = null;
                _moving = null;
                _loggedThisDrag = false;
                if (released != null && UwUTermPlugin.FeatureWindows.Value) Snap(released);
                return;
            }

            uDialog dragging = _modifierDrag ?? _moving;
            Preview(dragging);
            if (dragging != null)
            {
                if (_dragFrames == 0) _dragStarted = Time.realtimeSinceStartup;
                _dragFrames++;
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

        /// <summary>
        /// Which window the pointer is dragging is a fact about input, not a feature: the
        /// focus skip needs it whether or not snapping is switched on, so it is recorded
        /// unconditionally and only the snapping work below reads the setting.
        /// </summary>
        private static void OnMove(uDialog __instance)
        {
            if (ReferenceEquals(_moving, __instance)) return;

            _moving = __instance;
            if (!_loggedThisDrag)
            {
                _loggedThisDrag = true;
                Debug("move: dragging " + __instance.name);
            }

            if (UwUTermPlugin.FeatureWindows.Value) Unsnap(__instance);
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

            Rect current = WindowGeometry.LocalRect(dialog.RectTransform, parent);
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

            Rect current = WindowGeometry.LocalRect(rt, parent);
            WindowGeometry.Place(rt, parent, current.size, pointer + _grabOffset);
        }

        private static void EndDragTiming()
        {
            if (_dragFrames <= 0) return;

            if (UwUTermPlugin.SnapDebug.Value)
            {
                float seconds = Time.realtimeSinceStartup - _dragStarted;
                Debug($"drag: {_dragFrames} frames in {seconds:F2}s, {_dragFrames / Mathf.Max(0.0001f, seconds):F0} fps");
            }
            _dragFrames = 0;
        }

        /// <summary>Track the pointer during a drag and outline where a release would put
        /// the window.</summary>
        private static void Preview(uDialog dragged)
        {
            if (!UwUTermPlugin.FeatureWindows.Value || !UwUTermPlugin.SnapPreview.Value)
            {
                _ghost?.Hide();
                return;
            }

            if (dragged == null) { _ghost?.Hide(); return; }

            RectTransform parent = dragged.RectTransform.parent as RectTransform;
            if (parent == null) { _ghost?.Hide(); return; }

            if (!TryGetPointer(parent, out Vector2 pointer)) { _ghost?.Hide(); return; }

            Rect area = parent.rect;
            Zone zone = ZoneFor(area, pointer);
            if (zone == Zone.None) { _ghost?.Hide(); return; }

            if (_ghost == null || !_ghost.Owns(parent))
            {
                _ghost?.Destroy();
                _ghost = Ghost.Create(parent);
            }

            Rect target = TargetFor(WorkArea(area), zone);
            _ghost.Show(parent, target.size, target.center);
        }

        // ---- keyboard ----------------------------------------------------------------

        /// <summary>
        /// Ctrl+Alt+Shift plus a key snaps the focused window. Quadrants follow the maths
        /// convention - 1 top right, counter-clockwise from there - rather than reading
        /// order, so 1..4 map to the same corners they do on an x-y axis.
        ///
        /// Read from Input rather than the terminal's key handler because it has to work
        /// whatever window is focused, including ones that never see a keystroke of ours.
        /// </summary>
        private static void Hotkeys()
        {
            if (!UwUTermPlugin.FeatureWindows.Value || !UwUTermPlugin.EnableSnapHotkeys.Value) return;

            Zone zone = HotkeyZone();
            if (zone == Zone.None) return;

            uDialog focused = Focused();
            if (focused == null) return;

            Debug($"hotkey: {zone} -> {focused.name}");
            SnapTo(focused, zone);
        }

        private static Zone HotkeyZone()
        {
            if (UwUTermPlugin.SnapLeft.Value.IsDown()) return Zone.Left;
            if (UwUTermPlugin.SnapRight.Value.IsDown()) return Zone.Right;
            if (UwUTermPlugin.SnapFull.Value.IsDown()) return Zone.Full;
            if (UwUTermPlugin.SnapQuadrant1.Value.IsDown()) return Zone.TopRight;
            if (UwUTermPlugin.SnapQuadrant2.Value.IsDown()) return Zone.TopLeft;
            if (UwUTermPlugin.SnapQuadrant3.Value.IsDown()) return Zone.BottomLeft;
            if (UwUTermPlugin.SnapQuadrant4.Value.IsDown()) return Zone.BottomRight;
            return Zone.None;
        }

        /// <summary>The frontmost window: uDialog orders by sibling index and IsFocused
        /// reports whether it is the last active one.</summary>
        private static uDialog Focused()
        {
            foreach (uDialog dialog in Object.FindObjectsOfType<uDialog>())
                if (dialog != null && dialog.isVisible && dialog.IsFocused()) return dialog;

            return null;
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

            SnapTo(dialog, zone);
        }

        private static void SnapTo(uDialog dialog, Zone zone)
        {
            RectTransform rt = dialog.RectTransform;
            RectTransform parent = rt.parent as RectTransform;
            if (parent == null) return;

            if (!PreSnapSize.ContainsKey(dialog)) PreSnapSize[dialog] = rt.sizeDelta;

            Rect target = TargetFor(WorkArea(parent.rect), zone);
            WindowGeometry.Place(rt, parent, target.size, target.center, refreshText: true);
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

            Rect current = WindowGeometry.LocalRect(rt, parent);

            if (!TryGetPointer(parent, out Vector2 pointer))
            {
                WindowGeometry.Place(rt, parent, original, current.center, refreshText: true);
                return;
            }

            float grip = Mathf.Clamp01((pointer.x - current.xMin) / Mathf.Max(1f, current.width));
            grip = Mathf.Clamp(grip, 0.05f, 0.95f);

            float left = pointer.x - grip * original.x;
            float top = current.yMax;
            WindowGeometry.Place(rt, parent, original, new Vector2(left + original.x / 2f, top - original.y / 2f), refreshText: true);
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

        /// <summary>
        /// The part of the desktop a window may use.
        ///
        /// With the game's own two bars this is its maximise inset - 99% by 92%, centred,
        /// because a bar takes the same strip off the top as off the bottom. With one bar
        /// along the top the space is neither that size nor centred, so WindowArea works it
        /// out from the bar itself and both paths agree on where "filled" is.
        /// </summary>
        private static Rect WorkArea(Rect area)
        {
            if (Ui.DesktopArea.Claimed) return WindowArea.Area(area);

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
