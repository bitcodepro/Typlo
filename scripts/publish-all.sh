#!/bin/sh
# Builds self-contained single-file binaries for every supported OS into dist/.
# Works on Windows (Git Bash), macOS and Linux with the .NET 10 SDK.
set -e
cd "$(dirname "$0")/.."
for rid in win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64; do
  echo "== $rid"
  dotnet publish src/Typlo.App -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
    -o "dist/$rid"
done
sh scripts/package-macos.sh osx-arm64
sh scripts/package-macos.sh osx-x64
echo "Done: dist/"
