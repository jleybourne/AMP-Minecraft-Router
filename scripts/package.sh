#!/usr/bin/env bash
# Builds the plugin and zips it as MinecraftRouter/{MinecraftRouter.dll,WebRoot/Plugin.js},
# ready to drop into the ADS instance's Plugins folder.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

dotnet build src/MinecraftRouter -c Release --nologo -v q
VERSION="$(grep -oP '(?<=<Version>)[^<]+' src/MinecraftRouter/MinecraftRouter.csproj)"
OUT="$ROOT/src/MinecraftRouter/bin/Release/net8.0"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/MinecraftRouter/WebRoot"
cp "$OUT/MinecraftRouter.dll" "$STAGE/MinecraftRouter/"
cp "$OUT/WebRoot/Plugin.js" "$STAGE/MinecraftRouter/WebRoot/"

mkdir -p "$ROOT/dist"
ZIP="$ROOT/dist/MinecraftRouter-$VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE" && zip -qr "$ZIP" MinecraftRouter)
echo "$ZIP"
