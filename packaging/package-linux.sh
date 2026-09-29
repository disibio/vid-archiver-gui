#!/usr/bin/env bash
# Builds self-contained Linux packages (x64 and arm64): a .tar.gz with an install.sh, and an AppImage.
#
# Runs on Linux, or in WSL on Windows (it then uses the Windows dotnet.exe if Linux dotnet isn't installed;
# the repository must be on a Windows drive, e.g. /mnt/c/..., in that case).
#
# Usage: packaging/package-linux.sh           -> publish/linux/Vid-Archiver-GUI-<version>-linux-{x64,arm64}.tar.gz
#                                                publish/linux/Vid-Archiver-GUI-<version>-linux-{x64,arm64}.AppImage
# appimagetool is downloaded on first use (set APPIMAGETOOL to use your own copy).
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET=$(command -v dotnet || command -v dotnet.exe || true)
[[ -n "$DOTNET" ]] || { echo "dotnet (or dotnet.exe under WSL) not found" >&2; exit 1; }

# appimagetool is itself an AppImage; extracting instead of mounting means it works without FUSE (CI, WSL, containers).
export APPIMAGE_EXTRACT_AND_RUN=1
if [[ -z "${APPIMAGETOOL:-}" ]]; then
  APPIMAGETOOL="${XDG_CACHE_HOME:-$HOME/.cache}/vid-archiver-gui/appimagetool-$(uname -m).AppImage"
  if [[ ! -x "$APPIMAGETOOL" ]]; then
    mkdir -p "$(dirname "$APPIMAGETOOL")"
    curl -fsSL -o "$APPIMAGETOOL" \
      "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$(uname -m).AppImage"
    chmod +x "$APPIMAGETOOL"
  fi
fi

FLATPAK_ID=io.github.disibio.vid-archiver-gui

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
  rm -rf "$(dirname "$STAGE")"

  # AppImage: one file that runs on most distros. The .desktop, metainfo and icon are shared with the Flatpak.
  APPDIR=$(mktemp -d)/AppDir
  mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/metainfo"
  cp -R "$DEST/$RID/." "$APPDIR/usr/bin/"
  chmod +x "$APPDIR/usr/bin/VidArchiverGui"
  ln -s usr/bin/VidArchiverGui "$APPDIR/AppRun"
  sed 's/^Exec=.*/Exec=VidArchiverGui/' "packaging/flatpak/$FLATPAK_ID.desktop" > "$APPDIR/usr/share/applications/$FLATPAK_ID.desktop"
  ln -s "usr/share/applications/$FLATPAK_ID.desktop" "$APPDIR/$FLATPAK_ID.desktop"
  cp "packaging/flatpak/$FLATPAK_ID.metainfo.xml" "$APPDIR/usr/share/metainfo/"
  cp src/VidArchiverGui.App/Assets/icon.png "$APPDIR/$FLATPAK_ID.png"
  ln -s "$FLATPAK_ID.png" "$APPDIR/.DirIcon"

  case "$RID" in
    linux-x64) ARCH=x86_64 ;;
    linux-arm64) ARCH=aarch64 ;;
  esac
  ARCH=$ARCH "$APPIMAGETOOL" --no-appstream "$APPDIR" "$DEST/$NAME.AppImage"
  rm -rf "$(dirname "$APPDIR")" "$DEST/$RID"
done

ls -1 "$DEST"/*.tar.gz "$DEST"/*.AppImage
