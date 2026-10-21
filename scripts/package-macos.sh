#!/bin/sh
# Wraps a published osx-* folder into Typlo.app (menu-bar app without a Dock icon).
# Unsigned: on first launch use right click -> Open, then allow Accessibility in System Settings.
set -e
cd "$(dirname "$0")/.."
rid="${1:-osx-arm64}"
src="dist/$rid"
app="dist/$rid/Typlo.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS"
find "$src" -maxdepth 1 -type f -exec cp {} "$app/Contents/MacOS/" \;
chmod +x "$app/Contents/MacOS/Typlo" 2>/dev/null || true
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Typlo</string>
  <key>CFBundleDisplayName</key><string>Typlo</string>
  <key>CFBundleIdentifier</key><string>pro.typlo.app</string>
  <key>CFBundleVersion</key><string>1.0</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleExecutable</key><string>Typlo</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
# Apple Silicon refuses to run unsigned arm64 code: ad-hoc sign when building on a Mac.
if command -v codesign >/dev/null 2>&1; then codesign --force --deep -s - "$app"; fi
echo "Created $app"
