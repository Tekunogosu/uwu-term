using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using MailConfig;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UI.Dialogs;
using UwUTerm.Ui;
using Util;

namespace UwUTerm.Patches
{
    /// <summary>
    /// A "headers" link on each message in the mail client, showing everything the game
    /// actually knows about it.
    ///
    /// The client already has the sender and recipient - MailWindow just does not draw
    /// them. Its render loop reads
    ///     if (num > 0) { ... isSending ? userMail.address : mail.otherMail ... }
    /// so the address line is skipped for message 0, which is exactly the mail you sent to
    /// open a phishing thread. Direction comes from isSending and the counterpart from
    /// Mail.otherMail, so both ends are recoverable for every message.
    ///
    /// This deliberately reads like a mail header block while being honest that most of a
    /// real one does not exist here - the game keeps no timestamps at all.
    /// </summary>
    // MailWindow has two OnClickMail overloads - one taking a mail id, one taking an
    // index - so the argument types have to be spelled out. Matching on the name alone
    // resolves to whichever is declared first, and the patch then fails to bind.
    [HarmonyPatch(typeof(MailWindow), "OnClickMail", new[] { typeof(int), typeof(bool) })]
    internal static class MailHeaders
    {
        private const string LinkName = "UwUTerm.Headers";
        private const string CardName = "UwUTerm.Card";
        private const float LinkHeight = 18f;

        private static Overlay _panel;
        private static MailWindow _panelOwner;
        private static int _shown = -1;

        private static void Postfix(MailWindow __instance, int indexMail)
        {
            if (!UwUTermPlugin.MailHeaders.Value) return;

            Hide();
            SweepCards(__instance);

            if (UwUTermPlugin.MailDebug.Value)
                UwUTermPlugin.Log.LogInfo($"mail: opened {indexMail}, rows={__instance.replyObjects.Count}");

            if (indexMail < 0 || indexMail >= __instance.allMails.Count) return;
            Mail mail = __instance.allMails[indexMail];

            // replyObjects is filled newest-first, so row k is message (Count - 1 - k).
            List<GameObject> rows = __instance.replyObjects;
            for (int row = 0; row < rows.Count; row++)
            {
                int message = mail.messages.Count - 1 - row;
                if (message < 0 || message >= mail.messages.Count) continue;
                RectTransform card = UwUTermPlugin.MailCards.Value ? Card(rows[row]) : null;
                Attach(__instance, rows[row], mail, message, card);
            }
        }

        /// <summary>
        /// ClearReadMessages destroys the message rows but knows nothing about the cards we
        /// wrapped them in, so every click left another set of empty padded panels stacked
        /// in the container, pushing the thread further down each time. They have to be
        /// swept before the new rows are wrapped.
        ///
        /// Safe to remove all of them: the rows for the mail being opened are parented
        /// straight into the container and are not wrapped until afterwards.
        /// </summary>
        private static void SweepCards(MailWindow window)
        {
            if (window.templateReadContent == null) return;

            Transform container = window.templateReadContent.transform.parent;
            if (container == null) return;

            for (int i = container.childCount - 1; i >= 0; i--)
            {
                Transform child = container.GetChild(i);
                if (child != null && child.name == CardName)
                    UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        /// <summary>
        /// Wrap a message row in a panel so each message reads as its own card.
        ///
        /// The background cannot simply be added to the row: Unity UI draws a parent before
        /// its children, so an Image on - or under - the TMP_Text would paint over the words.
        /// It has to sit on an ancestor, hence re-parenting the row into a new object that
        /// takes its place in the layout.
        /// </summary>
        private static RectTransform Card(GameObject row)
        {
            if (row == null || row.transform.parent == null) return null;
            if (row.transform.parent.name == CardName) return (RectTransform)row.transform.parent;

            Transform container = row.transform.parent;
            int index = row.transform.GetSiblingIndex();

            var card = new GameObject(CardName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Outline));
            var cardRect = (RectTransform)card.transform;
            cardRect.SetParent(container, false);
            cardRect.SetSiblingIndex(index);

            UI_Theme theme = OS.GetThemeFromFile();
            var background = card.GetComponent<Image>();
            background.raycastTarget = false;
            if (theme != null)
            {
                Color32 fill = theme.contextualBackground;
                fill.a = 90;
                background.color = fill;

                Color32 edge = theme.outline;
                edge.a = 140;
                var outline = card.GetComponent<Outline>();
                outline.effectColor = edge;
                outline.effectDistance = new Vector2(1f, -1f);
            }

            var layout = card.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 8, 10);
            layout.spacing = 0f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = card.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            row.transform.SetParent(cardRect, false);

            if (UwUTermPlugin.MailDebug.Value)
                UwUTermPlugin.Log.LogInfo("mail: container components - " + Describe(container));

            return cardRect;
        }

        private static string Describe(Transform t)
        {
            var names = new List<string>();
            foreach (Component c in t.GetComponents<Component>())
                if (c != null) names.Add(c.GetType().Name);
            return string.Join(", ", names.ToArray());
        }

        private static void Attach(MailWindow window, GameObject row, Mail mail, int message, RectTransform card)
        {
            if (row == null) return;

            Transform host = card != null ? (Transform)card : row.transform;
            if (host.Find(LinkName) != null) return;

            TMP_Text body = row.GetComponent<TMP_Text>();

            var link = new GameObject(LinkName, typeof(RectTransform), typeof(CanvasRenderer));
            var rect = (RectTransform)link.transform;
            rect.SetParent(host, false);

            if (card != null)
            {
                // A row of the card's vertical layout, above the body - so the text simply
                // starts below it and no margin has to be reserved for a possible overlap.
                rect.SetAsFirstSibling();
                var element = link.AddComponent<LayoutElement>();
                element.preferredHeight = LinkHeight;
                element.flexibleWidth = 1f;
            }
            else
            {
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
                rect.anchoredPosition = new Vector2(-4f, -2f);
                rect.sizeDelta = new Vector2(90f, LinkHeight);
            }

            var label = link.AddComponent<TextMeshProUGUI>();
            label.text = "headers";
            label.alignment = TextAlignmentOptions.MidlineRight;
            label.enableWordWrapping = false;
            label.raycastTarget = true;
            label.fontSize = (body != null ? body.fontSize : 14f) * 0.8f;
            if (body != null) label.font = body.font;

            UI_Theme theme = OS.GetThemeFromFile();
            if (theme != null) label.color = theme.contextualHighlight;

            var button = link.AddComponent<Button>();
            button.targetGraphic = label;
            button.onClick.AddListener(() => Toggle(window, mail, message));

            // Without a card the link floats over the body, so space has to be reserved
            // for it. The game already uses the top margin this way for attachments, so add
            // to whatever it set rather than replacing it.
            if (card == null && body != null)
            {
                Vector4 margin = body.margin;
                margin.y += LinkHeight;
                body.margin = margin;
            }
        }

        private static void Toggle(MailWindow window, Mail mail, int message)
        {
            if (_shown == message && _panel != null && _panel.Visible)
            {
                Hide();
                return;
            }

            RectTransform parent = window.dialogo != null ? window.dialogo.RectTransform : null;
            if (parent == null) return;

            if (_panel == null || _panelOwner != window)
            {
                _panel?.Destroy();
                _panel = Overlay.CreateTopRight(parent, null, 13f, new Vector2(-12f, -60f));
                _panelOwner = window;
            }

            _panel.SetText(Build(window, mail, message));
            _panel.Show();
            _shown = message;
        }

        internal static void Dismiss(uDialog dialog)
        {
            if (_panelOwner == null || !ReferenceEquals(_panelOwner.dialogo, dialog)) return;

            _panel?.Destroy();
            _panel = null;
            _panelOwner = null;
            _shown = -1;
        }

        private static void Hide()
        {
            _panel?.Hide();
            _shown = -1;
        }

        private static string Build(MailWindow window, Mail mail, int message)
        {
            MailMessage entry = mail.messages[message];
            string mine = window.userMail != null ? window.userMail.address : "(unknown)";

            string from = entry.isSending ? mine : mail.otherMail;
            string to = entry.isSending ? mail.otherMail : mine;

            var sb = new StringBuilder(256);
            Line(sb, "From", from);
            Line(sb, "To", to);
            Line(sb, "Subject", entry.titulo);
            Line(sb, "Direction", entry.isSending ? "sent" : "received");
            Line(sb, "Message", (message + 1) + " of " + mail.messages.Count);
            Line(sb, "Thread-Id", mail.GetID());

            if (!string.IsNullOrEmpty(mail.idMission)) Line(sb, "X-Mission", mail.idMission);
            if (mail.isProtected) Line(sb, "X-Protected", "yes");
            if (!string.IsNullOrEmpty(entry.serialAttach)) Line(sb, "X-Attachment", "yes");
            if (mail.isUnread) Line(sb, "X-Unread", "yes");

            // No Date line: a mail carries no timestamp, and the clock reading at the moment
            // you opened it would be a different fact wearing the same label.
            return sb.ToString().TrimEnd('\n');
        }

        private static void Line(StringBuilder sb, string name, string value)
        {
            sb.Append("<b>").Append(name).Append(":</b> ")
              .Append(string.IsNullOrEmpty(value) ? "(none)" : value)
              .Append('\n');
        }
    }
}
