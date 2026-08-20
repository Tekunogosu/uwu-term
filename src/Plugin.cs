using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
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
        internal static ConfigEntry<bool> EnableReadline;
        internal static ConfigEntry<int> KillRingSize;
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
                "\n" +
                "Consecutive kills accumulate into one kill-ring entry, so Ctrl+W Ctrl+W then\n" +
                "Ctrl+Y brings both words back in order. Alt+Y only works straight after a\n" +
                "yank. Ctrl+C and Ctrl+Shift+C/V stay the game's.");

            KillRingSize = Config.Bind("Input", "KillRingSize", 10,
                "How many kills to remember for Alt+Y to cycle through.");
            MailHeaders = Config.Bind("Mail", "ShowHeaderLink", true,
                "Add a \"headers\" link to each message in the mail client, showing sender,\n" +
                "recipient, direction and the rest of what the game stores about it.");

            MailCards = Config.Bind("Mail", "CardStyle", true,
                "Draw each message in a thread as its own panel, so replies are separated\n" +
                "instead of running together.");

            MailDebug = Config.Bind("Diagnostics", "MailDebug", true,
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
                "         (a trailing space cannot be used - config values are trimmed)\n" +
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

            ReadlineDebug = Config.Bind("Diagnostics", "ReadlineDebug", true,
                "Log word-movement maths.");
            EnableLsColumns = Config.Bind("Output", "EnableLsColumns", true,
                "Reflow bare `ls` output into columns, the way `ls -C` does.");
            NormalizeLsFlags = Config.Bind("Output", "NormalizeLsFlags", true,
                "Accept ls flags in any order: -al, -a -l and -l -a are rewritten to the " +
                "-la the server expects.");
            EnableWindowSnap = Config.Bind("Windows", "EnableWindowSnap", true,
                "Drag a window to an edge to snap it: side for half, corner for a quarter.");
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
            SnapDebug = Config.Bind("Diagnostics", "SnapDebug", true,
                "Log window drag and snap-zone decisions.");
            DebugOutput = Config.Bind("Diagnostics", "DebugOutput", true,
                "Log every command sent and every output block received. Noisy - for " +
                "working out what the server actually sends.");

            _harmony = new Harmony(Guid);

            // Registered one at a time: a patch that fails to bind - a renamed method after
            // a game update, say - must not take the rest of the plugin down with it.
            Register("readline", () => _harmony.PatchAll(typeof(Readline)));
            Register("output", () => _harmony.PatchAll(typeof(Output)));
            Register("prompt", () => _harmony.PatchAll(typeof(Prompt)));
            Register("mail", () => _harmony.PatchAll(typeof(MailHeaders)));
            Register("history", () => History.Apply(_harmony));
            Register("windows", () => WindowSnap.Apply(_harmony));
            Register("window-close", () => _harmony.PatchAll(typeof(WindowClose)));
            PruneOrphanedSettings();
            Log.LogInfo($"{Name} {Version} ready.");
        }


        /// <summary>
        /// BepInEx keeps settings it reads from the file but nothing binds, so that a
        /// temporarily disabled plugin does not lose them. Across renames and removed
        /// features that turns into a pile of dead keys, so they are dropped once every
        /// Bind above has run - anything still unclaimed by now is genuinely gone.
        /// </summary>
        private void PruneOrphanedSettings()
        {
            try
            {
                var property = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");
                if (!(property?.GetValue(Config) is System.Collections.IDictionary orphans)) return;
                if (orphans.Count == 0) return;

                var names = new System.Collections.Generic.List<string>();
                foreach (object key in orphans.Keys) names.Add(key.ToString());

                orphans.Clear();
                Config.Save();
                Log.LogInfo("dropped orphaned settings: " + string.Join(", ", names.ToArray()));
            }
            catch (System.Exception e)
            {
                Log.LogWarning("could not prune orphaned settings: " + e.Message);
            }
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
                if (stamp == _configStamp) return;

                bool first = _configStamp == default(System.DateTime);
                _configStamp = stamp;
                if (first) return;

                Config.Reload();
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
