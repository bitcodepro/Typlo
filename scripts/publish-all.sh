#!/bin/sh
# Windows Native AOT needs a Windows host with the C++ build tools.
# Other hosts can still cross-publish the legacy Windows JIT build; CI produces Native AOT.
set -e
cd "$(dirname "$0")/.."
for rid in win-x64 linux-x64 linux-arm64 osx-arm64 osx-x64; do
  echo "== $rid"
  set -- -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
  if [ "$rid" = win-x64 ]; then
    case "$(uname -s)" in
      MINGW*|MSYS*|CYGWIN*) set -- -p:PublishAot=true ;;
      *) set -- -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ;;
    esac
  fi
  dotnet publish src/Typlo.App -c Release -r "$rid" --self-contained "$@" -p:DebugType=none \
    -o "dist/$rid"
done
sh scripts/package-macos.sh osx-arm64
sh scripts/package-macos.sh osx-x64
echo "Done: dist/"
