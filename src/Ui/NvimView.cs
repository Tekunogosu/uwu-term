using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UwUTerm.Nvim;
using UwUTerm.Patches;
using UwUTerm.Screen;
using Util;

namespace UwUTerm.Ui
{
    /// <summary>
    /// Draws an embedded neovim into a window.
    ///
    /// The picture is the same kind the terminal draws - a grid of cells rendered as one mesh -
    /// but where that one is composed from the game's lines, this one is handed cells by
    /// neovim. Everything after the grid is shared: the renderer, the metrics, the font.
    /// </summary>
    internal sealed class NvimView
    {
        private const int MinimumColumns = 20;
        private const int MinimumRows = 4;

        private readonly ScreenGrid _grid = new ScreenGrid();
        private readonly List<RpcClient.Notification> _arrived = new List<RpcClient.Notification>();

        private RpcClient _client;
        private NvimUi _ui;

        private GameObject _root;
        private RectTransform _rect;
        private TextMeshProUGUI _label;
        private Image _caret;
        private readonly List<Image> _backgroundQuads = new List<Image>();
        private RectTransform _backdrop;
        private RectTransform _overlay;

        private float _advance = 1f, _lineHeight = 1f;
        private float _lastWidth, _lastHeight;
        private bool _dirty;
        private bool _attached;

        internal bool Running => _client != null && _client.Running;

        /// <summary>
        /// The buffer as the window last saw it.
        ///
        /// GetSource is answered the moment it is asked, but reading a buffer out of neovim is
        /// a round trip - so the text is kept up to date as it changes instead. Neovim flushes
        /// after every change it makes, which is the cue to ask again.
        /// </summary>
        internal string Source { get; private set; } = "";

        private bool _reading;

        /// <summary>Raised when neovim stops, so the window can put the game's editor back
        /// rather than sitting on a dead picture.</summary>
        internal event System.Action<string> Ended;

        // ---- starting --------------------------------------------------------------------

        internal static NvimView Create(RectTransform parent, TMP_FontAsset font, float fontSize, string file)
        {
            if (parent == null || !NvimInstall.Available) return null;

            var view = new NvimView();
            view.Build(parent, font, fontSize);

            // -n skips the swap file: the buffer lives in the game's filesystem, not on this
            // machine, and a swap beside it would be a file nobody asked for.
            string arguments = "--embed -n";
            if (!string.IsNullOrEmpty(file)) arguments += " \"" + file + "\"";

            view._client = new RpcClient();
            view._client.Ended += why => view.Ended?.Invoke(why);

            if (!view._client.Start(NvimInstall.Location, arguments, NvimInstall.Workspace))
            {
                view.Destroy();
                return null;
            }

            view._ui = new NvimUi(view._grid);
            view._ui.Flushed += () => view._dirty = true;
            view._ui.Resized += (columns, rows) => view._grid.Resize(columns, rows);

            return view;
        }

        private void Build(RectTransform parent, TMP_FontAsset font, float fontSize)
        {
            // Three layers, because Unity draws a parent before its children and siblings in
            // order. Backgrounds have to be a sibling ahead of the text rather than a child of
            // it: anything parented to the label paints over the words no matter what its
            // sibling index says.
            _root = new GameObject("UwUTerm.Nvim", typeof(RectTransform));
            _rect = (RectTransform)_root.transform;
            _rect.SetParent(parent, false);
            Stretch(_rect, 4f);

            _backdrop = Layer("Backgrounds");
            RectTransform text = Layer("Text");
            _overlay = Layer("Cursor");

            _label = text.gameObject.AddComponent<TextMeshProUGUI>();
            _label.raycastTarget = false;
            _label.richText = true;
            _label.enableWordWrapping = false;
            _label.overflowMode = TextOverflowModes.Overflow;
            _label.alignment = TextAlignmentOptions.TopLeft;
            _label.margin = Vector4.zero;
            _label.lineSpacing = 0f;

            if (font != null) _label.font = font;
            _label.fontSize = fontSize;

            UI_Theme theme = OS.GetThemeFromFile();
            if (theme != null) _label.color = theme.terminalText;
        }

        /// <summary>A full-size child, added in the order it should be drawn.</summary>
        private RectTransform Layer(string name)
        {
            var layer = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            var rect = (RectTransform)layer.transform;
            rect.SetParent(_rect, false);
            Stretch(rect, 0f);
            rect.SetAsLastSibling();
            return rect;
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        // ---- running ---------------------------------------------------------------------

        internal void Tick()
        {
            if (_root == null || _client == null) return;

            // Redraws are queued by the reading thread and applied here, because everything
            // they touch ends up in the scene.
            _arrived.Clear();
            if (_client.Drain(_arrived) > 0)
                foreach (RpcClient.Notification notification in _arrived) _ui.Handle(notification);

            if (Resized() || !_attached) Attach();
            if (!_dirty) return;

            _dirty = false;
            _label.text = ScreenRenderer.Render(_grid, backgrounds: false);
            PaintBackgrounds();
            PlaceCaret();
            Reread();
        }

        /// <summary>Tell neovim how much room it has. Sent on the first tick and whenever the
        /// window changes size, which is what makes the editor reflow with the window.</summary>
        private void Attach()
        {
            int columns = Mathf.Max(MinimumColumns, Mathf.FloorToInt(_rect.rect.width / _advance));
            int rows = Mathf.Max(MinimumRows, Mathf.FloorToInt(_rect.rect.height / _lineHeight));

            _grid.Resize(columns, rows);

            if (!_attached)
            {
                _attached = true;
                var options = new Dictionary<string, object> { { "ext_linegrid", true }, { "rgb", true } };
                _client.Request("nvim_ui_attach", new object[] { columns, rows, options });
                return;
            }

            _client.Notify("nvim_ui_try_resize", new object[] { columns, rows });
        }

        private bool Resized()
        {
            Rect area = _rect.rect;
            if (Mathf.Approximately(area.width, _lastWidth) && Mathf.Approximately(area.height, _lastHeight))
                return false;

            _lastWidth = area.width;
            _lastHeight = area.height;

            _lineHeight = LineHeight();
            _advance = Advance();
            return true;
        }

        internal void Send(string keys)
        {
            if (_client == null || string.IsNullOrEmpty(keys)) return;
            _client.Notify("nvim_input", new object[] { keys });
        }

        /// <summary>
        /// The buffer as one string, handed to whoever asked once neovim answers.
        ///
        /// This is what the game compiles. The editor being replaced changes nothing about
        /// building - the source still goes out through the same call, it is simply read from
        /// here instead of from the game's line list.
        /// </summary>
        internal void ReadSource(System.Action<string> then)
        {
            if (_client == null) { then(null); return; }

            _client.Request("nvim_buf_get_lines", new object[] { 0, 0, -1, false }, (error, result) =>
            {
                if (!(result is object[] lines)) { then(null); return; }

                var text = new List<string>(lines.Length);
                foreach (object line in lines) text.Add(line as string ?? "");

                then(string.Join("\n", text.ToArray()));
            });
        }

        /// <summary>Keep the cached source current, one request at a time so a slow answer
        /// cannot pile up behind a fast typist.</summary>
        private void Reread()
        {
            if (_reading) return;
            _reading = true;

            ReadSource(text =>
            {
                if (text != null) Source = text;
                _reading = false;
            });
        }

        /// <summary>
        /// Name the buffer after the file the game opened.
        ///
        /// A buffer with no name of its own keeps whatever it was last written as, so :w after
        /// loading a second file would quietly overwrite the first. Naming it means :w writes
        /// what you are actually editing, into the workspace.
        /// </summary>
        internal void SetName(string name)
        {
            if (_client == null || string.IsNullOrEmpty(name)) return;
            _client.Request("nvim_buf_set_name", new object[] { 0, name });
        }

        /// <summary>
        /// Tell neovim what language the buffer is.
        ///
        /// Nothing here highlights anything - that is neovim's job, and it does it from the
        /// filetype. Setting one means a syntax file or a treesitter parser for the game's
        /// language starts working the moment it is installed, without the buffer having to be
        /// named something neovim already recognises.
        /// </summary>
        internal void SetFiletype(string filetype)
        {
            if (_client == null || string.IsNullOrEmpty(filetype)) return;
            _client.Request("nvim_set_option_value",
                new object[] { "filetype", filetype, new Dictionary<string, object> { { "buf", 0 } } });
        }

        internal void SetSource(string source)
        {
            if (_client == null) return;

            string[] split = (source ?? "").Replace("\r\n", "\n").Split('\n');
            var lines = new object[split.Length];
            for (int i = 0; i < split.Length; i++) lines[i] = split[i];

            _client.Request("nvim_buf_set_lines", new object[] { 0, 0, -1, false, lines });
        }

        // ---- drawing ---------------------------------------------------------------------

        /// <summary>
        /// Every run of cells sharing a background, drawn as a quad behind the text.
        ///
        /// Neovim gives nearly every cell a background, so these cannot go in the markup - TMP
        /// would paint each one over its own glyph. Behind the label they do what a background
        /// is supposed to do, and a row of ordinary text costs one quad rather than one per
        /// character.
        /// </summary>
        private void PaintBackgrounds()
        {
            int used = 0;

            for (int row = 0; row < _grid.Rows; row++)
            {
                int column = 0;
                while (column < _grid.Columns)
                {
                    uint colour = _grid[row, column].Background;
                    if (colour == Cell.Inherit) { column++; continue; }

                    int start = column;
                    while (column < _grid.Columns && _grid[row, column].Background == colour) column++;

                    Image quad = Quad(used++);
                    var rect = (RectTransform)quad.transform;
                    rect.sizeDelta = new Vector2((column - start) * _advance, _lineHeight);
                    rect.anchoredPosition = new Vector2(start * _advance, -row * _lineHeight);
                    quad.color = Colour(colour);
                    quad.enabled = true;
                }
            }

            for (int i = used; i < _backgroundQuads.Count; i++) _backgroundQuads[i].enabled = false;
        }

        private Image Quad(int index)
        {
            while (_backgroundQuads.Count <= index)
            {
                var go = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_backdrop, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);

                var image = go.AddComponent<Image>();
                image.raycastTarget = false;
                _backgroundQuads.Add(image);
            }
            return _backgroundQuads[index];
        }

        private static Color32 Colour(uint rgba) =>
            new Color32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

        private void PlaceCaret()
        {
            bool wanted = _grid.CaretRow >= 0 && _grid.CaretColumn >= 0;
            if (!wanted) { if (_caret != null) _caret.enabled = false; return; }

            if (_caret == null)
            {
                var caret = new GameObject("Caret", typeof(RectTransform), typeof(CanvasRenderer));
                var rect = (RectTransform)caret.transform;
                rect.SetParent(_overlay, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);

                _caret = caret.AddComponent<Image>();
                _caret.raycastTarget = false;
            }

            var caretRect = (RectTransform)_caret.transform;
            caretRect.sizeDelta = new Vector2(_advance, _lineHeight);
            caretRect.anchoredPosition = new Vector2(_grid.CaretColumn * _advance, -_grid.CaretRow * _lineHeight);

            UI_Theme theme = OS.GetThemeFromFile();
            Color32 colour = theme != null ? theme.terminalText : (Color32)Color.white;
            _caret.color = new Color32(colour.r, colour.g, colour.b, 0xB0);
            _caret.enabled = true;
        }

        private float LineHeight()
        {
            TMP_FontAsset asset = _label.font;
            if (asset == null || asset.faceInfo.pointSize <= 0) return Mathf.Max(1f, _label.fontSize * 1.2f);

            float scale = _label.fontSize / asset.faceInfo.pointSize * asset.faceInfo.scale;
            float height = asset.faceInfo.lineHeight * scale;
            return height > 0f ? height : Mathf.Max(1f, _label.fontSize * 1.2f);
        }

        private float Advance()
        {
            float width = _label.GetPreferredValues("MMMMMMMMMM").x / 10f;
            return width > 0f ? width : Mathf.Max(1f, _label.fontSize * 0.6f);
        }

        internal void Destroy()
        {
            _client?.Dispose();
            _client = null;

            if (_root != null) Object.Destroy(_root);
            _root = null;
        }
    }
}
