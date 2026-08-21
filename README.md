# UwUTerm

The cutest terminal and window QoL to ever exist for [Grey Hack](https://store.steampowered.com/app/605230/Grey_Hack/).

*Insert catgirl trans meme here*

BepInEx 5 plugin, Unity 2022.3 Mono.

Everything here is client-side: input handling, rendering and window geometry. Nothing
changes what is sent to the server beyond normalising `ls` flags it would have rejected.

## Features

**A rebuilt terminal.** The screen is drawn as a character grid rendered in one pass, so
drawing costs the same whether the scrollback holds fifty lines or fifty thousand. Line
height comes from the font's own metrics, wide characters take two columns, and long lines
wrap and re-wrap cleanly when the window is resized.

**Selection and clipboard**, the way a Linux desktop does it.

| | |
|---|---|
| Drag | selects, and fills the primary buffer |
| Middle-click | pastes the primary buffer — in a terminal or any other text field in the game |
| Double-click | selects a word; drag continues by words |
| Triple-click | selects a line; drag continues by lines |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | the system clipboard, shared with the rest of your machine |

The two buffers are independent, so selecting text never overwrites what you copied.

**Readline editing** in the terminal — the bindings you already have muscle memory for.

| | |
|---|---|
| Move | `C-a` start, `C-e` end, `C-b`/`C-f` char, `M-b`/`M-f` word, `C-Left`/`C-Right` word |
| Kill | `C-k` to end, `C-u` to start, `C-w` word back (whitespace), `M-Backspace`/`C-Backspace` word back, `M-d` word forward, `C-d` delete char |
| Yank | `C-y` paste last kill, `M-y` cycle the kill ring |
| Edit | `C-t` swap chars, `M-t` swap words, `M-u`/`M-l`/`M-c` case, `C-z` undo, `C-l` clear |
| History | `C-p` previous, `C-n` next, `C-r`/`C-s` incremental search |
| Find | `C-f` search the scrollback |

Consecutive kills accumulate into one kill-ring entry, directionally, as readline does. The
ring is shared across terminal windows, so you can kill in one and yank in another.

**Persistent history** — one history shared by every terminal and kept across sessions, with
bash's `ignorespace` and `ignoredups` and a regex for commands you never want recorded.

**Tab completion** as a menu. Tab cycles the candidates, Shift+Tab steps back, Escape puts
back what you had typed. Command names are left out of completions for arguments.

**Configurable prompt** — a template with named colour palettes, separate local and remote
variants, and per-role colours. The `[Prompt]` section of the config documents itself.

**Any font on your machine.** Copy a `.ttf` or `.otf` into `BepInEx/fonts/` and name it in
`TerminalFont`. Glyphs it doesn't have fall back to the game's own font, and the cursor can
be a block, bar or underline.

**Window snapping** — drag a window to a screen edge for half, a corner for a quarter, the
top to fill. `Ctrl+Alt+Shift` with the arrows or `1`-`4` snaps from the keyboard. Hold Ctrl
to drag a window from anywhere, not just its titlebar.

**Mail headers** — a "headers" link on each message showing sender, recipient and direction,
with each message in a thread drawn as its own card.

**`ls` tidying** — bare `ls` output is reflowed into columns, and `ls -al` / `ls -a -l` are
rewritten to the `-la` the server accepts.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (the Unix build on
   Linux/macOS) into the Grey Hack folder.
2. Drop `UwUTerm.dll` into `BepInEx/plugins/`.
3. On Linux, launch through Steam with launch options:
   `"<game folder>/run_bepinex.sh" %command%`

Grey Hack calls `RestartAppIfNecessary` on startup, so launching the game outside Steam
makes it re-exec itself through the Steam client and drop the `LD_PRELOAD` that BepInEx
needs. Use the launch option rather than running `run_bepinex.sh` directly.

Settings land in `BepInEx/config/com.tekunogosu.uwuterm.cfg` after the first run, with
keybinds in `com.tekunogosu.uwuterm.hotkeys.cfg`. Both are watched, so edits apply without
restarting the game.

## Build

Requires the .NET SDK (6 or newer). The plugin targets `net472`;
`Microsoft.NETFramework.ReferenceAssemblies` supplies the reference assemblies, so Mono
is not needed.

```sh
./build.sh              # Debug, and copies the dll into BepInEx/plugins
./build.sh Release
```

The build needs to find your Grey Hack install. In order of precedence:

1. `dotnet build -p:GreyHackDir=...`
2. a `GREYHACK_DIR` environment variable
3. `Local.props` beside the csproj (gitignored):
   ```xml
   <Project><PropertyGroup>
     <GreyHackDir>/path/to/Grey Hack</GreyHackDir>
   </PropertyGroup></Project>
   ```
4. the default Steam location for your platform

`Assembly-CSharp` is referenced publicized via `BepInEx.AssemblyPublicizer.MSBuild`, so
the game's private members are reachable at compile time.

### Reference sources

`tools/decompile.sh` writes readable C# for the terminal and window classes into `refs/`
(installing `ilspycmd` if needed). That directory is gitignored on purpose — it holds the
game's own decompiled code, which is not ours to redistribute.

## How it hooks in

- **`Terminal.OnGUI`** is the game's keyboard handler, an `Event.current` switch. Plain
  `Ctrl+letter` and `Alt+letter` are unclaimed, so readline bindings are simply added.
- **`TerminalListAdapter`** holds the scrollback and the line being edited. UwUTerm draws
  from that model and leaves the game's own rows in place but hidden, since they are still
  what a submitted command is read out of.
- **The prompt is patched at `TerminalListAdapter.AddText`**, after the game has decided a
  line is an input line, so the caret and the text sent to the server are unaffected.
- **The shell is server-side.** Commands go out through `SendInputUserToServer`; output
  comes back through `AddTexto`. Those two choke points are what `ls` handling uses.
- **Windows are `uDialog` instances.** Moving runs through `uDialog.OnTitleDrag`, resizing
  through `uDialog_ResizeListener` — separate paths, which is what lets snapping trigger
  on move without firing on resize.

## Compatibility

Written against Grey Hack as of 2026-08. It patches by type and method name, so a game
update that renames or reshapes those will break specific features — the plugin logs a
warning for each hook it cannot find rather than failing to load.

## License

MIT. See [LICENSE](LICENSE).

The terminal screen model is derived from [XtermSharp](https://github.com/migueldeicaza/XtermSharp),
itself a port of [xterm.js](https://github.com/xtermjs/xterm.js), both MIT. Their copyright
notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), and every file carrying
ported code names its origin in a header comment.
