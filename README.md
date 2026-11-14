# Emulator Shell

A macOS prototype that renders an Android emulator's device frame itself instead of using the
emulator's own window. The emulator runs headless (`-no-window -gpu host -grpc <port>`); this app streams
the display over the emulator's gRPC API and composites it under the skin artwork with
Core Animation, so the frame edge and screen corners are antialiased against the desktop.

A Windows version lives in `windows/` (WinUI 3 and C#); see [Windows](#windows) below. The rest of this
document describes the macOS app, which the Windows one follows feature for feature.

## Run

```bash
swift build
.build/debug/EmulatorShell
```

Without arguments the device manager opens: phones and tablets connected over USB or Wi-Fi, then AVDs
grouped by category on the left, the selected one's details on the right (see Physical devices). From there you start, stop, create, edit, duplicate, wipe and delete
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
| Show a device started elsewhere | **在本 App 中显示** connects a device window to the `grpc.port` in its `pid_*.ini` (with `grpc.token` as a bearer token when Studio set one; emulators that use `-grpc-use-jwt` are not supported). Closing that window only lets go of the device; **停止** shuts it down with SIGINT. Its sound stays with the program that started it. `EmulatorShell <avd>` for a device already running elsewhere does the same |
| Switch to the app that started it | the nearest of the emulator process and its ancestors that is a regular app (Android Studio, Terminal, the emulator's own window), activated; another Emulator Shell is also asked over a distributed notification to bring that device's window forward |
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
  the Desktop. ⌘← / ⌘→: rotate. ⇧⌘F: fold or unfold a foldable.
- A foldable has a rail at the device's right, always shown, with its postures: open, closed and half
  open; the one it is in is marked. Other devices have none.
- The power and volume buttons drawn on the frame are clickable; holding one holds the key.
- Right-click: the same device commands as a menu.

## Window size

A device window is one borderless window holding the device, the room above it for the title bar
(kept even while the bar is hidden) and the button pill below it, and for a foldable room on either
side for the posture rail (on the right; the left is as wide, so the device stays centred), so its
size is everything it shows. The bars do not scale and are as wide as the device: on a narrow one the
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
  放大 ⌘+ / 缩小 ⌘− (in steps of 10 % of one-to-one). Each grows or shrinks the device over 0.3 s,
  eased, from a still picture in a transparent window over the device's (`ZoomAnimation`), as the fold
  animation does: a step of the window's frame and the device drawn to fit it reach the screen a frame
  apart, which shook the device a few points all the way. The window resizes underneath over the same
  time, its edges each eased and rounded on their own so that the top stays put, and at the end the
  device is drawn again at the new size and shown live. Half open, which the flat picture would not
  show, the device is stretched to each step of the window instead.
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
| 麦克风 | Route the Mac's microphone to the device, restarting it with the emulator's own audio (see Sound) | `-audio coreaudio -allow-host-audio` |
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
artwork with the outer display; open or half-open, the `default` one. The two are drawn at different
pixel densities, like the displays (a Pixel 10 Pro Fold's closed artwork is 2495 pixels tall, its open
one 2266), so the closed one is scaled to the open one's: the closed device is then exactly the open
one's right half, as on a real device. It is cut down to the device's outline first: its layout leaves
59 pixels below it, which would make it taller than the open skin and shown smaller to fit the
screen.

The emulator holds a book-style foldable on its side while open (a cold boot reports rotation 3),
unlike the closed device. A real one stays in the hand as it is when it folds, so here a foldable is
held as its window was last (upright the first time) from its first frame on, and folding keeps the
turn: open and closed are the same way up, and ⌘← / ⌘→ turn either. The turn the frames report until
the emulator has caught up is not followed, so the window does not turn there and back.

Folding and unfolding are animated, however the posture changed, except for the posture a device
boots in. The animation plays in a transparent window over the device's (the folding half reaches past
its edges) from pictures of both states, while the live view is hidden: like a book closing, the half
left of the hinge, whose back is the outer display on Pixel Fold models, swings toward the viewer
over onto the right one with some perspective, and past 90° shows the
outer display, which lands exactly over the right half, their outlines sharing the top and the outer
edge. The right half stays put throughout, as if lying on a table. A turning half keeps its picture
as it is, neither darkened nor blurred. Each half is drawn as its frame and its display apart, so that
the display entered can light up on its own. The animation
starts a fifteenth of a second after its pictures are handed over, so that the render server has
taken them in and the start is not skipped. It moves like a real hinge over 0.6 s: stiff at
first, then the magnets pull the halves shut for the last tenth, where they bounce once; opening,
the magnets hold for a moment, then the hinge swings open and slows to a stop flat. The motion is
sampled into keyframes rather than eased, so it can have that shape. If the state entered is
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
live view goes dark at the switch and a blank frame never replaces a picture. The display entered is
dark in the animation, as a real one is until it turns on (a frame kept from the last time that
display was on would show whatever the device showed then, and jump to what it shows now), and lights
up once Android has turned it on, fading in over 0.2 s. An animation that ends before then keeps
its last picture until the display has lit up, or for 1.5 s if nothing comes (a device that goes to
sleep as it folds), so the live view takes over from a lit display. A device unfolded where it was
folded goes back exactly where it was.

While it switches displays Android draws the old layout stretched, then the wallpaper alone while the
windows redraw at the new size, then the new layout. A real device keeps the display off meanwhile
("Blocking screen on until initial contents have been drawn"); the emulator streams those frames
anyway, and reports no backlight change. So the guest's log is followed (`GuestLogWatch`: `adb logcat
-T 1 -e "Unblocked screen on"`, filtered in the guest; the emulator's `streamLogcat` sends nothing),
and the display entered lights up with the first frame after Android turned it on that differs from
the one shown then (2 % of a 32 × 32 grid of samples), or 0.5 s after: Android turns it on when the
windows report drawn, often a moment before their new layout comes through. Without word from the log
within 1.2 s of the posture change, the first frame that is not blank does.

Android's own unfold animation (the display lighting up from the hinge, icons moving out from the
middle) does not play in the emulator, even with the hinge angle swept slowly from 0° to 180°: the
layouts above are cut from one to the next.

Half open (the "More" panel's 折叠姿态, or `adb emu posture 2`) shows the inner display with the half
left of the hinge turned 30° toward the viewer about it, with the same depth as the fold animation,
about as Xcode's iPhone Duo shows "Partially Open"; the right half stays put. The live view draws that
half a second time for it (its own display layer showing the same frames, and frame), masking each copy
to its side of the hinge, and taps on it are found through the turn. Between open and half open the half
turns over 0.45 s; folding from half open starts from the turn, and unfolding into it ends there.

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

## Sound

The emulator starts with `-audio none` and this app plays the device's sound: `streamAudio` delivers
16-bit stereo at 48 kHz in packets of about 10 ms, silent ones included, and an `AVAudioPlayerNode`
plays them with at most 100 ms queued (about 30 ms usually). The engine runs only while there is
something to hear and stops after 2 s of silence, so a quiet device does not keep the Mac awake.

The emulator's own CoreAudio output (emulator 37.2.12) can deadlock: a vCPU thread that starts a stream
(`virtio_snd` → `AUD_set_active_out` → `AudioDeviceStart`) holds qemu's global lock and the backend's
voice lock while it waits for CoreAudio's playback thread, which is itself waiting for that voice lock
in `ca_out_device_wconv_ioproc`. The main loop and every vCPU stop, and after 15 s the emulator's hang
detector kills the process. With `-audio none` there is no CoreAudio thread in the emulator to take
part. Snapshots saved with the emulator's own audio load fine.

Arguments passed along from Android Studio or the command line that already choose (`-audio …`,
`-no-audio`) are kept, and this app then plays nothing.

Only the emulator's own audio reaches the Mac's microphone: `-audio none` gives the guest silence, and
feeding it over gRPC instead (`injectAudio`) crashes emulator 37.2.12 (`audio_forwarder_enable`, null
pointer) as soon as the stream opens, whichever audio backend runs. So turning on 麦克风 in the "More"
panel restarts the device with `-audio coreaudio -allow-host-audio` (after asking, and warning that this
can hit the deadlock), and turning it off restarts it with `-audio none`. The choice is kept per device
(`microphone.<avd>` in the app's defaults). A restart saves and loads the quick-boot snapshot, so it
takes a few seconds.

## Physical devices

The manager's sidebar starts with **物理设备**: every device adb sees that is not an emulator, from
`adb track-devices -l`, so a device shows up or goes away within a second of being plugged in or
unplugged, without polling. The SDK's own adb is used (the one Android Studio uses), so both share one adb
server; without platform-tools the pane offers to install them with `sdkmanager`. Each device's details
(name, Android version, display size and density, battery, storage) come from one `adb shell` call once it
is authorized; the name is `ro.product.marketname` where the vendor sets it, else the model. A device
connected over both USB and Wi-Fi is one row, using USB; two USB devices are always two rows, even when
they report the same serial number, as some cheap ones do.

| State | Shown |
|---|---|
| Authorized | Live preview (`screencap` every 2 s while selected, the stream's frames while it runs), 投屏, 截图, 录屏, and a "⋯" menu: switch to Wi-Fi (`adb tcpip 5555` + `adb connect`), install an APK or copy files, reboot, disconnect / forget a Wi-Fi device |
| Unauthorized | How to allow USB debugging on the device; it turns into the above once allowed |
| Offline | A paired Wi-Fi device away from the network stays listed (remembered in the defaults, `pairedDevices`) with 重新连接 and 忘记此设备 |
| None connected | A "连接 Android 设备…" row with the steps for USB and a link to Wi-Fi pairing |

**Wi-Fi pairing** (⇧⌘N or the toolbar, Android 11+) works like Android Studio's: a QR code
`WIFI:T:ADB;S:<name>;P:<password>;;` that the device scans under Wireless debugging, after which the device
offers that pairing service over mDNS and `adb pair` completes it; or the device's six-digit code for a
pairing service found with `adb mdns services`. adb then connects to paired devices by itself.

**Mirroring** runs scrcpy-server 5.0.1 (`vendor/scrcpy`, Apache 2.0) on the device: it is pushed to
`/data/local/tmp` (each time, as it deletes itself on start) and started with `app_process`; one
`adb forward` carries a video socket (H.264 from the device's encoder, decoded with VideoToolbox into
IOSurface-backed frames), an audio socket (raw 48 kHz 16-bit stereo, Android 11+, played like an
emulator's sound) and a control socket (`Scrcpy.swift`). The device gets the same kind of window as an
emulator: a plain bezel around a display of the size adb reports, the Window menu's sizes (物理尺寸 from
the panel's real dpi in `dumpsys display`), corner resizing and a remembered placement. It follows the
device's own rotation: a new video size says the device turned, and `dumpsys input` says which way round;
the rotate buttons are left out. Closing the window only stops mirroring.

- Input: click or drag is one finger; ⌥-drag adds a second finger mirrored about the centre (pinch);
  scrolling scrolls; keys as for an emulator, ASCII text typed as keys and anything else (Chinese,
  emoji) pasted through the device's clipboard; ⌘V pastes the Mac's clipboard; dropping an APK on the
  device installs it, other files go to `/sdcard/Download`.
- MIUI and HyperOS refuse input from adb until "USB debugging (Security settings)" is turned on in the
  developer options (`persist.security.adbinput`); the detail pane says so and the window reminds on
  the first touch. The screen still streams.
- The window's "More" popover: sound on the Mac or on the device, turning the device's own screen off
  while mirroring, keeping it awake, clipboard sync, showing touches, quality (long side 1280 / 1920 /
  original at 4 / 8 / 16 Mbit/s) and sending files. Options other than the screen are scrcpy start
  options, so changing them reconnects the stream; they are kept per device (`mirror.<id>`).
- A stream that ends while the window is open (unplugged, Wi-Fi lost, the server killed) covers the
  device with a message; it starts again on its own when the device comes back, on whichever connection.

**Screenshots** (⌘S, in the window or for the device selected in the manager) are `screencap` PNGs at the
display's resolution, on the Desktop as `<name>_<date>.png`. A black one (an app with `FLAG_SECURE`) is
not saved.

**Recording** (⇧⌘R, likewise) needs no window: it opens the stream if none runs. The decoded frames are
encoded again to H.264 with AVAssetWriter, at the size of the first frame; after a turn the frames are
fitted into that size with black bars, since one MP4 track cannot change size. The sound is added when the
device sends it. The file goes to the Desktop as `<name>_<date>.mp4` and is finished properly when the
device goes away or the app quits. While recording, the sidebar row and the detail pane show the time, and
the window shows it above the device.

`EMULATOR_SHELL_MIRROR_EMULATORS=1` lists running emulators among the physical devices too (scrcpy works
with them), for trying all this without a phone.

## Not done yet

- No scroll wheel or multi-touch on emulators (physical devices have both).
- Recording is for physical devices only; an emulator has the screenshot only.
- A foldable shows only the open or the closed display, not both at once (Pixel Fold's rear display
  mode), and the half-open posture is drawn as open.

## Windows

`windows/` holds the same app for Windows 10 (19041) and later: WinUI 3 (Windows App SDK 1.8, bundled with the
app), C# on .NET 10, the emulator's gRPC API through `Grpc.Net.Client` from the same protos (`windows/Protos`),
and the device drawn with the Visual layer and Win2D. Device window, device manager, "More" panel, foldables and
the SDK forwarder all work as described above, with these differences.

### Build and run

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) and the Android SDK at `%ANDROID_HOME%`
(or `%LOCALAPPDATA%\Android\Sdk`) with the command-line tools. `avdmanager` and `sdkmanager` run with Android
Studio's bundled Java when `JAVA_HOME` is unset or older than 17.

```bash
dotnet build windows/EmulatorShell -p:Platform=x64
```

`windows/EmulatorShell/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/EmulatorShell.exe [avd [emulator arguments]]`
opens the manager or a device, as on macOS. `powershell -ExecutionPolicy Bypass -File windows\build.ps1` builds a
release and installs it in `%LOCALAPPDATA%\Programs\Emulator Shell` with a Start menu shortcut.

`powershell -ExecutionPolicy Bypass -File windows\package.ps1` builds the installer for other PCs,
`windows\dist\EmulatorShell-<version>-x64.msi` (about 73 MB), with WiX 5 (`dotnet tool install --global wix
--version 5.0.2`) from `windows\installer\EmulatorShell.wxs`. The app and the forwarder are published
self-contained, so the PC needs neither .NET nor the Windows App SDK. The MSI installs per user, without an
administrator, into `%LOCALAPPDATA%\Programs\Emulator Shell` with a Start menu shortcut, and uninstalls from
Settings › Apps; a newer version, or a rebuild of the same one, replaces what is installed. The version is
`1.0.<number of commits>` unless `-Version` says otherwise. Publishing needs `EnableMsixTooling` in the project:
without it `dotnet publish` leaves out the app's `.pri`, and the manager fails to find the controls' styles.

`windows\scripts\make-icon.ps1` redraws `Assets\AppIcon.ico` and `AppIcon.png`.

### Differences from macOS

- **Frames** ride inside the gRPC messages. The emulator's shared-memory transport (`ImageTransport.MMAP`) sends
  no frames at all from the Windows emulator, whatever the handle (`file:///…`, a path, a named mapping). Over
  gRPC on localhost the display runs at 40–60 fps at about 800 × 1800 pixels.
- **Shutting down** uses gRPC `setVmState(SHUTDOWN)`, which saves the quick-boot snapshot like closing the
  emulator's window. A console Ctrl+C reaches `emulator.exe`, which on Windows is a launcher running
  `qemu-system-x86_64-headless.exe` as a child; the launcher's exit takes the emulator down mid-save. As on macOS
  it starts with `ANDROID_EMULATOR_WAIT_TIME_BEFORE_KILL=90`, so it is not killed 20 s into saving. Emulators
  started elsewhere are stopped through their gRPC port (with their `grpc.token`), or `adb emu kill` without one.
- **Sound** stays with the emulator: the deadlock that makes the macOS app play it is in the emulator's CoreAudio
  backend, which the Windows emulator does not use. So there is no `-audio none`, and 麦克风 switches the host
  microphone with `setMicrophoneState` at once, without restarting the device.
- **Devices started elsewhere**: 在本 App 中显示 and `EmulatorShell <avd>` connect as on macOS (bearer token from
  `grpc.token`; `-grpc-use-jwt` emulators are not supported). The program that started a device is the nearest of
  the emulator process and its ancestors with a window of its own; 切换到 brings that window forward. Another
  Emulator Shell is asked to bring its device window forward over a named pipe, `EmulatorShell.<pid>`, which each
  copy listens on.
- **The window** is borderless and see-through (a transparent system backdrop with the DWM frame extended over
  it). Its window region keeps clicks to the device, its corners and the bars; elsewhere they reach the windows
  behind, as on macOS. While the device turns, folds, half opens, is resized or changes to one of the window
  sizes, the window instead covers the area the device moves through and only the content moves, drawn on the
  same frame as the bars; so the fold animation plays in the device window itself rather than in a window over
  it, and the window sizes grow and shrink the live device (stretched, then drawn again at the end) rather than
  a still picture of it. Window positions and sizes are in screen pixels, scaled per monitor.
- **Keyboard**: the macOS menu shortcuts use Ctrl for ⌘: Ctrl+B Back, Ctrl+Shift+H Home, Ctrl+Shift+O
  Overview, Ctrl+L Power, Ctrl+Alt+↑/↓ volume, Ctrl+S screenshot, Ctrl+←/→ rotate, Ctrl+Shift+F fold, Ctrl+1/2/3
  sizes, Ctrl+=/Ctrl+− zoom, Ctrl+W close, Ctrl+M minimize, Ctrl+Shift+2 manager, Ctrl+Q quit. The window's
  minimize and close buttons sit at the trailing end of the title bar.
- **Menus**: there is no menu bar. Its menus are in the menu button (≡) at the leading end of the device's title
  bar, which a right click on the device also opens: 模拟器管理器, the 设备 and 窗口大小 submenus, the forwarder
  and the command-line tool, minimize, close and quit. The manager's ⋯ button has the forwarder, the
  command-line tool and Quit.
- **Notification area and jump list**: the app's icon in the notification area opens the manager on a click; its
  menu lists every AVD with its state (choosing one shows, starts or attaches it) and quits. Right-clicking the
  taskbar button lists 模拟器管理器 and every AVD. Each device the SDK forwarder starts runs in a copy of its own,
  so one copy, the first to take a named mutex (`EmulatorShell.Primary.<hash of the app folder>`), shows the icon
  and keeps the jump list; the next takes over when it quits. A launch without an AVD (the Start menu, the jump
  list, `emulator-shell`) asks that copy, over a named pipe, to show its manager instead of opening another.
- **Dragging** the frame or a bar moves the window with the pointer on every frame. The system's move loop
  (`WM_NCLBUTTONDOWN` on the caption), started from a XAML press, moved it by amounts unrelated to the pointer's.
- **Files**: settings in `%LOCALAPPDATA%\EmulatorShell\settings.json`; emulator output in
  `%LOCALAPPDATA%\EmulatorShell\Logs\emulator-<avd>.log`; running emulators are found through
  `%TEMP%\avd\running\pid_*.ini`. System images listed are the host's ABI (`x86_64`, or `arm64-v8a` on ARM).
- **SDK forwarder**: `emulator.exe` cannot be a script, so the forwarder is a small program
  (`windows/Forwarder`, built next to the app as `EmulatorShellForwarder.exe`). Turning it on copies it over
  `emulator\emulator.exe`, keeps the original as `emulator.real.exe`, and writes the app's path to
  `emulator\emulator-shell.ini`. It waits for the program it starts and returns its exit code. Calls are logged to
  `%LOCALAPPDATA%\EmulatorShell\Logs\forwarder.log`.
- **Command-line tool**: `emulator-shell.cmd` in `%LOCALAPPDATA%\Microsoft\WindowsApps`, which is on the PATH.
- **Quitting** ends the app once the manager and every device window are closed; the emulators finish saving
  their snapshots in the background first.

Debug builds read `EMULATOR_SHELL_TEST_SCRIPT` (device windows) and `EMULATOR_SHELL_MANAGER_SCRIPT` (the
manager): `;`-separated commands such as `rotate right`, `fold`, `resize 2 200 300`, `more`, `new`, `images`,
for checking the windows without driving the mouse (see `DeviceSession.Debug.cs` and `ManagerWindow.Debug.cs`).

Known issues, both in the Windows emulator (36.3.10) and recovered by a cold boot (the manager's ⋯ menu):

- A quick-boot snapshot saved within seconds of a boot or of resuming one has come back frozen (the screen keeps
  its last picture and ignores input; the VM reports running). Snapshots saved after longer sessions resume
  normally.
- A foldable resumed from its snapshot has lost its outer display: closed, the emulator logs `Display with id: 6
  does not exist` and streams nothing, so the closed device stays dark. After a cold boot it folds and unfolds as
  described above.
