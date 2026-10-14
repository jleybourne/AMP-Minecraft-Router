#!/usr/bin/env bash
# Pulls the two AMP assemblies the plugin compiles against (ModuleShared.dll and
# InstanceManagerPlugin.dll) out of the current AMP release into lib/.
# They're reference-only: AMP supplies them at runtime and they are never committed or shipped.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LIB="$ROOT/lib"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

URL="${AMP_DOWNLOAD_URL:-https://cubecoders.com/Downloads/AMP_Latest.zip}"
echo "Downloading $URL ..."
curl -fsSL "$URL" -o "$WORK/amp.zip"

unzip -q -o "$WORK/amp.zip" AMPVersion AMP_Linux_x86_64 \
  'Plugins/InstanceManagerPlugin/InstanceManagerPlugin.dll' -d "$WORK"
echo "AMP version: $(cat "$WORK/AMPVersion")"

# ModuleShared lives inside AMP's single-file bundle.
if ! command -v sfextract >/dev/null 2>&1; then
  dotnet tool install -g sfextract >/dev/null
  export PATH="$PATH:$HOME/.dotnet/tools"
fi
sfextract "$WORK/AMP_Linux_x86_64" -o "$WORK/bundle" >/dev/null

mkdir -p "$LIB"
cp "$WORK/bundle/ModuleShared.dll" "$LIB/"
cp "$WORK/Plugins/InstanceManagerPlugin/InstanceManagerPlugin.dll" "$LIB/"
cp "$WORK/AMPVersion" "$LIB/AMPVersion.txt"
echo "Reference assemblies written to $LIB"
