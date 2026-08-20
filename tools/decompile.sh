#!/usr/bin/env bash
# Dump readable C# for the terminal classes so the reflection in src/Patches can be
# replaced with real, compile-checked calls.
#
#   ./tools/decompile.sh                 # just the terminal types -> refs/*.cs
#   ./tools/decompile.sh --all           # whole assembly as a project -> refs/decompiled/
set -euo pipefail

GAME="${GREYHACK_DIR:-$HOME/.local/share/Steam/steamapps/common/Grey Hack}"
DLL="$GAME/Grey Hack_Data/Managed/Assembly-CSharp.dll"
OUT="$(cd "$(dirname "$0")/.." && pwd)/refs"

# Some distributions install the SDK outside the apphost's default search path
# (Gentoo puts it in /opt), and global dotnet tools then cannot find a runtime.
# Deriving the root from the dotnet on PATH works wherever it lives.
if [ -z "${DOTNET_ROOT:-}" ] && command -v dotnet >/dev/null 2>&1; then
    export DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
fi
export PATH="$PATH:$HOME/.dotnet/tools"

[ -f "$DLL" ] || { echo "Assembly-CSharp.dll not found at: $DLL" >&2; exit 1; }

if ! command -v ilspycmd >/dev/null 2>&1; then
    echo "Installing ilspycmd as a global dotnet tool..."
    dotnet tool install -g ilspycmd
    export PATH="$PATH:$HOME/.dotnet/tools"
fi

mkdir -p "$OUT"

if [ "${1:-}" = "--all" ]; then
    ilspycmd -p -o "$OUT/decompiled" "$DLL"
    echo "Wrote $OUT/decompiled"
    exit 0
fi

for type in \
    "TerminalPoolSystem.TerminalListAdapter" \
    "Terminal" \
    "ProgramVisual.TerminalWindow"
do
    echo "-> $type"
    ilspycmd -t "$type" "$DLL" > "$OUT/${type##*.}.cs"
done

echo "Wrote:"; ls -la "$OUT"/*.cs
