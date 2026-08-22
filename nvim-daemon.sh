#!/usr/bin/env bash
# Run the neovim the game's editor connects to.
#
# The game runs in a container - flatpak, or Steam's own runtime - and that container has no
# git, no compiler and no language servers, so an editor started inside it can load plugins
# but never install or build any. This one runs out here instead, on your machine, with your
# config and your tools, and the game attaches to it as a UI. Scripts you open in the game
# become buffers in this session alongside whatever else you have loaded.
#
# Put the address it prints into BepInEx/config/com.tekunogosu.uwuterm.cfg:
#
#     [Editor]
#     NvimAddress = <address>
#
# Stop it with Ctrl-C.
set -euo pipefail

program=${0##*/}
here=$(cd "$(dirname "$0")" 2>/dev/null && pwd)

usage() {
    cat <<USAGE
usage: $program [options]

  -a, --address ADDR   unix socket path, or host:port for tcp
                       (default: GAME/BepInEx/nvim-config/nvim.sock)
  -g, --game-dir DIR   the Grey Hack install (default: \$GREYHACK_DIR, then Local.props,
                       then the usual Steam locations)
  -w, --workspace DIR  where the session starts, so :w and :e land somewhere sensible
                       (default: GAME/BepInEx/workspace)
  -n, --nvim PATH      which neovim to run (default: nvim from PATH)
  -f, --force          replace a socket left behind by a session that died
  -1, --once           stop when the session exits, rather than starting another
      --allow-quit     let :q end the session, the way it would in an editor you started
                       to look at one thing. Without it, :q closes the file and :qa ends
                       the session.
      --print-address  print the resolved address and exit
  -h, --help           this

A tcp address is reachable by every process on the machine, and neovim's rpc will run any
lua it is given. Prefer the socket unless something is stopping you.
USAGE
}

die() { printf '%s: %s\n' "$program" "$1" >&2; exit 1; }

address=
game=
workspace=
nvim_bin=nvim
force=0
once=0
keepalive=1
print_only=0

while [ $# -gt 0 ]; do
    case $1 in
        -a|--address)   address=${2:?--address needs a value}; shift 2 ;;
        -g|--game-dir)  game=${2:?--game-dir needs a value}; shift 2 ;;
        -w|--workspace) workspace=${2:?--workspace needs a value}; shift 2 ;;
        -n|--nvim)      nvim_bin=${2:?--nvim needs a value}; shift 2 ;;
        -f|--force)     force=1; shift ;;
        -1|--once)      once=1; shift ;;
        --allow-quit)   keepalive=0; shift ;;
        --print-address) print_only=1; shift ;;
        -h|--help)      usage; exit 0 ;;
        *)              usage >&2; die "unknown option: $1" ;;
    esac
done

# ---- where the game is --------------------------------------------------------------
# Same order the build uses, so a Local.props that already names it is not repeated here.
# Unpacked from the release, this script sits in the game folder, so its own directory is
# the first place worth looking.
if [ -z "$game" ] && [ -n "$here" ] && [ -d "$here/BepInEx" ]; then
    game=$here
fi

if [ -z "$game" ]; then
    game=${GREYHACK_DIR:-$game}
fi

if [ -z "$game" ] && [ -n "$here" ] && [ -f "$here/Local.props" ]; then
    game=$(sed -n 's:.*<GreyHackDir>\(.*\)</GreyHackDir>.*:\1:p' "$here/Local.props" | head -1)
fi

if [ -z "$game" ]; then
    for candidate in \
        "$HOME/.local/share/Steam/steamapps/common/Grey Hack" \
        "$HOME/.steam/steam/steamapps/common/Grey Hack" \
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Grey Hack"
    do
        [ -d "$candidate" ] && { game=$candidate; break; }
    done
fi

[ -n "$game" ] || die "no Grey Hack install found - pass --game-dir, or set GREYHACK_DIR"
[ -d "$game" ] || die "not a directory: $game"

: "${address:=$game/BepInEx/nvim-config/nvim.sock}"
: "${workspace:=$game/BepInEx/workspace}"

if [ "$print_only" = 1 ]; then printf '%s\n' "$address"; exit 0; fi

# ---- checks -------------------------------------------------------------------------
command -v "$nvim_bin" >/dev/null 2>&1 || die "no neovim at '$nvim_bin' - pass --nvim"

is_tcp=0
case $address in
    */*) ;;
    *:[0-9]*) is_tcp=1 ;;
esac

if [ "$is_tcp" = 0 ]; then
    # sun_path is 108 bytes and the kernel will not stretch for anyone.
    if [ ${#address} -gt 107 ]; then
        die "socket path is ${#address} characters, and the limit is 107: $address"
    fi

    mkdir -p "$(dirname "$address")"

    if [ -e "$address" ]; then
        if "$nvim_bin" --server "$address" --remote-expr 1 >/dev/null 2>&1; then
            die "a session is already listening on $address"
        fi

        [ "$force" = 1 ] || die "$address is left over from a session that died - rerun with --force"
        rm -f "$address"
    fi

    # Neovim unlinks the socket itself on the way out - verified for TERM and HUP - so the
    # trap below is only for the paths that never reach it, and for a SIGKILL, which is what
    # --force above is for.
else
    printf '%s: warning: %s is a tcp port - anything on this machine can reach it, and\n' "$program" "$address" >&2
    printf '%s:          neovim rpc runs arbitrary lua. A unix socket does not have that reach.\n' "$program" >&2
fi

mkdir -p "$workspace"

# :q closes a window, and a session has one, so quitting a file quit the whole editor and
# took the other buffers with it. nvim-daemon.lua leaves a window behind for the quit to
# spare; :qa still ends the session, because it quits every window however many there are.
guard=()
if [ "$keepalive" = 1 ]; then
    if [ -n "$here" ] && [ -f "$here/nvim-daemon.lua" ]; then
        guard=(-c "luafile $here/nvim-daemon.lua")
    else
        printf '%s: warning: nvim-daemon.lua is not beside this script, so :q will end the session\n' \
            "$program" >&2
    fi
fi

# ---- go -----------------------------------------------------------------------------
printf '%s\n' "$("$nvim_bin" --version | head -1) at $nvim_bin"
printf 'listening on  %s\n' "$address"
printf 'workspace     %s\n' "$workspace"
printf '\nput this in %s/BepInEx/config/com.tekunogosu.uwuterm.cfg:\n\n' "$game"
printf '    [Editor]\n    NvimAddress = %s\n\n' "$address"
if [ "$keepalive" = 1 ]; then
    printf ':q closes the file, :qa ends the session. Ctrl-C to stop the daemon.\n'
else
    printf 'Ctrl-C to stop.\n'
fi

printf 'It stops when this terminal does. To outlive it:\n\n'
printf '    setsid -f %s\n\n' "$0"

cd "$workspace"

# :q on the session's last window quits neovim, and quitting an editor should not take the
# daemon with it - so another is started on the same address and the game reconnects. Ctrl-C
# is how you mean it, and stops for good.
# The session runs in the background and is waited on, rather than in the foreground: a
# trap does not run until the foreground command returns, so signalling this script alone -
# a service manager stopping it, rather than a Ctrl-C that reaches the whole group - would
# otherwise be noticed only once neovim had exited of its own accord.
stop=0
session=

trap 'stop=1; [ -n "$session" ] && kill -TERM "$session" 2>/dev/null' INT TERM HUP

immediate=0

while :; do
    began=$SECONDS
    "$nvim_bin" --headless "${guard[@]}" --listen "$address" &
    session=$!
    wait "$session" || true
    session=

    [ "$stop" -eq 0 ] || break
    [ "$once" -eq 0 ] || break

    if [ $((SECONDS - began)) -lt 2 ]; then
        immediate=$((immediate + 1))
    else
        immediate=0
    fi

    # Something is wrong with the config rather than with the session, and restarting into
    # the same failure forever helps nobody.
    if [ "$immediate" -ge 3 ]; then
        rm -f "$address"
        die "the session exited immediately three times - run '$nvim_bin' by hand to see why"
    fi

    printf '%s: session ended, starting another on %s\n' "$program" "$address"
done

rm -f "$address"
