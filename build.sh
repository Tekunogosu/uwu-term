#!/usr/bin/env bash
# Build and drop the plugin into Grey Hack's BepInEx/plugins.
set -euo pipefail
cd "$(dirname "$0")"
dotnet build -c "${1:-Debug}" ${GREYHACK_DIR:+-p:GreyHackDir="$GREYHACK_DIR"}
