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

        // Which buffer this window's script lives in. 0 is whatever is current, which is the
        // only buffer an embedded editor has. A shared session has many.
        private volatile int _buffer;

        // Which game file each buffer came from. A session holds the player's own buffers too,
        // and a buffer that is not in here is not one of the game's - saving it has nowhere to
        // go, which the game already handles by asking.
        private readonly Dictionary<int, string> _paths = new Dictionary<int, string>();

        /// <summary>The notification the session sends when the buffer on screen changes.</summary>
        private const string BufferEvent = "uwuterm_buf";

        /// <summary>Raised on the main thread when a different buffer comes to the front, so
        /// the window can follow it - the file it saves to is whichever one you are looking
        /// at, not whichever one it opened with.</summary>
        internal event System.Action<int> BufferEntered;
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

        internal bool Running => _client is { Running: true };

        /// <summary>Whether this view is a UI on a session somebody else is running, rather
        /// than an editor of its own.</summary>
        internal bool Attached => _client is { Attached: true };

        /// <summary>Whether neovim is taking the mouse, and so whether a click in this view is
        /// the editor's rather than the game's.</summary>
        internal bool MouseWanted => _ui != null && _ui.MouseWanted;

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

        /// <summary>
        /// An editor in this window.
        ///
        /// The shared session is offered to the first window only - it has one screen, and a
        /// second UI on it would mirror the first rather than show anything of its own. Every
        /// window after that runs an editor of its own, which has the filetype the mod
        /// installs and not much else, but is an editor. Nothing here ever gives up and hands
        /// the window back to the game's own rows: an editor with fewer tools in it beats no
        /// editor at all.
        /// </summary>
        internal static NvimView Create(RectTransform parent, TMP_FontAsset font, float fontSize,
                                        string file, bool joinSession = true, bool allowSpawn = true)
        {
            if (parent == null || !NvimInstall.Usable) return null;

            var view = new NvimView();
            view.Build(parent, font, fontSize);

            string address = joinSession ? NvimInstall.Address : null;

            // Joining is worth trying and not worth insisting on. If nothing is listening the
            // player has not started a session, which is a reason to run an editor here rather
            // than a reason to have none.
            if (address != null && !view.Join(address) && allowSpawn && NvimInstall.Available)
                UwUTermPlugin.Log.LogInfo("nvim: starting an editor in the game instead");

            // -n skips the swap file: the buffer lives in the game's filesystem, not on this
            // machine, and a swap beside it would be a file nobody asked for.
            string arguments = "--embed -n";
            if (!string.IsNullOrEmpty(file)) arguments += " \"" + file + "\"";

            if (view._client == null && allowSpawn && NvimInstall.Available)
            {
                view._client = view.Talk();

                if (!view._client.Start(NvimInstall.Location, arguments, NvimInstall.Workspace,
                                        NvimInstall.ChildEnvironment))
                    view._client = null;
            }

            if (view._client == null)
            {
                view.Destroy();
                return null;
            }

            if (view._client.Attached) view.WatchBuffers();

            view._ui = new NvimUi(view._grid);
            view._ui.Flushed += () => view._dirty = true;
            view._ui.Resized += (columns, rows) => view._grid.Resize(columns, rows);

            return view;
        }

        /// <summary>A client wired to report its ending through this view.</summary>
        private RpcClient Talk()
        {
            var client = new RpcClient();
            client.Ended += why => Ended?.Invoke(why);
            Listen(client);
            return client;
        }

        /// <summary>
        /// Say what neovim refuses, and optionally what it is asked.
        ///
        /// Almost every call here is sent without waiting for the answer, so a refused one used
        /// to leave no trace at all - a filetype that never took and a filetype that was never
        /// asked for looked identical from the game. Both arrive on the reading thread; the
        /// logger is the only thing touched, so there is nothing here for the main thread to do.
        /// </summary>
        private static void Listen(RpcClient client)
        {
            client.Failed += (method, error) =>
                UwUTermPlugin.Log.LogWarning($"nvim: {method} was refused - {Excuse(error)}");

            if (!UwUTermPlugin.NvimDebug.Value) return;

            client.Sent += (method, arguments) =>
                UwUTermPlugin.Log.LogInfo($"nvim: -> {method}({Brief(arguments)})");
        }

        /// <summary>How long to let neovim settle before asking a second time, in frames.</summary>
        private const int SettleFrames = 60;

        private int _askAgainAt = -1;
        private int _asking;

        private void Ask()
        {
            const string lua =
                "local b = ...\n" +
                "local ok, hl = pcall(function() return vim.treesitter.highlighter.active[b] ~= nil end)\n" +
                "return string.format('filetype=%s syntax=%s treesitter=%s name=%s lines=%d',\n" +
                "  vim.bo[b].filetype == '' and '(none)' or vim.bo[b].filetype,\n" +
                "  vim.b[b].current_syntax or '(none)',\n" +
                "  tostring(ok and hl or false),\n" +
                "  vim.api.nvim_buf_get_name(b) == '' and '(unnamed)' or vim.api.nvim_buf_get_name(b),\n" +
                "  vim.api.nvim_buf_line_count(b))";

            int buffer = _asking;
            _client.Request("nvim_exec_lua", new object[] { lua, new object[] { buffer } },
                (error, result) => UwUTermPlugin.Log.LogInfo(
                    error != null ? $"nvim: asking about the buffer failed - {Excuse(error)}"
                                  : $"nvim: buffer {buffer} is {result}"));
        }

        /// <summary>Neovim answers an error as [code, message]; the message is the half worth
        /// reading.</summary>
        private static string Excuse(object error) =>
            error is object[] parts && parts.Length >= 2 ? parts[1] as string ?? parts[1]?.ToString()
            : error?.ToString() ?? "no reason given";

        /// <summary>Enough of a call's arguments to recognise it, and no more - a buffer's worth
        /// of lines in a log is a log nobody reads.</summary>
        private static string Brief(object[] arguments)
        {
            if (arguments == null || arguments.Length == 0) return "";

            var said = new System.Text.StringBuilder();
            foreach (object argument in arguments)
            {
                if (said.Length > 0) said.Append(", ");
                if (said.Length > 120) { said.Append("..."); break; }

                string text = argument is object[] many ? $"[{many.Length}]" : argument?.ToString() ?? "nil";
                said.Append(text.Length > 60 ? text.Substring(0, 60) + "..." : text);
            }

            return said.ToString();
        }

        /// <summary>
        /// Try the session, and say whether it answered.
        ///
        /// Why it did not is worth printing rather than swallowing: a socket that is not there
        /// and a socket nothing is listening on read the same from in here, and the difference
        /// is whether the daemon was started or has died.
        /// </summary>
        private bool Join(string address)
        {
            string reason = null;

            var client = new RpcClient();
            client.Ended += why => reason = why;
            Listen(client);

            if (client.Connect(address))
            {
                client.Ended += why => Ended?.Invoke(why);
                _client = client;
                return true;
            }

            UwUTermPlugin.Log.LogWarning("nvim: " + (reason ?? "could not reach " + address));
            client.Dispose();
            return false;
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

        /// <summary>The room the editor believes it has, and what it made of it. Compared against
        /// the window's own size, this says whether a view that has stopped following the window
        /// is being told the wrong size or failing to act on the right one.</summary>
        internal string Describe() =>
            $"viewport {_rect.rect.width:F0}x{_rect.rect.height:F0}, " +
            $"last seen {_lastWidth:F0}x{_lastHeight:F0}, " +
            $"grid {_grid.Columns}x{_grid.Rows} at {_advance:F1}x{_lineHeight:F1}px";

        internal void Tick(bool focused)
        {
            if (_root == null || _client == null) return;

            // Redraws are queued by the reading thread and applied here, because everything
            // they touch ends up in the scene.
            _arrived.Clear();
            if (_client.Drain(_arrived) > 0)
            {
                foreach (RpcClient.Notification notification in _arrived)
                {
                    if (notification.Method == BufferEvent) Entered(notification);
                    else _ui.Handle(notification);
                }
            }

            if (_askAgainAt > 0 && Time.frameCount >= _askAgainAt)
            {
                _askAgainAt = -1;
                Ask();
            }

            if (Resized() || !_attached) Attach();

            Mouse(focused);

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

        // ---- the mouse -------------------------------------------------------------------

        /// <summary>Which button is down, as Unity numbers them, or -1 for none.</summary>
        private int _held = -1;
        private int _heldRow = -1, _heldColumn = -1;

        /// <summary>
        /// Polled, not taken as pointer events.
        ///
        /// Implementing IPointerDownHandler would put this object first in line for the click,
        /// ahead of everything the game already routes through the window - focusing it among
        /// them. Watching the mouse leaves that routing alone and takes a copy.
        /// </summary>
        private void Mouse(bool focused)
        {
            // A drag outlives the pointer leaving the view and the window losing focus. Letting
            // go of the button is the only thing that ends it, which is what makes selecting
            // against the very edge of the window possible at all.
            if (_held >= 0)
            {
                if (Input.GetMouseButton(_held))
                {
                    if (Under(out int row, out int column, clamp: true) &&
                        (row != _heldRow || column != _heldColumn))
                    {
                        _heldRow = row;
                        _heldColumn = column;
                        SendMouse(Named(_held), "drag", row, column);
                    }

                    return;
                }

                if (Under(out int endRow, out int endColumn, clamp: true))
                    SendMouse(Named(_held), "release", endRow, endColumn);

                _held = -1;
                return;
            }

            // Neovim asks for the mouse, and until it has, clicks belong to the game.
            if (!focused || _ui == null || !_ui.MouseWanted) return;

            for (int button = 0; button <= 2; button++)
            {
                if (!Input.GetMouseButtonDown(button)) continue;
                if (!Under(out int row, out int column, clamp: false)) continue;

                _held = button;
                _heldRow = row;
                _heldColumn = column;
                SendMouse(Named(button), "press", row, column);
                return;
            }

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f && Under(out int overRow, out int overColumn, clamp: false))
                SendMouse("wheel", wheel > 0f ? "up" : "down", overRow, overColumn);
        }

        /// <summary>Unity numbers the buttons, neovim names them.</summary>
        private static string Named(int button) =>
            button == 1 ? "right" : button == 2 ? "middle" : "left";

        private void SendMouse(string button, string action, int row, int column) =>
            _client.Notify("nvim_input_mouse",
                new object[] { button, action, Held(), 0, row, column });

        /// <summary>The modifiers as neovim spells them - one letter each, no separator.</summary>
        private static string Held()
        {
            string held = "";

            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) held += "S";
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) held += "C";
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) held += "A";

            return held;
        }

        /// <summary>The cell under the pointer. Clamped to the nearest real one when a drag has
        /// taken the pointer outside, refused outright when a click lands there.</summary>
        private bool Under(out int row, out int column, bool clamp)
        {
            row = 0;
            column = 0;

            Canvas canvas = _rect != null ? _rect.GetComponentInParent<Canvas>() : null;
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;

            if (_rect == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _rect, Input.mousePosition, camera, out Vector2 local))
                return false;

            Rect area = _rect.rect;
            if (!clamp && !area.Contains(local)) return false;

            row = Mathf.FloorToInt((area.yMax - Mathf.Clamp(local.y, area.yMin, area.yMax)) / _lineHeight);
            column = Mathf.FloorToInt((Mathf.Clamp(local.x, area.xMin, area.xMax) - area.xMin) / _advance);

            row = Mathf.Clamp(row, 0, Mathf.Max(0, _grid.Rows - 1));
            column = Mathf.Clamp(column, 0, Mathf.Max(0, _grid.Columns - 1));
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

            _client.Request("nvim_buf_get_lines", new object[] { _buffer, 0, -1, false }, (error, result) =>
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
            _client.Request("nvim_buf_set_name", new object[] { _buffer, name });
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
                new object[] { "filetype", filetype, new Dictionary<string, object> { { "buf", _buffer } } });

            SayHowItIsHighlighted();
        }

        /// <summary>
        /// What neovim made of the filetype we asked for.
        ///
        /// Setting the option is not the same as the buffer being highlighted: what listens for
        /// FileType decides that, and a config of the player's own may set it back, may attach a
        /// parser, or may have no parser to attach. The call going out unrefused says only that
        /// it was accepted, so this asks the buffer what it ended up as - the filetype it holds,
        /// whether a syntax was loaded, and whether treesitter is on it.
        ///
        /// Sent after the set, so it answers for the state that set left behind.
        /// </summary>
        private void SayHowItIsHighlighted()
        {
            if (!UwUTermPlugin.NvimDebug.Value || _client == null) return;

            _asking = _buffer;
            Ask();

            // And again once the editor has had a moment. A parser attaching is not part of
            // setting the option - it happens on the FileType that follows, and whatever does it
            // may take its time - so an answer taken immediately says only what was true before
            // anything had a chance to react.
            _askAgainAt = Time.frameCount + SettleFrames;
        }

        /// <summary>
        /// Put a script in front of the editor.
        ///
        /// An editor started for this window has the one buffer, so the script goes in it. A
        /// shared session is somebody's whole working set, and dropping a script on top of
        /// whatever they had open would be rude - so each one opened gets a buffer of its own
        /// and joins the rest. That is what makes a session worth pointing at instead of
        /// starting an editor per window: :ls, :b and every plugin see all of them.
        /// </summary>
        internal void Open(string name, string source, string filetype, string path = null)
        {
            if (_client == null) return;

            if (!_client.Attached)
            {
                Load(name, source, filetype);
                return;
            }

            // The same file arriving twice is a reload, not a second script - the game sends
            // the source again after a save - so it goes back into the buffer it already has
            // rather than growing the session a duplicate on every write.
            int already = BufferFor(path);
            if (already > 0)
            {
                _buffer = already;
                Load(name, source, filetype);
                _client.Notify("nvim_set_current_buf", new object[] { already });
                return;
            }

            // Made through lua so the answer is a plain buffer number - nvim_create_buf hands
            // back an ext-typed handle, which is fine to pass around but useless as the key for
            // which file a buffer came from - and so the reuse below is one round trip rather
            // than three.
            //
            // A window opens on an empty unnamed buffer, and opening a file into it should take
            // that buffer over the way :e does. Leaving it behind litters the session with a
            // scratch that has no name to switch back to and no file to save to.
            // The session's own empty buffer counts too: a headless neovim starts with one, so
            // taking only ours would leave the player looking at a session with two scratches
            // in it before they had opened anything.
            //
            // swapfile off because these are not files here. The script lives on the server and
            // reaches the buffer as text, and a session running the player's own config would
            // otherwise stop on E325 the moment two windows named the same buffer.
            const string lua =
                "local ours = ...\n" +
                "local function reusable(b)\n" +
                "  return b > 0 and vim.api.nvim_buf_is_valid(b)\n" +
                "    and vim.api.nvim_buf_get_name(b) == ''\n" +
                "    and not vim.bo[b].modified\n" +
                "    and vim.api.nvim_buf_line_count(b) == 1\n" +
                "    and vim.api.nvim_buf_get_lines(b, 0, 1, false)[1] == ''\n" +
                "end\n" +
                "local target\n" +
                "local current = vim.api.nvim_get_current_buf()\n" +
                "if reusable(ours) then target = ours\n" +
                "elseif reusable(current) then target = current\n" +
                "else target = vim.api.nvim_create_buf(true, false) end\n" +
                "vim.bo[target].swapfile = false\n" +
                "return target";

            _client.Request("nvim_exec_lua",
                new object[] { lua, new object[] { _buffer } },
                (error, result) =>
                {
                    int buffer = 0;
                    try { if (error == null && result != null) buffer = System.Convert.ToInt32(result); }
                    catch (System.Exception) { }

                    if (buffer <= 0) { Load(name, source, filetype); return; }

                    _buffer = buffer;
                    if (path != null) lock (_paths) _paths[buffer] = path;

                    Load(name, source, filetype);
                    _client.Notify("nvim_set_current_buf", new object[] { buffer });
                });
        }

        /// <summary>
        /// Ask the session to say when the buffer on screen changes.
        ///
        /// One autocmd rather than asking every frame - the window has to follow whatever the
        /// player switches to with :b, and a session they are also using themselves will
        /// switch for reasons this never hears about otherwise.
        /// </summary>
        private void WatchBuffers()
        {
            _client.Request("nvim_get_api_info", new object[0], (error, result) =>
            {
                if (error != null || !(result is object[] info) || info.Length < 1) return;

                const string lua =
                    "local channel = ...\n" +
                    "vim.api.nvim_create_autocmd('BufEnter', {\n" +
                    "  group = vim.api.nvim_create_augroup('uwuterm', { clear = true }),\n" +
                    "  callback = function(event) vim.rpcnotify(channel, '" + BufferEvent + "', event.buf) end,\n" +
                    "})";

                _client.Notify("nvim_exec_lua", new object[] { lua, new object[] { info[0] } });
            });
        }

        private void Entered(RpcClient.Notification notification)
        {
            if (notification.Arguments.Length < 1) return;

            int buffer;
            try { buffer = System.Convert.ToInt32(notification.Arguments[0]); }
            catch (System.Exception) { return; }

            _buffer = buffer;
            BufferEntered?.Invoke(buffer);
        }

        /// <summary>The game file a buffer came from, or null for one that is not the game's.</summary>
        internal string PathFor(int buffer)
        {
            lock (_paths) return _paths.TryGetValue(buffer, out string path) ? path : null;
        }

        /// <summary>The buffer already holding a file, or 0 for one not open yet.</summary>
        private int BufferFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;

            lock (_paths)
            {
                foreach (KeyValuePair<int, string> known in _paths)
                    if (known.Value == path) return known.Key;
            }

            return 0;
        }

        private void Load(string name, string source, string filetype)
        {
            SetName(name);
            SetSource(source);
            SetFiletype(filetype);
        }

        internal void SetSource(string source)
        {
            if (_client == null) return;

            string[] split = (source ?? "").Replace("\r\n", "\n").Split('\n');
            var lines = new object[split.Length];
            for (int i = 0; i < split.Length; i++) lines[i] = split[i];

            _client.Request("nvim_buf_set_lines", new object[] { _buffer, 0, -1, false, lines });

            // Writing the lines is what marks the buffer modified, and nobody has modified it -
            // this is the file as the server has it. Left set, :q argues about unsaved changes
            // on a script nothing has touched, the window's modified marker means nothing, and
            // an untouched buffer stops looking empty enough to open the next file into.
            _client.Request("nvim_set_option_value",
                new object[] { "modified", false, new Dictionary<string, object> { { "buf", _buffer } } });
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
            // Give the screen back. A session we joined carries on without us, with the buffer
            // still in it - it is the player's editor, and closing a game window is not a
            // reason to throw away what they were editing.
            if (_client != null && _client.Attached)
                _client.Notify("nvim_ui_detach", new object[0]);

            _client?.Dispose();
            _client = null;

            if (_root != null) Object.Destroy(_root);
            _root = null;
        }
    }
}
