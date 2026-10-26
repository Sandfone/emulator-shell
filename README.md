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

Quitting (⌘Q) or closing a device window (⌘W) shuts its emulator down the same way as closing the
emulator's own window, quick-boot snapshot included. Several devices can run at once; each gets
the next free gRPC port from 8554. Emulator output goes to `$TMPDIR/emulator-shell-<avd>.log`.
Set `EMULATOR_SHELL_DEBUG=1` to log touches, keys and, every 5 seconds, the frame rate with
frames the emulator skipped (gaps in `seq`), frames dropped because every display surface was
still on screen, and the latency from the emulator's frame timestamp. Set
`EMULATOR_SHELL_GRPC_FRAMES=1` to send pixels inside the gRPC messages instead of shared memory,
for comparison.

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
- Drag anywhere on the frame, the title bar or the button pill: move the window.
- Typing: printable ASCII, Enter, Tab, Backspace, Delete, arrows, Home/End, Page Up/Down.
- Esc or ⌘B: Back. ⇧⌘H: Home. ⇧⌘O: Overview. ⌘L: Power. ⌥⌘↑ / ⌥⌘↓: volume. ⌘S: screenshot to
  the Desktop. ⌘← / ⌘→: rotate.
- The power and volume buttons drawn on the frame are clickable; holding one holds the key.
- Right-click: the same device commands as a menu.

## Window size

A device window is one borderless window holding the device, the room above it for the title bar
(kept even while the bar is hidden) and the button pill below it, so its size is everything it
shows. The bars do not scale; the window is never narrower than the title bar's 300 pt.

- Drag any corner: the device keeps its proportions and the opposite corner stays put. A readout
  where the title bar goes shows the size, e.g. "80% · 1030 × 2298" (relative to one device pixel
  per screen pixel, and the display's size in screen pixels). The drag snaps to the sizes below.
  While dragging, the drawn layers are only stretched; on release the skin is drawn again at the
  new size and frames are requested at the new pixel size.
- Window menu: 像素一比一 ⌘1 (one device pixel per screen pixel), 物理尺寸 ⌘2 (the real
  device's size, from `hw.lcd.density` and the screen's size in millimetres), 适合屏幕 ⌘3,
  放大 ⌘+ / 缩小 ⌘− (in steps of 10 % of one-to-one).
- Limits: the device's long side is at least 360 pt, and the whole window fits the screen's visible
  area. A window dragged partly off the screen moves back once released.
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
| 折叠姿态 | Closed, half-open, open, flipped, tent; foldables only (`hw.sensor.hinge=yes`) | `setPosture` |

The emulator's own extended-controls window is not offered: a headless emulator has no Qt UI, and
`UiController.showExtendedControls` makes it crash.

The popover closes itself on clicks outside it, Esc, or when the app goes to the background.

## How it renders

The skin comes from the AVD's `skin.path`; a skin with only a landscape layout (Pixel Tablet) is
read in that orientation, and an AVD without a skin gets a plain dark rounded bezel sized from
`hw.lcd.width/height`. Layers, bottom to top:

1. The live display, scaled by the emulator to the window's backing pixel size.
2. The skin's `mask` image (bezel corners and camera cutout).
3. The skin's frame image.

The display rectangle's square corners stick out past the rounded frame on some skins; those
pixels are flood-filled from the corners of the mask and clipped from both the display and the
overlay, so they stay transparent instead of showing through.

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
- A foldable's window keeps its unfolded size; switching to the outer display when closed is not
  shown.
