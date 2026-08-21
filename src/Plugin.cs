using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UwUTerm.Patches;

namespace UwUTerm
{
    [BepInPlugin(Guid, Name, Version)]
    public class UwUTermPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.tekunogosu.uwuterm";
        public const string Name = "UwUTerm";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigFile Hotkeys;

        internal static ConfigEntry<bool> FeatureTerminal;
        internal static ConfigEntry<bool> FeatureEditor;
        internal static ConfigEntry<bool> FeatureDesktop;
        internal static ConfigEntry<bool> FeatureMail;
        internal static ConfigEntry<bool> FeatureWindows;

        internal static ConfigEntry<int> KillRingSize;
        internal static ConfigEntry<int> PanelBackgroundAlpha;
        internal static ConfigEntry<int> CompletionBackgroundAlpha;
        internal static ConfigEntry<float> ScreenPadding;
        internal static ConfigEntry<string> CursorStyle;
        internal static ConfigEntry<bool> CursorBlink;
        internal static ConfigEntry<string> TerminalFontName;
        internal static ConfigEntry<float> TerminalFontSize;
        internal static ConfigEntry<bool> TerminalFontFallback;
        internal static ConfigEntry<string> CompletionSelectedColor;
        internal static ConfigEntry<bool> FilterCommandNames;
        internal static ConfigEntry<string> KnownCommands;
        internal static ConfigEntry<string> IgnoreArgumentExtensions;
        internal static ConfigEntry<bool> MailCards;
        internal static ConfigEntry<string> SearchMatchTextColor;
        internal static ConfigEntry<string> SearchMatchHighlightColor;
        internal static ConfigEntry<string> SearchActiveTextColor;
        internal static ConfigEntry<string> SearchActiveHighlightColor;
        internal static ConfigEntry<bool> PersistHistory;
        internal static ConfigEntry<int> HistoryLimit;
        internal static ConfigEntry<bool> HistoryIgnoreSpacePrefix;
        internal static ConfigEntry<bool> HistoryIgnoreDuplicates;
        internal static ConfigEntry<string> HistoryIgnorePattern;
        internal static ConfigEntry<string> PromptTemplate;
        internal static ConfigEntry<string> PromptPalette;
        internal static ConfigEntry<string> PromptTemplateRemote;
        internal static ConfigEntry<string> PromptPaletteRemote;
        internal static ConfigEntry<string> PromptSym;
        internal static ConfigEntry<string> PromptSymRemote;
        internal static ConfigEntry<string> NvimPath;
        internal static ConfigEntry<string> NvimWorkspace;
        internal static ConfigEntry<string> NvimFiletype;
        internal static ConfigEntry<bool> NvimDownload;
        internal static ConfigEntry<float> BarSpacing;
        internal static ConfigEntry<bool> SkipDragFocus;
        internal static ConfigEntry<bool> SnapPreview;
        internal static ConfigEntry<bool> SnapTopMaximizes;
        internal static ConfigEntry<float> SnapEdgeMargin;
        internal static ConfigEntry<float> SnapCornerBand;
        internal static ConfigEntry<bool> EnableModifierDrag;
        internal static ConfigEntry<string> ModifierDragKey;

        internal static ConfigEntry<bool> CompletionDebug;
        internal static ConfigEntry<bool> MailDebug;
        internal static ConfigEntry<bool> ScreenDebug;
        internal static ConfigEntry<bool> ListFonts;
        internal static ConfigEntry<bool> DumpDesktop;
        internal static ConfigEntry<bool> ProbeHost;
        internal static ConfigEntry<bool> SnapDebug;
        internal static ConfigEntry<bool> DebugOutput;

        internal static ConfigEntry<bool> EnableSnapHotkeys;
        internal static ConfigEntry<KeyboardShortcut> SnapLeft;
        internal static ConfigEntry<KeyboardShortcut> SnapRight;
        internal static ConfigEntry<KeyboardShortcut> SnapFull;
        internal static ConfigEntry<KeyboardShortcut> SnapQuadrant1;
        internal static ConfigEntry<KeyboardShortcut> SnapQuadrant2;
        internal static ConfigEntry<KeyboardShortcut> SnapQuadrant3;
        internal static ConfigEntry<KeyboardShortcut> SnapQuadrant4;
        internal static ConfigEntry<KeyboardShortcut> SearchScrollback;
        internal static ConfigEntry<KeyboardShortcut> HistorySearchBackward;
        internal static ConfigEntry<KeyboardShortcut> HistorySearchForward;

        private Harmony _harmony;
        private float _nextConfigCheck;
        private System.DateTime _configStamp;

        private void Awake()
        {
            Log = Logger;

            FeatureTerminal = Config.Bind("Features", "Terminal", true,
                "Replace the terminal. Grid screen, readline editing, recalling and searching\n" +
                "history, scrollback search, tab completion, the custom prompt and ls tidying.\n" +
                "\n" +
                "All of it or none of it - off gives you the game's own terminal. Whether\n" +
                "history is kept between sessions is PersistHistory below.");

            FeatureEditor = Config.Bind("Features", "CodeEditor", true,
                "Edit code in neovim instead of the game's editor. Needs a neovim binary; see\n" +
                "the [Editor] section. Without one the game's editor opens as normal.");

            FeatureDesktop = Config.Bind("Features", "Desktop", true,
                "Put the taskbar, clock and widgets in one bar along the top, and give the\n" +
                "space the bottom bar used back to the desktop.");

            PersistHistory = Config.Bind("Features", "PersistHistory", true,
                "Keep command history across terminals and sessions, in\n" +
                "BepInEx/config/" + Guid + ".history\n" +
                "\n" +
                "Separate from Terminal because it writes a file. That file is plain text and\n" +
                "holds whatever you typed, in-game passwords included. IgnoreSpacePrefix and\n" +
                "IgnorePattern under [History] can keep chosen commands out of it.");

            FeatureMail = Config.Bind("Features", "Mail", true,
                "Add a headers link to each message in the mail client.");

            FeatureWindows = Config.Bind("Features", "WindowSnapping", true,
                "Drag a window to a screen edge to snap it, and snap from the keyboard.");

            // ---- terminal ----------------------------------------------------------------

            KillRingSize = Config.Bind("Terminal", "KillRingSize", 10,
                "How many kills Alt+Y cycles through.");

            ScreenPadding = Config.Bind("Terminal", "ScreenPadding", 10f,
                "Pixels between the terminal's edge and its text.");

            CursorStyle = Config.Bind("Terminal", "CursorStyle", "block",
                "Cursor shape: block, bar or underline.");

            CursorBlink = Config.Bind("Terminal", "CursorBlink", true,
                "Blink the cursor. Only the focused terminal draws one; the rest show an\n" +
                "outline.");

            TerminalFontName = Config.Bind("Terminal", "Font", "",
                "Font to render the terminal in. Blank keeps the game's own.\n" +
                "\n" +
                "Copy the .ttf or .otf into BepInEx/fonts/ and name it here:\n" +
                "\n" +
                "    cp /usr/share/fonts/hack/Hack-Regular.ttf <game>/BepInEx/fonts/\n" +
                "    Font = Hack\n" +
                "\n" +
                "The copy is required. Steam runs the game in a container with its own\n" +
                "/usr/share/fonts, so fonts installed on the machine are not reachable.\n" +
                "\n" +
                "Matching is on the filename, ignoring case and punctuation, and the regular\n" +
                "weight wins over bold and italic. An absolute path also works. These are\n" +
                "searched too:\n" +
                "  ~/.fonts, ~/.local/share/fonts\n" +
                "  /run/host/usr/share/fonts\n" +
                "  /usr/share/fonts\n" +
                "  %WINDIR%/Fonts\n" +
                "\n" +
                "Use a monospace font. `ls -l` and `ps` are padded to fixed columns by the\n" +
                "server, and a proportional font leaves them ragged.");

            TerminalFontSize = Config.Bind("Terminal", "FontSize", 0f,
                "Point size for terminal text. 0 keeps the game's.");

            TerminalFontFallback = Config.Bind("Terminal", "FontFallback", true,
                "Fall back to the game's font for glyphs the chosen one lacks. The game ships a\n" +
                "different monospace font per locale, and most programming fonts cover none of\n" +
                "them.");

            PanelBackgroundAlpha = Config.Bind("Terminal", "PanelBackgroundAlpha", 128,
                "Opacity, 0-255, of the panels behind scrollback search, history search and the\n" +
                "mail headers.");

            CompletionBackgroundAlpha = Config.Bind("Terminal", "CompletionBackgroundAlpha", 0,
                "Opacity, 0-255, of the completion strip.");

            CompletionSelectedColor = Config.Bind("Terminal", "CompletionSelectedColor", "#ffd75f",
                "Colour of the highlighted candidate in the completion menu.");

            FilterCommandNames = Config.Bind("Terminal", "FilterCommandNames", true,
                "Leave command names out of completions for arguments. The server answers with\n" +
                "everything in scope because it does not know which slot is being filled.");

            KnownCommands = Config.Bind("Terminal", "KnownCommands",
                "aircrack, aireplay, airmon, apt-get, build, cat, cd, chgrp, chmod, chown, " +
                "clear, cp, decipher, echo, exit, ftp, groupadd, groupdel, groups, help, " +
                "ifconfig, iwconfig, iwlist, kill, ls, mkdir, mv, nmap, nslookup, passwd, " +
                "ping, ps, pwd, reboot, rm, scanlib, shutdown, smtp_user_list, ssh, sudo, " +
                "touch, useradd, userdel, whoami, whois",
                "Extra names treated as commands by the filter above. The real list is read\n" +
                "from /bin on the machine; this covers names that live elsewhere.");

            IgnoreArgumentExtensions = Config.Bind("Terminal", "IgnoreArgumentExtensions", ".exe",
                "Extensions never offered when completing an argument. Completing the first\n" +
                "word still offers them.");

            SearchMatchTextColor = Config.Bind("Terminal", "SearchMatchColor", "#7fb4ff",
                "Text colour for scrollback search matches other than the current one.");

            SearchActiveTextColor = Config.Bind("Terminal", "SearchActiveColor", "#ffd75f",
                "Text colour for the match you are on.");

            SearchMatchHighlightColor = Config.Bind("Terminal", "SearchMatchHighlight", "",
                "Optional background behind matches. RGBA hex, blank for none. Keep the alpha\n" +
                "low, around #7fb4ff40.");

            SearchActiveHighlightColor = Config.Bind("Terminal", "SearchActiveHighlight", "",
                "Optional background behind the current match. RGBA hex, blank for none.");

            // ---- history -----------------------------------------------------------------

            HistoryLimit = Config.Bind("History", "HistoryLimit", 500,
                "How many commands to keep. Oldest are dropped first.");

            HistoryIgnoreSpacePrefix = Config.Bind("History", "IgnoreSpacePrefix", false,
                "Do not record commands typed with a leading space (bash's ignorespace).");

            HistoryIgnoreDuplicates = Config.Bind("History", "IgnoreDuplicates", true,
                "Do not record a command identical to the one before it (bash's ignoredups).");

            HistoryIgnorePattern = Config.Bind("History", "IgnorePattern", "",
                "Regex - commands matching it are never recorded. Blank disables the check.\n" +
                "Example:  ^\\s*ssh\\s");

            // ---- prompt ------------------------------------------------------------------

            PromptTemplate = Config.Bind("Prompt", "Prompt",
                "{user}@{host}:{path}{sym}{sp}",
                "Prompt layout.\n" +
                "\n" +
                "TEXT     {user} {host} {path} {sym} {ip} {device} {pid}\n" +
                "COLOUR   {#name} (from Palette) or {#rrggbb} (literal), {/} clears it\n" +
                "SPACING  \\n or {nl} = newline, {sp} = space\n" +
                "\n" +
                "The template is the whole prompt; the server's trailing space is dropped, so\n" +
                "end with {sp}. Literal text is uncoloured unless a {#...} is in effect.\n" +
                "Variables colour themselves from Palette and hand the colour back, so no {/}\n" +
                "is needed after one.\n" +
                "\n" +
                "Two lines:  +-[{user}@{host}] - [{path}]\\n+-[{sym}]{sp}");

            PromptPalette = Config.Bind("Prompt", "Palette",
                "user:#50fa7b, user.root:#ff5555, user.guest:#f8f8f2, host:#8be9fd, path:#bd93f9",
                "Colours for prompt variables, as name:colour pairs. The # is optional.\n" +
                "\n" +
                "A name matching a variable colours it. Add '.root' or '.guest' to vary it by\n" +
                "who you are logged in as. Names are also usable as {#name} in the template.\n" +
                "A variable with no entry here inherits the running colour.");

            PromptSym = Config.Bind("Prompt", "Sym", "root:#, user:$, guest:$",
                "What {sym} prints, per role. Any role left out keeps the server's symbol.");

            PromptTemplateRemote = Config.Bind("Prompt", "PromptRemote", "",
                "Used instead of Prompt on a remote machine. Blank means use Prompt.");

            PromptPaletteRemote = Config.Bind("Prompt", "PaletteRemote",
                "user:#ffb86c, user.root:#ff2222, host:#ffb86c",
                "Overlays Palette when remote. Only the names listed here change.");

            PromptSymRemote = Config.Bind("Prompt", "SymRemote", "",
                "Overlays Sym when remote. Only the roles listed here change.");

            // ---- editor ------------------------------------------------------------------

            NvimPath = Config.Bind("Editor", "NvimPath", "",
                "Where the neovim binary is. Blank looks in BepInEx/nvim/bin.");

            NvimWorkspace = Config.Bind("Editor", "NvimWorkspace", "",
                "The directory the editor starts in. Blank means BepInEx/workspace, made on\n" +
                "first use.\n" +
                "\n" +
                "A folder on your machine, holding none of the game's files - a script lives on\n" +
                "the server and reaches the editor as text. :w writes here; the window's save\n" +
                "button compiles into the game.");

            NvimFiletype = Config.Bind("Editor", "NvimFiletype", "greyscript",
                "The filetype neovim is told the buffer is. Highlighting follows from this, so\n" +
                "a syntax file or treesitter parser for it works once installed. Blank lets\n" +
                "neovim infer it from the file name.");

            NvimDownload = Config.Bind("Editor", "NvimDownload", false,
                "Off by default. Fetches neovim into BepInEx/nvim on startup when it is not\n" +
                "already there.\n" +
                "\n" +
                "To install it yourself:\n" +
                "  Linux    https://github.com/neovim/neovim/releases/latest/download/nvim-linux-x86_64.tar.gz\n" +
                "  Windows  https://github.com/neovim/neovim/releases/latest/download/nvim-win64.zip\n" +
                "\n" +
                "Unpack it so the binary sits at BepInEx/nvim/bin/nvim (nvim.exe on Windows).");

            // ---- desktop -----------------------------------------------------------------

            BarSpacing = Config.Bind("Desktop", "BarSpacing", 12f,
                "Pixels between the things in the top bar. The gap before the window list is\n" +
                "twice this.");

            // ---- mail --------------------------------------------------------------------

            MailCards = Config.Bind("Mail", "CardStyle", true,
                "Draw each message in a thread as its own panel.");

            // ---- windows -----------------------------------------------------------------

            SkipDragFocus = Config.Bind("Windows", "SkipDragFocus", true,
                "Skip the game's per-frame refocus while dragging a window.\n" +
                "\n" +
                "Focusing reorders siblings, which rebuilds every window's batches, so dragging\n" +
                "with several windows open drops to a fraction of the frame rate. Skipping the\n" +
                "repeats holds it at whatever the rest of the game runs at.");

            SnapPreview = Config.Bind("Windows", "SnapPreview", true,
                "Outline where a dragged window will land before you let go.");

            SnapTopMaximizes = Config.Bind("Windows", "SnapTopMaximizes", true,
                "Dragging to the top edge fills the desktop.");

            SnapEdgeMargin = Config.Bind("Windows", "SnapEdgeMargin", 0.04f,
                "How close to an edge the pointer must be to snap, as a fraction of the\n" +
                "desktop.");

            SnapCornerBand = Config.Bind("Windows", "SnapCornerBand", 0.3f,
                "Fraction of desktop height at each end of a side edge that counts as a corner\n" +
                "rather than the middle.");

            EnableModifierDrag = Config.Bind("Windows", "EnableModifierDrag", true,
                "Hold a modifier and drag anywhere on a window to move it.");

            ModifierDragKey = Config.Bind("Windows", "ModifierDragKey", "ctrl",
                "Modifier for drag-from-anywhere: ctrl, alt or shift.");

            // ---- diagnostics -------------------------------------------------------------

            CompletionDebug = Config.Bind("Diagnostics", "CompletionDebug", false,
                "Log what Tab asks the server to complete and what comes back.");

            MailDebug = Config.Bind("Diagnostics", "MailDebug", false,
                "Log when a mail is opened and how many message rows were found.");

            ScreenDebug = Config.Bind("Diagnostics", "ScreenDebug", false,
                "Log grid sizes, scrolling and what is drawing inside a terminal or editor.");

            ListFonts = Config.Bind("Diagnostics", "ListFonts", false,
                "Log every directory Font searches and the fonts in each. Printed once.");

            DumpDesktop = Config.Bind("Diagnostics", "DumpDesktop", false,
                "Log the desktop's UI hierarchy once - what is parented where and how it is\n" +
                "anchored.");

            ProbeHost = Config.Bind("Diagnostics", "ProbeHost", false,
                "Log once what the game process can reach: its environment, which directories\n" +
                "exist, and whether a child process can be started.");

            SnapDebug = Config.Bind("Diagnostics", "SnapDebug", false,
                "Log window drag and snap-zone decisions.");

            DebugOutput = Config.Bind("Diagnostics", "DebugOutput", false,
                "Log every command sent and every output block received. Noisy.");

            _harmony = new Harmony(Guid);

            // Registered one at a time: a patch that fails to bind - a renamed method after
            // a game update, say - must not take the rest of the plugin down with it.
            Register("readline", () => _harmony.PatchAll(typeof(Readline)));
            Register("output", () => _harmony.PatchAll(typeof(Output)));
            Register("line-submit", () => _harmony.PatchAll(typeof(LineSubmit)));
            Register("prompt", () => _harmony.PatchAll(typeof(Prompt)));
            Register("mail", () => _harmony.PatchAll(typeof(MailHeaders)));
            Register("history", () => History.Apply(_harmony));
            Register("completion", () => Completion.Apply(_harmony));
            Register("completion-request", () => _harmony.PatchAll(typeof(CompletionRequest)));
            Register("windows", () => WindowSnap.Apply(_harmony));
            Register("window-area", () => WindowArea.Apply(_harmony));
            Register("clock", () => DesktopClock.Apply(_harmony));
            Register("nvim", () => NvimEditor.Apply(_harmony));
            Register("terminal-font", () => TerminalFont.Apply(_harmony));
            Register("screen", () => ScreenTakeover.Apply(_harmony));
            Register("screen-clipboard", () => ScreenClipboard.Apply(_harmony));
            Register("window-close", () => _harmony.PatchAll(typeof(WindowClose)));
            BindHotkeys();
            PruneOrphanedSettings(Config, "settings");
            PruneOrphanedSettings(Hotkeys, "hotkeys");
            SectionFirst(Config.ConfigFilePath, "Features");
            Log.LogInfo($"{Name} {Version} ready.");
        }


        /// <summary>
        /// BepInEx keeps settings it reads from the file but nothing binds, so that a
        /// temporarily disabled plugin does not lose them. Across renames and removed
        /// features that turns into a pile of dead keys, so they are dropped once every
        /// Bind above has run - anything still unclaimed by now is genuinely gone.
        /// </summary>
        private void PruneOrphanedSettings(ConfigFile file, string label)
        {
            if (file == null) return;
            try
            {
                var property = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");
                if (!(property?.GetValue(file) is System.Collections.IDictionary orphans)) return;
                if (orphans.Count == 0) return;

                var names = new System.Collections.Generic.List<string>();
                foreach (object key in orphans.Keys) names.Add(key.ToString());

                orphans.Clear();
                file.Save();
                Log.LogInfo($"dropped orphaned {label}: " + string.Join(", ", names.ToArray()));
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"could not prune orphaned {label}: " + e.Message);
            }
        }

        /// <summary>
        /// Move a section to the top of the file it was written into.
        ///
        /// Sections come out in alphabetical order, which puts the one saying what is switched
        /// on somewhere in the middle. There is no ordering hint to ask for, so the file is
        /// rearranged once after it is written.
        /// </summary>
        private void SectionFirst(string path, string section)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return;

                string[] lines = System.IO.File.ReadAllLines(path);
                string header = "[" + section + "]";

                int start = System.Array.IndexOf(lines, header);
                if (start <= 0) return;

                int end = start + 1;
                while (end < lines.Length && !lines[end].StartsWith("[")) end++;

                // The file's own comment header stays where it is; the section goes under it.
                int after = 0;
                while (after < lines.Length && (lines[after].StartsWith("##") || lines[after].Length == 0)) after++;
                if (after >= start) return;

                var reordered = new System.Collections.Generic.List<string>(lines.Length);
                reordered.AddRange(new System.ArraySegment<string>(lines, 0, after));
                reordered.AddRange(new System.ArraySegment<string>(lines, start, end - start));
                reordered.AddRange(new System.ArraySegment<string>(lines, after, start - after));
                reordered.AddRange(new System.ArraySegment<string>(lines, end, lines.Length - end));

                System.IO.File.WriteAllLines(path, reordered.ToArray());

                // Ours, not someone editing it - so the watcher does not read it back as a
                // change and announce a reload that changed nothing.
                _configStamp = System.IO.File.GetLastWriteTimeUtc(path);
            }
            catch (System.Exception e)
            {
                Log.LogWarning($"could not move [{section}] to the top: {e.Message}");
            }
        }

        /// <summary>
        /// Hotkeys live in their own file: there are enough of them now that mixing them
        /// with colours and toggles makes both harder to find, and rebinding is the kind of
        /// thing people do without wanting to read past everything else.
        /// </summary>
        private void BindHotkeys()
        {
            Hotkeys = new ConfigFile(
                System.IO.Path.Combine(Paths.ConfigPath, Guid + ".hotkeys.cfg"), true, Info.Metadata);

            EnableSnapHotkeys = Hotkeys.Bind("Windows", "EnableSnapHotkeys", true,
                "Snap the focused window from the keyboard.");

            SnapLeft = Hotkeys.Bind("Windows", "SnapLeft",
                new KeyboardShortcut(KeyCode.LeftArrow, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Left half of the desktop.");

            SnapRight = Hotkeys.Bind("Windows", "SnapRight",
                new KeyboardShortcut(KeyCode.RightArrow, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Right half of the desktop.");

            SnapFull = Hotkeys.Bind("Windows", "SnapFull",
                new KeyboardShortcut(KeyCode.UpArrow, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Fill the desktop.");

            SnapQuadrant1 = Hotkeys.Bind("Windows", "SnapQuadrant1",
                new KeyboardShortcut(KeyCode.Alpha1, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Top right. Quadrants are numbered as on an x-y axis, counter-clockwise from\n" +
                "top right.");

            SnapQuadrant2 = Hotkeys.Bind("Windows", "SnapQuadrant2",
                new KeyboardShortcut(KeyCode.Alpha2, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Top left.");

            SnapQuadrant3 = Hotkeys.Bind("Windows", "SnapQuadrant3",
                new KeyboardShortcut(KeyCode.Alpha3, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Bottom left.");

            SnapQuadrant4 = Hotkeys.Bind("Windows", "SnapQuadrant4",
                new KeyboardShortcut(KeyCode.Alpha4, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Bottom right.");

            SearchScrollback = Hotkeys.Bind("Terminal", "SearchScrollback",
                new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl),
                "Search the terminal scrollback. Press again for the next match.\n" +
                "\n" +
                "The readline keys - Ctrl+A/E/K/U/W, Alt+B/F and the rest - are not rebindable.\n" +
                "Only the three here are, because they collide with existing habits.");

            HistorySearchBackward = Hotkeys.Bind("Terminal", "HistorySearchBackward",
                new KeyboardShortcut(KeyCode.R, KeyCode.LeftControl),
                "Incremental history search backwards, and step to the next older match.");

            HistorySearchForward = Hotkeys.Bind("Terminal", "HistorySearchForward",
                new KeyboardShortcut(KeyCode.S, KeyCode.LeftControl),
                "Search history forwards again.");
        }

        private void Register(string feature, System.Action patch)
        {
            try
            {
                patch();
            }
            catch (System.Exception e)
            {
                Log.LogError($"{feature} could not be patched, that feature is off: {e.Message}");
            }
        }

        private void Update()
        {
            Patches.WindowSnap.Tick();
            Patches.ScreenTakeover.Tick();
            Ui.PrimaryPaste.Tick();
            Ui.DesktopReport.Tick();
            HostProbe.Tick();
            Patches.NvimEditor.Tick();
            Ui.DesktopBar.Tick();
            PollConfigFile();
        }

        /// <summary>
        /// BepInEx does not watch the config file, so editing it by hand changes nothing
        /// until something calls Reload. Polling its timestamp is enough to make edits
        /// take effect while the game is running.
        /// </summary>
        private void PollConfigFile()
        {
            if (UnityEngine.Time.unscaledTime < _nextConfigCheck) return;
            _nextConfigCheck = UnityEngine.Time.unscaledTime + 2f;

            try
            {
                System.DateTime stamp = System.IO.File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                if (Hotkeys != null)
                {
                    System.DateTime hotkeyStamp = System.IO.File.GetLastWriteTimeUtc(Hotkeys.ConfigFilePath);
                    if (hotkeyStamp > stamp) stamp = hotkeyStamp;
                }
                if (stamp == _configStamp) return;

                bool first = _configStamp == default(System.DateTime);
                _configStamp = stamp;
                if (first) return;

                Config.Reload();
                Hotkeys?.Reload();
                TerminalFont.OnConfigReloaded();
                Log.LogInfo("config reloaded");
            }
            catch (System.Exception e)
            {
                Log.LogWarning("config reload failed: " + e.Message);
            }
        }

        /// <summary>
        /// Called when the plugin is torn down, and on the way out of the game.
        ///
        /// The editor runs neovim as a child process, and a child outlives its parent unless
        /// something ends it - which is how the game can look like it is still running after
        /// its window has gone.
        /// </summary>
        private void OnDestroy()
        {
            Patches.NvimEditor.Shutdown();
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit() => Patches.NvimEditor.Shutdown();
    }
}
