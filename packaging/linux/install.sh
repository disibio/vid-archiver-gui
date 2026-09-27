#!/usr/bin/env sh
# Installs Vid Archiver GUI for the current user (no root needed) and adds it to the application menu.
#   ./install.sh            install / update
#   ./install.sh --remove   uninstall (your settings in ~/.config/VidArchiverGui are kept)
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
TARGET="${XDG_DATA_HOME:-$HOME/.local/share}/vid-archiver-gui"
DESKTOP="${XDG_DATA_HOME:-$HOME/.local/share}/applications/vid-archiver-gui.desktop"

if [ "${1:-}" = "--remove" ]; then
  rm -rf "$TARGET" "$DESKTOP"
  echo "Vid Archiver GUI removed."
  exit 0
fi

mkdir -p "$TARGET" "$(dirname "$DESKTOP")"
cp -R "$HERE/." "$TARGET/"
chmod +x "$TARGET/VidArchiverGui"

cat > "$DESKTOP" <<EOF
[Desktop Entry]
Type=Application
Name=Vid Archiver GUI
Comment=Download with yt-dlp and file everything into the right folder
Exec="$TARGET/VidArchiverGui"
Icon=$TARGET/icon.png
Terminal=false
Categories=AudioVideo;Network;
EOF

echo "Installed to $TARGET"
echo "Start it from your application menu, or run: \"$TARGET/VidArchiverGui\""
