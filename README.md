# UwUTerm

The cutest terminal and window QoL to ever exist for [Grey Hack](https://store.steampowered.com/app/605230/Grey_Hack/).

*Insert catgirl trans meme here*

BepInEx 5 plugin, Unity 2022.3 Mono.

Everything here is client-side: input handling, rendering and window geometry. Nothing
changes what is sent to the server beyond normalising `ls` flags it would have rejected.

## Features

**Readline editing** in the terminal - the bindings you already have muscle memory for.

| | |
|---|---|
| Move | `C-a` start, `C-e` end, `C-b`/`C-f` char, `M-b`/`M-f` word, `C-Left`/`C-Right` word |
| Kill | `C-k` to end, `C-u` to start, `C-w` word back (whitespace), `M-Backspace`/`C-Backspace` word back, `M-d` word forward, `C-d` delete char |
| Yank | `C-y` paste last kill, `M-y` cycle the kill ring |
| Edit | `C-t` swap chars, `M-t` swap words, `M-u`/`M-l`/`M-c` case, `C-z` undo, `C-l` clear |
| History | `C-p` previous, `C-n` next |

Consecutive kills accumulate into one kill-ring entry, directionally, as readline does.
The ring is shared across terminal windows, so you can kill in one and yank in another.

**Configurable prompt** - a template with named colour palettes, separate local and
remote variants, and per-role colours. See the `[Prompt]` section of the config, which
documents itself.

**Window snapping** - drag a window to a screen edge for half, a corner for a quarter,
the top to fill. Hold Ctrl (configurable) to drag a window from anywhere, not just its
titlebar.

**`ls` tidying** - bare `ls` output is reflowed into columns, and `ls -al` / `ls -a -l`
are rewritten to the `-la` the server actually accepts.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (the Unix build on
   Linux/macOS) into the Grey Hack folder.
2. Drop `UwUTerm.dll` into `BepInEx/plugins/`.
3. On Linux, launch through Steam with launch options:
   `"<game folder>/run_bepinex.sh" %command%`

Grey Hack calls `RestartAppIfNecessary` on startup, so launching the game outside Steam
makes it re-exec itself through the Steam client and drop the `LD_PRELOAD` that BepInEx
needs. Use the launch option rather than running `run_bepinex.sh` directly.

Settings land in `BepInEx/config/com.tekunogosu.uwuterm.cfg` after the first run. The
plugin watches that file, so edits apply without restarting the game.

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
(installing `ilspycmd` if needed). That directory is gitignored on purpose - it holds the
game's own decompiled code, which is not ours to redistribute.

## How it hooks in

- **`Terminal.OnGUI`** is the game's keyboard handler, an `Event.current` switch. Plain
  `Ctrl+letter` and `Alt+letter` are unclaimed, and unhandled keys fall through to a
  character filter that rejects control characters - so readline bindings can simply be
  added.
- **`TerminalListAdapter`** is the line editor: an OSA recycling list whose last row is
  editable. Line text is `Data[Data.Count - 1].line`, and the caret sits *after*
  `charIndexInput`, with `minPosCursor` marking where the editable region starts.
- **The prompt is patched at `TerminalListAdapter.AddText`**, not `Terminal.AddTexto`.
  `AddTexto` decides whether a line is an input line via `texto.Equals(pwd)`; rewriting
  it there would fail that comparison, leave `minPosCursor` at 0, and let the caret walk
  into the prompt - or send the prompt to the server as part of the command.
- **The shell is server-side.** Commands go out through `SendInputUserToServer`; output
  comes back through `AddTexto`. Those two choke points are what `ls` handling uses.
- **Windows are `uDialog` instances.** Moving runs through `uDialog.OnTitleDrag`, resizing
  through `uDialog_ResizeListener` - separate paths, which is what lets snapping trigger
  on move without firing on resize.

## Compatibility

Written against Grey Hack as of 2026-08. It patches by type and method name, so a game
update that renames or reshapes those will break specific features - the plugin logs a
warning for each hook it cannot find rather than failing to load.

## License

MIT. See [LICENSE](LICENSE).
