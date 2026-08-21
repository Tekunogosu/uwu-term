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
        internal static ConfigEntry<bool> EnableReadline;
        internal static ConfigEntry<int> KillRingSize;
        internal static ConfigEntry<int> PanelBackgroundAlpha;
        internal static ConfigEntry<int> CompletionBackgroundAlpha;
        internal static ConfigEntry<string> TerminalFontName;
        internal static ConfigEntry<float> TerminalFontSize;
        internal static ConfigEntry<bool> TerminalFontFallback;
        internal static ConfigEntry<bool> ListFonts;
        internal static ConfigEntry<bool> EnableScreen;
        internal static ConfigEntry<float> ScreenPadding;
        internal static ConfigEntry<string> CursorStyle;
        internal static ConfigEntry<bool> CursorBlink;
        internal static ConfigEntry<bool> ScreenDebug;
        internal static ConfigEntry<bool> MenuComplete;
        internal static ConfigEntry<string> CompletionSelectedColor;
        internal static ConfigEntry<bool> CompletionDebug;
        internal static ConfigEntry<bool> FilterCommandNames;
        internal static ConfigEntry<string> KnownCommands;
        internal static ConfigEntry<string> IgnoreArgumentExtensions;
        internal static ConfigEntry<bool> MailHeaders;
        internal static ConfigEntry<bool> MailCards;
        internal static ConfigEntry<bool> MailDebug;
        internal static ConfigEntry<string> SearchMatchTextColor;
        internal static ConfigEntry<string> SearchMatchHighlightColor;
        internal static ConfigEntry<string> SearchActiveTextColor;
        internal static ConfigEntry<string> SearchActiveHighlightColor;
        internal static ConfigEntry<bool> PersistHistory;
        internal static ConfigEntry<int> HistoryLimit;
        internal static ConfigEntry<bool> HistoryIgnoreSpacePrefix;
        internal static ConfigEntry<bool> HistoryIgnoreDuplicates;
        internal static ConfigEntry<string> HistoryIgnorePattern;
        internal static ConfigEntry<bool> EnableLsColumns;
        internal static ConfigEntry<bool> ColorizePrompt;
        internal static ConfigEntry<string> PromptTemplate;
        internal static ConfigEntry<string> PromptPalette;
        internal static ConfigEntry<string> PromptTemplateRemote;
        internal static ConfigEntry<string> PromptPaletteRemote;
        internal static ConfigEntry<string> PromptSym;
        internal static ConfigEntry<string> PromptSymRemote;
        internal static ConfigEntry<bool> ReadlineDebug;
        internal static ConfigEntry<bool> NormalizeLsFlags;
        internal static ConfigEntry<bool> EnableWindowSnap;
        internal static ConfigEntry<bool> SnapTopMaximizes;
        internal static ConfigEntry<bool> SnapPreview;
        internal static ConfigEntry<bool> SkipDragFocus;

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
        internal static ConfigEntry<float> SnapEdgeMargin;
        internal static ConfigEntry<float> SnapCornerBand;
        internal static ConfigEntry<bool> SnapDebug;
        internal static ConfigEntry<bool> EnableModifierDrag;
        internal static ConfigEntry<string> ModifierDragKey;
        internal static ConfigEntry<bool> DebugOutput;

        private Harmony _harmony;
        private float _nextConfigCheck;
        private System.DateTime _configStamp;

        private void Awake()
        {
            Log = Logger;

            EnableReadline = Config.Bind("Input", "EnableReadline", true,
                "Readline editing in the terminal.\n" +
                "\n" +
                "MOVE    Ctrl+A/E start, end        Ctrl+B/F char\n" +
                "        Alt+B/F word              Ctrl+Left/Right word\n" +
                "KILL    Ctrl+K to end             Ctrl+U to start\n" +
                "        Ctrl+W word back (whitespace-delimited)\n" +
                "        Alt+Backspace / Ctrl+Backspace word back\n" +
                "        Alt+D word forward        Ctrl+D delete char forward\n" +
                "YANK    Ctrl+Y paste last kill    Alt+Y cycle back through the ring\n" +
                "EDIT    Ctrl+T swap chars         Alt+T swap words\n" +
                "        Alt+U/L/C upper, lower, capitalise word\n" +
                "        Ctrl+Z undo               Ctrl+L clear screen\n" +
                "HIST    Ctrl+P/N previous, next\n" +
                "        Ctrl+R/S search history backwards, forwards\n" +
                "FIND    Ctrl+F search the scrollback\n" +
                "\n" +
                "Consecutive kills accumulate into one kill-ring entry, so Ctrl+W Ctrl+W then\n" +
                "Ctrl+Y brings both words back in order. Alt+Y only works straight after a\n" +
                "yank. Ctrl+C and Ctrl+Shift+C/V stay the game's.");

            KillRingSize = Config.Bind("Input", "KillRingSize", 10,
                "How many kills to remember for Alt+Y to cycle through.");
            PanelBackgroundAlpha = Config.Bind("Interface", "PanelBackgroundAlpha", 128,
                "Opacity, 0-255, of the panel behind the floating overlays - scrollback\n" +
                "search, history search and the mail headers. They sit over content, so a\n" +
                "backing makes them readable.");

            CompletionBackgroundAlpha = Config.Bind("Interface", "CompletionBackgroundAlpha", 0,
                "Opacity, 0-255, of the completion strip. It gets a line of its own rather\n" +
                "than covering anything, so it needs no backing.");

            EnableScreen = Config.Bind("Screen", "EnableScreen", false,
                "Draw the terminal as a character grid instead of as a list of text objects.\n" +
                "\n" +
                "The game gives every line its own text object, its own layout element and its\n" +
                "own measurement pass, so what it costs to draw grows with how much has been\n" +
                "printed, and a line's height - measured once, the first time it is shown - is\n" +
                "then cached forever. This draws the visible rows as one mesh and computes a\n" +
                "row's height from the font's own metrics, so it costs the same whether the\n" +
                "scrollback holds fifty lines or fifty thousand, and cannot go stale.\n" +
                "\n" +
                "The game's own rows are hidden rather than removed, because they are still\n" +
                "the model a submitted command is read out of and the one Readline edits.\n" +
                "Turning this back off restores them immediately, without a restart.\n" +
                "\n" +
                "Unfinished: selecting text with the mouse still works on the hidden rows, so\n" +
                "the highlight is invisible while the selection itself is real. Copy still\n" +
                "copies what you selected.");

            ScreenPadding = Config.Bind("Screen", "ScreenPadding", 10f,
                "Pixels of breathing room between the terminal's edge and its text.\n" +
                "\n" +
                "Only applies when EnableScreen is on. Text starts at the very edge of the\n" +
                "scroll area otherwise, where the window's own mask clips the first column.");

            CursorStyle = Config.Bind("Screen", "CursorStyle", "block",
                "Shape of the terminal cursor: block, bar or underline.\n" +
                "\n" +
                "A block covers the character and is drawn translucent so the letter reads\n" +
                "through it. A bar sits before the character and an underline beneath it, both\n" +
                "solid, since a thin translucent line is barely visible.\n" +
                "\n" +
                "All three are sized from the cell, so they follow whatever TerminalFont and\n" +
                "TerminalFontSize are set to.");

            CursorBlink = Config.Bind("Screen", "CursorBlink", true,
                "Blink the cursor, half a second on and half off. Off leaves it lit.\n" +
                "\n" +
                "Only the terminal you are typing in draws a cursor either way - an unfocused\n" +
                "one would otherwise sit there blinking alongside it with nothing to type into.");

            TerminalFontName = Config.Bind("Interface", "TerminalFont", "",
                "Font to render the terminal in. Blank keeps the game's own.\n" +
                "\n" +
                "Copy the .ttf or .otf into BepInEx/fonts/ and name it here:\n" +
                "\n" +
                "    cp /usr/share/fonts/hack/Hack-Regular.ttf <game>/BepInEx/fonts/\n" +
                "    TerminalFont = Hack\n" +
                "\n" +
                "The copy is what makes it work. Steam runs the game inside a container with a\n" +
                "/usr/share/fonts of its own, holding six DejaVu faces and nothing else, so the\n" +
                "fonts installed on the machine cannot be reached from in here. BepInEx/fonts\n" +
                "can be, because the game is running out of it.\n" +
                "\n" +
                "Matching is on the filename, ignoring case and punctuation, and the regular\n" +
                "weight wins over its bold and italic siblings - so \"Hack\" finds\n" +
                "Hack-Regular.ttf. A font whose filename does not resemble the family name it\n" +
                "is known by has to be named as the file. An absolute path also works.\n" +
                "\n" +
                "These are searched as well, and are enough wherever nothing is sandboxing the\n" +
                "game:\n" +
                "  ~/.fonts, ~/.local/share/fonts\n" +
                "  /run/host/usr/share/fonts   the real system, seen from inside a container\n" +
                "  /usr/share/fonts\n" +
                "  %WINDIR%/Fonts\n" +
                "Set ListFonts under [Diagnostics] to see which of them this game can read.\n" +
                "\n" +
                "Pick a monospace font. The caret is drawn inline so it lands correctly either\n" +
                "way, but the server pads `ls -l` and `ps` output to fixed columns and the `ls`\n" +
                "reflow measures one glyph's advance - a proportional font leaves both ragged.\n" +
                "\n" +
                "Only terminal windows change. Titlebars, the mail client and the desktop keep\n" +
                "the game's fonts, which its layouts are measured against.");

            TerminalFontSize = Config.Bind("Interface", "TerminalFontSize", 0f,
                "Point size for terminal text. 0 keeps whatever the game set, which is what\n" +
                "you want unless the font you chose renders larger or smaller than the one it\n" +
                "replaced.\n" +
                "\n" +
                "Every open terminal remeasures its rows when this changes, so the lines close\n" +
                "up around a smaller size rather than leaving it stranded in the taller rows\n" +
                "the old size was measured into.");

            TerminalFontFallback = Config.Bind("Interface", "TerminalFontFallback", true,
                "Fall back to the game's own font for glyphs the chosen one does not have.\n" +
                "\n" +
                "The game ships a different monospace font per locale - cyrillic, japanese and\n" +
                "chinese each have their own - and a programming font typically covers none of\n" +
                "them. Leaving this on means those characters keep rendering instead of coming\n" +
                "out as empty boxes.");

            MenuComplete = Config.Bind("Input", "MenuComplete", true,
                "Tab cycles through completions instead of printing them all and stopping at\n" +
                "the longest common prefix. Shift+Tab steps back, Escape restores what you\n" +
                "had typed, anything else accepts the selection and carries on.");

            CompletionSelectedColor = Config.Bind("Input", "CompletionSelectedColor", "#ffd75f",
                "Colour of the highlighted candidate in the completion menu.");

            FilterCommandNames = Config.Bind("Input", "FilterCommandNames", true,
                "Leave command names out of completions for arguments. The server answers\n" +
                "with everything in scope, commands included, because it does not know which\n" +
                "slot you are filling - useful for the first word, noise after it.");

            KnownCommands = Config.Bind("Input", "KnownCommands",
                "aircrack, aireplay, airmon, apt-get, build, cat, cd, chgrp, chmod, chown, " +
                "clear, cp, decipher, echo, exit, ftp, groupadd, groupdel, groups, help, " +
                "ifconfig, iwconfig, iwlist, kill, ls, mkdir, mv, nmap, nslookup, passwd, " +
                "ping, ps, pwd, reboot, rm, scanlib, shutdown, smtp_user_list, ssh, sudo, " +
                "touch, useradd, userdel, whoami, whois",
                "Names treated as commands by the filter above.\n" +
                "\n" +
                "This is only a starting point. Completing on an empty prompt asks the server\n" +
                "for the command slot, and the answer is the real command list for that\n" +
                "machine - so one Tab on a blank line teaches the filter everything actually\n" +
                "installed there, custom binaries included. This list covers you until then.");

            IgnoreArgumentExtensions = Config.Bind("Input", "IgnoreArgumentExtensions", ".exe",
                "File extensions never offered when completing an argument. .exe is a\n" +
                "windowed program - something you launch, never something you pass to\n" +
                "another command. Completing the first word still offers them.");

            CompletionDebug = Config.Bind("Diagnostics", "CompletionDebug", true,
                "Log the raw candidate list the server sends back for a Tab completion.");

            MailHeaders = Config.Bind("Mail", "ShowHeaderLink", true,
                "Add a \"headers\" link to each message in the mail client, showing sender,\n" +
                "recipient, direction and the rest of what the game stores about it.");

            MailCards = Config.Bind("Mail", "CardStyle", true,
                "Draw each message in a thread as its own panel, so replies are separated\n" +
                "instead of running together.");

            MailDebug = Config.Bind("Diagnostics", "MailDebug", false,
                "Log when a mail is opened and how many message rows were found.");

            SearchMatchTextColor = Config.Bind("Search", "MatchTextColor", "#7fb4ff",
                "Text colour for matches other than the one you are on.");

            SearchActiveTextColor = Config.Bind("Search", "ActiveTextColor", "#ffd75f",
                "Text colour for the match you are currently on.");

            SearchMatchHighlightColor = Config.Bind("Search", "MatchHighlightColor", "",
                "Optional background behind matches other than the active one. RGBA hex,\n" +
                "blank for none.\n" +
                "\n" +
                "TMP draws a background as a filled quad OVER the glyphs - it is what the\n" +
                "game uses to censor addresses in streaming mode - so a solid colour hides\n" +
                "the very text you searched for. Keep the alpha low if you use one, around\n" +
                "#7fb4ff40, and check it against your theme.");

            SearchActiveHighlightColor = Config.Bind("Search", "ActiveHighlightColor", "",
                "Optional background behind the active match. Same caveat as\n" +
                "MatchHighlightColor - keep the alpha low.");

            PersistHistory = Config.Bind("History", "PersistHistory", true,
                "Keep command history across terminals and across sessions, in\n" +
                "BepInEx/config/" + Guid + ".history\n" +
                "\n" +
                "All open terminals share one history, so a command typed in one is\n" +
                "immediately available with Up in another.\n" +
                "\n" +
                "The file is plain text and holds whatever you typed, in-game passwords\n" +
                "included. Nothing in the game can read it. IgnoreSpacePrefix and\n" +
                "IgnorePattern below can keep chosen commands out of it if you want that.");

            HistoryLimit = Config.Bind("History", "HistoryLimit", 500,
                "How many commands to keep. Oldest are dropped first.");

            HistoryIgnoreSpacePrefix = Config.Bind("History", "IgnoreSpacePrefix", false,
                "Commands typed with a leading space are not recorded (bash's ignorespace).\n" +
                "Off by default - turn it on if you want a way to skip individual commands.");

            HistoryIgnoreDuplicates = Config.Bind("History", "IgnoreDuplicates", true,
                "Do not record a command identical to the one before it (bash's ignoredups).");

            HistoryIgnorePattern = Config.Bind("History", "IgnorePattern", "",
                "Regex - commands matching it are never recorded. Blank disables the check.\n" +
                "Example, to keep every ssh invocation out of the file:  ^\\s*ssh\\s");

            ColorizePrompt = Config.Bind("Prompt", "ColorizePrompt", true,
                "Replace the server's prompt with a custom one.");

            PromptTemplate = Config.Bind("Prompt", "Prompt",
                "{user}@{host}:{path}{sym}{sp}",
                "Prompt layout.\n" +
                "\n" +
                "TEXT     {user} {host} {path} {sym} {ip} {device} {pid}\n" +
                "COLOUR   {#name} (from Palette) or {#rrggbb} (literal)\n" +
                "         {/} clears it again\n" +
                "SPACING  \\n or {nl} = newline, {sp} = space\n" +
                "         The template is the entire prompt - the trailing space the server\n" +
                "         sends is dropped, so end with {sp} or your command runs into it.\n" +
                "         (a literal trailing space will not survive - config values are trimmed)\n" +
                "\n" +
                "HOW COLOUR WORKS\n" +
                "Literal text is not coloured at all by default, so it uses the terminal\n" +
                "theme's own text colour. A {#...} sets a colour for the literal text that\n" +
                "follows and stays in effect until the next {#...} or a {/}.\n" +
                "\n" +
                "Variables colour themselves from Palette and then hand the colour back to\n" +
                "whatever {#...} was running, so you never need a {/} after one. A variable\n" +
                "Palette does not name is left to the running colour instead.\n" +
                "\n" +
                "Mixing the two:\n" +
                "  {#8be9fd}[{user}@{host}] {path}{sym}{sp}\n" +
                "the brackets, the @ and the space are cyan; {user} and {host} use their\n" +
                "Palette colours and cyan resumes after each; {path} likewise; {sym} has no\n" +
                "Palette entry, so it stays cyan.\n" +
                "\n" +
                "Two lines, nothing coloured by the template:\n" +
                "  +-[{user}@{host}] - [{path}]\\n+-[{sym}]{sp}");

            PromptPalette = Config.Bind("Prompt", "Palette",
                "user:#50fa7b, user.root:#ff5555, user.guest:#f8f8f2, host:#8be9fd, path:#bd93f9",
                "Colours for prompt variables, as name:colour pairs. The # is optional.\n" +
                "\n" +
                "A name matching a variable colours that variable: 'host' colours {host}.\n" +
                "Add '.root' or '.guest' to vary it by who you are logged in as - {user}\n" +
                "tries 'user.root' first and falls back to 'user'. This works for every\n" +
                "variable, so 'path.root' turns the path red only while you are root.\n" +
                "\n" +
                "Names are also usable in the template as {#name}, which is how you colour\n" +
                "literal text without repeating hex codes. Anything that is valid hex is\n" +
                "read as a colour rather than a name, so avoid naming an entry 'abc'.\n" +
                "\n" +
                "A variable with no entry here is not coloured - it inherits the running\n" +
                "{#...} colour, or the terminal theme if none is set.");

            PromptSym = Config.Bind("Prompt", "Sym", "root:#, user:$, guest:$",
                "What {sym} prints, per role: root, user, guest.\n" +
                "The server sends # for root and $ for everyone else; what you set here\n" +
                "overrides it, and any role you leave out keeps the server's symbol.");

            PromptTemplateRemote = Config.Bind("Prompt", "PromptRemote", "",
                "Used instead of Prompt when connected to a remote machine.\n" +
                "Blank means use Prompt. Fill it in only when you want a different layout\n" +
                "out there - for colour or symbol changes alone, use the two below.");

            PromptPaletteRemote = Config.Bind("Prompt", "PaletteRemote",
                "user:#ffb86c, user.root:#ff2222, host:#ffb86c",
                "Overlays Palette when remote - only the names listed here change, the rest\n" +
                "fall through to Palette.");

            PromptSymRemote = Config.Bind("Prompt", "SymRemote", "",
                "Overlays Sym when remote - only the roles listed here change.");

            ReadlineDebug = Config.Bind("Diagnostics", "ReadlineDebug", false,
                "Log word-movement maths.");

            ScreenDebug = Config.Bind("Diagnostics", "ScreenDebug", false,
                "Log when the grid screen takes over a terminal, and the grid size it chose.");

            ListFonts = Config.Bind("Diagnostics", "ListFonts", false,
                "Log every directory TerminalFont searches, whether this game can read it, and\n" +
                "the fonts in it. Printed once, whether set at startup or turned on while the\n" +
                "game runs. A name TerminalFont cannot match prints the same thing, so this is\n" +
                "only needed to browse before choosing.");
            EnableLsColumns = Config.Bind("Output", "EnableLsColumns", true,
                "Reflow bare `ls` output into columns, the way `ls -C` does.");
            NormalizeLsFlags = Config.Bind("Output", "NormalizeLsFlags", true,
                "Accept ls flags in any order: -al, -a -l and -l -a are rewritten to the " +
                "-la the server expects.");
            EnableWindowSnap = Config.Bind("Windows", "EnableWindowSnap", true,
                "Drag a window to an edge to snap it: side for half, corner for a quarter.");
            SkipDragFocus = Config.Bind("Windows", "SkipDragFocus", true,
                "Skip the game's per-frame re-focus while dragging a window.\n" +
                "\n" +
                "uDialog re-focuses a window on every frame of a drag, and focusing reorders\n" +
                "siblings - which dirties the whole canvas and rebuilds the batches for every\n" +
                "window on screen. After the first frame the window is already frontmost, so\n" +
                "the repeats do nothing but cost frames. This is the single biggest\n" +
                "performance fix in the mod - dragging with several windows open went from\n" +
                "about 33 fps to 120.");

            SnapPreview = Config.Bind("Windows", "SnapPreview", true,
                "Outline where a dragged window will land before you let go.");

            SnapTopMaximizes = Config.Bind("Windows", "SnapTopMaximizes", true,
                "Dragging to the top edge fills the desktop.");
            SnapEdgeMargin = Config.Bind("Windows", "SnapEdgeMargin", 0.04f,
                "How close to an edge the pointer must be to snap, as a fraction of the desktop.");
            SnapCornerBand = Config.Bind("Windows", "SnapCornerBand", 0.3f,
                "Fraction of desktop height at the top and bottom of a side edge that counts " +
                "as a corner (quarter) rather than the middle (half).");
            EnableModifierDrag = Config.Bind("Windows", "EnableModifierDrag", true,
                "Hold a modifier and drag anywhere on a window to move it.");
            ModifierDragKey = Config.Bind("Windows", "ModifierDragKey", "ctrl",
                "Modifier for drag-from-anywhere: ctrl, alt or shift.");
            SnapDebug = Config.Bind("Diagnostics", "SnapDebug", false,
                "Log window drag and snap-zone decisions.");
            DebugOutput = Config.Bind("Diagnostics", "DebugOutput", false,
                "Log every command sent and every output block received. Noisy - for " +
                "working out what the server actually sends.");

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
            Register("windows", () => WindowSnap.Apply(_harmony));
            Register("terminal-font", () => TerminalFont.Apply(_harmony));
            Register("screen", () => ScreenTakeover.Apply(_harmony));
            Register("screen-clipboard", () => ScreenClipboard.Apply(_harmony));
            Register("window-close", () => _harmony.PatchAll(typeof(WindowClose)));
            BindHotkeys();
            PruneOrphanedSettings(Config, "settings");
            PruneOrphanedSettings(Hotkeys, "hotkeys");
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
                "Quadrants are numbered as on an x-y axis, counter-clockwise from top right.\n" +
                "Quadrant 1: top right.");

            SnapQuadrant2 = Hotkeys.Bind("Windows", "SnapQuadrant2",
                new KeyboardShortcut(KeyCode.Alpha2, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Quadrant 2: top left.");

            SnapQuadrant3 = Hotkeys.Bind("Windows", "SnapQuadrant3",
                new KeyboardShortcut(KeyCode.Alpha3, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Quadrant 3: bottom left.");

            SnapQuadrant4 = Hotkeys.Bind("Windows", "SnapQuadrant4",
                new KeyboardShortcut(KeyCode.Alpha4, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftShift),
                "Quadrant 4: bottom right.");

            SearchScrollback = Hotkeys.Bind("Terminal", "SearchScrollback",
                new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl),
                "Search the terminal scrollback. Pressing it again steps to the next match.\n" +
                "\n" +
                "The readline editing keys - Ctrl+A/E/K/U/W, Alt+B/F and the rest - are not\n" +
                "rebindable. They are standard across every shell and terminal, and moving\n" +
                "them tends to cause more confusion than it solves. Only these three are\n" +
                "here, because they are the ones that collide with existing habits.");

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
            Patches.Completion.Tick();
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

        private void OnDestroy() => _harmony?.UnpatchSelf();
    }
}
