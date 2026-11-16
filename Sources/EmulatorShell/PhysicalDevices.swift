import AppKit
import CoreImage
import CoreMedia
import Observation

/// A phone or tablet connected over USB or Wi-Fi, as the device manager lists it.
struct PhysicalDevice: Identifiable, Hashable {
    enum State: Hashable {
        /// Authorized and usable.
        case online
        /// Plugged in, waiting for "Allow USB debugging" on the device.
        case unauthorized
        /// adb is still setting up the connection.
        case connecting
        /// A paired Wi-Fi device that is not connected, or a USB device adb reports offline.
        case offline
        /// The Mac may not open the USB device (adb's "no permissions").
        case noPermissions
    }

    /// "device:" and the device's serial number (`ro.serialno`), or adb's serial until that is known.
    let id: String
    /// The adb serial to use: the USB one when the device is connected both ways; nil while offline.
    var serial: String?
    /// Every adb serial this device is connected by.
    var serials: [String]
    var state: State
    var isWireless: Bool
    var name: String
    var info: DeviceInfo?
    /// For a paired Wi-Fi device: where it was last reached, and when.
    var address: String?
    var lastSeen: Date?
    var pairedAt: Date?

    var isOnline: Bool { state == .online && serial != nil }
}

/// Physical devices: following adb's device list, reading each device's details, and the streams, mirror
/// windows and recordings that use scrcpy-server.
@MainActor
@Observable
final class PhysicalDeviceStore {
    struct Toast: Identifiable, Equatable {
        let id = UUID()
        let text: String
        var url: URL?
        var isError = false
    }

    private(set) var devices: [PhysicalDevice] = []
    private(set) var adbInstalled = Adb.isInstalled
    /// Installing platform-tools, 0...1.
    private(set) var installProgress: Double?
    /// When each device's recording started, by device id.
    private(set) var recordings: [String: Date] = [:]
    /// The latest picture of each device's screen, for the detail pane's preview.
    private(set) var thumbnails: [String: CGImage] = [:]
    /// Devices whose screen is turned off while mirroring.
    private(set) var screensOff: Set<String> = []
    /// Devices with a stream whose sound reaches the Mac.
    private(set) var audioDevices: Set<String> = []
    /// Devices whose stream reported that they refuse input from adb.
    private(set) var inputRefused: Set<String> = []
    /// A device being switched to Wi-Fi, reconnected or the like, by id.
    private(set) var busy: Set<String> = []
    var toast: Toast?
    /// Shown as the manager's alert.
    var onError: ((String) -> Void)?

    @ObservationIgnored private var adbDevices: [AdbDevice] = []
    @ObservationIgnored private var infos: [String: DeviceInfo] = [:]
    @ObservationIgnored private var fetching: Set<String> = []
    @ObservationIgnored private var tracker: AdbDeviceTracker?
    @ObservationIgnored private(set) var streams: [String: ScrcpyStream] = [:]
    @ObservationIgnored private(set) var sessions: [String: DeviceSession] = [:]
    @ObservationIgnored private var recorders: [String: RecorderBox] = [:]
    /// Times a stream has failed in a row, so a broken one is not restarted forever.
    @ObservationIgnored private var failures: [String: Int] = [:]
    /// Devices the Mac's clipboard is on its way to.
    private(set) var sendingClipboard: Set<String> = []
    @ObservationIgnored private var clipboardTimer: Timer?
    @ObservationIgnored private var pasteboardCount = NSPasteboard.general.changeCount

    private static let pairedKey = "pairedDevices"
    /// `EMULATOR_SHELL_MIRROR_EMULATORS=1` lists emulators here too (scrcpy works with them), for trying the
    /// physical-device features without a phone.
    private static let listsEmulators = ProcessInfo.processInfo.environment["EMULATOR_SHELL_MIRROR_EMULATORS"] == "1"

    init() {
        startTracking()
    }

    // MARK: - Device list

    private func startTracking() {
        adbInstalled = Adb.isInstalled
        guard adbInstalled, tracker == nil else { return }
        let tracker = AdbDeviceTracker { [weak self] devices in
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.adbChanged(devices) } }
        }
        self.tracker = tracker
        tracker.start()
        rebuild()
    }

    func stopTracking() {
        tracker?.stop()
        tracker = nil
    }

    private func adbChanged(_ list: [AdbDevice]) {
        adbDevices = list.filter { !$0.isEmulator || Self.listsEmulators }
        let online = Set(adbDevices.filter { $0.state == "device" }.map(\.serial))
        infos = infos.filter { online.contains($0.key) }
        for device in adbDevices where device.state == "device" && infos[device.serial] == nil {
            fetchInfo(device.serial)
        }
        rebuild()
    }

    private func fetchInfo(_ serial: String) {
        guard !fetching.contains(serial) else { return }
        fetching.insert(serial)
        Task {
            defer { fetching.remove(serial) }
            for attempt in 0..<3 {
                do {
                    let info = try await DeviceInfo.read(serial: serial)
                    guard adbDevices.contains(where: { $0.serial == serial && $0.state == "device" }) else { return }
                    infos[serial] = info
                    rebuild()
                    return
                } catch {
                    Log.error("cannot read \(serial)'s details (attempt \(attempt + 1)): \(error)")
                    try? await Task.sleep(for: .seconds(1))
                }
            }
        }
    }

    /// Reads a device's details again, e.g. its battery while its detail pane is shown.
    func refreshInfo(_ id: String) async {
        guard let serial = device(id)?.serial, let info = try? await DeviceInfo.read(serial: serial) else { return }
        infos[serial] = info
        rebuild()
    }

    func device(_ id: String) -> PhysicalDevice? {
        devices.first { $0.id == id }
    }

    /// One row per device: a device connected over both USB and Wi-Fi is the same serial number twice.
    private func rebuild() {
        // Only a Wi-Fi connection joins a USB one of the same serial number: some cheap devices share one
        // ("0123456789ABCDEF"), and two cables are two devices.
        var groups: [String: [AdbDevice]] = [:]
        var order: [String] = []
        var cabled: [String: String] = [:]
        for device in adbDevices.sorted(by: { !$0.isWireless && $1.isWireless }) {
            let serialNumber = infos[device.serial]?.serialNumber
            let key: String
            if !device.isWireless {
                key = device.serial
                if let serialNumber, cabled[serialNumber] == nil { cabled[serialNumber] = key }
            } else {
                key = serialNumber.flatMap { cabled[$0] } ?? serialNumber ?? device.serial
            }
            if groups[key] == nil { order.append(key) }
            groups[key, default: []].append(device)
        }
        var paired = pairedDevices
        var pairedChanged = false
        var list: [PhysicalDevice] = []
        for key in order {
            let members = groups[key] ?? []
            let usable = members.filter { $0.state == "device" }
            let primary = usable.first { !$0.isWireless } ?? usable.first ?? members[0]
            let info = infos[primary.serial]
            let state: PhysicalDevice.State
            switch primary.state {
            case "device": state = info == nil ? .connecting : .online
            case "unauthorized": state = .unauthorized
            case "authorizing", "connecting": state = .connecting
            case "no permissions": state = .noPermissions
            default: state = .offline
            }
            let name = info?.marketName ?? (state == .unauthorized ? "未授权的设备" : primary.model ?? primary.serial)
            var device = PhysicalDevice(id: "device:" + key, serial: primary.state == "device" ? primary.serial : nil,
                                        serials: members.map(\.serial), state: state, isWireless: primary.isWireless,
                                        name: name, info: info)
            if let wireless = usable.first(where: \.isWireless), let serialNumber = info?.serialNumber {
                // Remembered so the device stays listed while it is away, and can be reached again.
                var entry = paired[serialNumber] ?? [:]
                entry["name"] = name
                if wireless.serial.contains(":") { entry["address"] = wireless.serial }
                entry["lastSeen"] = Date().timeIntervalSince1970
                if entry["pairedAt"] == nil { entry["pairedAt"] = Date().timeIntervalSince1970 }
                paired[serialNumber] = entry
                pairedChanged = true
            }
            if let entry = info?.serialNumber.flatMap({ paired[$0] }) {
                device.address = entry["address"] as? String
                device.pairedAt = (entry["pairedAt"] as? Double).map(Date.init(timeIntervalSince1970:))
            }
            list.append(device)
        }
        let present = Set(list.compactMap { $0.info?.serialNumber } + order)
        for (serialNumber, entry) in paired where !present.contains(serialNumber) {
            list.append(PhysicalDevice(
                id: "device:" + serialNumber, serial: nil, serials: [], state: .offline, isWireless: true,
                name: entry["name"] as? String ?? serialNumber, info: nil, address: entry["address"] as? String,
                lastSeen: (entry["lastSeen"] as? Double).map(Date.init(timeIntervalSince1970:)),
                pairedAt: (entry["pairedAt"] as? Double).map(Date.init(timeIntervalSince1970:))))
        }
        if pairedChanged { pairedDevices = paired }
        let rank: (PhysicalDevice) -> Int = {
            switch $0.state {
            case .online: 0
            case .connecting, .unauthorized, .noPermissions: 1
            case .offline: 2
            }
        }
        list.sort { rank($0) != rank($1) ? rank($0) < rank($1) : $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        if list != devices { devices = list }
        resumeStreams()
    }

    private var pairedDevices: [String: [String: Any]] {
        get { UserDefaults.standard.dictionary(forKey: Self.pairedKey) as? [String: [String: Any]] ?? [:] }
        set { UserDefaults.standard.set(newValue, forKey: Self.pairedKey) }
    }

    // MARK: - Options

    func options(for id: String) -> ScrcpyStream.Options {
        var options = ScrcpyStream.Options()
        let saved = UserDefaults.standard.dictionary(forKey: "mirror.\(id)") ?? [:]
        if let value = saved["maxSize"] as? Int { options.maxSize = value }
        if let value = saved["bitRate"] as? Int { options.bitRate = value }
        if let value = saved["audio"] as? Bool { options.audio = value }
        if let value = saved["stayAwake"] as? Bool { options.stayAwake = value }
        if let value = saved["showTouches"] as? Bool { options.showTouches = value }
        if let value = saved["clipboardSync"] as? Bool { options.clipboardSync = value }
        // Sound needs Android 11; asking an older device only opens a socket it reports disabled on.
        if (device(id)?.info?.sdk ?? 0) < 30 { options.audio = false }
        return options
    }

    /// Saves the options and reconnects a running stream with them.
    func setOptions(_ options: ScrcpyStream.Options, for id: String) {
        UserDefaults.standard.set([
            "maxSize": options.maxSize, "bitRate": options.bitRate, "audio": options.audio,
            "stayAwake": options.stayAwake, "showTouches": options.showTouches, "clipboardSync": options.clipboardSync,
        ], forKey: "mirror.\(id)")
        updateClipboardTimer()
        guard let stream = streams[id] else { return }
        let effective = self.options(for: id)
        if effective != stream.options {
            screensOff.remove(id)
            stream.restart(with: effective)
        }
    }

    // MARK: - Streams

    /// The device's stream, started if it was not running.
    private func stream(for id: String) -> ScrcpyStream? {
        if let stream = streams[id] { return stream }
        guard let device = device(id), let serial = device.serial else { return nil }
        let stream = ScrcpyStream(serial: serial, options: options(for: id))
        stream.onStateChange = { [weak self] state in self?.streamStateChanged(id, state) }
        stream.onVideoSize = { [weak self] size in self?.sessions[id]?.physicalVideoSizeChanged(size) }
        stream.onAudioAvailable = { [weak self] available in
            guard let self else { return }
            if available { audioDevices.insert(id) } else { audioDevices.remove(id) }
            sessions[id]?.physicalAudioChanged(available)
        }
        stream.onClipboard = { [weak self] text in self?.deviceCopied(text, id: id) }
        stream.onInputRefused = { [weak self] in
            guard let self, inputRefused.insert(id).inserted else { return }
            sessions[id]?.physicalInputRefused()
        }
        streams[id] = stream
        stream.start()
        updateClipboardTimer()
        return stream
    }

    private func streamStateChanged(_ id: String, _ state: ScrcpyStream.State) {
        sessions[id]?.physicalStreamChanged(state)
        switch state {
        case .streaming:
            failures[id] = nil
        case let .ended(message):
            screensOff.remove(id)
            audioDevices.remove(id)
            if recorders[id] != nil {
                Task { await stopRecording(id, reason: "设备断开了") }
            }
            guard sessions[id] != nil else {
                releaseStream(id)
                return
            }
            // The window stays and waits for the device to come back; a stream that fails while the device is
            // still there is tried again a couple of times.
            let failed = failures[id, default: 0] + 1
            failures[id] = failed
            if message != nil, failed <= 2, device(id)?.isOnline == true {
                Task { [weak self] in
                    try? await Task.sleep(for: .seconds(Double(failed)))
                    guard let self, self.sessions[id] != nil, let stream = self.streams[id], case .ended = stream.state,
                          self.device(id)?.isOnline == true else { return }
                    stream.start()
                }
            }
        case .connecting:
            break
        }
    }

    /// A window waiting for its device starts streaming again once the device is back (USB re-plugged,
    /// Wi-Fi reached again), on whichever connection it came back by.
    private func resumeStreams() {
        for (id, session) in sessions {
            guard let device = device(id), let serial = device.serial, let stream = streams[id], case .ended = stream.state,
                  failures[id, default: 0] <= 2 || stream.serial != serial else { continue }
            if stream.serial != serial {
                stream.stop()
                streams[id] = nil
                guard let fresh = self.stream(for: id) else { continue }
                session.physicalStreamReplaced(fresh)
            } else {
                failures[id] = nil
                stream.start()
            }
        }
    }

    private func releaseStream(_ id: String) {
        guard sessions[id] == nil, recorders[id] == nil, let stream = streams[id] else { return }
        streams[id] = nil
        stream.onStateChange = nil
        stream.stop()
        screensOff.remove(id)
        audioDevices.remove(id)
        // A new stream finds out again: the setting may have been turned on meanwhile.
        inputRefused.remove(id)
        failures[id] = nil
        updateClipboardTimer()
    }

    // MARK: - Mirroring

    func mirror(_ id: String, cascade: Int) {
        if let session = sessions[id] {
            session.bringToFront()
            return
        }
        guard let device = device(id), device.isOnline, let info = device.info, let stream = stream(for: id) else { return }
        do {
            let session = try DeviceSession(physical: device, info: info, stream: stream, store: self, cascade: cascade)
            sessions[id] = session
            session.onStateChange = { [weak self] state in
                guard state == .stopped, let self else { return }
                self.sessions[id] = nil
                self.releaseStream(id)
            }
            session.setRecording(since: recordings[id])
        } catch {
            onError?("无法投屏“\(device.name)”：\(error.localizedDescription)")
            releaseStream(id)
        }
    }

    func isMirroring(_ id: String) -> Bool { sessions[id] != nil }

    func setScreenOff(_ off: Bool, for id: String) {
        guard let stream = streams[id], stream.state == .streaming else { return }
        stream.setDisplayPower(!off)
        if off { screensOff.insert(id) } else { screensOff.remove(id) }
    }

    // MARK: - Screenshots

    /// Saves the screen to the Desktop at the display's resolution.
    func screenshot(_ id: String, completion: ((Result<URL, Error>) -> Void)? = nil) {
        guard let device = device(id), let serial = device.serial else { return }
        let url = ScreenRecorder.desktopURL(name: device.name, extension: "png")
        let frame = streams[id].flatMap { $0.state == .streaming ? $0.lastFrame : nil }
        Task {
            do {
                // `screencap` gives the display's own resolution; the stream's frame, which may be scaled
                // down, only if that fails.
                let png: Data
                do {
                    png = try await Adb.screenshotPNG(serial: serial)
                } catch {
                    guard let frame, let data = Self.png(of: frame) else { throw error }
                    png = data
                }
                // An app that forbids screenshots (FLAG_SECURE) comes through black.
                if let image = NSBitmapImageRep(data: png)?.cgImage, Self.isBlack(image) {
                    throw ShellError("画面是全黑的：当前应用可能不允许截屏")
                }
                try png.write(to: url)
                Log.info("screenshot saved to \(url.path)")
                if completion == nil { toast = Toast(text: "截图已存到桌面", url: url) }
                completion?(.success(url))
            } catch {
                Log.error("screenshot failed: \(error)")
                if completion == nil { toast = Toast(text: "截图失败：\(error.localizedDescription)", isError: true) }
                completion?(.failure(error))
            }
        }
    }

    /// Core Image contexts are safe to use from any thread.
    nonisolated private static let imageContext = CIContext()

    nonisolated static func png(of frame: CVPixelBuffer) -> Data? {
        let image = CIImage(cvPixelBuffer: frame)
        return imageContext.pngRepresentation(of: image, format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!)
    }

    nonisolated static func cgImage(of frame: CVPixelBuffer) -> CGImage? {
        let image = CIImage(cvPixelBuffer: frame)
        return imageContext.createCGImage(image, from: image.extent)
    }

    /// Whether every pixel of a 16 × 16 reduction is all but black.
    nonisolated static func isBlack(_ image: CGImage) -> Bool {
        var pixels = [UInt8](repeating: 0, count: 16 * 16 * 4)
        guard let context = CGContext(data: &pixels, width: 16, height: 16, bitsPerComponent: 8, bytesPerRow: 64,
                                      space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return false }
        context.draw(image, in: CGRect(x: 0, y: 0, width: 16, height: 16))
        return stride(from: 0, to: pixels.count, by: 4).allSatisfy { max(pixels[$0], pixels[$0 + 1], pixels[$0 + 2]) < 6 }
    }

    // MARK: - Recording

    func isRecording(_ id: String) -> Bool { recordings[id] != nil }

    func toggleRecording(_ id: String) {
        if recordings[id] != nil {
            Task { await stopRecording(id, reason: nil) }
        } else {
            startRecording(id)
        }
    }

    /// Records the stream, starting one (without a window) if none runs. The file is made with the first
    /// frame, whose size it keeps.
    private func startRecording(_ id: String) {
        guard let device = device(id), let stream = stream(for: id) else { return }
        let box = RecorderBox(url: ScreenRecorder.desktopURL(name: device.name, extension: "mp4"), withAudio: stream.options.audio)
        recorders[id] = box
        recordings[id] = Date()
        sessions[id]?.setRecording(since: recordings[id])
        let key = "recorder"
        stream.addFrameSink(key) { frame, time in box.append(frame, at: time) }
        stream.addAudioSink(key) { samples, time in box.appendAudio(samples, at: time) }
        // A frame comes only when the screen changes; the one shown now starts the file at once.
        if let frame = stream.lastFrame, stream.state == .streaming { box.append(frame, at: ScreenRecorder.now) }
    }

    private func stopRecording(_ id: String, reason: String?) async {
        guard let box = recorders.removeValue(forKey: id) else { return }
        streams[id]?.removeSinks("recorder")
        recordings[id] = nil
        sessions[id]?.setRecording(since: nil)
        releaseStream(id)
        let url = box.url
        let saved = await box.finish()
        let text = saved ? (reason.map { "\($0)，录屏已存到桌面" } ?? "录屏已存到桌面") : "录屏没有内容，没有保存"
        if let session = sessions[id] {
            session.showToast(text, url: saved ? url : nil)
        } else {
            toast = Toast(text: text, url: saved ? url : nil, isError: !saved)
        }
    }

    // MARK: - Preview

    /// Keeps the device's preview current while its detail pane is shown: the stream's frames if one runs,
    /// otherwise a `screencap` every couple of seconds. Ends when the calling task is cancelled.
    func followPreview(_ id: String) async {
        while !Task.isCancelled {
            if let stream = streams[id], stream.state == .streaming, let frame = stream.lastFrame {
                if let image = Self.cgImage(of: frame) { thumbnails[id] = image }
                try? await Task.sleep(for: .seconds(1))
            } else if let serial = device(id)?.serial {
                if let png = try? await Adb.screenshotPNG(serial: serial), let image = NSBitmapImageRep(data: png)?.cgImage {
                    thumbnails[id] = image
                }
                try? await Task.sleep(for: .seconds(2))
            } else {
                try? await Task.sleep(for: .seconds(2))
            }
        }
    }

    // MARK: - Clipboard

    /// The device copied something: the Mac's clipboard gets it.
    private func deviceCopied(_ text: String, id: String) {
        guard options(for: id).clipboardSync, NSPasteboard.general.string(forType: .string) != text else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        pasteboardCount = NSPasteboard.general.changeCount
    }

    /// Puts `text` on the device's clipboard, to paste there; `completion` gets an error message or nil, and
    /// without it a toast says how it went. The device's stream carries it if one runs; otherwise a short stream
    /// of its own does, without sound and without turning the screen on.
    func sendToClipboard(_ text: String, id: String, completion: ((String?) -> Void)? = nil) {
        let report: (String?) -> Void = { [weak self] error in
            if let error { Log.error("sending the clipboard failed: \(error)") } else { Log.info("clipboard sent to \(id)") }
            if let completion { return completion(error) }
            self?.toast = error.map { Toast(text: $0, isError: true) } ?? Toast(text: "已添加到设备的剪贴板，在设备上长按输入框粘贴")
        }
        guard !text.isEmpty, let serial = device(id)?.serial, !sendingClipboard.contains(id) else { return }
        sendingClipboard.insert(id)
        let sending = ClipboardSending { [weak self] error in
            self?.sendingClipboard.remove(id)
            report(error)
        }
        Task {
            try? await Task.sleep(for: .seconds(15))
            sending.finish("设备没有响应")
        }
        if let stream = streams[id], stream.state == .streaming {
            stream.setClipboard(text) { sending.finish(nil) }
            return
        }
        let carrier = ScrcpyStream(serial: serial, options: .init(maxSize: 480, bitRate: 1_000_000, audio: false, stayAwake: false,
                                                                showTouches: false, clipboardSync: false, powerOn: false))
        sending.onFinish = { carrier.onStateChange = nil; carrier.stop() }
        carrier.onStateChange = { [weak carrier] state in
            switch state {
            case .streaming: carrier?.setClipboard(text) { sending.finish(nil) }
            case let .ended(message): sending.finish(message ?? "连接断开了")
            case .connecting: break
            }
        }
        carrier.start()
    }

    /// While a stream with clipboard sync runs, what is copied on the Mac goes to the device.
    private func updateClipboardTimer() {
        let wanted = streams.keys.contains { options(for: $0).clipboardSync }
        if wanted, clipboardTimer == nil {
            pasteboardCount = NSPasteboard.general.changeCount
            clipboardTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.pollPasteboard() }
            }
        } else if !wanted {
            clipboardTimer?.invalidate()
            clipboardTimer = nil
        }
    }

    private func pollPasteboard() {
        let count = NSPasteboard.general.changeCount
        guard count != pasteboardCount else { return }
        pasteboardCount = count
        guard let text = NSPasteboard.general.string(forType: .string) else { return }
        for (id, stream) in streams where stream.state == .streaming && options(for: id).clipboardSync {
            stream.setClipboard(text, paste: false)
        }
    }

    // MARK: - Connections

    private func run(_ id: String, _ what: String, _ body: @escaping (PhysicalDevice) async throws -> Void) {
        guard let device = device(id), !busy.contains(id) else { return }
        busy.insert(id)
        Task {
            defer { busy.remove(id) }
            do { try await body(device) } catch { onError?("\(what)没有完成：\(error.localizedDescription)") }
        }
    }

    /// `adb tcpip` and `adb connect`: the device can then be unplugged.
    func switchToWireless(_ id: String) {
        run(id, "切换为无线连接") { device in
            guard let serial = device.serial else { return }
            let address = try await Adb.switchToWireless(serial: serial)
            self.toast = Toast(text: "已通过 Wi-Fi 连接（\(address)），可以拔掉数据线了")
        }
    }

    func reconnect(_ id: String) {
        run(id, "重新连接") { device in
            if let address = device.address, device.serial == nil || device.isWireless {
                try await Adb.connect(address)
            } else if let serial = device.serial ?? device.serials.first {
                _ = try await Adb.text(["reconnect"], serial: serial, timeout: .seconds(10))
            }
        }
    }

    func disconnect(_ id: String) {
        run(id, "断开连接") { device in
            for serial in device.serials where serial.contains(":") { try await Adb.disconnect(serial) }
        }
    }

    /// Forgets a paired Wi-Fi device: it is disconnected and no longer listed while away.
    func forget(_ id: String) {
        guard let device = device(id) else { return }
        var paired = pairedDevices
        if let serialNumber = device.info?.serialNumber ?? (device.serial == nil ? String(id.dropFirst("device:".count)) : nil) {
            paired[serialNumber] = nil
        }
        pairedDevices = paired
        if device.serials.contains(where: { $0.contains(":") }) { disconnect(id) }
        rebuild()
    }

    func reboot(_ id: String) {
        run(id, "重启设备") { device in
            guard let serial = device.serial else { return }
            _ = try await Adb.text(["reboot"], serial: serial, timeout: .seconds(15))
        }
    }

    /// Installs APKs and copies other files to the device's Download folder.
    func send(_ urls: [URL], to id: String) {
        guard let serial = device(id)?.serial else { return }
        Task {
            var installed = 0, copied = 0
            var failures: [String] = []
            for url in urls {
                do {
                    if url.pathExtension.lowercased() == "apk" {
                        _ = try await Adb.text(["install", "-r", url.path], serial: serial, timeout: .seconds(300))
                        installed += 1
                    } else {
                        _ = try await Adb.text(["push", url.path, "/sdcard/Download/"], serial: serial, timeout: .seconds(600))
                        copied += 1
                    }
                } catch {
                    failures.append("\(url.lastPathComponent)：\(error.localizedDescription)")
                }
            }
            var parts: [String] = []
            if installed > 0 { parts.append("已安装 \(installed) 个应用") }
            if copied > 0 { parts.append("\(copied) 个文件已存到“下载”文件夹") }
            let text = parts.isEmpty ? "没有完成" : parts.joined(separator: "，")
            if let session = sessions[id] { session.showToast(text, url: nil) } else { toast = Toast(text: text) }
            if !failures.isEmpty { onError?(failures.joined(separator: "\n")) }
        }
    }

    /// `sdkmanager --install platform-tools`, for an SDK without adb.
    func installPlatformTools() {
        guard installProgress == nil else { return }
        installProgress = 0
        Task {
            defer { installProgress = nil }
            do {
                _ = try await Sdk.run("sdkmanager", ["--install", "platform-tools"], input: String(repeating: "y\n", count: 4)) { line in
                    guard let range = line.range(of: #"[0-9]+(?=%)"#, options: .regularExpression), let percent = Double(line[range]) else { return }
                    Task { @MainActor in self.installProgress = percent / 100 }
                }
            } catch {
                onError?("无法安装 Platform-Tools：\(error.localizedDescription)")
            }
            startTracking()
        }
    }

    // MARK: - Quitting

    var hasWork: Bool { !sessions.isEmpty || !recorders.isEmpty }

    /// Finishes every recording and closes the streams; `done` runs once the files are complete.
    func stopAll(then done: @escaping () -> Void) {
        let ids = Array(recorders.keys)
        sessions.values.forEach { $0.close() }
        Task {
            for id in ids { await stopRecording(id, reason: nil) }
            for id in Array(streams.keys) {
                streams[id]?.stop()
                streams[id] = nil
            }
            stopTracking()
            done()
        }
    }
}

/// A recording whose file is made once the first frame gives its size; fed from the stream's threads.
final class RecorderBox: @unchecked Sendable {
    let url: URL
    private let withAudio: Bool
    private let lock = NSLock()
    private var recorder: ScreenRecorder?
    private var failed = false

    init(url: URL, withAudio: Bool) {
        self.url = url
        self.withAudio = withAudio
    }

    func append(_ frame: CVPixelBuffer, at time: CMTime) {
        lock.lock()
        if recorder == nil, !failed {
            do {
                recorder = try ScreenRecorder(url: url, size: CGSize(width: CVPixelBufferGetWidth(frame), height: CVPixelBufferGetHeight(frame)),
                                              withAudio: withAudio)
            } catch {
                failed = true
                Log.error("cannot start recording: \(error)")
            }
        }
        let recorder = self.recorder
        lock.unlock()
        recorder?.append(frame, at: time)
    }

    func appendAudio(_ samples: Data, at time: CMTime) {
        lock.lock()
        let recorder = self.recorder
        lock.unlock()
        recorder?.appendAudio(samples, at: time)
    }

    func finish() async -> Bool {
        let recorder = lock.withLock {
            failed = true
            return self.recorder
        }
        return await recorder?.finish(at: ScreenRecorder.now) ?? false
    }
}

/// One sending of the clipboard, which ends once: acknowledged, failed or timed out.
@MainActor
private final class ClipboardSending {
    private var report: ((String?) -> Void)?
    /// Run as it ends, before the report: closing a stream opened for it.
    var onFinish: (() -> Void)?

    init(report: @escaping (String?) -> Void) { self.report = report }

    func finish(_ error: String?) {
        guard let report else { return }
        self.report = nil
        onFinish?()
        onFinish = nil
        report(error)
    }
}
