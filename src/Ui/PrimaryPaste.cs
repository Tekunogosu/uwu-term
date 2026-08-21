using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Middle-click pastes the primary buffer anywhere in the game that takes typing.
    ///
    /// The buffer is one static string for the whole process, so text highlighted in a
    /// terminal is already reachable from the code editor, a mail reply or a chat box - all
    /// that was missing was somewhere to put it. This finds whatever input field is under the
    /// pointer and inserts at its caret, which is what middle-click does on a desktop.
    ///
    /// Terminals are not handled here. They have no input field to find, and their own screen
    /// already answers the middle button against the line being edited.
    /// </summary>
    internal static class PrimaryPaste
    {
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        internal static void Tick()
        {
            if (!Input.GetMouseButtonDown(2)) return;
            if (TerminalClipboard.Primary.Length == 0) return;
            if (EventSystem.current == null) return;

            var pointer = new PointerEventData(EventSystem.current) { position = Input.mousePosition };
            Hits.Clear();
            EventSystem.current.RaycastAll(pointer, Hits);

            for (int i = 0; i < Hits.Count; i++)
            {
                GameObject hit = Hits[i].gameObject;

                var modern = hit.GetComponentInParent<TMP_InputField>();
                if (modern != null) { Insert(modern); return; }

                var legacy = hit.GetComponentInParent<InputField>();
                if (legacy != null) { Insert(legacy); return; }
            }
        }

        private static void Insert(TMP_InputField field)
        {
            if (field.readOnly || !field.interactable) return;

            string text = Fit(field.lineType != TMP_InputField.LineType.SingleLine);
            string current = field.text ?? "";
            int at = Mathf.Clamp(field.caretPosition, 0, current.Length);

            field.text = current.Insert(at, text);
            field.ActivateInputField();
            field.caretPosition = at + text.Length;
        }

        private static void Insert(InputField field)
        {
            if (field.readOnly || !field.interactable) return;

            string text = Fit(field.lineType != InputField.LineType.SingleLine);
            string current = field.text ?? "";
            int at = Mathf.Clamp(field.caretPosition, 0, current.Length);

            field.text = current.Insert(at, text);
            field.ActivateInputField();
            field.caretPosition = at + text.Length;
        }

        /// <summary>A single-line field cannot hold a line break, and one arriving in a field
        /// that submits on Enter would be worse than useless.</summary>
        private static string Fit(bool multiline) =>
            multiline ? TerminalClipboard.Primary : TerminalClipboard.ForInput(TerminalClipboard.Primary);
    }
}
