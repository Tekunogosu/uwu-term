using System.Text;
using UI.Dialogs;
using UnityEngine;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Write the desktop's UI hierarchy to the log once, deep enough to reach the widgets -
    /// a limit that stops above them describes a bar whose contents are a guess.
    ///
    /// Anchors, sizes and which objects carry which components are decided in the scene, and
    /// none of that is visible in decompiled code. Rearranging the bars means knowing what is
    /// parented where and how each piece is anchored, so this reads it off the running game.
    /// </summary>
    internal static class DesktopReport
    {
        private static bool _done;

        internal static void Tick()
        {
            if (_done || !UwUTermPlugin.DumpDesktop.Value) return;

            uDialog_TaskBar bar = uDialog_TaskBar.Singleton;
            if (bar == null) return;

            _done = true;

            // Climb to the canvas so the whole desktop is described, not just the bar.
            Transform root = bar.transform;
            while (root.parent != null && root.GetComponentInParent<Canvas>() != null &&
                   root.parent.GetComponentInParent<Canvas>() != null)
                root = root.parent;

            var sb = new StringBuilder(4096);
            sb.Append("desktop: hierarchy under ").Append(root.name).Append('\n');
            Hierarchy.Describe(root, sb, 0, 6);

            UwUTermPlugin.Log.LogInfo(sb.ToString());
        }
    }
}
