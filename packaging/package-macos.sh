#!/usr/bin/env bash
# Builds "Vid Archiver GUI.app" for macOS (Apple Silicon and Intel) and zips each one.
#
# Run this ON A MAC: Apple Silicon refuses to launch unsigned binaries, and the ad-hoc signature below
# (codesign -s -) can only be applied on macOS. The result is not notarized, so on first launch users
# right-click the app and choose Open (or allow it under System Settings > Privacy & Security).
#
# Usage: packaging/package-macos.sh            -> publish/macos/Vid-Archiver-GUI-<version>-osx-{arm64,x64}.zip
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
DEST=publish/macos
rm -rf "$DEST"
mkdir -p "$DEST"

for RID in osx-arm64 osx-x64; do
  BUILD="$DEST/$RID"
  APP="$BUILD/Vid Archiver GUI.app"
  dotnet publish src/VidArchiverGui.App -c Release -r "$RID" --self-contained -o "$BUILD/files"

  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$BUILD/files/." "$APP/Contents/MacOS/"
  chmod +x "$APP/Contents/MacOS/VidArchiverGui"
  cp packaging/macos/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

  cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Vid Archiver GUI</string>
  <key>CFBundleDisplayName</key><string>Vid Archiver GUI</string>
  <key>CFBundleIdentifier</key><string>io.github.vidarchivergui</string>
  <key>CFBundleExecutable</key><string>VidArchiverGui</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>${VERSION%%-*}</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
EOF

  if [[ "$(uname)" == "Darwin" ]]; then
    codesign --force --deep --sign - "$APP"
    ditto -c -k --keepParent "$APP" "$DEST/Vid-Archiver-GUI-$VERSION-$RID.zip"
  else
    echo "warning: not on macOS; the app is unsigned and won't launch on Apple Silicon until signed on a Mac" >&2
    (cd "$BUILD" && zip -qry "../Vid-Archiver-GUI-$VERSION-$RID.zip" "Vid Archiver GUI.app")
  fi
  rm -rf "$BUILD/files"
done

ls -1 "$DEST"/*.zip
