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
    private(set) var window: NSWindow?

    private var emulator: EmulatorProcess?
    private var client: EmulatorClient?
    private var deviceView: DeviceView?
    private var shell: DeviceShellView?
    private var chrome: DeviceChrome?
    private let skin: DeviceSkin
    /// Points per skin pixel the user chose. The device is shown smaller only while it does not fit the
    /// screen, e.g. turned on its side; it returns to this size when it fits again.
    private var preferredScale: CGFloat = 1
    /// During a corner drag: the dragged corner and the opposite one, which stays put.
    private var resizing: (corner: ResizeHandle.Corner, anchor: CGPoint)?
    /// Side of the square box the screen stream was last requested at, in pixels.
    private var streamSide = 0
    private var clipboard: ClipboardSync?
    private var sharedFrames: SharedFrameBuffer?
    private var tasks: [Task<Void, Never>] = []
    private var streamTask: Task<Void, Never>?
    /// Device input, sent strictly in order: separate Tasks may run out of order and swap a key's down and up.
    private var input: AsyncStream<@Sendable () async -> Void>.Continuation?
    /// Counterclockwise quarter turns the emulator reports the device is held at.
    private var deviceTurns = 0
    private var lastSurface: IOSurface?

    /// `cascade` offsets the window so several devices do not open on top of each other.
    /// `extraArguments` go to the emulator as they are, e.g. the ones Android Studio or Flutter passed.
    init(avd: Avd, grpcPort: Int, coldBoot: Bool, extraArguments: [String] = [], cascade: Int) throws {
        self.avd = avd
        self.grpcPort = grpcPort
        skin = try DeviceSkin.load(avd: avd)
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
        tasks.append(Task { [weak self] in
            try? await Task.sleep(for: Self.streamsClosing)
            self?.emulator?.interrupt()
            // Saving a snapshot has taken over 30 s here; only much later is the emulator considered stuck.
            try? await Task.sleep(for: .seconds(90))
            self?.emulator?.terminate()
        })
    }

    /// For app termination: interrupts every emulator first so their snapshots are saved in parallel.
    static func stopAll(_ sessions: [DeviceSession]) {
        sessions.forEach { $0.closeStreams() }
        if !sessions.isEmpty { Thread.sleep(forTimeInterval: Self.streamsClosing / .seconds(1)) }
        sessions.forEach { $0.emulator?.interrupt() }
        sessions.forEach { $0.emulator?.stop(timeout: 90) }
        sessions.forEach { $0.tearDown() }
    }

    /// How long the emulator gets to wind down this app's streams before it is asked to shut down.
    private static let streamsClosing = Duration.milliseconds(500)

    /// Ends the screen and clipboard streams. An emulator that shuts down while its streaming calls are
    /// still open sometimes crashes in its gRPC code (`bad_function_call`) before saving its snapshot.
    private func closeStreams() {
        streamTask?.cancel()
        streamTask = nil
        clipboard?.stop()
    }

    func run(_ command: DeviceCommand) {
        switch command {
        case let .key(key): enqueue { await $0.sendKey(key) }
        case let .action(action): perform(action)
        }
    }

    /// The latest frame, for the device manager's preview.
    func thumbnail() -> CGImage? {
        guard let lastSurface else { return nil }
        let image = CIImage(ioSurface: lastSurface)
        return CIContext(options: [.cacheIntermediates: false]).createCGImage(image, from: image.extent)
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
        deviceTurns = turns
        let shell = DeviceShellView(deviceView: view)
        let size = DeviceShellView.boxSize(device: view.size(forQuarterTurns: turns))
        shell.setFrameSize(size)
        let window = DeviceWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.borderless], backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = true
        window.contentView = shell
        window.title = avd.name
        window.isReleasedWhenClosed = false
        window.onMoved = { [weak self] in self?.windowMoved() }
        var frame = CGRect(origin: .zero, size: size)
        if let savedTopLeft {
            frame.origin = CGPoint(x: savedTopLeft.x, y: savedTopLeft.y - size.height)
        } else {
            let offset = CGFloat(cascade % 6) * 32
            frame.origin = CGPoint(x: (visible.midX - size.width / 2 + offset).rounded(),
                                   y: (visible.midY - size.height / 2 - offset).rounded())
        }
        window.setFrame(DeviceWindow.fitted(frame, on: screen), display: false)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(view)

        let chrome = DeviceChrome(container: shell, deviceName: avd.name, subtitle: subtitle(skin: skin))
        chrome.onKey = { [weak self] key, pressed in self?.sendKey(key, pressed: pressed) }
        chrome.onAction = { [weak self] action in self?.perform(action) }
        shell.chrome = chrome
        shell.onResize = { [weak self] phase in self?.resize(phase) }

        view.onTouch = { [weak self] point, pressed in
            Log.debug("touch \(pressed ? "down" : "up") at \(Int(point.x)),\(Int(point.y))")
            self?.enqueue { await $0.sendTouch(x: Int(point.x), y: Int(point.y), pressed: pressed) }
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
        let resolution = "\(Int(skin.display.width)) × \(Int(skin.display.height))"
        guard let folder = avd.sysdir?.split(separator: "/").first(where: { $0.hasPrefix("android-") }) else { return resolution }
        return "API \(folder.dropFirst("android-".count)) · \(resolution)"
    }

    private func makeContextMenu() -> NSMenu {
        let menu = NSMenu(title: "设备")
        for (index, group) in DeviceCommand.menuGroups.enumerated() {
            if index > 0 { menu.addItem(.separator()) }
            for (title, command, shortcut, modifiers) in group {
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
            target = fitScale(turns: deviceTurns, in: screen?.visibleFrame.size ?? window.frame.size)
        case .larger, .smaller:
            // In steps of 10 % of one-to-one pixels: 50 %, 60 %, 70 % …
            let pixel = pixelScale(on: screen)
            let percent = view.scale / pixel * 10
            let step = zoom == .larger ? (percent + 0.05).rounded(.down) + 1 : (percent - 0.05).rounded(.up) - 1
            target = max(step, 1) / 10 * pixel
        }
        let scale = min(max(target, minScale), fitScale(turns: deviceTurns, in: screen?.visibleFrame.size ?? window.frame.size))
        preferredScale = scale
        let box = DeviceShellView.boxSize(device: view.size(forQuarterTurns: deviceTurns, scale: scale))
        let old = window.frame
        let frame = CGRect(x: (old.midX - box.width / 2).rounded(), y: (old.maxY - box.height).rounded(), width: box.width, height: box.height)
        view.setLiveScale(scale)
        window.setFrame(DeviceWindow.fitted(frame, on: screen), display: true)
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
            let frame = window.frame
            resizing = (corner, CGPoint(x: corner.isLeft ? frame.maxX : frame.minX, y: corner.isTop ? frame.minY : frame.maxY))
        case let .changed(point):
            guard let (corner, anchor) = resizing else { return }
            let visible = screen?.visibleFrame ?? .infinite
            let skinSize = view.skinSize(forQuarterTurns: deviceTurns)
            let rooms = DeviceShellView.topRoom + DeviceShellView.bottomRoom
            // Follows whichever way the pointer went further.
            var scale = max((abs(point.y - anchor.y) - rooms) / skinSize.height, abs(point.x - anchor.x) / skinSize.width)
            // As large as fits between the fixed corner and the screen's edges.
            let room = CGSize(width: corner.isLeft ? anchor.x - visible.minX : visible.maxX - anchor.x,
                              height: corner.isTop ? visible.maxY - anchor.y : anchor.y - visible.minY)
            let largest = fitScale(turns: deviceTurns, in: room)
            scale = snapped(min(max(scale, minScale), max(largest, minScale)), largest: largest, on: screen)
            view.setLiveScale(scale)
            let box = DeviceShellView.boxSize(device: view.size(forQuarterTurns: deviceTurns, scale: scale))
            window.setFrame(CGRect(x: corner.isLeft ? anchor.x - box.width : anchor.x,
                                   y: corner.isTop ? anchor.y : anchor.y - box.height,
                                   width: box.width, height: box.height), display: true)
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
            let old = window.frame
            let frame = CGRect(x: old.minX, y: old.maxY - box.height, width: box.width, height: box.height)
            window.setFrame(DeviceWindow.fitted(frame, on: screen), display: true)
        }
        commitSize()
    }

    /// Once the size has settled: sharp layers at the new size, and frames streamed at its pixel size.
    private func commitSize() {
        guard let view = deviceView else { return }
        view.commitScale()
        shell?.needsLayout = true
        window?.invalidateShadow()
        savePlacement()
        let side = Int(max(view.displayPixelSize.width, view.displayPixelSize.height))
        if side != streamSide, streamTask != nil, let client {
            Log.debug("display is now \(side) px; requesting frames at that size")
            startStream(client: client)
        }
    }

    /// Snaps to the one-to-one, physical and fit sizes within a few points of the pointer.
    private func snapped(_ scale: CGFloat, largest: CGFloat, on screen: NSScreen?) -> CGFloat {
        let height = deviceView?.skinSize(forQuarterTurns: deviceTurns).height ?? 0
        let candidates = [pixelScale(on: screen), physicalScale(on: screen), largest].compactMap { $0 }
        return candidates.first { abs($0 - scale) * height < 8 && $0 <= largest && $0 >= minScale } ?? scale
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

    /// Device pixels per skin pixel: skins may draw the display at another size than the device's.
    private var devicePixelsPerSkinPixel: CGFloat {
        guard let width = avd.config["hw.lcd.width"].flatMap(Double.init), skin.display.width > 0 else { return 1 }
        return CGFloat(width) / skin.display.width
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

    /// Remembers the chosen size, the device's turn and where the window's top-left corner is, for the next launch.
    private func savePlacement() {
        guard let frame = window?.frame else { return }
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
        let emulator = EmulatorProcess(avdName: avd.id, grpcPort: grpcPort, gpu: gpu,
                                       extraArguments: (coldBoot ? ["-no-snapshot-load"] : []) + extraArguments)
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
        chrome?.controls = controls

        tasks.append(Task { [weak self] in
            do {
                try await client.waitUntilReachable(timeout: .seconds(120)) { booted in
                    Log.info("gRPC endpoint reachable, guest booted: \(booted)")
                }
                self?.startStream(client: client)
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
        input?.finish()
        clipboard?.stop()
        client?.shutdown()
        chrome?.close()
        window?.orderOut(nil)
        window?.close()
        sharedFrames = nil
        lastSurface = nil
        state = .stopped
    }

    /// (Re)opens the screen stream at the display's current pixel size; a new stream starts with the current frame.
    private func startStream(client: EmulatorClient) {
        let pixelSize = deviceView?.displayPixelSize ?? .zero
        // A square box: the emulator fits the upright image into it, so one stream serves both orientations.
        let side = Int(max(pixelSize.width, pixelSize.height))
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
            let surface = pool.write(frame)
            counter.tick(frame, shown: surface != nil)
            guard let surface else { return }
            let rotation = frame.rotation
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    self?.lastSurface = surface
                    self?.deviceView?.show(frame: surface, rotation: rotation)
                    self?.deviceTurned(to: rotation)
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
        case .screenshot: takeScreenshot()
        case .rotateLeft: rotate(by: 1)
        case .rotateRight: rotate(by: -1)
        }
    }

    // MARK: - Rotation

    /// Asks the emulator to turn the device; the shell follows once frames report the new orientation.
    private func rotate(by turns: Int) {
        let target = ((deviceTurns + turns) % 4 + 4) % 4
        let degrees: [Float] = [0, 90, 180, -90]
        Log.debug("rotate to \(degrees[target])°")
        enqueue { await $0.setRotation(degrees: degrees[target]) }
        // A screen that does not redraw (a portrait-only app) sends no frame to report the new orientation.
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
        guard turns != deviceTurns, let window, let view = deviceView else { return }
        deviceTurns = turns
        let screen = window.screen ?? NSScreen.main
        let scale = shownScale(turns: turns, on: screen)
        if scale != view.scale {
            view.setLiveScale(scale)
            view.commitScale()
        }
        let box = DeviceShellView.boxSize(device: view.size(forQuarterTurns: turns))
        let old = window.frame
        let frame = CGRect(x: (old.midX - box.width / 2).rounded(), y: (old.midY - box.height / 2).rounded(),
                           width: box.width, height: box.height)
        view.setQuarterTurns(turns, animated: true)
        window.setFrame(DeviceWindow.fitted(frame, on: screen), display: true, animate: true)
        shell?.needsLayout = true
        commitSize()
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
