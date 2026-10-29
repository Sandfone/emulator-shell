import AppKit
import CoreImage

/// A device-window command, from the main menu, the window's context menu or the chrome.
enum DeviceCommand {
    case key(String)
    case action(DeviceChrome.Action)

    /// The Device menu: title, command, key equivalent, modifiers; groups are separated.
    @MainActor static let menuGroups: [[(String, DeviceCommand, String, NSEvent.ModifierFlags)]] = {
        let left = String(Character(UnicodeScalar(NSLeftArrowFunctionKey)!))
        let right = String(Character(UnicodeScalar(NSRightArrowFunctionKey)!))
        let up = String(Character(UnicodeScalar(NSUpArrowFunctionKey)!))
        let down = String(Character(UnicodeScalar(NSDownArrowFunctionKey)!))
        return [
            [
                ("返回", .key("GoBack"), "b", [.command]),
                ("主屏幕", .key("GoHome"), "h", [.command, .shift]),
                ("最近任务", .key("AppSwitch"), "o", [.command, .shift]),
            ],
            [
                ("截图", .action(.screenshot), "s", [.command]),
                ("向左旋转", .action(.rotateLeft), left, [.command]),
                ("向右旋转", .action(.rotateRight), right, [.command]),
                ("折叠 / 展开", .action(.toggleFold), "f", [.command, .shift]),
            ],
            [
                ("音量 −", .key("AudioVolumeDown"), down, [.command, .option]),
                ("音量 +", .key("AudioVolumeUp"), up, [.command, .option]),
                ("电源", .key("Power"), "l", [.command]),
            ],
        ]
    }()
}

/// A window size the Window menu offers.
enum DeviceZoom {
    /// One device pixel per screen pixel.
    case actualPixels
    /// The display as large as the real device's, judging by the AVD's density and the Mac screen's size.
    case physical
    /// As large as fits the screen.
    case fit
    case larger, smaller

    /// The Window menu's items: title, zoom, key equivalent; all with ⌘.
    static let menuItems: [(String, DeviceZoom, String)] = [
        ("像素一比一", .actualPixels, "1"),
        ("物理尺寸", .physical, "2"),
        ("适合屏幕", .fit, "3"),
        ("放大", .larger, "+"),
        ("缩小", .smaller, "-"),
    ]
}

/// One running device: the emulator process, its gRPC connection and the window that shows it.
@MainActor
final class DeviceSession {
    enum State { case booting, running, stopping, stopped }

    let avd: Avd
    let grpcPort: Int
    private(set) var state: State = .booting {
        didSet { onStateChange?(state) }
    }
    var onStateChange: ((State) -> Void)?
    /// Asks for the device to be stopped and started again with the same arguments.
    var onRestart: (() -> Void)?
    private(set) var window: DeviceWindow?

    private var emulator: EmulatorProcess?
    private var client: EmulatorClient?
    private var deviceView: DeviceView?
    private var shell: DeviceShellView?
    private var chrome: DeviceChrome?
    /// The skin shown: a foldable's `closedSkin` while it is closed, otherwise `openSkin`.
    private var skin: DeviceSkin
    private let openSkin: DeviceSkin
    private let closedSkin: DeviceSkin?
    /// Whether a foldable with `closedSkin` is closed, which shows that skin.
    private var isClosed = false
    /// For a foldable, until its first frame: the turn it is held at, which the emulator's own (on its side
    /// while open) may differ from. Then, until frames report that turn or `holdingSince` is a few seconds
    /// old, other turns they report are not followed.
    private var heldTurns: Int?
    private var holdingSince: Date?
    /// A fold or unfold playing, with the live view hidden.
    private var foldAnimation: FoldAnimation?
    /// After folding: where the closed device was put, and where the open one was, so that unfolding a device
    /// that has not been moved meanwhile puts it back exactly, without rounding creeping in.
    private var unfoldsTo: (closed: CGPoint, open: CGPoint)?
    /// From a posture change until its animation has ended (it starts once its pictures are drawn).
    private var folding = false
    /// Tells a fold whose pictures are being drawn whether another has come since.
    private var foldID = 0
    private var liveTargetDrawing = false
    /// Ends a fold animation that finished before the display entered lit up with a live frame.
    private var foldEndWaiting: (() -> Void)?
    /// While folding: when the posture changed, and when Android turned the display entered on, with what it
    /// showed then (see `liveFrameSettled`).
    private var foldStartedAt: Date?
    private var screenOn: (at: Date, signature: [UInt8])?
    /// Watches the guest's log for the display entered turning on.
    private var screenOnWatch: GuestLogWatch?
    /// Pixels per point of the pictures in the animation playing.
    private var pictureScale: CGFloat = 2
    /// Points per skin pixel the user chose. The device is shown smaller only while it does not fit the
    /// screen, e.g. turned on its side; it returns to this size when it fits again.
    private var preferredScale: CGFloat = 1
    /// During a corner drag: the dragged corner, where it started, the opposite corner (which stays put) and
    /// the scale the drag started at.
    private var resizing: (corner: ResizeHandle.Corner, start: CGPoint, anchor: CGPoint, scale: CGFloat)?
    /// Side of the square box the screen stream was last requested at, in pixels.
    private var streamSide = 0
    private var clipboard: ClipboardSync?
    private var sharedFrames: SharedFrameBuffer?
    private var tasks: [Task<Void, Never>] = []
    private var streamTask: Task<Void, Never>?
    private var postureTask: Task<Void, Never>?
    /// The device's sound, when this app plays it (see `DeviceAudio`).
    private var audio: DeviceAudio?
    private var audioTask: Task<Void, Never>?
    /// Device input, sent strictly in order: separate Tasks may run out of order and swap a key's down and up.
    private var input: AsyncStream<@Sendable () async -> Void>.Continuation?
    /// Counterclockwise quarter turns the emulator reports the device is held at.
    private var deviceTurns = 0
    private var lastSurface: IOSurface?
    /// The quarter turns `lastSurface` was reported at.
    private var lastRotation = 0

    /// `cascade` offsets the window so several devices do not open on top of each other.
    /// `extraArguments` go to the emulator as they are, e.g. the ones Android Studio or Flutter passed.
    init(avd: Avd, grpcPort: Int, coldBoot: Bool, extraArguments: [String] = [], cascade: Int) throws {
        self.avd = avd
        self.grpcPort = grpcPort
        openSkin = try DeviceSkin.load(avd: avd)
        skin = openSkin
        do {
            // The outer display's pixels are denser than the inner one's, so its skin draws the same device
            // taller; scaled to the open skin's, the closed device is the open one's right half. Cut down to
            // the device first, so that it fits the screen wherever the open one does.
            let openSkin = openSkin
            closedSkin = try avd.closedSkinFolder.map(DeviceSkin.load(folder:)).map {
                $0.croppedToBody().scaled(by: openSkin.body.height / $0.body.height)
            }
        } catch {
            Log.error("\(avd.id): closed skin unusable, staying with the open one: \(error)")
            closedSkin = nil
        }
        if closedSkin != nil { FoldAnimation.prepare() }
        showWindow(cascade: cascade)
        try startEmulator(coldBoot: coldBoot, extraArguments: extraArguments)
    }

    var serial: String? {
        RunningEmulator.scan()[avd.id]?.serial
    }

    func bringToFront() {
        NSApp.activate()
        window?.makeKeyAndOrderFront(nil)
    }

    /// Shuts the emulator down like closing its window: the quick-boot snapshot is saved first.
    func stop() {
        guard state == .booting || state == .running else { return }
        state = .stopping
        window?.orderOut(nil)
        closeStreams()
        emulator?.interrupt()
        tasks.append(Task { [weak self] in
            try? await Task.sleep(for: .seconds(EmulatorProcess.shutdownTimeout))
            self?.emulator?.terminate()
        })
    }

    /// For quitting: every device window goes away at once and the emulators save their snapshots in
    /// parallel, without holding up the main thread; `done` runs once they have all exited.
    static func stopAll(_ sessions: [DeviceSession], then done: @escaping () -> Void) {
        let stopping = sessions.filter { $0.state == .booting || $0.state == .running }
        for session in stopping {
            session.state = .stopping
            session.window?.orderOut(nil)
            session.closeStreams()
        }
        stopping.forEach { $0.emulator?.interrupt() }
        Task {
            let deadline = ContinuousClock.now + .seconds(EmulatorProcess.shutdownTimeout)
            while sessions.contains(where: { $0.emulator?.isRunning == true }), ContinuousClock.now < deadline {
                try? await Task.sleep(for: .milliseconds(100))
            }
            sessions.forEach { $0.emulator?.terminate() }
            done()
        }
    }

    /// The last resort when the app ends without `stopAll(_:then:)` having finished: waits on the main thread.
    static func stopAll(_ sessions: [DeviceSession]) {
        sessions.forEach { $0.closeStreams() }
        sessions.forEach { $0.emulator?.interrupt() }
        sessions.forEach { $0.emulator?.stop() }
        sessions.forEach { $0.tearDown() }
    }

    /// Ends the screen, posture, sound and clipboard streams, so nothing is drawn, played or synced while the
    /// emulator shuts down.
    private func closeStreams() {
        streamTask?.cancel()
        streamTask = nil
        postureTask?.cancel()
        postureTask = nil
        screenOnWatch?.stop()
        screenOnWatch = nil
        audioTask?.cancel()
        audioTask = nil
        audio?.stop()
        clipboard?.stop()
    }

    func run(_ command: DeviceCommand) {
        switch command {
        case let .key(key): enqueue { await $0.sendKey(key) }
        case let .action(action): perform(action)
        }
    }

    /// The latest frame, for the device manager's preview, with the skin and turn it is shown at.
    func thumbnail() -> DeviceThumbnail? {
        guard let lastSurface else { return nil }
        let image = CIImage(ioSurface: lastSurface)
        guard let screen = Self.imageContext.createCGImage(image, from: image.extent) else { return nil }
        return DeviceThumbnail(screen: screen, quarterTurns: lastRotation,
                               skinFolder: isClosed ? avd.closedSkinFolder : nil)
    }

    // MARK: - Window

    private func showWindow(cascade: Int) {
        let saved = UserDefaults.standard.dictionary(forKey: placementKey)
        let savedTopLeft = (saved?["x"] as? Double).flatMap { x in (saved?["top"] as? Double).map { CGPoint(x: x, y: $0) } }
        let screen = savedTopLeft.flatMap { point in NSScreen.screens.first { $0.frame.contains(point) } } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? CGRect(x: 0, y: 0, width: 1440, height: 900)
        let longSide = max(skin.size.width, skin.size.height)
        preferredScale = (saved?["scale"] as? Double).map { CGFloat($0) }
            ?? min(fitScale(turns: 0, in: visible.size), 900 / longSide)
        // Opened the way it was last held, which is how a quick boot brings the device back.
        let turns = (saved?["turns"] as? Int).map { ($0 % 4 + 4) % 4 } ?? 0
        let scale = shownScale(turns: turns, on: screen)

        let view = DeviceView(skin: skin, scale: scale)
        view.setQuarterTurns(turns, animated: false)
        if closedSkin != nil { heldTurns = turns }
        deviceTurns = turns
        let shell = DeviceShellView(deviceView: view)
        let size = DeviceShellView.boxSize(device: view.size(forQuarterTurns: turns))
        let window = DeviceWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.borderless, .miniaturizable], backing: .buffered, defer: false)
        shell.setFrameSize(window.frame.size)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = true
        window.contentView = shell
        window.title = avd.name
        window.isReleasedWhenClosed = false
        window.onMoved = { [weak self] in self?.windowMoved() }
        window.onScreenTopReached = { [weak self] reached in self?.chrome?.holdTitleBar(reached) }
        var frame = CGRect(origin: .zero, size: size)
        if let savedTopLeft {
            frame.origin = CGPoint(x: savedTopLeft.x, y: savedTopLeft.y - size.height)
        } else {
            let offset = CGFloat(cascade % 6) * 32
            frame.origin = CGPoint(x: (visible.midX - size.width / 2 + offset).rounded(),
                                   y: (visible.midY - size.height / 2 - offset).rounded())
        }
        window.setBox(DeviceWindow.fitted(frame, on: screen), display: false)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(view)

        let chrome = DeviceChrome(container: shell, deviceName: avd.name, subtitle: subtitle(skin: skin), foldable: closedSkin != nil)
        chrome.onKey = { [weak self] key, pressed in self?.sendKey(key, pressed: pressed) }
        chrome.onAction = { [weak self] action in self?.perform(action) }
        shell.chrome = chrome
        shell.onResize = { [weak self] phase in self?.resize(phase) }

        view.onTouch = { [weak self] point, pressed in
            guard let self else { return }
            let point = self.touchPoint(point)
            Log.debug("touch \(pressed ? "down" : "up") at \(Int(point.x)),\(Int(point.y))")
            self.enqueue { await $0.sendTouch(x: Int(point.x), y: Int(point.y), pressed: pressed) }
        }
        view.onSideButton = { [weak self] key, pressed in self?.sendKey(key, pressed: pressed) }
        view.onScreenPress = { [weak chrome] in chrome?.dismiss() }
        view.onKey = { [weak self] event in
            guard let output = KeyMapping.map(event) else { return }
            switch output {
            case let .key(key): self?.enqueue { await $0.sendKey(key) }
            case let .text(text): self?.enqueue { await $0.sendText(text) }
            }
        }
        view.contextMenuProvider = { [weak self] in self?.makeContextMenu() ?? NSMenu() }

        self.window = window
        self.shell = shell
        self.chrome = chrome
        deviceView = view
        shell.layoutSubtreeIfNeeded()
    }

    /// "API 37.1 · 1280 × 2856", from the system image folder name and the display size.
    private func subtitle(skin: DeviceSkin) -> String {
        let resolution = "\(Int(skin.resolution.width)) × \(Int(skin.resolution.height))"
        guard let folder = avd.sysdir?.split(separator: "/").first(where: { $0.hasPrefix("android-") }) else { return resolution }
        return "API \(folder.dropFirst("android-".count)) · \(resolution)"
    }

    private func makeContextMenu() -> NSMenu {
        let menu = NSMenu(title: "设备")
        for (index, group) in DeviceCommand.menuGroups.enumerated() {
            if index > 0 { menu.addItem(.separator()) }
            for (title, command, shortcut, modifiers) in group where isAvailable(command) {
                let item = ClosureMenuItem(title: title, keyEquivalent: shortcut) { [weak self] in self?.run(command) }
                item.keyEquivalentModifierMask = modifiers
                menu.addItem(item)
            }
        }
        return menu
    }

    // MARK: - Size

    /// Shows the device at one of the Window menu's sizes.
    func zoom(_ zoom: DeviceZoom) {
        guard let window, let view = deviceView else { return }
        let screen = window.screen ?? NSScreen.main
        let target: CGFloat
        switch zoom {
        case .actualPixels:
            target = pixelScale(on: screen)
        case .physical:
            guard let scale = physicalScale(on: screen) else {
                chrome?.showResizeReadout("不知道这台 Mac 屏幕的尺寸")
                chrome?.showResizeReadout(nil)
                return
            }
            target = scale
        case .fit:
            target = fitScale(turns: deviceTurns, in: screen?.visibleFrame.size ?? window.box.size)
        case .larger, .smaller:
            // In steps of 10 % of one-to-one pixels: 50 %, 60 %, 70 % …
            let pixel = pixelScale(on: screen)
            let percent = view.scale / pixel * 10
            let step = zoom == .larger ? (percent + 0.05).rounded(.down) + 1 : (percent - 0.05).rounded(.up) - 1
            target = max(step, 1) / 10 * pixel
        }
        let scale = min(max(target, minScale), fitScale(turns: deviceTurns, in: screen?.visibleFrame.size ?? window.box.size))
        preferredScale = scale
        let box = DeviceShellView.boxSize(device: view.size(forQuarterTurns: deviceTurns, scale: scale))
        let old = window.box
        let frame = CGRect(x: (old.midX - box.width / 2).rounded(), y: (old.maxY - box.height).rounded(), width: box.width, height: box.height)
        view.setLiveScale(scale)
        window.setBox(DeviceWindow.fitted(frame, on: screen), display: true)
        chrome?.showResizeReadout(readout(scale, on: screen))
        chrome?.showResizeReadout(nil)
        commitSize()
    }

    /// Whether `zoom` would change the window, for the menu.
    func canZoom(_ zoom: DeviceZoom) -> Bool {
        guard let window, let view = deviceView else { return false }
        let screen = window.screen ?? NSScreen.main
        switch zoom {
        case .physical: return physicalScale(on: screen) != nil
        case .larger: return view.scale < fitScale(turns: deviceTurns, in: screen?.visibleFrame.size ?? .zero) - 0.0001
        case .smaller: return view.scale > minScale + 0.0001
        case .actualPixels, .fit: return true
        }
    }

    /// A corner drag: the device keeps its proportions and the opposite corner stays where it is.
    private func resize(_ phase: ResizeHandle.Phase) {
        guard let window, let view = deviceView else { return }
        let screen = window.screen ?? NSScreen.main
        switch phase {
        case let .began(corner):
            let frame = window.box
            resizing = (corner,
                        CGPoint(x: corner.isLeft ? frame.minX : frame.maxX, y: corner.isTop ? frame.maxY : frame.minY),
                        CGPoint(x: corner.isLeft ? frame.maxX : frame.minX, y: corner.isTop ? frame.minY : frame.maxY),
                        view.liveScale)
        case let .changed(point):
            guard let (corner, start, anchor, startScale) = resizing else { return }
            let visible = screen?.visibleFrame ?? .infinite
            let skinSize = view.skinSize(forQuarterTurns: deviceTurns)
            // How far the corner moved outwards, per axis. The device grows by the part of that along its own
            // diagonal, so it changes smoothly from the size it had, whichever way the pointer goes; the
            // window's minimum width does not get in the way as it would measuring from the fixed corner.
            let dx = (point.x - start.x) * (corner.isLeft ? -1 : 1)
            let dy = (point.y - start.y) * (corner.isTop ? 1 : -1)
            let diagonal = skinSize.width * skinSize.width + skinSize.height * skinSize.height
            var scale = startScale + (dx * skinSize.width + dy * skinSize.height) / diagonal
            // As large as fits between the fixed corner and the screen's edges.
            let room = CGSize(width: corner.isLeft ? anchor.x - visible.minX : visible.maxX - anchor.x,
                              height: corner.isTop ? visible.maxY - anchor.y : anchor.y - visible.minY)
            let largest = fitScale(turns: deviceTurns, in: room)
            scale = snapped(min(max(scale, minScale), max(largest, minScale)), largest: largest, on: screen)
            view.setLiveScale(scale)
            let box = DeviceShellView.boxSize(device: view.size(forQuarterTurns: deviceTurns, scale: scale))
            let frame = CGRect(x: corner.isLeft ? anchor.x - box.width : anchor.x, y: corner.isTop ? anchor.y : anchor.y - box.height,
                               width: box.width, height: box.height)
            if frame != window.box { window.setBox(frame, display: true) }
            chrome?.showResizeReadout(readout(scale, on: screen))
        case .ended:
            guard resizing != nil else { return }
            resizing = nil
            chrome?.showResizeReadout(nil)
            preferredScale = view.liveScale
            commitSize()
        }
    }

    /// After a drag, maybe onto another screen: the preferred size if it fits there, else as large as fits.
    private func windowMoved() {
        guard let window, let view = deviceView else { return }
        let screen = window.screen ?? NSScreen.main
        let scale = shownScale(turns: deviceTurns, on: screen)
        if abs(scale - view.scale) > 0.0001 {
            view.setLiveScale(scale)
            let box = DeviceShellView.boxSize(device: view.size(forQuarterTurns: deviceTurns, scale: scale))
            let old = window.box
            let frame = CGRect(x: old.minX, y: old.maxY - box.height, width: box.width, height: box.height)
            window.setBox(DeviceWindow.fitted(frame, on: screen), display: true)
        }
        commitSize()
    }

    /// Once the size has settled: sharp layers at the new size, and frames streamed at its pixel size.
    /// The square the screen is streamed in, in pixels: a square because the emulator fits the upright picture
    /// into it, so one stream serves both orientations. For a foldable it fits either display at the chosen
    /// size, so folding never asks for a new stream: asking while the emulator switches displays has crashed
    /// it (writing frames past the end of the shared buffer).
    private var wantedStreamSide: Int {
        guard let view = deviceView else { return 0 }
        let shown = max(view.displayPixelSize.width, view.displayPixelSize.height)
        guard let closedSkin else { return Int(shown) }
        // At the chosen size, which a fold does not change though either display may be shown smaller to fit.
        let pixelsPerSkinPixel = max(view.scale, preferredScale) * (window?.backingScaleFactor ?? 2)
        let displays = [openSkin, closedSkin].map { max($0.display.width, $0.display.height) * pixelsPerSkinPixel }
        // Whole pixels, with one to spare for the display grown to the pixel grid.
        return Int(max(shown, (displays.max() ?? 0).rounded(.up) + 1))
    }

    private func commitSize() {
        guard let view = deviceView else { return }
        view.commitScale()
        shell?.needsLayout = true
        window?.invalidateShadow()
        savePlacement()
        let side = wantedStreamSide
        if side != streamSide, streamTask != nil, let client {
            Log.debug("display is now \(side) px; requesting frames at that size")
            startStream(client: client)
        }
    }

    /// Snaps to the one-to-one, physical and fit sizes within a few points of the pointer.
    private func snapped(_ scale: CGFloat, largest: CGFloat, on screen: NSScreen?) -> CGFloat {
        let height = deviceView?.skinSize(forQuarterTurns: deviceTurns).height ?? 0
        let candidates = [pixelScale(on: screen), physicalScale(on: screen), largest].compactMap { $0 }
        return candidates.first { abs($0 - scale) * height < 6 && $0 <= largest && $0 >= minScale } ?? scale
    }

    /// "52 % · 666 × 1486": the size relative to one-to-one pixels, and the preset's name or the display's
    /// size in screen pixels.
    private func readout(_ scale: CGFloat, on screen: NSScreen?) -> String {
        let percent = "\(Int((scale / pixelScale(on: screen) * 100).rounded()))%"
        let close = { (other: CGFloat?) in other.map { abs($0 - scale) < 0.0005 } ?? false }
        if close(pixelScale(on: screen)) { return percent + " · 像素一比一" }
        if close(physicalScale(on: screen)) { return percent + " · 物理尺寸" }
        if let screen, close(fitScale(turns: deviceTurns, in: screen.visibleFrame.size)) { return percent + " · 适合屏幕" }
        let backing = screen?.backingScaleFactor ?? 2
        let width = Int((skin.display.width * scale * backing).rounded()), height = Int((skin.display.height * scale * backing).rounded())
        return percent + " · \(width) × \(height)"
    }

    /// The scale to show with the device at `turns`: the preferred one, unless it does not fit `screen`.
    private func shownScale(turns: Int, on screen: NSScreen?) -> CGFloat {
        guard let visible = screen?.visibleFrame else { return preferredScale }
        return max(min(preferredScale, fitScale(turns: turns, in: visible.size)), minScale)
    }

    /// The largest scale at which the window, with the device at `turns`, fits in `area`.
    private func fitScale(turns: Int, in area: CGSize) -> CGFloat {
        let size = turns % 2 == 0 ? skin.size : CGSize(width: skin.size.height, height: skin.size.width)
        let rooms = DeviceShellView.topRoom + DeviceShellView.bottomRoom
        return min((area.height - rooms) / size.height, area.width / size.width)
    }

    /// The device's long side is never shorter than 360 points.
    private var minScale: CGFloat { 360 / max(skin.size.width, skin.size.height) }

    /// Device pixels per skin pixel: skins may draw the display at another size than the device's. `hw.lcd.*`
    /// describes the open display, so a foldable's closed skin is measured by the open one.
    private var devicePixelsPerSkinPixel: CGFloat {
        guard let width = avd.config["hw.lcd.width"].flatMap(Double.init), openSkin.display.width > 0 else { return 1 }
        return CGFloat(width) / openSkin.display.width
    }

    private func pixelScale(on screen: NSScreen?) -> CGFloat {
        devicePixelsPerSkinPixel / (screen?.backingScaleFactor ?? 2)
    }

    /// From the AVD's density (dots per inch) and the screen's size in millimetres; nil if either is unknown.
    private func physicalScale(on screen: NSScreen?) -> CGFloat? {
        guard let screen, let density = avd.config["hw.lcd.density"].flatMap(Double.init), density > 0,
              let display = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
        else { return nil }
        let millimetres = CGDisplayScreenSize(display).width
        guard millimetres > 0 else { return nil }
        let pointsPerInch = screen.frame.width / (millimetres / 25.4)
        return devicePixelsPerSkinPixel / CGFloat(density) * pointsPerInch
    }

    private var placementKey: String { "deviceWindow.\(avd.id)" }

    /// Remembers the chosen size, the device's turn and where the box's top-left corner is, for the next launch.
    private func savePlacement() {
        guard let frame = window?.box else { return }
        UserDefaults.standard.set(["scale": Double(preferredScale), "x": Double(frame.minX), "top": Double(frame.maxY), "turns": deviceTurns], forKey: placementKey)
    }

    // MARK: - Emulator

    private func startEmulator(coldBoot: Bool, extraArguments: [String]) throws {
        let client = try EmulatorClient(port: grpcPort)
        self.client = client
        tasks.append(Task.detached {
            do { try await client.run() } catch { Log.error("gRPC connections ended: \(error)") }
        })
        let (stream, continuation) = AsyncStream<@Sendable () async -> Void>.makeStream()
        input = continuation
        tasks.append(Task.detached {
            for await send in stream { await send() }
        })

        // Without a window the emulator's "auto" GPU mode falls back to software rendering (~20 fps), so the
        // host GPU is used unless the AVD asks for software rendering.
        let software = ["swiftshader_indirect", "swiftshader", "swangle", "swangle_indirect", "lavapipe"]
        let gpu = software.contains(avd.config["hw.gpu.mode"] ?? "") ? "swiftshader_indirect" : "host"
        // The emulator's own sound output can deadlock it (see `DeviceAudio`), so this app plays the sound,
        // unless the arguments passed along already say what to do with it. Only the emulator's own audio
        // reaches the Mac's microphone, so a device that uses it runs with that, and takes the risk.
        let audioOptions: Set<String> = ["-audio", "-no-audio", "-noaudio"]
        let choosesAudio = !extraArguments.contains(where: audioOptions.contains)
        let usesMicrophone = choosesAudio && Self.usesMicrophone(avd)
        if choosesAudio && !usesMicrophone { audio = DeviceAudio() }
        let audioArguments = !choosesAudio ? [] : usesMicrophone ? ["-audio", "coreaudio", "-allow-host-audio"] : ["-audio", "none"]
        let emulator = EmulatorProcess(avdName: avd.id, grpcPort: grpcPort, gpu: gpu,
                                       extraArguments: (coldBoot ? ["-no-snapshot-load"] : []) + audioArguments + extraArguments)
        emulator.onExit = { [weak self] in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.emulatorExited() } }
        }
        let logFile = FileManager.default.temporaryDirectory.appendingPathComponent("emulator-shell-\(avd.id).log")
        try emulator.start(logFile: logFile)
        self.emulator = emulator
        Log.info("launched emulator for \(avd.id) on gRPC port \(grpcPort), log: \(logFile.path)")

        let clipboard = ClipboardSync(client: client)
        self.clipboard = clipboard
        let controls = DeviceControls(client: client, avd: avd, clipboardSync: clipboard.isEnabled)
        controls.onClipboardSync = { [weak clipboard] on in clipboard?.isEnabled = on }
        if choosesAudio {
            controls.usesMicrophone = usesMicrophone
            controls.restartForMicrophone = { [weak self] on in
                guard let self else { return }
                UserDefaults.standard.set(on, forKey: Self.microphoneKey(self.avd))
                self.onRestart?()
            }
        }
        chrome?.controls = controls

        tasks.append(Task { [weak self] in
            do {
                try await client.waitUntilReachable(timeout: .seconds(120)) { booted in
                    Log.info("gRPC endpoint reachable, guest booted: \(booted)")
                }
                self?.startStream(client: client)
                self?.followPosture(client: client)
                self?.playAudio(client: client)
                while await !client.isBooted() {
                    try await Task.sleep(for: .seconds(1))
                }
                Log.info("\(self?.avd.id ?? "") booted")
                guard let self, self.state == .booting else { return }
                self.state = .running
                self.chrome?.setReady(true)
                self.clipboard?.start()
            } catch is CancellationError {
            } catch {
                self?.fail(error)
            }
        })
    }

    private func emulatorExited() {
        guard state != .stopped else { return }
        if state != .stopping { Log.info("emulator for \(avd.id) exited") }
        tearDown()
    }

    private func tearDown() {
        guard state != .stopped else { return }
        tasks.forEach { $0.cancel() }
        streamTask?.cancel()
        postureTask?.cancel()
        screenOnWatch?.stop()
        audioTask?.cancel()
        audio?.stop()
        input?.finish()
        clipboard?.stop()
        client?.shutdown()
        chrome?.close()
        shell?.stopTracking()
        window?.orderOut(nil)
        window?.close()
        sharedFrames = nil
        lastSurface = nil
        state = .stopped
    }

    /// (Re)opens the screen stream at the display's current pixel size; a new stream starts with the current frame.
    private func startStream(client: EmulatorClient) {
        let side = wantedStreamSide
        streamSide = side
        streamTask?.cancel()
        streamTask = Task { [weak self] in
            do {
                try await self?.streamScreen(client: client, side: side)
            } catch {
                if !Task.isCancelled, self?.state == .booting || self?.state == .running { self?.fail(error) }
            }
        }
    }

    private func streamScreen(client: EmulatorClient, side: Int) async throws {
        // EMULATOR_SHELL_GRPC_FRAMES=1 keeps pixels in the gRPC messages, for comparing the two transports.
        if sharedFrames.map({ $0.size < side * side * 4 }) ?? true,
           ProcessInfo.processInfo.environment["EMULATOR_SHELL_GRPC_FRAMES"] != "1" {
            // A larger window needs a larger buffer. The stream being replaced may still be using the old one,
            // which it keeps alive until it ends, so the new one gets its own file.
            let url = FileManager.default.temporaryDirectory.appendingPathComponent("emulator-shell-\(avd.id)-\(side).frames")
            sharedFrames = try SharedFrameBuffer(url: url, size: side * side * 4)
        }
        Log.info("streaming frames via \(sharedFrames.map { $0.handle } ?? "gRPC messages")")

        let counter = FrameCounter()
        let pool = SurfacePool()
        try await client.streamScreen(width: side, height: side, shared: sharedFrames) { [weak self] frame in
            counter.noteShape(frame)
            let surface = pool.write(frame)
            counter.tick(frame, shown: surface != nil)
            guard let surface else { return }
            let rotation = frame.rotation, folded = frame.folded
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self else { return }
                    // A foldable's frames of the other state, which keep coming for a moment after its posture
                    // changes, do not belong in the skin it now shows.
                    guard self.closedSkin == nil || folded == self.isClosed else { return }
                    self.lastSurface = surface
                    self.lastRotation = rotation
                    self.deviceView?.show(frame: surface, rotation: rotation)
                    self.showLiveFold(folded: folded)
                    self.holdTurn(reported: rotation)
                    self.deviceTurned(to: rotation)
                }
            }
        }
    }

    private func enqueue(_ send: @escaping @Sendable (EmulatorClient) async -> Void) {
        guard let client else { return }
        input?.yield { await send(client) }
    }

    private func sendKey(_ key: String, pressed: Bool) {
        Log.debug("key \(key) \(pressed ? "down" : "up")")
        enqueue { await $0.sendKey(key, type: pressed ? .keydown : .keyup) }
    }

    private func perform(_ action: DeviceChrome.Action) {
        switch action {
        case .close: stop()
        case .minimize: window?.miniaturize(nil)
        case .screenshot: takeScreenshot()
        case .rotateLeft: rotate(by: 1)
        case .rotateRight: rotate(by: -1)
        case .toggleFold: toggleFold()
        }
    }

    /// Whether `command` applies to this device: folding only to a foldable with a closed skin.
    func isAvailable(_ command: DeviceCommand) -> Bool {
        if case .action(.toggleFold) = command { return closedSkin != nil }
        return true
    }

    /// Folds an open foldable shut or opens a closed one; the posture notification then plays the animation.
    private func toggleFold() {
        guard closedSkin != nil, !folding else { return }
        let posture: PostureValue = isClosed ? .postureOpened : .postureClosed
        enqueue { _ = try? await $0.setPosture(posture) }
    }

    // MARK: - Rotation

    /// Asks the emulator to turn the device; the shell follows once frames report the new orientation.
    private func rotate(by turns: Int) {
        turn(to: deviceTurns + turns)
    }

    /// Asks the emulator to hold the device at `turns` counterclockwise quarter turns.
    /// `reopensIfSilent` reopens the screen stream when no frame reports the turn soon; not while folding,
    /// when the emulator is switching displays.
    private func turn(to turns: Int, reopensIfSilent: Bool = true) {
        let target = (turns % 4 + 4) % 4
        let degrees: [Float] = [0, 90, 180, -90]
        Log.debug("rotate to \(degrees[target])°")
        enqueue { await $0.setRotation(degrees: degrees[target]) }
        // A screen that does not redraw (a portrait-only app) sends no frame to report the new orientation.
        guard reopensIfSilent else { return }
        Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(400))
            guard let self, self.deviceTurns != target, let client = self.client else { return }
            Log.debug("no frame at the new orientation yet; reopening the screen stream")
            self.startStream(client: client)
        }
    }

    /// Turns the window with the device. A device that no longer fits the screen is shown smaller until it
    /// fits again at the preferred size.
    private func deviceTurned(to turns: Int) {
        // While folding the animation shows the device; the live view takes the turn once it is done.
        guard turns != deviceTurns, !folding, heldTurns == nil, let view = deviceView else { return }
        deviceTurns = turns
        // Turned, it may be shown at another size to fit; the turn animates that too.
        view.setQuarterTurns(turns, scale: shownScale(turns: turns, on: window?.screen ?? NSScreen.main), animated: true)
        reshapeWindow(animate: true)
    }

    /// Fits the window around the device as it is now held and drawn, keeping its centre where it was.
    private func reshapeWindow(animate: Bool) {
        guard let window, let view = deviceView else { return }
        let screen = window.screen ?? NSScreen.main
        let scale = shownScale(turns: deviceTurns, on: screen)
        if scale != view.scale {
            view.setLiveScale(scale)
            view.commitScale()
        }
        let box = DeviceShellView.boxSize(device: view.size(forQuarterTurns: deviceTurns))
        let old = window.box
        let frame = CGRect(x: (old.midX - box.width / 2).rounded(), y: (old.midY - box.height / 2).rounded(),
                           width: box.width, height: box.height)
        let fitted = DeviceWindow.fitted(frame, on: screen)
        shell?.needsLayout = true
        if animate {
            // Along with the device's turn, ending with it.
            window.setBox(fitted, duration: DeviceView.turnDuration) { [weak self] in self?.commitSize() }
        } else {
            window.setBox(fitted, display: true)
            commitSize()
        }
    }

    // MARK: - Folding

    /// The emulator reads touch coordinates in the open display's pixels and scales them to the display the guest
    /// shows, so a closed foldable's points on its outer display are scaled up to that size first.
    private func touchPoint(_ point: CGPoint) -> CGPoint {
        guard skin.display.size != openSkin.display.size else { return point }
        return CGPoint(x: (point.x * openSkin.display.width / skin.display.width).rounded(.down),
                       y: (point.y * openSkin.display.height / skin.display.height).rounded(.down))
    }

    /// The emulator holds a book-style foldable on its side while open, unlike the closed one: after a cold boot it
    /// reports a turn of 3. Folding keeps the turn, as a real device stays in the hand, so a foldable is held
    /// as its window was last (upright the first time) from its first frame on, and the open and closed
    /// device stay the same way up. Turns the frames report meanwhile are not followed, so the window does not
    /// turn there and back.
    private func holdTurn(reported: Int) {
        guard let turns = heldTurns else { return }
        guard let since = holdingSince else {
            holdingSince = Date()
            if reported != turns {
                Log.debug("foldable reported turn \(reported); holding it at \(turns)")
                turn(to: turns, reopensIfSilent: false)
            } else {
                heldTurns = nil
            }
            return
        }
        if reported == turns || Date().timeIntervalSince(since) > 5 { heldTurns = nil }
    }

    /// Follows the posture of a foldable, whether set from the "More" panel, the console or a snapshot.
    private func followPosture(client: EmulatorClient) {
        guard avd.config["hw.sensor.hinge"] == "yes" else { return }
        postureTask?.cancel()
        postureTask = Task { [weak self] in
            do {
                try await client.streamPosture { posture in
                    DispatchQueue.main.async { MainActor.assumeIsolated { self?.postureChanged(to: posture) } }
                }
            } catch {
                if !Task.isCancelled { Log.error("posture notifications ended: \(error)") }
            }
        }
        // Android keeps a display it switches to off until the windows on it have drawn, and logs when it
        // turns it on; the emulator streams what it draws meanwhile, which a real device never shows.
        screenOnWatch?.stop()
        let avdId = avd.id
        screenOnWatch = GuestLogWatch(serial: { RunningEmulator.scan()[avdId]?.serial }, pattern: "Unblocked screen on") { [weak self] _ in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.screenTurnedOn() } }
        }
    }

    /// While folding, notes that Android has turned the display entered on, and what it showed then.
    private func screenTurnedOn() {
        guard folding, screenOn == nil, let surface = lastSurface else { return }
        Log.debug("display turned on")
        screenOn = (Date(), Self.signature(of: surface))
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.settleTime) { [weak self] in
            guard let self else { return }
            self.showLiveFold(folded: self.isClosed)
        }
    }

    /// How long after Android turns a display on its windows may still be redrawing at the new size.
    private static let settleTime: TimeInterval = 0.5
    /// How long after a fold to light up the display entered when the log has not said it turned on.
    private static let unannouncedTime: TimeInterval = 1.2

    /// Whether a frame of the display entered shows what Android lit it up with, not one of the frames it
    /// draws while it switches displays: the old layout stretched, then the wallpaper alone while the windows
    /// redraw. Android turns the display on when they report drawn, which is often a moment before their new
    /// layout comes through; so the frame must also differ from the one shown then, or come a while after.
    private func liveFrameSettled(_ surface: IOSurface) -> Bool {
        guard let started = foldStartedAt else { return true }
        guard let screenOn else { return Date().timeIntervalSince(started) > Self.unannouncedTime }
        return Date().timeIntervalSince(screenOn.at) >= Self.settleTime
            || Self.differs(Self.signature(of: surface), screenOn.signature)
    }

    /// Whether the device uses the Mac's microphone, which it hears only through the emulator's own audio.
    static func usesMicrophone(_ avd: Avd) -> Bool {
        UserDefaults.standard.bool(forKey: microphoneKey(avd))
    }

    private static func microphoneKey(_ avd: Avd) -> String { "microphone.\(avd.id)" }

    private func playAudio(client: EmulatorClient) {
        guard let audio else { return }
        audioTask?.cancel()
        audioTask = Task.detached {
            do {
                try await client.streamAudio(sampleRate: DeviceAudio.sampleRate) { audio.play($0) }
            } catch {
                if !Task.isCancelled { Log.error("audio stream ended: \(error)") }
            }
        }
    }

    /// A closed foldable shows its outer display, so the window switches to that skin, animated like a book
    /// closing. The device stays held as it is (see `holdTurn`), so the two states are the same way up.
    private func postureChanged(to posture: PostureValue) {
        Log.debug("posture \(posture)")
        chrome?.controls?.posture = posture
        guard let closedSkin, let view = deviceView else { return }
        let closed = posture == .postureClosed
        guard closed != isClosed else { return }
        isClosed = closed
        chrome?.setFolded(closed)
        let entering = closed ? closedSkin : openSkin
        skin = entering
        // Played for a change one sees happen, not for the posture a quick boot comes back in.
        guard state == .running, let window, window.isVisible, let surface = lastSurface, let center = deviceCenterOnScreen else {
            view.setSkin(entering)
            reshapeWindow(animate: false)
            return
        }
        // The closed device lies exactly over the open one's right half: their outlines share the top and the
        // outer edge.
        let geometry = FoldAnimation.Geometry(open: view.size(forQuarterTurns: 0, skin: openSkin, scale: view.scale),
                                              closed: view.size(forQuarterTurns: 0, skin: closedSkin, scale: view.scale),
                                              hinge: openSkin.display.midX * view.scale,
                                              closedOrigin: CGPoint(x: (openSkin.body.maxX - closedSkin.body.maxX) * view.scale,
                                                                    y: (openSkin.body.minY - closedSkin.body.minY) * view.scale))
        // The state entered is shown as large as it fits, which may differ from the state left; the animation
        // grows or shrinks to it while it folds.
        let growth = shownScale(turns: deviceTurns, on: window.screen) / view.scale
        // The closed device folds where the left half lands and unfolds where it is; it moves if the window
        // for the state entered, which follows the device, would not fit the screen there.
        let offset = geometry.offset(turns: deviceTurns)
        let finalSize = view.size(forQuarterTurns: deviceTurns, skin: entering, scale: view.scale * growth)
        let closedFrom = closed ? CGPoint(x: center.x + offset.x, y: center.y + offset.y) : center
        let closedTo: CGPoint
        let finalCenter: CGPoint
        if closed {
            finalCenter = fittedCenter(device: finalSize, center: closedFrom, on: window.screen)
            closedTo = finalCenter
        } else {
            // Unfolded at the new size, the open device's centre is that much further from the closed one's.
            let grown = CGPoint(x: offset.x * growth, y: offset.y * growth)
            let unmoved = unfoldsTo.flatMap { abs($0.closed.x - center.x) < 2 && abs($0.closed.y - center.y) < 2 ? $0.open : nil }
            finalCenter = fittedCenter(device: finalSize, center: unmoved ?? CGPoint(x: center.x - grown.x, y: center.y - grown.y),
                                       on: window.screen)
            closedTo = CGPoint(x: finalCenter.x + grown.x, y: finalCenter.y + grown.y)
        }
        // The window ends on whole points; the animation ends exactly where the live view then shows the device,
        // which is centred across the box, `bottomRoom` above its bottom.
        let finalBox = boxFrame(device: finalSize, center: finalCenter, on: window.screen)
        let landed = CGPoint(x: finalBox.minX + finalSize.width / 2, y: finalBox.minY + DeviceShellView.bottomRoom + finalSize.height / 2)
        let closedEnd = closed ? landed : CGPoint(x: landed.x + (closedTo.x - finalCenter.x), y: landed.y + (closedTo.y - finalCenter.y))

        // The pictures take a while to draw, which would hold up the window's resizing (driven from this thread)
        // behind the fold (driven by the render server); so they are drawn elsewhere, and both start together
        // once they are ready. Meanwhile the live view keeps its last frame rather than the next state's.
        view.freezesFrames = true
        folding = true
        foldStartedAt = Date()
        screenOn = nil
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.unannouncedTime) { [weak self] in
            guard let self else { return }
            self.showLiveFold(folded: self.isClosed)
        }
        foldID += 1
        let id = foldID, turns = deviceTurns, leavingTurns = lastRotation
        let scale = window.backingScaleFactor * max(1, growth)
        pictureScale = scale
        let openSkin = openSkin
        IOSurfaceIncrementUseCount(surface)
        DispatchQueue.global(qos: .userInteractive).async { [weak self] in
            let drawing = Date()
            defer { Log.debug("fold pictures drawn in \(Int(Date().timeIntervalSince(drawing) * 1000)) ms") }
            let leaving = Self.image(of: surface)
            IOSurfaceDecrementUseCount(surface)
            // The display entered is dark, as a real one is until it turns on, until its first live frame
            // lights it up: a frame from the last time it was on would show what the device showed then.
            let openPicture = FoldAnimation.picture(skin: openSkin, screen: closed ? leaving : nil, screenTurns: leavingTurns,
                                                    size: geometry.open, scale: scale)
            let closedPicture = FoldAnimation.picture(skin: closedSkin, screen: closed ? nil : leaving, screenTurns: leavingTurns,
                                                      size: geometry.closed, scale: scale)
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self, self.foldID == id, let view = self.deviceView, let window = self.window else { return }
                    view.setSkin(entering)
                    view.freezesFrames = false
                    guard let openPicture, let closedPicture else {
                        self.folding = false
                        self.reshapeWindow(animate: false)
                        return
                    }
                    view.isHidden = true
                    self.foldAnimation?.finish()
                    var animation: FoldAnimation?
                    animation = FoldAnimation(closing: closed, geometry: geometry, open: openPicture, closed: closedPicture,
                                              closedCenter: (closedFrom, closedEnd), growth: growth, turns: turns,
                                              over: window) { [weak self] in
                        guard let self, let animation, self.foldAnimation === animation else { return }
                        let end = { [weak self] in
                            guard let self, self.foldAnimation === animation else { return }
                            self.foldEndWaiting = nil
                            self.foldAnimation = nil
                            self.folding = false
                            self.foldStartedAt = nil
                            self.screenOn = nil
                            self.foldEnded(turns: turns, center: landed)
                            self.unfoldsTo = closed ? self.deviceCenterOnScreen.map { ($0, center) } : nil
                            animation.finish()
                        }
                        // The display entered lights up in the animation's last picture, which stays until it has
                        // (or a little while, for a device that goes to sleep as it folds), so that the live view
                        // takes over from a lit display.
                        if animation.showsLiveTarget {
                            end()
                        } else {
                            self.foldEndWaiting = end
                            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                                if !animation.hasLiveTarget { end() }
                            }
                        }
                    }
                    self.foldAnimation = animation
                    // The window, and with it the bars, takes the new state's size along with the fold.
                    if let animation { window.setBox(finalBox, duration: FoldAnimation.foldDuration, start: animation.start) }
                }
            }
        }
    }

    /// Whether a frame is all but black, as the first ones of a display the emulator has just switched to are.
    nonisolated private static func isBlank(_ surface: IOSurface) -> Bool {
        surface.lock(options: .readOnly, seed: nil)
        defer { surface.unlock(options: .readOnly, seed: nil) }
        let base = surface.baseAddress.assumingMemoryBound(to: UInt8.self)
        let width = surface.width, height = surface.height, row = surface.bytesPerRow
        // An 8 × 8 grid of samples is plenty: the status bar alone has bright pixels.
        for y in stride(from: height / 16, to: height, by: max(1, height / 8)) {
            for x in stride(from: width / 16, to: width, by: max(1, width / 8)) {
                let pixel = base + y * row + x * 4
                if pixel[0] > 12 || pixel[1] > 12 || pixel[2] > 12 { return false }
            }
        }
        return true
    }

    /// A 32 × 32 grid of a frame's colours, to tell one picture from another.
    private static func signature(of surface: IOSurface) -> [UInt8] {
        surface.lock(options: .readOnly, seed: nil)
        defer { surface.unlock(options: .readOnly, seed: nil) }
        let base = surface.baseAddress.assumingMemoryBound(to: UInt8.self)
        let width = surface.width, height = surface.height, row = surface.bytesPerRow
        var samples: [UInt8] = []
        samples.reserveCapacity(32 * 32 * 3)
        for j in 0..<32 {
            for i in 0..<32 {
                let pixel = base + (height * (2 * j + 1) / 64) * row + (width * (2 * i + 1) / 64) * 4
                samples += [pixel[0], pixel[1], pixel[2]]
            }
        }
        return samples
    }

    /// Whether two signatures show different pictures: a few percent of the grid changed, as the icons
    /// coming onto a bare wallpaper do, not just the clock.
    private static func differs(_ a: [UInt8], _ b: [UInt8]) -> Bool {
        guard a.count == b.count else { return true }
        var changed = 0
        for k in stride(from: 0, to: a.count, by: 3)
        where (0..<3).contains(where: { abs(Int(a[k + $0]) - Int(b[k + $0])) > 24 }) {
            changed += 1
        }
        return changed > a.count / 3 / 50
    }

    /// A frame as an image, for the fold animation's pictures; on any thread.
    nonisolated private static func image(of surface: IOSurface) -> CGImage? {
        let image = CIImage(ioSurface: surface)
        return imageContext.createCGImage(image, from: image.extent)
    }

    nonisolated private static let imageContext = CIContext(options: [.cacheIntermediates: false])

    /// Shows the live device again where and as the animation left it: held at the turn the emulator was asked
    /// for, which frames confirm shortly if they have not yet.
    private func foldEnded(turns: Int, center: CGPoint) {
        guard let view = deviceView else { return }
        window?.stopBoxAnimation()
        if turns != deviceTurns {
            deviceTurns = turns
            view.setQuarterTurns(turns, animated: false)
        }
        placeDevice(center: center)
        view.isHidden = false
        if lastRotation != deviceTurns { deviceTurned(to: lastRotation) }
    }

    /// Where a device of `device` points centred at `center` ends up once its window is kept on `screen`.
    private func fittedCenter(device: CGSize, center: CGPoint, on screen: NSScreen?) -> CGPoint {
        let box = DeviceShellView.boxSize(device: device)
        let frame = CGRect(x: center.x - box.width / 2, y: center.y - device.height / 2 - DeviceShellView.bottomRoom,
                           width: box.width, height: box.height)
        let fitted = DeviceWindow.fitted(frame, on: screen)
        return CGPoint(x: center.x + fitted.minX - frame.minX, y: center.y + fitted.minY - frame.minY)
    }

    /// While folding, and after until the animation ends, the first settled live frame of the state being
    /// entered lights up its dark display in the animation; drawn elsewhere, like the pictures, so the
    /// window's resizing is not held up.
    private func showLiveFold(folded: Bool) {
        guard let animation = foldAnimation, !animation.hasLiveTarget, !liveTargetDrawing, animation.closing == folded,
              let surface = lastSurface, !Self.isBlank(surface), liveFrameSettled(surface), let view = deviceView else { return }
        liveTargetDrawing = true
        let skin = folded ? closedSkin ?? openSkin : openSkin
        let size = view.size(forQuarterTurns: 0, skin: skin, scale: view.scale)
        let scale = pictureScale, turns = lastRotation
        IOSurfaceIncrementUseCount(surface)
        DispatchQueue.global(qos: .userInteractive).async { [weak self] in
            let screen = Self.image(of: surface)
            IOSurfaceDecrementUseCount(surface)
            let picture = screen.flatMap { FoldAnimation.picture(skin: skin, screen: $0, screenTurns: turns, size: size, scale: scale)?.screen }
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    self?.liveTargetDrawing = false
                    guard let picture, let current = self?.foldAnimation, current === animation else { return }
                    animation.showLiveTarget(picture) { self?.foldEndWaiting?() }
                }
            }
        }
    }

    /// The device's centre on screen.
    private var deviceCenterOnScreen: CGPoint? {
        guard let window, let shell else { return nil }
        let frame = window.convertToScreen(shell.convert(shell.deviceFrame, to: nil))
        return CGPoint(x: frame.midX, y: frame.midY)
    }

    /// Moves and sizes the window so the device's centre is at `center` (screen coordinates), keeping it on screen.
    private func placeDevice(center: CGPoint) {
        guard let window, let view = deviceView else { return }
        let screen = window.screen ?? NSScreen.main
        let scale = shownScale(turns: deviceTurns, on: screen)
        if scale != view.scale {
            view.setLiveScale(scale)
            view.commitScale()
        }
        window.setBox(boxFrame(device: view.size(forQuarterTurns: deviceTurns), center: center, on: screen), display: true)
        shell?.needsLayout = true
        commitSize()
    }

    /// The box for a device of `device` points centred at `center`, kept on `screen`.
    private func boxFrame(device: CGSize, center: CGPoint, on screen: NSScreen?) -> CGRect {
        let box = DeviceShellView.boxSize(device: device)
        // The device sits `bottomRoom` above the box's bottom edge, centred across it.
        let frame = CGRect(x: (center.x - box.width / 2).rounded(), y: (center.y - device.height / 2 - DeviceShellView.bottomRoom).rounded(),
                           width: box.width, height: box.height)
        return DeviceWindow.fitted(frame, on: screen)
    }

    // MARK: - Screenshot

    private func takeScreenshot() {
        guard let client else { return }
        deviceView?.flash()
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd_HH.mm.ss"
        let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0]
        let url = desktop.appendingPathComponent("\(avd.id)_\(formatter.string(from: Date())).png")
        Task { [weak self] in
            do {
                try await client.screenshotPNG().write(to: url)
                Log.info("screenshot saved to \(url.path)")
                self?.chrome?.showToast("截图已存到桌面") {
                    NSWorkspace.shared.activateFileViewerSelecting([url])
                }
            } catch {
                Log.error("screenshot failed: \(error)")
            }
        }
    }

    private func fail(_ error: Error) {
        Log.error("\(avd.id): \(error)")
        let alert = NSAlert()
        alert.messageText = "“\(avd.name)”出错了"
        alert.informativeText = "\(error)"
        alert.runModal()
        stop()
    }
}

/// A running device's latest frame as the device manager's preview shows it.
struct DeviceThumbnail {
    /// Upright for the viewer, like the frames.
    let screen: CGImage
    /// Counterclockwise quarter turns the device is held at.
    let quarterTurns: Int
    /// The skin shown instead of the AVD's own, e.g. a foldable's closed one.
    let skinFolder: URL?
}

/// A menu item that runs a closure, for menus whose items are built from data.
final class ClosureMenuItem: NSMenuItem {
    private let handler: () -> Void

    init(title: String, keyEquivalent: String, handler: @escaping () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: keyEquivalent)
        target = self
    }

    @available(*, unavailable)
    required init(coder: NSCoder) { fatalError() }

    @objc private func run() { handler() }
}

/// Logs the frame rate every few seconds so streaming throughput is visible in the terminal.
private final class FrameCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var frames = 0
    /// Frames the emulator produced but never sent, judging by gaps in `seq`.
    private var skipped = 0
    /// Frames received while every surface was still on screen.
    private var dropped = 0
    private var lastSeq: UInt32?
    private var windowStart = Date()
    private var lastShape: (width: Int, height: Int, rotation: Int)?

    /// Logs each change of the frames' size or reported rotation.
    func noteShape(_ frame: RawFrame) {
        lock.lock()
        defer { lock.unlock() }
        let shape = (width: frame.width, height: frame.height, rotation: frame.rotation)
        guard lastShape.map({ $0 != shape }) ?? true else { return }
        lastShape = shape
        Log.debug("frames now \(frame.width)x\(frame.height), rotation \(frame.rotation)")
    }

    func tick(_ frame: RawFrame, shown: Bool) {
        lock.lock()
        defer { lock.unlock() }
        frames += 1
        if let lastSeq, frame.seq > lastSeq + 1 { skipped += Int(frame.seq - lastSeq - 1) }
        lastSeq = frame.seq
        if !shown { dropped += 1 }
        let elapsed = Date().timeIntervalSince(windowStart)
        if elapsed >= 5 {
            let latency = Date().timeIntervalSince1970 * 1000 - Double(frame.timestampUs) / 1000
            Log.debug(String(
                format: "%.1f fps at %dx%d, %d skipped by emulator, %d dropped, latency %.0f ms",
                Double(frames) / elapsed, frame.width, frame.height, skipped, dropped, latency
            ))
            frames = 0
            skipped = 0
            dropped = 0
            windowStart = Date()
        }
    }
}
