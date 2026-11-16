import AppKit
import CoreMedia
import VideoToolbox

/// A physical device's screen, sound and input through scrcpy-server (`vendor/scrcpy`), which runs on the
/// device with `app_process` as the shell user: no app is installed.
///
/// One adb forward tunnel carries three sockets, connected in this order: video (H.264 from the device's
/// encoder, decoded here with VideoToolbox), audio (raw 48 kHz 16-bit stereo, Android 11+) and control
/// (input from here, the device's clipboard back). All integers on them are big-endian. The protocol is
/// scrcpy 5.0.1's; the server refuses a client of another version.
///
/// The stream outlives its connections: `restart(with:)` reconnects with other options, and the sinks
/// (a device window, a recording) keep receiving.
@MainActor
final class ScrcpyStream {
    nonisolated static let serverVersion = "5.0.1"

    struct Options: Equatable {
        /// The video's long side at most, in pixels; 0 for the device's own size.
        var maxSize = 1920
        var bitRate = 8_000_000
        var audio = true
        var stayAwake = true
        var showTouches = false
        var clipboardSync = true
        /// Turns the screen on as the stream starts.
        var powerOn = true
    }

    enum State: Equatable {
        case connecting, streaming, ended(String?)
    }

    let serial: String
    private(set) var options: Options
    private(set) var state: State = .connecting {
        didSet { onStateChange?(state) }
    }
    var onStateChange: ((State) -> Void)?
    /// The video's size, as the latest session reported it; touch positions are given in it.
    private(set) var videoSize: CGSize = .zero
    var onVideoSize: ((CGSize) -> Void)?
    /// The device's clipboard changed (with clipboard sync on).
    var onClipboard: ((String) -> Void)?
    /// Whether the device sends its sound: Android 11 and later, with `options.audio`.
    private(set) var hasAudio = false
    var onAudioAvailable: ((Bool) -> Void)?
    /// The device refused an input event from adb (MIUI and HyperOS until "USB 调试（安全设置）" is on).
    var onInputRefused: (() -> Void)?

    private var connection: ScrcpyConnection?
    private let sinks = ScrcpySinks()
    /// What to call when the device acknowledges a clipboard, by the sequence number sent with it.
    private var clipboardAcks: [UInt64: () -> Void] = [:]
    private var clipboardSequence: UInt64 = 0

    init(serial: String, options: Options) {
        self.serial = serial
        self.options = options
    }

    func start() {
        guard connection == nil else { return }
        state = .connecting
        let connection = ScrcpyConnection(serial: serial, options: options, sinks: sinks)
        connection.onEvent = { [weak self, weak connection] event in
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    guard let self, let connection, self.connection === connection else { return }
                    self.handle(event)
                }
            }
        }
        self.connection = connection
        connection.start()
    }

    func stop() {
        connection?.stop()
        connection = nil
        if case .ended = state {} else { state = .ended(nil) }
    }

    /// Reconnects with other options (a moment without frames); sinks stay.
    func restart(with options: Options) {
        self.options = options
        connection?.stop()
        connection = nil
        start()
    }

    private func handle(_ event: ScrcpyConnection.Event) {
        switch event {
        case .streaming:
            state = .streaming
        case let .videoSize(size):
            videoSize = size
            onVideoSize?(size)
        case let .audio(available):
            hasAudio = available
            onAudioAvailable?(available)
        case let .clipboard(text):
            onClipboard?(text)
        case let .clipboardAck(sequence):
            clipboardAcks.removeValue(forKey: sequence)?()
        case .inputRefused:
            onInputRefused?()
        case let .ended(message):
            connection = nil
            hasAudio = false
            clipboardAcks.removeAll()
            state = .ended(message)
        }
    }

    // MARK: Sinks

    /// Receives each decoded frame (32-bit BGRA, IOSurface-backed) with its time on the host clock, on the
    /// stream's own thread.
    func addFrameSink(_ key: AnyHashable, _ sink: @escaping @Sendable (CVPixelBuffer, CMTime) -> Void) {
        sinks.setFrame(key, sink)
    }

    /// Receives the sound as 16-bit interleaved stereo at 48 kHz, with the time of its first sample.
    func addAudioSink(_ key: AnyHashable, _ sink: @escaping @Sendable (Data, CMTime) -> Void) {
        sinks.setAudio(key, sink)
    }

    func removeSinks(_ key: AnyHashable) {
        sinks.remove(key)
    }

    /// The latest frame, for the device manager's preview and screenshots.
    var lastFrame: CVPixelBuffer? { sinks.lastFrame }

    // MARK: Control

    enum KeyAction: UInt8 { case down = 0, up = 1 }
    enum TouchAction: UInt8 { case down = 0, up = 1, move = 2 }

    /// The finger a single touch is; scrcpy's "generic finger".
    nonisolated static let fingerPointer: UInt64 = 0xFFFF_FFFF_FFFF_FFFE
    /// The mirrored second finger of a pinch.
    nonisolated static let virtualFingerPointer: UInt64 = 0xFFFF_FFFF_FFFF_FFFD

    func injectKey(_ keycode: Int32, action: KeyAction, metaState: Int32 = 0) {
        var message = ControlMessage(type: 0)
        message.u8(action.rawValue)
        message.i32(keycode)
        message.u32(0)
        message.i32(metaState)
        send(message)
    }

    /// Text the device types as if from a keyboard; ASCII only, 300 bytes at most per message.
    func injectText(_ text: String) {
        var rest = Substring(text)
        while !rest.isEmpty {
            var chunk = rest.prefix(300)
            while chunk.utf8.count > 300 { chunk = chunk.dropLast() }
            var message = ControlMessage(type: 1)
            message.string(String(chunk))
            send(message)
            rest = rest.dropFirst(chunk.count)
        }
    }

    /// A touch at `point`, in the current video's pixels.
    func injectTouch(_ action: TouchAction, pointer: UInt64 = fingerPointer, at point: CGPoint) {
        guard let position = position(point) else { return }
        var message = ControlMessage(type: 2)
        message.u8(action.rawValue)
        message.u64(pointer)
        message.bytes(position)
        message.u16(action == .up ? 0 : 0xFFFF)
        message.i32(0)
        message.i32(0)
        send(message)
    }

    /// Scrolling at `point` by wheel ticks (positive: right / up).
    func injectScroll(at point: CGPoint, horizontal: Double, vertical: Double) {
        guard let position = position(point) else { return }
        func fixed(_ value: Double) -> UInt16 {
            let clamped = min(max(value / 16, -1), 1)
            let int = Int32(clamped * 32768)
            return UInt16(bitPattern: Int16(clamping: min(int, 0x7FFF)))
        }
        var message = ControlMessage(type: 3)
        message.bytes(position)
        message.u16(fixed(horizontal))
        message.u16(fixed(vertical))
        message.i32(0)
        send(message)
    }

    /// Sets the device's clipboard; `paste` also pastes it where the cursor is (Android 7+).
    func setClipboard(_ text: String, paste: Bool) {
        setClipboard(text, paste: paste, sequence: 0)
    }

    /// Sets the device's clipboard and calls `acknowledged` once the device has; not if the stream ends first.
    func setClipboard(_ text: String, acknowledged: @escaping () -> Void) {
        clipboardSequence += 1
        clipboardAcks[clipboardSequence] = acknowledged
        setClipboard(text, paste: false, sequence: clipboardSequence)
    }

    /// A non-zero `sequence` asks the device to acknowledge it.
    private func setClipboard(_ text: String, paste: Bool, sequence: UInt64) {
        var message = ControlMessage(type: 9)
        message.u64(sequence)
        message.u8(paste ? 1 : 0)
        var data = Data(text.utf8)
        if data.count > 262_130 { data = data.prefix(262_130) }
        message.u32(UInt32(data.count))
        message.bytes(data)
        send(message)
    }

    /// Turns the device's own display off or on; the stream goes on either way.
    func setDisplayPower(_ on: Bool) {
        var message = ControlMessage(type: 10)
        message.u8(on ? 1 : 0)
        send(message)
    }

    private func position(_ point: CGPoint) -> Data? {
        guard videoSize.width > 0 else { return nil }
        var message = ControlMessage(type: 0)
        message.data.removeAll()
        message.i32(Int32(point.x.rounded(.down)))
        message.i32(Int32(point.y.rounded(.down)))
        message.u16(UInt16(videoSize.width))
        message.u16(UInt16(videoSize.height))
        return message.data
    }

    private func send(_ message: ControlMessage) {
        connection?.send(message.data)
    }
}

/// A control message, built field by field in big-endian order.
private struct ControlMessage {
    var data: Data

    init(type: UInt8) { data = Data([type]) }

    mutating func u8(_ value: UInt8) { data.append(value) }
    mutating func u16(_ value: UInt16) { withUnsafeBytes(of: value.bigEndian) { data.append(contentsOf: $0) } }
    mutating func u32(_ value: UInt32) { withUnsafeBytes(of: value.bigEndian) { data.append(contentsOf: $0) } }
    mutating func i32(_ value: Int32) { u32(UInt32(bitPattern: value)) }
    mutating func u64(_ value: UInt64) { withUnsafeBytes(of: value.bigEndian) { data.append(contentsOf: $0) } }
    mutating func bytes(_ value: Data) { data.append(value) }
    mutating func string(_ value: String) {
        let bytes = Data(value.utf8)
        u32(UInt32(bytes.count))
        data.append(bytes)
    }
}

/// Where decoded frames and sound go; read from the stream's threads, changed from the main thread.
final class ScrcpySinks: @unchecked Sendable {
    private let lock = NSLock()
    private var frames: [AnyHashable: @Sendable (CVPixelBuffer, CMTime) -> Void] = [:]
    private var audio: [AnyHashable: @Sendable (Data, CMTime) -> Void] = [:]
    private var last: CVPixelBuffer?

    func setFrame(_ key: AnyHashable, _ sink: @escaping @Sendable (CVPixelBuffer, CMTime) -> Void) {
        lock.lock(); frames[key] = sink; lock.unlock()
    }

    func setAudio(_ key: AnyHashable, _ sink: @escaping @Sendable (Data, CMTime) -> Void) {
        lock.lock(); audio[key] = sink; lock.unlock()
    }

    func remove(_ key: AnyHashable) {
        lock.lock(); frames[key] = nil; audio[key] = nil; lock.unlock()
    }

    var lastFrame: CVPixelBuffer? {
        lock.lock(); defer { lock.unlock() }
        return last
    }

    func deliver(_ frame: CVPixelBuffer, at time: CMTime) {
        lock.lock()
        last = frame
        let targets = Array(frames.values)
        lock.unlock()
        targets.forEach { $0(frame, time) }
    }

    func deliverAudio(_ samples: Data, at time: CMTime) {
        lock.lock()
        let targets = Array(audio.values)
        lock.unlock()
        targets.forEach { $0(samples, time) }
    }
}

// MARK: - Connection

/// One run of scrcpy-server: pushing it, starting it, its sockets and the threads reading them.
private final class ScrcpyConnection: @unchecked Sendable {
    enum Event {
        case streaming
        case videoSize(CGSize)
        case audio(Bool)
        case clipboard(String)
        case clipboardAck(UInt64)
        case inputRefused
        case ended(String?)
    }

    var onEvent: (@Sendable (Event) -> Void)?

    private let serial: String
    private let options: ScrcpyStream.Options
    private let sinks: ScrcpySinks
    private let lock = NSLock()
    private var stopped = false
    private var ended = false
    private var server: Process?
    private var sockets: [ScrcpySocket] = []
    private var control: ScrcpySocket?
    private let controlQueue = DispatchQueue(label: "scrcpy.control")
    /// The server's last log lines, for the message when it fails.
    private var serverLog: [String] = []
    private var inputRefused = false

    init(serial: String, options: ScrcpyStream.Options, sinks: ScrcpySinks) {
        self.serial = serial
        self.options = options
        self.sinks = sinks
    }

    func start() {
        Thread.detachNewThread { [self] in
            Thread.current.name = "scrcpy \(serial)"
            do {
                try run()
            } catch {
                finish(isStopped ? nil : "\(error)")
            }
        }
    }

    func stop() {
        lock.lock()
        stopped = true
        let sockets = self.sockets
        let server = self.server
        lock.unlock()
        // Closing the sockets makes the server exit; one that does not (a sleeping device) is killed.
        sockets.forEach { $0.shutdown() }
        if let server {
            DispatchQueue.global().asyncAfter(deadline: .now() + 1) { if server.isRunning { server.terminate() } }
        }
    }

    func send(_ data: Data) {
        controlQueue.async { [weak self] in
            guard let self, let control = self.lockedControl else { return }
            do { try control.write(data) } catch { Log.debug("scrcpy control write failed: \(error)") }
        }
    }

    private var lockedControl: ScrcpySocket? {
        lock.lock(); defer { lock.unlock() }
        return control
    }

    private var isStopped: Bool {
        lock.lock(); defer { lock.unlock() }
        return stopped
    }

    private func finish(_ message: String?) {
        lock.lock()
        guard !ended else { return lock.unlock() }
        ended = true
        stopped = true
        let sockets = self.sockets
        let server = self.server
        let log = serverLog
        lock.unlock()
        sockets.forEach { $0.shutdown() }
        if let server, server.isRunning {
            DispatchQueue.global().asyncAfter(deadline: .now() + 1) { if server.isRunning { server.terminate() } }
        }
        // The server's own error says more than a closed socket.
        let serverError = log.last { $0.contains("ERROR") || $0.contains("Exception") }
        let reason = message.map { serverError.map { "\($0)" } ?? $0 }
        if let reason { Log.error("scrcpy \(serial): \(reason)") }
        onEvent?(.ended(reason))
    }

    private func run() throws {
        guard let jar = Self.serverJar else { throw ShellError("找不到 scrcpy-server（应在 App 的 Resources 或源码的 vendor/scrcpy 里）") }
        let remote = "/data/local/tmp/scrcpy-server.jar"
        // The server deletes its jar as it starts (`cleanup`), so it is pushed every time.
        try adbSync(["push", jar.path, remote], timeout: 30)
        guard !isStopped else { return }

        let scid = String(format: "%08x", UInt32.random(in: 0..<0x7FFF_FFFF))
        let port = try forward(socket: "scrcpy_\(scid)")
        defer { _ = try? adbSync(["forward", "--remove", "tcp:\(port)"], timeout: 5) }

        var arguments = [
            "CLASSPATH=\(remote)", "app_process", "/", "com.genymobile.scrcpy.Server", ScrcpyStream.serverVersion,
            "scid=\(scid)", "log_level=info", "tunnel_forward=true", "video_codec=h264",
            "audio=\(options.audio)", "control=true",
            "max_size=\(options.maxSize)", "video_bit_rate=\(options.bitRate)",
            "stay_awake=\(options.stayAwake)", "show_touches=\(options.showTouches)",
            "clipboard_autosync=\(options.clipboardSync)", "power_on=\(options.powerOn)",
        ]
        if options.audio { arguments.append("audio_codec=raw") }
        try launchServer(arguments)

        // adb accepts the connection even before the server listens; the server's first byte says it does.
        var video: ScrcpySocket?
        for _ in 0..<100 {
            guard !isStopped else { return }
            if let socket = try? ScrcpySocket(port: port), (try? socket.read(1)) != nil {
                video = socket
                break
            }
            Thread.sleep(forTimeInterval: 0.1)
        }
        guard let video else { throw ShellError("scrcpy-server 没有响应") }
        register(video)
        let audio = options.audio ? try ScrcpySocket(port: port) : nil
        audio.map(register)
        let control = try ScrcpySocket(port: port)
        control.setNoDelay()
        register(control)
        lock.lock(); self.control = control; lock.unlock()
        // Every socket is connected; the tunnel is no longer needed.
        _ = try? adbSync(["forward", "--remove", "tcp:\(port)"], timeout: 5)

        let name = try video.read(64)
        Log.info("scrcpy \(serial): streaming \(String(decoding: name.prefix { $0 != 0 }, as: UTF8.self))")
        onEvent?(.streaming)

        if let audio { Thread.detachNewThread { [self] in readAudio(audio) } }
        Thread.detachNewThread { [self] in readDeviceMessages(control) }
        try readVideo(video)
    }

    private func register(_ socket: ScrcpySocket) {
        lock.lock()
        sockets.append(socket)
        let stopped = self.stopped
        lock.unlock()
        if stopped { socket.shutdown() }
    }

    // MARK: Video

    private func readVideo(_ socket: ScrcpySocket) throws {
        let codec = try socket.readU32()
        guard codec == 0x6832_3634 else { throw ShellError("视频编码不是 H.264（\(String(codec, radix: 16))）") }
        let decoder = H264Decoder { [sinks] frame, time in sinks.deliver(frame, at: time) }
        while true {
            let header = try socket.read(12)
            let first = header.readU32(at: 0)
            if first & 0x8000_0000 != 0 {
                // A new session: the encoder restarted, e.g. the device turned; its size comes with it.
                let size = CGSize(width: Int(header.readU32(at: 4)), height: Int(header.readU32(at: 8)))
                decoder.reset()
                onEvent?(.videoSize(size))
                continue
            }
            let flags = header.readU64(at: 0)
            let length = Int(header.readU32(at: 8))
            let payload = try socket.read(length)
            if flags & (1 << 62) != 0 {
                decoder.configure(payload)
            } else {
                decoder.decode(payload, pts: Int64(flags & ((1 << 61) - 1)))
            }
        }
    }

    // MARK: Audio

    private func readAudio(_ socket: ScrcpySocket) {
        do {
            let codec = try socket.readU32()
            guard codec == 0x0072_6177 else {
                // 0: the device cannot capture its sound (Android 10 or older); 1: it failed to.
                onEvent?(.audio(false))
                return
            }
            onEvent?(.audio(true))
            var clock = HostClockMapper()
            while true {
                let header = try socket.read(12)
                let flags = header.readU64(at: 0)
                let payload = try socket.read(Int(header.readU32(at: 8)))
                guard flags & (1 << 62) == 0 else { continue }
                sinks.deliverAudio(payload, at: clock.hostTime(pts: Int64(flags & ((1 << 61) - 1))))
            }
        } catch {
            // The audio socket closing is not fatal; the video ends the connection.
            onEvent?(.audio(false))
        }
    }

    // MARK: Device messages

    private func readDeviceMessages(_ socket: ScrcpySocket) {
        do {
            while true {
                let type = try socket.read(1)[0]
                switch type {
                case 0:
                    let length = Int(try socket.readU32())
                    let text = String(decoding: try socket.read(length), as: UTF8.self)
                    onEvent?(.clipboard(text))
                case 1:
                    onEvent?(.clipboardAck(try socket.read(8).readU64(at: 0)))
                case 2:
                    _ = try socket.read(2)
                    let size = Int(try socket.read(2).readU16(at: 0))
                    _ = try socket.read(size)
                default:
                    throw ShellError("unknown device message \(type)")
                }
            }
        } catch {
            finish(isStopped ? nil : "控制通道断开")
        }
    }

    // MARK: adb

    private func launchServer(_ arguments: [String]) throws {
        let process = Process()
        process.executableURL = Adb.executable
        process.arguments = ["-s", serial, "shell"] + arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = output
        process.standardInput = FileHandle.nullDevice
        let collector = LineSplitter { [weak self] line in
            guard let self else { return }
            Log.debug("scrcpy-server \(self.serial): \(line)")
            self.lock.lock()
            self.serverLog.append(line)
            if self.serverLog.count > 20 { self.serverLog.removeFirst() }
            // The server says why an injected event failed; HyperOS 2 sets no property that would say so first.
            let refused = !self.inputRefused
                && (line.contains("INJECT_EVENTS") || line.contains("USB debugging (Security Settings)"))
            if refused { self.inputRefused = true }
            self.lock.unlock()
            if refused { self.onEvent?(.inputRefused) }
        }
        output.fileHandleForReading.readabilityHandler = { collector.append($0.availableData) }
        process.terminationHandler = { [weak self] _ in
            output.fileHandleForReading.readabilityHandler = nil
            guard let self else { return }
            self.finish(self.isStopped ? nil : "scrcpy-server 退出了")
        }
        try process.run()
        lock.lock(); server = process; lock.unlock()
    }

    /// `adb forward` on the first free port from 27183, scrcpy's range.
    private func forward(socket: String) throws -> Int {
        for port in 27183...27299 where Self.canBind(port) {
            if (try? adbSync(["forward", "tcp:\(port)", "localabstract:\(socket)"], timeout: 5)) != nil { return port }
        }
        throw ShellError("没有空闲的本地端口")
    }

    @discardableResult
    private func adbSync(_ arguments: [String], timeout: TimeInterval) throws -> Data {
        let semaphore = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var result: Result<Data, Error> = .failure(ShellError("adb 超时"))
        let serial = self.serial
        Task.detached {
            do { result = .success(try await Adb.run(arguments, serial: serial, timeout: .seconds(timeout))) } catch { result = .failure(error) }
            semaphore.signal()
        }
        if semaphore.wait(timeout: .now() + timeout + 2) == .timedOut { throw ShellError("adb \(arguments.first ?? "") 超时") }
        return try result.get()
    }

    private static func canBind(_ port: Int) -> Bool {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        return withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } == 0
        }
    }

    /// The server jar: in the app bundle's Resources, beside the executable, or in the source tree's
    /// `vendor/scrcpy` for a build run from `.build`.
    static var serverJar: URL? {
        var candidates: [URL] = []
        if let resources = Bundle.main.resourceURL { candidates.append(resources.appendingPathComponent("scrcpy-server")) }
        if let executable = Bundle.main.executableURL?.resolvingSymlinksInPath() {
            candidates.append(executable.deletingLastPathComponent().appendingPathComponent("scrcpy-server"))
        }
        let source = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        candidates.append(source.appendingPathComponent("vendor/scrcpy/scrcpy-server"))
        return candidates.first { FileManager.default.fileExists(atPath: $0.path) }
    }
}

/// Splits a process's output into lines.
private final class LineSplitter: @unchecked Sendable {
    private let lock = NSLock()
    private var pending = ""
    private let onLine: (String) -> Void

    init(onLine: @escaping (String) -> Void) { self.onLine = onLine }

    func append(_ data: Data) {
        guard !data.isEmpty else { return }
        lock.lock()
        pending += String(decoding: data, as: UTF8.self)
        var lines: [String] = []
        while let end = pending.firstIndex(where: \.isNewline) {
            lines.append(String(pending[..<end]))
            pending = String(pending[pending.index(after: end)...])
        }
        lock.unlock()
        lines.filter { !$0.isEmpty }.forEach(onLine)
    }
}

// MARK: - Socket

/// A blocking TCP socket to the adb forward on localhost.
private final class ScrcpySocket: @unchecked Sendable {
    private let fd: Int32
    private let lock = NSLock()
    private var isShut = false

    init(port: Int) throws {
        fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { throw ShellError("socket: \(String(cString: strerror(errno)))") }
        var on: Int32 = 1
        // A write to a socket the device closed must fail, not kill the app with SIGPIPE.
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        let result = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard result == 0 else {
            close(fd)
            throw ShellError("connect: \(String(cString: strerror(errno)))")
        }
    }

    deinit { close(fd) }

    func setNoDelay() {
        var on: Int32 = 1
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &on, socklen_t(MemoryLayout<Int32>.size))
    }

    func read(_ count: Int) throws -> Data {
        var data = Data(count: count)
        var offset = 0
        while offset < count {
            let received = data.withUnsafeMutableBytes { buffer in
                recv(fd, buffer.baseAddress!.advanced(by: offset), count - offset, 0)
            }
            if received > 0 {
                offset += received
            } else if received < 0 && errno == EINTR {
                continue
            } else {
                throw ShellError(received == 0 ? "连接已关闭" : "recv: \(String(cString: strerror(errno)))")
            }
        }
        return data
    }

    func readU32() throws -> UInt32 { try read(4).readU32(at: 0) }

    func write(_ data: Data) throws {
        try data.withUnsafeBytes { buffer in
            var offset = 0
            while offset < buffer.count {
                let sent = Darwin.send(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset, 0)
                if sent > 0 {
                    offset += sent
                } else if sent < 0 && errno == EINTR {
                    continue
                } else {
                    throw ShellError("send: \(String(cString: strerror(errno)))")
                }
            }
        }
    }

    func shutdown() {
        lock.lock()
        defer { lock.unlock() }
        guard !isShut else { return }
        isShut = true
        Darwin.shutdown(fd, SHUT_RDWR)
    }
}

private extension Data {
    func readU16(at offset: Int) -> UInt16 {
        self[startIndex + offset ..< startIndex + offset + 2].reduce(0) { $0 << 8 | UInt16($1) }
    }

    func readU32(at offset: Int) -> UInt32 {
        self[startIndex + offset ..< startIndex + offset + 4].reduce(0) { $0 << 8 | UInt32($1) }
    }

    func readU64(at offset: Int) -> UInt64 {
        self[startIndex + offset ..< startIndex + offset + 8].reduce(0) { $0 << 8 | UInt64($1) }
    }
}

// MARK: - Decoding

/// Maps a stream's timestamps (microseconds on the device's clock) onto the host clock, anchored at the
/// first packet; re-anchored if they drift more than half a second from when packets arrive.
struct HostClockMapper {
    private var anchor: (pts: Int64, host: CMTime)?

    mutating func hostTime(pts: Int64) -> CMTime {
        let now = ScreenRecorder.now
        if let anchor {
            let time = CMTimeAdd(anchor.host, CMTime(value: pts - anchor.pts, timescale: 1_000_000))
            if abs(CMTimeGetSeconds(CMTimeSubtract(time, now))) < 0.5 { return time }
        }
        anchor = (pts, now)
        return now
    }

    mutating func reset() { anchor = nil }
}

/// Decodes the H.264 stream with VideoToolbox into IOSurface-backed BGRA frames, which Core Animation shows
/// as they are and the recorder encodes again.
private final class H264Decoder {
    private let output: (CVPixelBuffer, CMTime) -> Void
    private var format: CMVideoFormatDescription?
    private var session: VTDecompressionSession?
    private var clock = HostClockMapper()

    init(output: @escaping (CVPixelBuffer, CMTime) -> Void) {
        self.output = output
    }

    deinit { invalidate() }

    func reset() {
        clock.reset()
    }

    /// A config packet: the SPS and PPS, Annex B.
    func configure(_ data: Data) {
        let units = Self.nalUnits(data)
        guard let sps = units.first(where: { ($0.first ?? 0) & 0x1F == 7 }),
              let pps = units.first(where: { ($0.first ?? 0) & 0x1F == 8 }) else { return }
        var description: CMVideoFormatDescription?
        let status = sps.withUnsafeBytes { spsBytes in
            pps.withUnsafeBytes { ppsBytes in
                let pointers = [spsBytes.bindMemory(to: UInt8.self).baseAddress!, ppsBytes.bindMemory(to: UInt8.self).baseAddress!]
                let sizes = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: nil, parameterSetCount: 2, parameterSetPointers: pointers, parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4, formatDescriptionOut: &description)
            }
        }
        guard status == noErr, let description else {
            Log.error("cannot read the stream's parameter sets (\(status))")
            return
        }
        if let session, VTDecompressionSessionCanAcceptFormatDescription(session, formatDescription: description) {
            format = description
            return
        }
        invalidate()
        format = description
        let attributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:] as [String: Any],
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ]
        var session: VTDecompressionSession?
        let created = VTDecompressionSessionCreate(allocator: nil, formatDescription: description, decoderSpecification: nil,
                                                   imageBufferAttributes: attributes as CFDictionary,
                                                   outputCallback: nil, decompressionSessionOut: &session)
        guard created == noErr, let session else {
            Log.error("cannot start the video decoder (\(created))")
            return
        }
        VTSessionSetProperty(session, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        self.session = session
    }

    func decode(_ data: Data, pts: Int64) {
        guard let session, let format else { return }
        // Annex B start codes become 4-byte lengths; parameter sets in the frame are left out.
        var avcc = Data(capacity: data.count + 16)
        for unit in Self.nalUnits(data) where ![7, 8].contains((unit.first ?? 0) & 0x1F) {
            var length = UInt32(unit.count).bigEndian
            withUnsafeBytes(of: &length) { avcc.append(contentsOf: $0) }
            avcc.append(unit)
        }
        guard !avcc.isEmpty else { return }
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: avcc.count, blockAllocator: nil,
                                                 customBlockSource: nil, offsetToData: 0, dataLength: avcc.count, flags: 0,
                                                 blockBufferOut: &block) == noErr, let block else { return }
        avcc.withUnsafeBytes { _ = CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: avcc.count) }
        let time = clock.hostTime(pts: pts)
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: time, decodeTimeStamp: .invalid)
        var size = avcc.count
        var sample: CMSampleBuffer?
        guard CMSampleBufferCreateReady(allocator: nil, dataBuffer: block, formatDescription: format, sampleCount: 1,
                                        sampleTimingEntryCount: 1, sampleTimingArray: &timing, sampleSizeEntryCount: 1,
                                        sampleSizeArray: &size, sampleBufferOut: &sample) == noErr, let sample else { return }
        let output = self.output
        let status = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sample, flags: [], infoFlagsOut: nil) { status, _, image, presentation, _ in
            guard status == noErr, let image else { return }
            output(image, presentation)
        }
        if status == kVTInvalidSessionErr { invalidate() }
    }

    private func invalidate() {
        if let session { VTDecompressionSessionInvalidate(session) }
        session = nil
    }

    /// The NAL units of Annex B data, without their start codes.
    static func nalUnits(_ data: Data) -> [Data] {
        var units: [Data] = []
        let bytes = [UInt8](data)
        var index = 0, start = -1
        while index + 2 < bytes.count {
            if bytes[index] == 0, bytes[index + 1] == 0, bytes[index + 2] == 1 {
                if start >= 0 {
                    var end = index
                    if end > start, bytes[end - 1] == 0 { end -= 1 }
                    units.append(Data(bytes[start..<end]))
                }
                index += 3
                start = index
            } else {
                index += 1
            }
        }
        if start >= 0, start < bytes.count { units.append(Data(bytes[start...])) }
        return units
    }
}

// MARK: - Keys

/// Android key codes for the key names device windows use (the emulator's W3C names).
enum AndroidKey {
    static func code(for key: String) -> Int32? {
        [
            "GoBack": 4, "GoHome": 3, "AppSwitch": 187, "Power": 26,
            "AudioVolumeUp": 24, "AudioVolumeDown": 25,
            "Enter": 66, "Tab": 61, "Backspace": 67, "Delete": 112,
            "ArrowLeft": 21, "ArrowRight": 22, "ArrowUp": 19, "ArrowDown": 20,
            "Home": 122, "End": 123, "PageUp": 92, "PageDown": 93,
        ][key]
    }

    static let paste: Int32 = 279
}
