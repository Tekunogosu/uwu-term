using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UwUTerm.Ui
{
    /// <summary>
    /// What is parented where, and how each piece is anchored.
    ///
    /// Anchors, sizes and which objects carry which components are decided in the scene, and none
    /// of that is visible in decompiled code. Anything in the mod that has to fit itself into a
    /// prefab it cannot read is guessing until this has been run against the object in question.
    /// </summary>
    internal static class Hierarchy
    {
        internal static void Describe(Transform root, StringBuilder sb, int depth, int limit)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);

                sb.Append(' ', (depth + 1) * 2).Append(child.name);
                if (!child.gameObject.activeSelf) sb.Append(" [off]");

                if (child is RectTransform rect)
                    sb.Append($"  anchor({rect.anchorMin.x:F2},{rect.anchorMin.y:F2})-" +
                              $"({rect.anchorMax.x:F2},{rect.anchorMax.y:F2}) " +
                              $"pivot({rect.pivot.x:F2},{rect.pivot.y:F2}) " +
                              $"size({rect.rect.width:F0}x{rect.rect.height:F0}) " +
                              $"pos({rect.anchoredPosition.x:F0},{rect.anchoredPosition.y:F0})");

                string parts = Components(child);
                if (parts.Length > 0) sb.Append("  <").Append(parts).Append('>');

                string says = Says(child);
                if (says.Length > 0) sb.Append("  \"").Append(says).Append('"');

                string does = Does(child);
                if (does.Length > 0) sb.Append("  -> ").Append(does);

                sb.Append('\n');

                // Nothing below something switched off. A browser window carries a panel per kind
                // of page it can show and all but one are hidden, which is the whole of the report
                // if they are followed.
                if (depth + 1 < limit && child.gameObject.activeSelf) Describe(child, sb, depth + 1, limit);
            }
        }

        /// <summary>Only the components that say what an object is for - every UI object has a
        /// RectTransform and a CanvasRenderer, and listing those buries the useful ones.</summary>
        private static string Components(Transform t)
        {
            var names = new List<string>();
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null) continue;

                string name = c.GetType().Name;
                if (name == "RectTransform" || name == "CanvasRenderer") continue;
                names.Add(name);
            }
            return string.Join(",", names.ToArray());
        }

        /// <summary>What an object reads as on screen. A label is how a player names the thing they
        /// are pointing at, and it is the only way to match a report to what they described.</summary>
        private static string Says(Transform t)
        {
            string text = null;

            var tmp = t.GetComponent<TMP_Text>();
            if (tmp != null) text = tmp.text;

            if (string.IsNullOrEmpty(text))
            {
                var legacy = t.GetComponent<Text>();
                if (legacy != null) text = legacy.text;
            }

            if (string.IsNullOrEmpty(text)) return "";

            text = text.Replace('\n', ' ');
            return text.Length > 48 ? text.Substring(0, 48) + "..." : text;
        }

        /// <summary>What a button is wired to in the scene. The method name is the one thing about a
        /// prefab-authored click that a translated label cannot give us, so a button can be found
        /// again by what it does rather than by what it says.</summary>
        private static string Does(Transform t)
        {
            var button = t.GetComponent<Button>();
            if (button == null || button.onClick == null) return "";

            var calls = new List<string>();
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            {
                Object target = button.onClick.GetPersistentTarget(i);
                string method = button.onClick.GetPersistentMethodName(i);
                calls.Add((target != null ? target.GetType().Name + "." : "") + method);
            }

            return string.Join(",", calls.ToArray());
        }
    }
}
