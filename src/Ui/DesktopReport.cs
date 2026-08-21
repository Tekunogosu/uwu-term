using System.Text;
using UI.Dialogs;
using UnityEngine;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Write the desktop's UI hierarchy to the log once.
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
            Describe(root, sb, 0, 4);

            UwUTermPlugin.Log.LogInfo(sb.ToString());
        }

        private static void Describe(Transform t, StringBuilder sb, int depth, int limit)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);

                sb.Append(' ', (depth + 1) * 2).Append(child.name);
                if (!child.gameObject.activeSelf) sb.Append(" [off]");

                if (child is RectTransform rect)
                    sb.Append($"  anchor({rect.anchorMin.x:F2},{rect.anchorMin.y:F2})-" +
                              $"({rect.anchorMax.x:F2},{rect.anchorMax.y:F2}) " +
                              $"size({rect.rect.width:F0}x{rect.rect.height:F0}) " +
                              $"pos({rect.anchoredPosition.x:F0},{rect.anchoredPosition.y:F0})");

                string parts = Components(child);
                if (parts.Length > 0) sb.Append("  <").Append(parts).Append('>');
                sb.Append('\n');

                if (depth + 1 < limit) Describe(child, sb, depth + 1, limit);
            }
        }

        /// <summary>Only the components that say what an object is for - every UI object has a
        /// RectTransform and a CanvasRenderer, and listing those buries the useful ones.</summary>
        private static string Components(Transform t)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null) continue;

                string name = c.GetType().Name;
                if (name == "RectTransform" || name == "CanvasRenderer") continue;
                names.Add(name);
            }
            return string.Join(",", names.ToArray());
        }
    }
}
