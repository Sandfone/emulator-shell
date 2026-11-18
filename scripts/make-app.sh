#!/bin/zsh
# Builds a release EmulatorShell and wraps it in "Emulator Shell.app", installed in ~/Applications so it
# can be started from Launchpad, Spotlight or the Dock. Run it again after changing the code.
# Usage: scripts/make-app.sh [install folder, default ~/Applications]
set -euo pipefail

root=${0:A:h:h}
dest=${1:-$HOME/Applications}
app="$dest/Emulator Shell.app"
work="$root/.build/app"

cd "$root"
swift build -c release
bin="$(swift build -c release --show-bin-path)"
binary="$bin/EmulatorShell"
# Symbols that would make the app abort at run time (see the script) stop the build here.
scripts/check-async-symbols.py "$bin"

rm -rf "$work"
mkdir -p "$work/Emulator Shell.app/Contents/MacOS" "$work/Emulator Shell.app/Contents/Resources" "$work/AppIcon.iconset"
bundle="$work/Emulator Shell.app"
cp "$binary" "$bundle/Contents/MacOS/EmulatorShell"
# The device side of mirroring physical devices (scrcpy-server, Apache 2.0), with its license.
cp "$root/vendor/scrcpy/scrcpy-server" "$bundle/Contents/Resources/scrcpy-server"
cp "$root/vendor/scrcpy/LICENSE" "$bundle/Contents/Resources/scrcpy-LICENSE"

swift scripts/make-icon.swift "$work/icon.png"
for px in 16 32 128 256 512; do
    sips -z $px $px "$work/icon.png" --out "$work/AppIcon.iconset/icon_${px}x${px}.png" >/dev/null
    sips -z $((px * 2)) $((px * 2)) "$work/icon.png" --out "$work/AppIcon.iconset/icon_${px}x${px}@2x.png" >/dev/null
done
iconutil -c icns "$work/AppIcon.iconset" -o "$bundle/Contents/Resources/AppIcon.icns"

# The identifier stays "EmulatorShell", the defaults domain the bare executable uses, so window sizes and
# settings are shared with `emulator-shell` and the SDK forwarder.
version=$(git describe --always --dirty 2>/dev/null || echo 0)
cat > "$bundle/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Emulator Shell</string>
    <key>CFBundleDisplayName</key><string>Emulator Shell</string>
    <key>CFBundleIdentifier</key><string>EmulatorShell</string>
    <key>CFBundleExecutable</key><string>EmulatorShell</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>15.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSMicrophoneUsageDescription</key><string>把 Mac 的麦克风接到 Android 模拟器。</string>
</dict>
</plist>
EOF

codesign --force --sign - "$bundle"

mkdir -p "$dest"
rm -rf "$app"
mv "$bundle" "$app"
# Let Launch Services pick up the new icon and Info.plist.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$app"
echo "Installed $app"
