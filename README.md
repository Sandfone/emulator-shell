# Emulator Shell

A macOS prototype that renders an Android emulator's device frame itself instead of using the
emulator's own window. The emulator runs headless (`-no-window -gpu host -grpc <port>`); this app streams
the display over the emulator's gRPC API and composites it under the skin artwork with
Core Animation, so the frame edge and screen corners are antialiased against the desktop.

## Run

```bash
swift build
.build/debug/EmulatorShell
```

Without arguments the device manager opens: AVDs grouped by category on the left, the selected
one's details on the right. From there you start, stop, create, edit, duplicate, wipe and delete
AVDs and download or remove system images. `EmulatorShell <avd> [emulator arguments]` opens that device
directly, without the manager, and quits when its window closes.

`scripts/make-app.sh` builds a release binary and installs it as `~/Applications/Emulator Shell.app`
(another folder as its argument), so the manager can be started from Launchpad, Spotlight or the Dock.
Run it again after changing the code. The bundle identifier is `EmulatorShell`, the defaults domain of
the bare executable, so window sizes and settings are the same whichever way the app is started.

Quitting (⌘Q) or closing a device window (⌘W) shuts its emulator down the same way as closing the
emulator's own window, quick-boot snapshot included. The windows go at once; the app finishes
quitting in the background once every emulator has saved its snapshot (about 10 s for a phone). Several devices can run at once; each gets
the next free gRPC port from 8554. Emulator output goes to `$TMPDIR/emulator-shell-<avd>.log`.
Set `EMULATOR_SHELL_DEBUG=1` to log touches, keys, foldable postures, changes of the frames' size or
reported rotation and, every 5 seconds, the frame rate with
frames the emulator skipped (gaps in `seq`), frames dropped because every display surface was
still on screen, and the latency from the emulator's frame timestamp. Set
`EMULATOR_SHELL_GRPC_FRAMES=1` to send pixels inside the gRPC messages instead of shared memory,
for comparison.

SwiftPM links through clang with `--sysroot`, from which clang does not read the SDK's version, so the
binary would record the deployment target (15.0) as its SDK and AppKit would draw the app as on macOS 15
(old window buttons, controls and toolbar). `Package.swift` therefore also passes the selected Xcode's
SDK as `-isysroot` to the link; `otool -l .build/debug/EmulatorShell | grep -A4 LC_BUILD_VERSION`
shows the SDK the binary records.

Requires the Android SDK at `$ANDROID_HOME` (or `~/Library/Android/sdk`) with the command-line
tools, Java for `avdmanager` and `sdkmanager` (`$JAVA_HOME`, Android Studio's bundled runtime or
Homebrew's OpenJDK), and `protoc` at `/opt/homebrew/bin/protoc` for code generation.

## Using it from Android Studio, Flutter and the command line

Android Studio, Flutter and the `android` CLI all run the SDK's `emulator/emulator` and have no setting
for another program. **Emulator Shell › 让 Android Studio 等工具使用 Emulator Shell** replaces that file
with a forwarder script and keeps the original beside it as `emulator.real`:

- A device launch (`-avd X` or `@X`) runs `EmulatorShell X <other arguments>` in the same process, so a
  launcher that watches the emulator process (Studio does) sees it end when the device window closes.
  Arguments this app sets itself (`-no-window`, `-grpc`, `-gpu`) and Studio's tool-window flags
  (`-qt-hide-window`, `-grpc-use-token`, `-idle-grpc-timeout`) are dropped; the rest
  (`-netdelay`, `-no-snapshot-load`, `-wipe-data`, …) go to the emulator.
- Everything else runs `emulator.real` unchanged: `-list-avds`, `-version`, `-accel-check`, headless
  `-no-window` runs such as Gradle Managed Devices, and the emulators this app starts itself (they carry
  `EMULATOR_SHELL_PASSTHROUGH=1`).
- Calls are logged to `~/Library/Logs/EmulatorShell-forwarder.log`.

In Android Studio also turn off Settings › Tools › Emulator › "Launch in the Running Devices tool
window". Turning the menu item off moves `emulator.real` back. An SDK update that installs a new
emulator replaces the forwarder; the manager notices on opening and offers to install it again (the new
binary then becomes `emulator.real`).

**Emulator Shell › 安装命令行工具 emulator-shell** links `emulator-shell` into `/opt/homebrew/bin`
(or `/usr/local/bin`, `~/.local/bin`): `emulator-shell <avd> [emulator arguments]` opens a device,
`emulator-shell` alone the manager.

## Device manager

| Action | Implementation |
|---|---|
| List AVDs | `~/.android/avd/*.ini` and each AVD's `config.ini`; re-read whenever the app becomes active |
| Running status | this app's sessions, plus `pid_*.ini` files emulators write to `~/Library/Caches/TemporaryItems/avd/running`, so emulators started elsewhere show up too |
| Start / cold boot | `emulator -avd <id> -no-window -gpu host -grpc <port>` (`-no-snapshot-load` for cold boot) |
| Create | `avdmanager create avd -n <id> -k <image> -d <device> -c <sd>M`, then the form's values written into `config.ini` |
| Edit | `config.ini` only; device type and SD card size are fixed after creation |
| Wipe data | deletes `userdata-qemu.img*`, `cache.img*` and `snapshots/` |
| Delete | `avdmanager delete avd -n <id>` |
| Device types | `avdmanager list device`, grouped by tag and name; XR and glasses profiles are left out |
| System images | arm64 folders under `system-images/`; a folder without `source.properties` is an interrupted install. Downloadable ones come from `sdkmanager --list`, cached in `~/Library/Caches/EmulatorShell/sdkmanager-list.txt` and refreshed when the images sheet opens. Download and remove use `sdkmanager --install` / `--uninstall`. |

## Input

- Click / drag on the screen: single-finger touch.
- Drag anywhere on the frame, the title bar or the button pill: move the window. Once a drag brings the
  window's top against the top of the screen, the title bar shows, since the pointer can no longer reach
  the band above the device that shows it; it hides on release unless the pointer is in that band.
- Typing: printable ASCII, Enter, Tab, Backspace, Delete, arrows, Home/End, Page Up/Down.
- Esc or ⌘B: Back. ⇧⌘H: Home. ⇧⌘O: Overview. ⌘L: Power. ⌥⌘↑ / ⌥⌘↓: volume. ⌘S: screenshot to
  the Desktop. ⌘← / ⌘→: rotate. ⇧⌘F: fold or unfold a foldable (also a title bar button).
- The power and volume buttons drawn on the frame are clickable; holding one holds the key.
- Right-click: the same device commands as a menu.

## Window size

A device window is one borderless window holding the device, the room above it for the title bar
(kept even while the bar is hidden) and the button pill below it, so its size is everything it
shows. The bars do not scale and the window is always as wide as the device: on a narrow one the
title bar leaves out the device name, then the volume and power buttons (also on the frame and in
the Device menu), then rotate and screenshot; the window buttons at its leading edge and "More" always
stay. Those are the system's close (the same as ⌘W), minimize (⌘M) and zoom buttons; zoom is greyed
out, since a device window has no full screen and its sizes are in the Window menu. An 8 pt transparent margin
around it holds the outer part of the corner resize areas.

- Drag a corner of the device: it keeps its proportions and the window's opposite corner stays put,
  with the bars included in the size and the whole window kept within the screen's visible area. It
  grows by how far the pointer moves along the device's diagonal, so it changes smoothly from its
  current size in any direction. Like a standard window's corner, each area reaches 8 pt outside the
  device's rectangle and in to just past the body's rounded corner (measured from the skin's
  artwork), so it sits on the corner one sees. The pointer shows the resize cursor exactly while it
  is over one (and the app is active; no app can change the cursor from the background), and a press
  there always resizes rather than reaching the window behind. A readout
  where the title bar goes shows the size, e.g. "80% · 1030 × 2298" (relative to one device pixel
  per screen pixel, and the display's size in screen pixels). The drag snaps to the sizes below.
  While dragging, the drawn layers are only stretched; on release the skin is drawn again at the
  new size and frames are requested at the new pixel size.
- Window menu: 像素一比一 ⌘1 (one device pixel per screen pixel), 物理尺寸 ⌘2 (the real
  device's size, from `hw.lcd.density` and the screen's size in millimetres), 适合屏幕 ⌘3,
  放大 ⌘+ / 缩小 ⌘− (in steps of 10 % of one-to-one).
- Limits: the device's long side is at least 360 pt, and the whole window fits the screen's visible
  area. A window dragged partly off the screen moves back once released.
- Rotating animates the turn over 0.3 s, and the window resizes along with it, eased the same way;
  when the turned device is shown at another size to fit, the turn animates that too.
- Rotating keeps the size. If the turned device does not fit, it is shown smaller until it fits
  again at the chosen size; moving the window to a smaller screen works the same way.
- Size, orientation and position are remembered per AVD (`deviceWindow.<avd>` in the app's
  defaults).

## "More" panel

The ⋯ button in a device's title bar opens a panel of emulator features, all through gRPC:

| Tile | What it does | Call |
|---|---|---|
| 位置 | Latitude/longitude or a preset city | `setGps` / `getGps` |
| 电池 | Charge level and charger (none, AC, USB, wireless) | `setBattery` / `getBattery` |
| 剪贴板同步 | Two-way clipboard sync on or off | `streamClipboard` / `setClipboard` |
| 来电与短信 | Incoming call, remote answer, hang up, hold; receive an SMS | `sendPhone` / `sendSms` |
| 指纹 | One touch of finger 1 on the sensor | `sendFingerprint` |
| 麦克风 | Route the Mac's microphone to the device | `setMicrophoneState` |
| 亮度 | Screen backlight | `setBrightness` / `getBrightness` |
| 快照 | List, save, restore and delete snapshots | `SnapshotService` |
| 折叠姿态 | The postures in the AVD's `hw.sensor.posture_list` (closed, half-open, open, flipped, tent); foldables only (`hw.sensor.hinge=yes`) | `setPosture` |

The emulator's own extended-controls window is not offered: a headless emulator has no Qt UI, and
`UiController.showExtendedControls` makes it crash.

The popover closes itself on clicks outside it, Esc, or when the app goes to the background.

## How it renders

The skin comes from the AVD's `skin.path`; a skin with only a landscape layout (Pixel Tablet) is
read in that orientation, and an AVD without a skin gets a plain dark rounded bezel sized from
`hw.lcd.width/height`. A foldable's skin (Pixel Fold, 9 Pro Fold, 10 Pro Fold) has no `layout` of
its own but a `default` folder for the unfolded device and a `closed` one for the folded device;
new AVDs get the top folder as `skin.path`, as Android Studio writes it. Layers, bottom to top:

1. The live display, scaled by the emulator to the window's backing pixel size.
2. The skin's `mask` image (bezel corners and camera cutout).
3. The skin's frame image.

The display rectangle's square corners stick out past the rounded frame on some skins; those
pixels are flood-filled from the corners of the mask and clipped from both the display and the
overlay, so they stay transparent instead of showing through.

## Foldables

The window follows the posture through `streamNotification`, whether it was set from the "More" panel,
the emulator console (`adb emu fold`, `posture`) or a snapshot. Closed, it shows the skin's `closed`
artwork with the outer display; open or half-open, the `default` one, at the same scale, so the two
keep their real proportions.

The emulator holds a book-style foldable on its side while open (a cold boot reports rotation 3),
unlike the closed device. A real one stays in the hand as it is when it folds, so here a foldable is
held as its window was last (upright the first time) from its first frame on, and folding keeps the
turn: open and closed are the same way up, and ⌘← / ⌘→ turn either. The turn the frames report until
the emulator has caught up is not followed, so the window does not turn there and back.

Folding and unfolding are animated, however the posture changed, except for the posture a device
boots in. The animation plays in a transparent window over the device's (the folding half reaches past
its edges) from pictures of both states, while the live view is hidden: like a book closing, the half
left of the hinge, whose back is the outer display on Pixel Fold models, swings toward the viewer
over onto the right one with some perspective, darkening as it turns away, and past 90° shows the
outer display, which lands on the right half. If the state entered is
shown at another size (it does not fit the screen at the chosen one) or its window would not fit
where it lands, it grows or moves there meanwhile. The window, and with it the title bar and the
button pill, resizes over the same time with the same easing, ending with the fold: stepped on each
refresh of the screen at the point the fold has reached (an AppKit animator keeps its own clock and
lands late, nudging the window once the live view is back), and ending on the whole points where the
live view then shows the device. Unfolding plays
the same backwards. The pictures are drawn off the main thread and both animations start together
once they are ready, so the window's resizing (driven from the main thread) is not held up behind the
fold (driven by the render server).

Frames of the other state, which keep coming for a moment after the posture changes, are never shown
in a skin, and the first frames of a display the emulator has just switched to are blank; so the
live view goes dark at the switch, a blank frame never replaces a picture, and an animation that ends
before the new display's first real frame keeps its last picture until it comes (1.5 s at most). The outer display's first live frame replaces its picture
as soon as it arrives; the side not on screen otherwise shows its last frame, or a dark display. A
device unfolded where it was folded goes back exactly where it was.

A foldable is streamed at a size that fits either display at the chosen scale, so folding never asks
for a new stream: asking while the emulator switches displays has crashed it (gfxstream writing a
frame past the end of the shared buffer).

Touch coordinates are read by the emulator in the open display's pixels (`hw.lcd.width/height`) and
scaled to the display the guest shows, so while closed the outer display's points are scaled up to
that size before they are sent.

## Frame transport

`streamScreenshot` is called with `ImageTransport.MMAP` and a `file://` handle to
`$TMPDIR/emulator-shell-<avd>-<side>.frames` (a new file when a larger window needs more room), so the emulator writes RGBA pixels straight into that
mapped file and each gRPC message only announces a frame. On arrival the frame is swizzled to
BGRA (forcing alpha opaque) into one of four IOSurfaces, which become the display layer's contents
without another upload. Scrolling Settings on an M2 runs at about 60 fps with under 15 ms
latency.

`-gpu host` matters as much as the transport: with `-no-window` the AVD's `hw.gpu.mode=auto`
falls back to SwiftShader, and the guest itself only renders about 20 fps. Switching GPU mode
invalidates the quick-boot snapshot once, so the first launch after the switch cold-boots.
Sending pixels inside the gRPC messages instead (about 5 MB per frame) cannot keep up with a
60 fps guest; the stream falls seconds behind.

## Not done yet

- No scroll wheel, multi-touch or audio output.
- A foldable shows only the open or the closed display, not both at once (Pixel Fold's rear display
  mode), and the half-open posture is drawn as open.
