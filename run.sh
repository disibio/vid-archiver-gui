#!/usr/bin/env sh
# Builds and runs Vid Archiver GUI on macOS/Linux.
set -e
cd "$(dirname "$0")"
dotnet run --project src/VidArchiverGui.App -c Release
