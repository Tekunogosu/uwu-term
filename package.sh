#!/usr/bin/env bash
# Build a release and lay it out the way a player unpacks it.
#
# A zip mirrors the game folder, so extracting it there puts the plugin where it belongs and
# leaves everything else alone. Only what the game loads goes in - the readme, the licence and
# the notices are the repository's, and unpacking them into a game folder litters it. Nothing
# in it is built for a platform: the dll is IL, and the same file runs under Unity's mono on
# Windows and Linux alike. What differs between them is which neovim you point it at, not what
# you install.
#
# Two archives, because the daemon scripts serve one case - flatpak Steam and the Steam Linux
# Runtime, where the editor has to attach to a neovim started outside the container. Anyone
# whose neovim the game can launch itself takes the plain one and downloads nothing extra.
set -euo pipefail
cd "$(dirname "$0")"

configuration=${1:-Release}
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' UwUTerm.csproj | head -1)
plugin_stage=dist/UwUTerm-$version
daemon_stage=dist/UwUTerm-$version-nvim-daemon

rm -rf "$plugin_stage" "$daemon_stage" "$plugin_stage.zip" "$daemon_stage.zip"
mkdir -p "$plugin_stage/BepInEx/plugins" "$daemon_stage"

# DeployToGame off: packaging should not need a game installed, and should not touch one
# that is. The compile still does, for the assemblies it references.
dotnet build -c "$configuration" -p:DeployToGame=false --nologo -v quiet

cp "bin/$configuration/net472/UwUTerm.dll" "$plugin_stage/BepInEx/plugins/"
cp -r "$plugin_stage/." "$daemon_stage/"
cp nvim-daemon.sh nvim-daemon.lua "$daemon_stage/"

# Zip a stage directory and list what went in. Modes are carried across: zipfile drops them
# otherwise, and nvim-daemon.sh is meant to be run rather than read.
pack() {
	python3 - "$1" <<-'PY'
	import os, sys, zipfile
	stage = sys.argv[1]
	with zipfile.ZipFile(stage + '.zip', 'w', zipfile.ZIP_DEFLATED) as zf:
	    for folder, _, files in os.walk(stage):
	        for name in sorted(files):
	            path = os.path.join(folder, name)
	            info = zipfile.ZipInfo.from_file(path, os.path.relpath(path, stage))
	            info.compress_type = zipfile.ZIP_DEFLATED
	            info.external_attr = (os.stat(path).st_mode & 0xFFFF) << 16
	            with open(path, 'rb') as handle:
	                zf.writestr(info, handle.read())
	PY

	printf '\n%s\n\n' "$1.zip"
	python3 - "$1.zip" <<-'PY'
	import sys, zipfile
	with zipfile.ZipFile(sys.argv[1]) as zf:
	    for info in zf.infolist():
	        print('  %7d  %s' % (info.file_size, info.filename))
	PY
}

pack "$plugin_stage"
pack "$daemon_stage"

printf '\nUnpack one of them into the Grey Hack folder: the plain plugin, or the plugin with\n'
printf 'the daemon scripts for attaching the editor to a neovim outside the game.\n'
