#!/usr/bin/env bash
# Builds self-contained Linux packages (x64 and arm64) as .tar.gz, each with an install.sh.
#
# Runs on Linux, or in WSL on Windows (it then uses the Windows dotnet.exe if Linux dotnet isn't installed;
# the repository must be on a Windows drive, e.g. /mnt/c/..., in that case).
#
# Usage: packaging/package-linux.sh           -> publish/linux/Vid-Archiver-GUI-<version>-linux-{x64,arm64}.tar.gz
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET=$(command -v dotnet || command -v dotnet.exe || true)
[[ -n "$DOTNET" ]] || { echo "dotnet (or dotnet.exe under WSL) not found" >&2; exit 1; }

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
DEST=publish/linux
rm -rf "$DEST"
mkdir -p "$DEST"

for RID in linux-x64 linux-arm64; do
  NAME="Vid-Archiver-GUI-$VERSION-$RID"
  STAGE=$(mktemp -d)/vid-archiver-gui
  "$DOTNET" publish src/VidArchiverGui.App -c Release -r "$RID" --self-contained -p:PublishSingleFile=true -o "$DEST/$RID"

  mkdir -p "$STAGE"
  cp -R "$DEST/$RID/." "$STAGE/"
  chmod +x "$STAGE/VidArchiverGui"
  cp packaging/linux/install.sh "$STAGE/install.sh"
  cp src/VidArchiverGui.App/Assets/icon.png "$STAGE/icon.png"
  chmod +x "$STAGE/install.sh"

  tar -czf "$DEST/$NAME.tar.gz" -C "$(dirname "$STAGE")" vid-archiver-gui
  rm -rf "$(dirname "$STAGE")" "$DEST/$RID"
done

ls -1 "$DEST"/*.tar.gz
