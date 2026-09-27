#!/usr/bin/env bash
# Regenerates nuget-sources.json: every NuGet package the app needs, so Flathub can build it without network access.
# Run it (on Linux or in WSL) whenever package references or the .NET version change.
#
# Needs flatpak with org.freedesktop.Sdk//26.08 and org.freedesktop.Sdk.Extension.dotnet10//26.08 installed.
set -euo pipefail
cd "$(dirname "$0")"

GENERATOR=https://raw.githubusercontent.com/flatpak/flatpak-builder-tools/master/dotnet/flatpak-dotnet-generator.py
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
curl -fsSL "$GENERATOR" -o "$TMP/flatpak-dotnet-generator.py"

# --runtime last: it takes every argument after it.
python3 "$TMP/flatpak-dotnet-generator.py" --dotnet 10 --freedesktop 26.08 \
  nuget-sources.json ../../src/VidArchiverGui.App/VidArchiverGui.App.csproj \
  --runtime linux-x64 linux-arm64

echo "Updated $(pwd)/nuget-sources.json"
