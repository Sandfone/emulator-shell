#!/bin/zsh
# Builds the macOS installer: dist/EmulatorShell-<version>-<arch>.dmg. Opening it shows a window drawn as a
# landscape Android screen, the app to drag onto Applications, and 检查开发环境, a script that checks the
# Android SDK, Java, AVDs and connected phones. The disk image and its volume carry the app's icon.
# Finder lays out the window, so the first run asks to let Terminal (or whatever runs this) control Finder.
# Usage: scripts/make-dmg.sh
set -euo pipefail

root=${0:A:h:h}
cd "$root"

version="1.0.$(git rev-list --count HEAD)"
arch=$(uname -m)
name="Emulator Shell"
work="$root/.build/dmg"
stage="$work/stage"
art="$work/art"
dmg="$root/dist/EmulatorShell-$version-$arch.dmg"

rm -rf "$work"
mkdir -p "$stage/.background" "$art" "$root/dist"

# The app, built as scripts/make-app.sh builds it, straight into the staging folder.
EMULATOR_SHELL_NO_REGISTER=1 scripts/make-app.sh "$stage"
app="$stage/$name.app"

swift scripts/make-dmg-art.swift "$art" "$version"
# One TIFF with both resolutions, so the background is sharp on Retina displays.
tiffutil -cathidpicheck "$art/background.png" "$art/background@2x.png" -out "$stage/.background/background.tiff" 2>/dev/null
ln -s /Applications "$stage/Applications"
cp "scripts/dmg/检查开发环境.command" "$stage/"
chmod +x "$stage/检查开发环境.command"

# A writable image first, so Finder can save the window's layout into it.
rw="$work/rw.dmg"
hdiutil create -quiet -srcfolder "$stage" -volname "$name" -fs HFS+ -format UDRW -ov "$rw"
mount=$(hdiutil attach -readwrite -noverify -noautoopen "$rw" | sed -n 's|^/dev/[^[:space:]]*[[:space:]]*Apple_HFS[[:space:]]*||p')
trap 'hdiutil detach -quiet "$mount" 2>/dev/null || true' EXIT
volume=${mount:t}

# The check's custom icon; the volume's comes after Finder has saved the layout.
swift scripts/set-icon.swift "$art/check-icon.png" "$mount/检查开发环境.command"
SetFile -a E "$mount/检查开发环境.command"   # hide the .command extension

# Window: 660 × 500 points of content (make-dmg-art.swift draws for this size), no toolbar or status bar.
# Positions are icon centres, the same as in make-dmg-art.swift.
osascript <<EOF
tell application "Finder"
    tell disk "$volume"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set the bounds of container window to {200, 120, 860, 648}
        set options to the icon view options of container window
        set arrangement of options to not arranged
        set icon size of options to 96
        set text size of options to 13
        set label position of options to bottom
        set background picture of options to file ".background:background.tiff"
        set position of item "$name.app" of container window to {180, 222}
        set position of item "Applications" of container window to {480, 222}
        set position of item "检查开发环境.command" of container window to {120, 384}
        close
        open
        update without registering applications
        delay 2
        close
    end tell
end tell
EOF

# The volume's icon: .VolumeIcon.icns (hdiutil -srcfolder leaves dot files like it out) and the custom-icon flag.
cp "$app/Contents/Resources/AppIcon.icns" "$mount/.VolumeIcon.icns"
SetFile -a V "$mount/.VolumeIcon.icns"
SetFile -a C "$mount"

chmod -Rf go-w "$mount" 2>/dev/null || true
rm -rf "$mount/.fseventsd" "$mount/.Trashes"
sync
hdiutil detach -quiet "$mount"
trap - EXIT

rm -f "$dmg"
hdiutil convert -quiet "$rw" -format ULFO -o "$dmg"
# The .dmg file itself shows the app's icon in Finder and on the desktop.
swift scripts/set-icon.swift "$app/Contents/Resources/AppIcon.icns" "$dmg"
rm -rf "$work"
echo "Built $dmg ($(du -h "$dmg" | cut -f1))"
