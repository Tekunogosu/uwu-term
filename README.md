# UwUTerm

The cutest terminal and window QoL to ever exist for [Grey Hack](https://store.steampowered.com/app/605230/Grey_Hack/).

*Insert catgirl trans meme here*


This project orignally started with *just* the terminal modifications and then turned into other QoL as I found new things I could change. All sections have their own feature flag that you can disable if desired. 


BepInEx 5 plugin

Everything here is client-side: input handling, rendering and window geometry. Nothing
changes what is sent to the server beyond normalising `ls` flags it would have rejected.

# Features
All sections are define by `Title (config feature flag)`. 
 
## A rebuilt terminal *(Terminal)*

The terminal has been completely re-written.. well mostly. The UI still looks the same but I've added all the things you would want from a modern terminal. The architecture was modeled after the XtermSharp C# project, specifically the packed cell, buffer line, scrollback and the reflow strategies. 


### Selection and clipboard *(Terminal)*
There is now two buffers that are independent of each other, just like a normal linux DE. 

| | |
|---|---|
| Drag | selects, and fills the primary buffer |
| Middle-click | pastes the primary buffer — in a terminal or any other text field in the game |
| Double-click | selects a word; drag continues by words |
| Triple-click | selects a line; drag continues by lines |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | copies to the system clipboard, shared with the rest of your machine |


## Readline editing *(Terminal)*
(Part of the Terminal feature)

| | |
|---|---|
| Move | `C-a` start, `C-e` end, `C-b` char, `M-b`/`M-f` word, `C-Left`/`C-Right` word |
| Kill | `C-k` to end, `C-u` to start, `C-w` word back (whitespace), `M-Backspace`/`C-Backspace` word back, `M-d` word forward, `C-d` delete char |
| Yank | `C-y` paste last kill, `M-y` cycle the kill ring |
| Edit | `C-t` swap chars, `M-t` swap words, `M-u`/`M-l`/`M-c` case, `C-z` undo, `C-l` clear |
| History | `C-p` previous, `C-n` next, `C-r`/`C-s` incremental search |
| Find | `C-f` search the scrollback — which is why `C-f` is not forward-char. `C-f`, `C-r` and `C-s` are the three rebindable keys, in the hotkeys file |

Consecutive kills accumulate into one kill-ring entry, directionally, as readline does. The
ring is shared across terminal windows, so you can kill in one and yank in another.

## Tab completion *(Terminal)*
Tab completion now shows a horizontal menu, similar to how zsh is configured. Tab cycles the candidates, Shift+Tab steps back, Escape puts back what you had typed. The default command names are left out of completions for arguments, you can modify this in the config file .

```ini
FilterCommandNames = true
KnownCommands = aircrack, aireplay, airmon, apt-get, build, cat, cd, chgrp, chmod, chown, clear, cp, decipher, echo, exit, ftp, groupadd, groupdel, groups, help, ifconfig, iwconfig, iwlist, kill, ls, mkdir, mv, nmap, nslookup, passwd, ping, ps, pwd, reboot, rm, scanlib, shutdown, smtp_user_list, ssh, sudo, touch, useradd, userdel, whoami, whois
```

## Configurable prompt *(Terminal)*
*(Note: All the configs hot reload, so you can make changes to the config while in-game and see the changes almost immediately)*

The prompt is completely customizable now. Make it look howevery you can thing. I chose not to implement the same coloring as a true terminal because honestly, its a pita to read and would have to be converted to something Unity could understand anway. 

This one is **off by default**, because the game already lets you change your prompt by editing bash itself through `CodeEditor.exe -code bash`, and a prompt you wrote there should not be overwritten by one you did not. Turn on `[Features] Prompt` to hand it to the `[Prompt]` section instead. Everything else about the terminal is unaffected either way.

You can change the cursor to be block, underline, or bar. see `CursorStyle`

See the `[Prompt]` section of the config for all config options, I'll touch on a few here.

### Palette *(Terminal)*

To save some space on prompt line, the `Palette` var lets you set specific colors for the keywords {name}{path} and such. When prompt is parsed, it looks up the palette, changes the color to what is specifies and then goes back to the previous global color. This also lets you define colors for local vs remote and different types of users (root, user, guest).


This is my prompt & palette:
```ini
Prompt = {#888bbb}┌─[{user}@{host}]-[{path}]-[{pid}]\n└─[{sym}]{sp}
Palette = user:#50fa7b, user.root:#ff5555, user.guest:#f8f8f2, host:#8be9fd, path:#bd93f9, sym:#bbeeff
```

### Font *(Terminal)*

Copy a `.ttf` or `.otf` into `BepInEx/UwUTerm/fonts/` and name it in `TerminalFont` config option. Glyphs it doesn't have fall back to the game's own font. If you inted to use the new nvim editor with a IDE plugin like AstroNvim, install a nerdfont version of your font and set it to that. The fonts set here affect the code editor as well, its the same rendering system.


## Persistent history *(PersistHistory)*
There is now a .bash_history-like file that holds your past commands, at `BepInEx/UwUTerm/workspace/.history`. Its not seperated by user, so everything is saved - I might change it to be user@host based if its wanted/requested.



## The top bar *(Desktop)*

The game gives you two bars — a thin one along the top with the menu button, clock and notification icons, and a taskbar along the bottom holding your windows. This rolls both into one bar at the top and hands the space the bottom one was using back to the desktop, so the icon grid gets the whole screen.

The window buttons are drawn by the mod instead of the game. The game rebuilt every button from scratch whenever any window was focused — a GameObject per window, per click — where these are built once when a window opens and told what changed after that. They also fit: however many windows you have open, the row stops where the widgets start instead of running underneath them.

Everything else in the bar is still the game's own widget, just moved. The clock, the notification icons and the user name are the same objects, so they behave the same and follow whatever theme you pick in the appearance menu. Alt-tab still works too — the buttons carry the game's own task component rather than a copy of it.

Maximising and snapping know the bar is there, so a maximised window fills the desktop underneath it instead of tucking under it.

The `[Desktop]` section sizes everything in pixels: `BarHeight`, `TaskWidth`, `MinTaskWidth` (how narrow a button may get before the row is allowed to run long), `BarFontSize`, `BarGap` between the groups, `MenuGap` between the start button and your name, and `WidgetPadding` around each widget. `BarScale` multiplies all of them. Worth knowing that the game's own UI-size setting is a reference resolution rather than a scale — its "100%" is 1920x1080 stretched to your display, so it isn't 1:1 on anything wider — while these numbers are actual pixels.

`MenuIcon` puts the mod's own mark on the start menu button in place of the game's. Off by default for now.

This one takes effect on restart, since the bar takes the game's widgets over as it starts.

##  Window snapping *(WindowSnapping)*
Drag a window to a screen edge for half, a corner for a quarter, the top to fill. There is also a snapping ghost to show you where the winow will expand.

`Ctrl+Alt+Shift` with the arrows or `1`-`4` snaps from the keyboard. 

Hold `Ctrl` to drag a window from anywhere, not just its titlebar.


## Remembered window positions *(PersistWindows)*

Windows open where you last had them, size as well as place. It remembers per kind of window, and it keeps a list rather than a single spot, so if you left three terminals open they come back as three terminals in three places instead of three stacked on one.

Nothing needs saving by hand — it writes to `BepInEx/config/com.tekunogosu.uwuterm.windows` on its own. If you change resolution, or make the bar taller, anything that would come back off the edge of the screen is pulled onto it.

## Browser tabs *(BrowserTabs)*

A row of tabs across the top of the browser, one page each. `+` or `Ctrl+T` opens one; the `×` on a tab, a middle click anywhere on it, or `Ctrl+W` closes one; `Ctrl+1`..`Ctrl+9` pick by position — `Ctrl+9` being the last one however many there are. The keys are ignored while you are typing in a text field, so they never end up in the address bar.

A tab holds everything the page had, because a tab *is* a browser: its own history, its own bank session, its own half-finished download. Switching away from one costs it nothing and a page still loading in a background tab carries on loading.

That does mean a tab is a real `Browser.exe` — the server will not answer a browser with no process behind it, so every tab uses RAM and shows up in `ps`, exactly as a second browser window does today. It is launched as the browser it was opened from: open the browser as root and its tabs are root too, open it over an ssh connection and its tabs open on that machine.

Launching `Browser.exe` still opens a window of its own, whether from a terminal, a script or the desktop icon. Tabs only ever come from the `+` button and `Ctrl+T`.

The row sits in the bar the game already had. Grey Hack's browser ships with a single tab reading "Main Page" and a `+` button beside it that was left switched off — an unfinished tab bar. The tabs replace those two and go in their place, so the page is exactly as tall as it was before.

Closing a window that is carrying more than one tab asks first. `[Browser] ConfirmClose = false` closes them all without a question, and the `[Browser]` section also sets the width of a tab and its font size.

## Mail headers *(Mail)*
I added a small "headers" link to the upper right coner of each message so you can see everything about your email, include who you are talking to.. I forget sometimes so thats why this is here. 

I also cleaned up the mail client slightly by putting the messages in cards to make it look a bit more organized. I might do more at some point, this was just sort of tossed in.

## `ls` tidying *(CommandTidy)* - *(ls)*

Bare `ls` output is reflowed into columns, and `ls -al` / `ls -a -l` are rewritten to the `-la` the server accepts. 
If you have remade `ls`, turn this off so it doesnt affect your custom output. The CommandTidy stuff was created with the new terminal in mind, it technically works without it, but it may not behave as well without it.


# Neovim *is* the CodeEditor *(CodeEditor)*
Added a full featured embedded nvim client to replace the default CodeEditor.


## The editor

Set `CodeEditor = true` and neovim replaces the game's code editor. `nvim --embed` speaks
msgpack-rpc and describes its screen as a grid of cells and attributes, so
the window renders the editor's own screen rather than imitating one. Keys and mouse both
reach it: click, drag, wheel, double-click and the modifiers, on the same terms as any
neovim. This IS nvim, so your nvim config controls how it behaves.

Because this is a process on your machine, you can easily save/load scripts from your desktop to/from the game. Saving with nvim `:w` will write to the `BepInEx/UwUTerm/workspace` folder by default which you can adjust in the config. The save button and compile button in the default UI is still there and is how you save a script to the in-game filesystem or compile. No more copy + pasting to/from the game or having to use Greybels functions to send it to the game if you dont want.  

The buffer's filetype is set to `greyscript`, so syntax highlighting and language support
are whatever you install. The
[greybel language server](https://github.com/ayecue/greybel-languageserver) documents nvim
setup. 

Config `NvimFiletype` changes the name, or blank leaves neovim to infer it.

### Where things go

| | |
|---|---|
| `BepInEx/UwUTerm/` | Everything the mod owns. Made on first run; folders from an older install are moved in automatically. |
| `BepInEx/UwUTerm/nvim/` | The neovim install. Replace the whole directory to upgrade. |
| `BepInEx/UwUTerm/nvim-config/config/nvim/` | `init.lua` and everything else neovim reads as config. |
| `BepInEx/UwUTerm/nvim-config/data/nvim/site/pack/*/start/` | Plugins, when the game starts the editor. |
| `BepInEx/UwUTerm/workspace/` | Where the editor starts and `:w` writes. Command history is the `.history` file in here. |

The `nvim-config` setup is primarily for those having to play the game using steam through flatpak. Flatpak sandoxes steam and thus the games "home" files are not the same as your actual desktop.  

Set `NvimConfig = system` to use your systems nvim - which will only work **IF** you are on a native install of steam, **NOT** flatpak.

The editor is started with `XDG_CONFIG_HOME` and the other XDG variables pointed at
`nvim-config`. Neovim reads `$XDG_CONFIG_HOME/nvim` on every platform — `~/AppData/Local`
is what Windows defaults that variable to — so the layout is the same everywhere and does
not depend on what the launcher sets `HOME` to. Under flatpak Steam it would otherwise be `~/.var/app/com.valvesoftware.Steam/config` so we force it to be our chosen directory.


### Nvim Sessions 

By default the game starts one neovim per editor window. What that editor can do depends on
where the game runs:

| | |
|---|---|
| **Native Windows or Linux Steam** | The editor is a process on your machine, with your `PATH`, `git` and compilers. `NvimConfig = system` points it at your existing config. |
| **Flatpak Steam, Steam Linux Runtime** | The container lacks pretty much any tools which limits a lot of plugins BUT (see below) |

`NvimAddress` covers the second case. We can tell nvim to connect to a socket that we create outside the game so its running YOUR nvim not the one that would be limited to the flatpak sandbox. 

```sh
./nvim-daemon.sh          # prints the address and the config line to paste
```

```ini
[Editor]
NvimAddress = /path/to/Grey Hack/BepInEx/UwUTerm/nvim-config/nvim.sock
```

The address is a path — a unix socket, or a named pipe on Windows — or `host:port` for tcp.
Neovim's rpc executes arbitrary lua. A loopback port is reachable by every process on the
machine, whereas a socket or pipe is reachable by whoever can open it. If you are using flatpak, make sure flatpak has access to that folder or it wont work.

`nvim-daemon.sh` is a bash script, and there is no Windows equivalent because Windows does
not have the container problem. To run a session there anyway, start one and point
`NvimAddress` at it:

```
nvim --headless --listen \\.\pipe\uwuterm
```

### One session per window

`NvimSessionPerWindow` is on by default. Rather than every window attaching to the one session
on `NvimAddress`, each window asks that session to start a neovim of its own beside it — on your
machine, with your config, your plugins and your language servers. So the second and third
windows are as capable as the first. One is kept warming ahead of time, because a window opens
faster than a neovim starts.

Turn it off and every window attaches to the daemon's session directly. One neovim has one
screen, so they all show the same buffer at the size of the smallest window — which is the
limitation this setting exists to remove.

Behaviour with a session:

- A window's session is its own. Scripts opened in that window are buffers in it, so `:ls`, `:b`
  and plugins see them there — not in the session you have open in a terminal, which is the one
  handing the sessions out rather than one being drawn.
- Closing a game window ends the session that window was given. With `NvimSessionPerWindow = false`
  it only detaches, and the daemon's session and its buffers stay.
- If the session ends, the daemon starts another on the same address and the game
  reconnects. `Ctrl-C` stops the daemon; `--once` disables the restarting.
- If nothing is listening, the game starts an editor of its own. The log records which
  happened.

Leaving `NvimAddress` blank keeps the default: one editor per window, no daemon.

### Buffers and saving

A window opens on an empty buffer, and opening a script takes that buffer over rather than
leaving an unnamed one behind - the same thing `:e` does. Reopening a script returns to the
buffer it already has. Nothing the game loads is marked modified; it is the file as the
server has it until you change it.


### Plugins

A directory under `pack/<any name>/start/` is loaded at startup — neovim's own package
mechanism, needing nothing else:

```sh
cd "<game>/BepInEx/UwUTerm/nvim-config/data/nvim/site/pack/greyhack/start"
git clone --depth 1 https://github.com/nvim-lualine/lualine.nvim
```

Clone from your machine rather than from a shell inside the game. Under flatpak Steam or the
Steam Linux Runtime there is no `git`, compiler or `node` in the container, so anything
needing a build has to be built outside and copied in. A treesitter parser is a `.so` in
`parser/` beside `init.lua` with its queries in `queries/<language>/`; build it against the
container's libc, which is usually older than a rolling distribution's.

Neither restriction applies to a session reached through `NvimAddress`, or to a native
install with `NvimConfig = system`. Both run outside the container with your own tools, and
a plugin manager works normally.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) into the Grey Hack
   folder — the Unix build on Linux and macOS, the Windows build on Windows.
2. Unpack a release zip into the same folder. It mirrors the game's layout, so
   `UwUTerm.dll` lands in `BepInEx/plugins/`. Take `UwUTerm-<version>.zip` unless you
   need the daemon scripts; `UwUTerm-<version>-nvim-daemon.zip` adds those.
3. On Linux, launch through Steam with launch options:
   `"<game folder>/run_bepinex.sh" %command%`. On Windows BepInEx's own installer handles
   the launch and no launch option is needed.
4. For the editor, put a neovim at `BepInEx/UwUTerm/nvim/bin/nvim` (`nvim/bin/nvim.exe` on Windows)
   or set `NvimDownload = true` to have one fetched. `NvimPath` names one somewhere else.


Settings land in `BepInEx/config/com.tekunogosu.uwuterm.cfg` after the first run, with
keybinds in `com.tekunogosu.uwuterm.hotkeys.cfg`. Both are hot reloaded, so edits apply without
restarting the game.

## Build

Requires the .NET SDK (6 or newer). The plugin targets `net472`;
`Microsoft.NETFramework.ReferenceAssemblies` supplies the reference assemblies, so Mono
is not needed.

```sh
./build.sh              # Debug, and copies the dll into BepInEx/plugins
./build.sh Release
./package.sh            # Release, laid out as players unpack it, into dist/
```

`package.sh` builds with the game deploy switched off and writes two zips into `dist/`, each
holding what it ships under the paths it occupies in the game folder. `UwUTerm-<version>.zip`
is the plugin alone; `UwUTerm-<version>-nvim-daemon.zip` adds `nvim-daemon.sh` and
`nvim-daemon.lua`, which only the container case needs. They are the release artifacts;
building them needs a game install, unpacking one does not.

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
