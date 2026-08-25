using System.Collections.Generic;
using UI.Dialogs;
using UnityEngine;
using UwUTerm.Ui;

namespace UwUTerm.Browser
{
    /// <summary>
    /// A row of browser windows shown one at a time, which is what a tab is here.
    ///
    /// A tab keeps its page because it *is* a browser: its own <c>HtmlBrowser</c>, its own PowerUI
    /// document, its own back/forward list, its own logged-in bank panel. Nothing is saved and
    /// restored across a switch, so there is nothing that can be saved incompletely - the members
    /// of a group differ only in which one the player can see.
    ///
    /// That shape is not a preference. <c>NetworkHelperServer.AccessWebServerRpc</c> looks the
    /// window's PID up in the server's process list and answers <c>ERROR_UNKPROC</c> when it is not
    /// there, so a tab has to be a real <c>Browser.exe</c> - a real process, with the RAM and the
    /// <c>ps</c> entry that implies. A tab invented on the client could not load a page at all.
    ///
    /// A hidden member is never deactivated. <c>PlayerClientMethods.GetVentana</c> finds the window
    /// a server reply belongs to with <c>GetComponentsInChildren&lt;Ventana&gt;()</c>, which does not
    /// look at inactive objects - so a tab switched away from mid-load would have its page quietly
    /// dropped and never ask again. It stays active and loading, and is hidden through the dialog's
    /// CanvasGroup instead.
    ///
    /// The animator goes off with it. uDialog leaves its animator enabled holding the last frame of
    /// whatever clip played, and those clips animate alpha - so an alpha written once is an alpha
    /// that lasts until the next frame. The active member is left exactly as the game made it, and
    /// only the hidden ones are held down, re-asserted every tick rather than written once.
    /// </summary>
    internal sealed class TabGroup
    {
        private readonly List<uDialog> _members = new List<uDialog>();
        private int _active;

        internal int Count => _members.Count;
        internal int ActiveIndex => _active;
        internal IList<uDialog> Members => _members;

        internal uDialog Active =>
            _active >= 0 && _active < _members.Count ? _members[_active] : null;

        internal bool Holds(uDialog window) => _members.Contains(window);

        /// <summary>Whether this window is a member being kept out of sight. A group of one has no
        /// hidden member: its only window is the one on screen.</summary>
        internal bool IsBackground(uDialog window) =>
            window != null && window != Active && _members.Contains(window);

        /// <summary>Take a window into the group and show it, which is what opening a tab does.
        /// It lands on the rectangle the group already occupies, so the window does not appear to
        /// move when the player switches back.</summary>
        internal void Adopt(uDialog window)
        {
            if (window == null || _members.Contains(window)) return;

            uDialog from = Active;
            _members.Add(window);
            _active = _members.Count - 1;

            // Raised either way. A window prepared to open as a tab has had its show animation
            // dropped, and that animation is the only thing that would have restored the alpha a
            // hidden window was built with - so a window nothing ends up switching to is a window
            // that never appears.
            if (from == null) { Appear(window); return; }

            CopyPlace(from, window);
            Conceal(from);
            Reveal(window);
        }

        internal void Activate(int index)
        {
            if (index < 0 || index >= _members.Count || index == _active) return;

            uDialog from = Active;
            uDialog to = _members[index];
            _active = index;

            if (from == to) return;
            if (from != null) { CopyPlace(from, to); Conceal(from); }
            Reveal(to);
        }

        internal void Activate(uDialog window)
        {
            int index = _members.IndexOf(window);
            if (index >= 0) Activate(index);
        }

        /// <summary>Drop a window that has gone - closed by us, closed by the game, or destroyed
        /// with the desktop. The tab beside it takes over, the way a browser hands focus on.</summary>
        internal void Forget(uDialog window)
        {
            int index = _members.IndexOf(window);
            if (index < 0) return;

            bool wasActive = index == _active;
            _members.RemoveAt(index);

            if (_members.Count == 0) { _active = 0; return; }

            if (index < _active || _active >= _members.Count) _active--;
            if (_active < 0) _active = 0;

            if (wasActive) Reveal(Active);
        }

        /// <summary>Members destroyed without telling us - a window torn down with the scene, or a
        /// close that never reached our seam.</summary>
        internal bool Prune()
        {
            bool changed = false;
            bool lostActive = false;

            for (int i = _members.Count - 1; i >= 0; i--)
            {
                if (_members[i] != null) continue;

                if (i == _active) lostActive = true;
                else if (i < _active) _active--;

                _members.RemoveAt(i);
                changed = true;
            }

            if (_active >= _members.Count) _active = _members.Count - 1;
            if (_active < 0) _active = 0;

            // Only when the window that was on screen is the one that went: revealing raises the
            // window, and a group nobody touched must not jump in front of what the player is on.
            if (lostActive) Reveal(Active);
            return changed;
        }

        /// <summary>Hold every hidden member down, every frame. An animator that re-asserts the
        /// frame it is holding would otherwise fade a background tab back into view.</summary>
        internal void Hold()
        {
            uDialog active = Active;
            for (int i = 0; i < _members.Count; i++)
                if (_members[i] != null && _members[i] != active) Conceal(_members[i]);
        }

        private static void Reveal(uDialog window)
        {
            Appear(window);
            if (window != null) window.Focus();
        }

        /// <summary>Make a window visible without raising it. A window the game is holding down -
        /// minimised, or on its way out - is left alone: only the animation it plays knows when it
        /// is due back.</summary>
        private static void Appear(uDialog window)
        {
            if (window == null || !window.isVisible) return;

            CanvasGroup group = window.CanvasGroup;
            if (group != null)
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }

            if (window.Animator != null) window.Animator.enabled = true;
        }

        private static void Conceal(uDialog window)
        {
            if (window == null) return;

            // Before the CanvasGroup, so the alpha written below is the last word on it.
            if (window.Animator != null && window.Animator.enabled) window.Animator.enabled = false;

            CanvasGroup group = window.CanvasGroup;
            if (group == null) return;

            if (group.alpha != 0f) group.alpha = 0f;
            if (group.interactable) group.interactable = false;
            if (group.blocksRaycasts) group.blocksRaycasts = false;
        }

        /// <summary>Put one member exactly where another is. Only the visible member's geometry is
        /// ever the group's, so this runs at the moment the visible one changes rather than every
        /// frame for windows nobody is looking at.</summary>
        private static void CopyPlace(uDialog from, uDialog to)
        {
            if (from == null || to == null) return;

            RectTransform source = from.RectTransform;
            RectTransform target = to.RectTransform;
            RectTransform parent = source != null ? source.parent as RectTransform : null;
            if (source == null || target == null || parent == null) return;

            Rect place = WindowGeometry.LocalRect(source, parent);
            WindowGeometry.Place(target, parent, place.size, place.center, refreshText: true);
        }
    }
}
