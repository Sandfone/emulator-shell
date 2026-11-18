import Foundation
import GRPCCore
import GRPCNIOTransportHTTP2
import SwiftProtobuf

typealias Controller = Android_Emulation_Control_EmulatorController.Client<HTTP2ClientTransport.Posix>
typealias BatteryCharger = Android_Emulation_Control_BatteryState.BatteryCharger
typealias PhoneOperation = Android_Emulation_Control_PhoneCall.Operation
typealias PostureValue = Android_Emulation_Control_Posture.PostureValue
typealias SnapshotDetails = Android_Emulation_Control_SnapshotDetails

extension Task where Success == Never, Failure == Never {
    /// `Task.sleep(for:)` for this app's own code, which must not call that one: the compiler emits a
    /// specialized `Clock.sleep(for:tolerance:)` into every module that does, under one symbol name but with
    /// async frames of different sizes (128 bytes here, 112 in grpc-swift-nio-transport, on x86_64), and the
    /// linker keeps one. gRPC's reconnect backoff then ran this app's copy in a frame sized for its own and
    /// aborted in `swift_task_dealloc` ("freed pointer was not the last allocation") when the sleep returned,
    /// about a second after an emulator went away. `scripts/check-async-symbols.py` looks for such symbols.
    static func pause(for duration: Duration) async throws {
        let (seconds, attoseconds) = duration.components
        try await sleep(nanoseconds: UInt64(max(0, seconds)) * 1_000_000_000 + UInt64(max(0, attoseconds)) / 1_000_000_000)
    }
}

/// Adds `authorization: Bearer <token>` to every call.
private struct BearerToken: ClientInterceptor {
    let token: String

    func intercept<Input: Sendable, Output: Sendable>(
        request: StreamingClientRequest<Input>,
        context: ClientContext,
        next: (StreamingClientRequest<Input>, ClientContext) async throws -> StreamingClientResponse<Output>
    ) async throws -> StreamingClientResponse<Output> {
        var request = request
        request.metadata.replaceOrAddString("Bearer \(token)", forKey: "authorization")
        return try await next(request, context)
    }
}

/// Thin wrapper over the emulator's gRPC services: `EmulatorController` and `SnapshotService`.
final class EmulatorClient: Sendable {
    private static let maxFrameBytes = 64 * 1024 * 1024

    private let port: Int
    private let grpc: GRPCClient<HTTP2ClientTransport.Posix>
    private let controller: Controller
    private let snapshotService: Android_Emulation_Control_SnapshotService.Client<HTTP2ClientTransport.Posix>

    /// `token` is the bearer token an emulator started with `-grpc-use-token` (as Android Studio does) wants
    /// on every call; it writes it into its `pid_*.ini` as `grpc.token`.
    init(port: Int, token: String? = nil) throws {
        self.port = port
        let transport = try HTTP2ClientTransport.Posix(
            target: .ipv4(address: "127.0.0.1", port: port),
            transportSecurity: .plaintext
        )
        grpc = GRPCClient(transport: transport, interceptors: token.map { [BearerToken(token: $0)] } ?? [])
        controller = Controller(wrapping: grpc)
        snapshotService = .init(wrapping: grpc)
    }

    /// Drives the underlying connections; returns when `shutdown()` is called.
    func run() async throws {
        try await grpc.runConnections()
    }

    func shutdown() {
        grpc.beginGracefulShutdown()
    }

    /// Waits until the emulator accepts connections on its gRPC port, which `run()` should wait for, so that
    /// gRPC does not spend the emulator's start failing to connect and backing off.
    func waitUntilListening(timeout: Duration) async throws {
        let deadline = ContinuousClock.now + timeout
        while ContinuousClock.now < deadline {
            if Self.isListening(port: port) { return }
            try await Task.pause(for: .milliseconds(200))
        }
        throw ShellError("emulator did not open its gRPC port \(port) within \(timeout)")
    }

    /// Whether a TCP connection to `port` on this Mac is accepted.
    private static func isListening(port: Int) -> Bool {
        let descriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard descriptor >= 0 else { return false }
        defer { close(descriptor) }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(port).bigEndian
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        return withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
    }

    /// Polls `getStatus` until the gRPC endpoint answers, then until the guest reports booted.
    func waitUntilReachable(timeout: Duration, onStatus: @Sendable (Bool) -> Void) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now + timeout
        while clock.now < deadline {
            if let status = try? await controller.getStatus(Google_Protobuf_Empty()) {
                onStatus(status.booted)
                return
            }
            try await Task.pause(for: .milliseconds(500))
        }
        throw ShellError("emulator gRPC endpoint did not answer within \(timeout)")
    }

    /// Streams display frames scaled to `width`×`height` pixels until cancelled. With `shared`, the emulator
    /// writes pixels into that region and each message only announces a new frame; otherwise pixels ride in the message.
    func streamScreen(
        width: Int,
        height: Int,
        shared: SharedFrameBuffer?,
        onFrame: @escaping @Sendable (RawFrame) -> Void
    ) async throws {
        var format = Android_Emulation_Control_ImageFormat()
        format.format = .rgba8888
        format.width = UInt32(width)
        format.height = UInt32(height)
        if let shared {
            format.transport.channel = .mmap
            format.transport.handle = shared.handle
        }

        var options = CallOptions.defaults
        options.maxResponseMessageBytes = Self.maxFrameBytes
        // grpc-swift-nio-transport sizes the stream's decoder from the *request* limit (default 4 MiB),
        // which a single RGBA frame already exceeds.
        options.maxRequestMessageBytes = Self.maxFrameBytes

        try await controller.streamScreenshot(format, options: options) { response in
            for try await image in response.messages {
                let width = Int(image.format.width)
                let height = Int(image.format.height)
                let byteCount = width * height * 4
                guard width > 0, height > 0 else { continue }
                func frame(_ pixels: UnsafeRawPointer) -> RawFrame {
                    RawFrame(
                        width: width,
                        height: height,
                        seq: image.seq,
                        timestampUs: image.timestampUs,
                        rotation: image.format.rotation.rotation.rawValue,
                        folded: image.format.hasFoldedDisplay,
                        pixels: pixels
                    )
                }
                if let shared {
                    guard byteCount <= shared.size else { continue }
                    onFrame(frame(shared.base))
                } else {
                    guard image.image.count >= byteCount else { continue }
                    image.image.withUnsafeBytes { onFrame(frame($0.baseAddress!)) }
                }
            }
        }
    }

    func sendTouch(x: Int, y: Int, pressed: Bool) async {
        var touch = Android_Emulation_Control_Touch()
        touch.x = Int32(x)
        touch.y = Int32(y)
        touch.identifier = 0
        touch.pressure = pressed ? 1 : 0
        var event = Android_Emulation_Control_TouchEvent()
        event.touches = [touch]
        await send { try await $0.sendTouch(event) }
    }

    /// `key` is a W3C `KeyboardEvent.key` value, e.g. "a", "Enter", "GoBack", "GoHome", "Power".
    /// Sending `keydown` and `keyup` separately lets Android see real long presses (power menu, volume repeat).
    func sendKey(_ key: String, type: Android_Emulation_Control_KeyboardEvent.KeyEventType = .keypress) async {
        var event = Android_Emulation_Control_KeyboardEvent()
        event.eventType = type
        event.key = key
        await send { try await $0.sendKey(event) }
    }

    func sendText(_ text: String) async {
        var event = Android_Emulation_Control_KeyboardEvent()
        event.text = text
        await send { try await $0.sendKey(event) }
    }

    /// Turns the virtual device about the axis through the screen; the emulator reports the resulting
    /// orientation in each frame's `format.rotation`.
    func setRotation(degrees: Float) async {
        var model = Android_Emulation_Control_PhysicalModelValue()
        model.target = .rotation
        model.value.data = [0, 0, degrees]
        // The default SMOOTH glide leaves frames sent at its start reporting the old orientation.
        model.interpolation = .step
        await send { try await $0.setPhysicalModel(model) }
    }

    /// Full-resolution PNG of the display, upright as the device is currently held.
    func screenshotPNG() async throws -> Data {
        var format = Android_Emulation_Control_ImageFormat()
        format.format = .png
        var options = CallOptions.defaults
        options.maxResponseMessageBytes = Self.maxFrameBytes
        options.maxRequestMessageBytes = Self.maxFrameBytes
        return try await controller.getScreenshot(format, options: options).image
    }

    func isBooted() async -> Bool {
        (try? await controller.getStatus(Google_Protobuf_Empty()).booted) ?? false
    }

    func setClipboard(_ text: String) async {
        var clip = Android_Emulation_Control_ClipData()
        clip.text = text
        await send { try await $0.setClipboard(clip) }
    }

    /// Delivers the guest clipboard now and on every change made inside the guest, until cancelled.
    func streamClipboard(onText: @escaping @Sendable (String) -> Void) async throws {
        try await controller.streamClipboard(Google_Protobuf_Empty()) { response in
            for try await clip in response.messages {
                onText(clip.text)
            }
        }
    }

    /// Delivers the foldable posture now and on every change, whoever made it, until cancelled.
    func streamPosture(onPosture: @escaping @Sendable (PostureValue) -> Void) async throws {
        try await controller.streamNotification(Google_Protobuf_Empty()) { response in
            for try await notification in response.messages {
                if case let .posture(posture)? = notification.type { onPosture(posture.value) }
            }
        }
    }

    /// Delivers the device's sound, 16-bit stereo samples at `sampleRate`, while it plays, until cancelled.
    func streamAudio(sampleRate: Int, onSamples: @escaping @Sendable (Data) -> Void) async throws {
        var format = Android_Emulation_Control_AudioFormat()
        format.samplingRate = UInt64(sampleRate)
        format.channels = .stereo
        format.format = .audFmtS16
        try await controller.streamAudio(format) { response in
            for try await packet in response.messages {
                onSamples(packet.audio)
            }
        }
    }

    // MARK: - Device state, for the "More" panel. These throw so the panel can say what went wrong.

    func location() async throws -> (latitude: Double, longitude: Double) {
        let gps = try await controller.getGps(Google_Protobuf_Empty())
        return (gps.latitude, gps.longitude)
    }

    func setLocation(latitude: Double, longitude: Double) async throws {
        var gps = Android_Emulation_Control_GpsState()
        gps.latitude = latitude
        gps.longitude = longitude
        gps.satellites = 12
        gps.passiveUpdate = true
        _ = try await controller.setGps(gps)
    }

    func battery() async throws -> Android_Emulation_Control_BatteryState {
        try await controller.getBattery(Google_Protobuf_Empty())
    }

    func setBattery(level: Int, charger: BatteryCharger) async throws {
        var state = Android_Emulation_Control_BatteryState()
        state.hasBattery_p = true
        state.isPresent = true
        state.chargeLevel = Int32(level)
        state.charger = charger
        state.health = .good
        state.status = charger == .none ? .discharging : (level >= 100 ? .full : .charging)
        _ = try await controller.setBattery(state)
    }

    /// Simulates the network side of a call: an incoming call from `number`, hanging up, holding.
    func phone(_ operation: PhoneOperation, number: String) async throws {
        var call = Android_Emulation_Control_PhoneCall()
        call.operation = operation
        call.number = number
        try Self.check(try await controller.sendPhone(call))
    }

    func receiveSms(from number: String, text: String) async throws {
        var message = Android_Emulation_Control_SmsMessage()
        message.srcAddress = number
        message.text = text
        try Self.check(try await controller.sendSms(message))
    }

    private static func check(_ response: Android_Emulation_Control_PhoneResponse) throws {
        let reasons: [Android_Emulation_Control_PhoneResponse.Response: String] = [
            .badNumber: "号码格式不对", .invalidAction: "当前没有可操作的通话", .actionFailed: "模拟器内部出错", .radioOff: "设备的无线电已关闭（飞行模式？）",
        ]
        guard response.response == .ok else { throw ShellError(reasons[response.response] ?? "电话操作失败（\(response.response)）") }
    }

    func touchFingerprint(_ touching: Bool, finger: Int) async throws {
        var fingerprint = Android_Emulation_Control_Fingerprint()
        fingerprint.isTouching = touching
        fingerprint.touchID = Int32(finger)
        _ = try await controller.sendFingerprint(fingerprint)
    }

    /// Whether the guest hears the Mac's microphone.
    func usesHostMicrophone() async throws -> Bool {
        try await controller.getMicrophoneState(Google_Protobuf_Empty()).realAudioEnabled
    }

    func setUsesHostMicrophone(_ enabled: Bool) async throws {
        var state = Android_Emulation_Control_MicrophoneState()
        state.realAudioEnabled = enabled
        _ = try await controller.setMicrophoneState(state)
    }

    /// Screen backlight, 0...255.
    func brightness() async throws -> Int {
        var request = Android_Emulation_Control_BrightnessValue()
        request.target = .lcd
        return Int(try await controller.getBrightness(request).value)
    }

    func setBrightness(_ value: Int) async throws {
        var request = Android_Emulation_Control_BrightnessValue()
        request.target = .lcd
        request.value = UInt32(max(0, min(255, value)))
        _ = try await controller.setBrightness(request)
    }

    func setPosture(_ value: PostureValue) async throws {
        var posture = Android_Emulation_Control_Posture()
        posture.value = value
        _ = try await controller.setPosture(posture)
    }

    func snapshots() async throws -> [SnapshotDetails] {
        var filter = Android_Emulation_Control_SnapshotFilter()
        filter.statusFilter = .all
        return try await snapshotService.listSnapshots(filter).snapshots
    }

    func saveSnapshot(_ id: String) async throws {
        try Self.check(try await snapshotService.saveSnapshot(Self.package(id)), action: "保存")
    }

    func loadSnapshot(_ id: String) async throws {
        try Self.check(try await snapshotService.loadSnapshot(Self.package(id)), action: "加载")
    }

    func deleteSnapshot(_ id: String) async throws {
        try Self.check(try await snapshotService.deleteSnapshot(Self.package(id)), action: "删除")
    }

    private static func package(_ id: String) -> Android_Emulation_Control_SnapshotPackage {
        var package = Android_Emulation_Control_SnapshotPackage()
        package.snapshotID = id
        return package
    }

    private static func check(_ result: Android_Emulation_Control_SnapshotPackage, action: String) throws {
        guard result.success else {
            let reason = String(decoding: result.err, as: UTF8.self)
            throw ShellError("快照\(action)失败" + (reason.isEmpty ? "" : "：\(reason)"))
        }
    }

    private func send(_ call: (Controller) async throws -> Void) async {
        do {
            try await call(controller)
        } catch {
            Log.error("gRPC input call failed: \(error)")
        }
    }
}

enum Log {
    static func info(_ message: String) {
        write("[emulator-shell] \(message)\n")
    }

    static let isDebug = ProcessInfo.processInfo.environment["EMULATOR_SHELL_DEBUG"] == "1"

    static func debug(_ message: String) {
        if isDebug { info(message) }
    }

    static func error(_ message: String) {
        write("[emulator-shell] ERROR \(message)\n")
        keep(message)
    }

    /// Errors also go to `~/Library/Logs/EmulatorShell.log` (beside the forwarder's log, and shown by Console):
    /// the app usually runs without anyone reading its output. Past 1 MB it starts over, the previous file kept
    /// as `EmulatorShell.old.log`.
    static let fileURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/EmulatorShell.log")
    private static let fileLock = NSLock()
    nonisolated(unsafe) private static let fileDateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return formatter
    }()

    private static func keep(_ message: String) {
        fileLock.lock()
        defer { fileLock.unlock() }
        let manager = FileManager.default
        try? manager.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let size = (try? manager.attributesOfItem(atPath: fileURL.path))?[.size] as? Int, size > 1024 * 1024 {
            let old = fileURL.deletingLastPathComponent().appendingPathComponent("EmulatorShell.old.log")
            try? manager.removeItem(at: old)
            try? manager.moveItem(at: fileURL, to: old)
        }
        let line = "\(fileDateFormatter.string(from: Date())) [\(ProcessInfo.processInfo.processIdentifier)] \(message)\n"
        if let handle = try? FileHandle(forWritingTo: fileURL) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: Data(line.utf8))
        } else {
            try? Data(line.utf8).write(to: fileURL)
        }
    }

    /// Launchers such as Flutter close the pipe they read this output from once the device is up. The
    /// non-throwing `write(_:)` raises an Objective-C exception then, which aborts the app; this one fails quietly.
    private static func write(_ line: String) {
        try? FileHandle.standardError.write(contentsOf: Data(line.utf8))
    }
}
