import Foundation

/// The SDK's adb. Android Studio uses the same one, so both share one adb server: a second adb of another
/// version would restart the server each time either is used, dropping the other's connections.
enum Adb {
    static var executable: URL { Sdk.root.appendingPathComponent("platform-tools/adb") }
    static var isInstalled: Bool { FileManager.default.isExecutableFile(atPath: executable.path) }

    /// Runs adb and returns what it wrote to standard output; throws with its error output when it fails or
    /// takes longer than `timeout`.
    static func run(_ arguments: [String], serial: String? = nil, input: Data? = nil,
                    timeout: Duration = .seconds(30)) async throws -> Data {
        let process = Process()
        process.executableURL = executable
        process.arguments = (serial.map { ["-s", $0] } ?? []) + arguments
        let output = Pipe(), errors = Pipe()
        process.standardOutput = output
        process.standardError = errors
        let stdin = Pipe()
        process.standardInput = stdin
        let out = DataCollector(), err = DataCollector()
        output.fileHandleForReading.readabilityHandler = { out.append($0.availableData) }
        errors.fileHandleForReading.readabilityHandler = { err.append($0.availableData) }

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
                let timer = DispatchWorkItem { if process.isRunning { process.terminate() } }
                process.terminationHandler = { process in
                    timer.cancel()
                    output.fileHandleForReading.readabilityHandler = nil
                    errors.fileHandleForReading.readabilityHandler = nil
                    out.append(output.fileHandleForReading.readDataToEndOfFile())
                    err.append(errors.fileHandleForReading.readDataToEndOfFile())
                    if process.terminationStatus == 0, process.terminationReason == .exit {
                        continuation.resume(returning: out.data)
                    } else if process.terminationReason == .uncaughtSignal {
                        continuation.resume(throwing: ShellError("adb \(arguments.first ?? "") 超时"))
                    } else {
                        let message = String(decoding: err.data.isEmpty ? out.data : err.data, as: UTF8.self)
                            .trimmingCharacters(in: .whitespacesAndNewlines)
                        continuation.resume(throwing: ShellError(message.isEmpty ? "adb \(arguments.first ?? "") 失败（\(process.terminationStatus)）" : message))
                    }
                }
                do {
                    // Cancelled before it started: it never runs.
                    guard !Task.isCancelled else { throw CancellationError() }
                    try process.run()
                    if let input { stdin.fileHandleForWriting.write(input) }
                    try? stdin.fileHandleForWriting.close()
                    let seconds = Double(timeout.components.seconds) + Double(timeout.components.attoseconds) / 1e18
                    DispatchQueue.global().asyncAfter(deadline: .now() + seconds, execute: timer)
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        } onCancel: {
            // `terminate()` raises on a task that has not been launched.
            if process.isRunning { process.terminate() }
        }
    }

    static func text(_ arguments: [String], serial: String? = nil, timeout: Duration = .seconds(30)) async throws -> String {
        String(decoding: try await run(arguments, serial: serial, timeout: timeout), as: UTF8.self)
    }

    /// Runs `command` in the device's shell. adb before Android 7 reports no exit status, so the output is
    /// what tells success apart.
    static func shell(_ command: String, serial: String, timeout: Duration = .seconds(30)) async throws -> String {
        try await text(["shell", command], serial: serial, timeout: timeout)
    }

    /// The device's screen as PNG, at its own resolution.
    static func screenshotPNG(serial: String) async throws -> Data {
        let data = try await run(["exec-out", "screencap", "-p"], serial: serial, timeout: .seconds(20))
        // A PNG starts with 0x89 'P' 'N' 'G'; anything else is an error message.
        guard data.count > 8, data.starts(with: [0x89, 0x50, 0x4E, 0x47]) else {
            let message = String(decoding: data.prefix(200), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            throw ShellError(message.isEmpty ? "截图失败" : message)
        }
        return data
    }

    // MARK: - Wireless

    /// `adb mdns services`: devices on the network that offer pairing (`_adb-tls-pairing._tcp`) or that are
    /// paired and offer a connection (`_adb-tls-connect._tcp`).
    struct MdnsService: Hashable {
        let name: String
        let type: String
        let host: String
        let port: Int
        var address: String { "\(host):\(port)" }
        var isPairing: Bool { type.hasPrefix("_adb-tls-pairing") }
        var isConnect: Bool { type.hasPrefix("_adb-tls-connect") }
    }

    static func mdnsServices() async throws -> [MdnsService] {
        let output = try await text(["mdns", "services"], timeout: .seconds(10))
        return output.split(whereSeparator: \.isNewline).compactMap { line in
            let fields = line.split(whereSeparator: { $0 == "\t" || $0 == " " }).map(String.init)
            guard fields.count >= 3, let colon = fields[2].lastIndex(of: ":"),
                  let port = Int(fields[2][fields[2].index(after: colon)...]) else { return nil }
            return MdnsService(name: fields[0], type: fields[1], host: String(fields[2][..<colon]), port: port)
        }
    }

    /// Pairs with a device showing a pairing code or scanning this app's QR code (Android 11+).
    static func pair(address: String, code: String) async throws {
        let output = try await text(["pair", address, code], timeout: .seconds(20))
        guard output.localizedCaseInsensitiveContains("Successfully paired") else {
            throw ShellError(output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    static func connect(_ address: String) async throws {
        let output = try await text(["connect", address], timeout: .seconds(15))
        guard output.contains("connected to") else {
            throw ShellError(output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    static func disconnect(_ address: String) async throws {
        _ = try await text(["disconnect", address], timeout: .seconds(10))
    }

    /// Switches a USB device to adb over TCP on port 5555 and connects to it there, the way that works on any
    /// Android version; returns the address.
    static func switchToWireless(serial: String) async throws -> String {
        let route = try await shell("ip -f inet addr show wlan0", serial: serial)
        guard let range = route.range(of: #"inet ([0-9.]+)"#, options: .regularExpression) else {
            throw ShellError("这台设备没有连 Wi-Fi")
        }
        let ip = route[range].dropFirst("inet ".count)
        _ = try await text(["tcpip", "5555"], serial: serial, timeout: .seconds(15))
        let address = "\(ip):5555"
        // adbd restarts on the device; it takes a moment to listen.
        var lastError: Error = ShellError("连接 \(address) 失败")
        for _ in 0..<10 {
            try await Task.sleep(for: .milliseconds(700))
            do {
                try await connect(address)
                return address
            } catch {
                lastError = error
            }
        }
        throw lastError
    }
}

/// One line of `adb devices -l` / `adb track-devices -l`.
struct AdbDevice: Hashable, Sendable {
    let serial: String
    /// `device`, `unauthorized`, `offline`, `authorizing`, `connecting`, `no permissions`, …
    let state: String
    /// `usb`, `product`, `model`, `device`, `transport_id`.
    let info: [String: String]

    var isEmulator: Bool { serial.hasPrefix("emulator-") }
    /// Connected over the network: `ip:port`, or a paired device's mDNS name.
    var isWireless: Bool { serial.contains(":") || serial.contains("._adb-tls-connect.") }
    var model: String? { info["model"]?.replacingOccurrences(of: "_", with: " ") }

    static func parse(_ text: Substring) -> [AdbDevice] {
        text.split(whereSeparator: \.isNewline).compactMap { line in
            guard !line.hasPrefix("List of devices"), !line.hasPrefix("*") else { return nil }
            // `-l` pads the serial with spaces rather than a tab; the state may hold spaces ("no permissions
            // (…)"); the details are `key:value` words at the end.
            var words = line.split(whereSeparator: { $0 == " " || $0 == "\t" }).map(String.init)
            guard words.count >= 2 else { return nil }
            let serial = words.removeFirst()
            var info: [String: String] = [:]
            while let last = words.last, last.range(of: #"^[a-z_]+:[^/\[\]]+$"#, options: .regularExpression) != nil {
                let colon = last.firstIndex(of: ":")!
                info[String(last[..<colon])] = String(last[last.index(after: colon)...])
                words.removeLast()
            }
            let state = words.joined(separator: " ").components(separatedBy: " (").first ?? ""
            return AdbDevice(serial: serial, state: state, info: info)
        }
    }
}

/// Follows `adb track-devices -l`, which reports the whole device list each time it changes, so a device shows
/// up or goes away within a second of being plugged in, with no polling. adb starts its server if none runs;
/// if the server goes away (`adb kill-server`, an SDK update) the tracker starts again.
final class AdbDeviceTracker: @unchecked Sendable {
    private let onChange: @Sendable ([AdbDevice]) -> Void
    private let lock = NSLock()
    private var process: Process?
    private var stopped = false
    private var buffer = Data()

    init(onChange: @escaping @Sendable ([AdbDevice]) -> Void) {
        self.onChange = onChange
    }

    func start() {
        lock.lock()
        defer { lock.unlock() }
        guard !stopped, process == nil, Adb.isInstalled else { return }
        let process = Process()
        process.executableURL = Adb.executable
        process.arguments = ["track-devices", "-l"]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else { return }
            self?.received(data)
        }
        process.terminationHandler = { [weak self] _ in
            output.fileHandleForReading.readabilityHandler = nil
            self?.ended()
        }
        do {
            try process.run()
            self.process = process
            buffer = Data()
        } catch {
            Log.error("cannot run adb track-devices: \(error)")
        }
    }

    func stop() {
        lock.lock()
        stopped = true
        let process = self.process
        self.process = nil
        lock.unlock()
        process?.terminate()
    }

    private func ended() {
        lock.lock()
        process = nil
        let stopped = self.stopped
        lock.unlock()
        guard !stopped else { return }
        onChange([])
        DispatchQueue.global().asyncAfter(deadline: .now() + 2) { [weak self] in self?.start() }
    }

    /// Each report is four hex digits giving its length, then that many bytes of `adb devices -l` lines.
    private func received(_ data: Data) {
        lock.lock()
        buffer.append(data)
        var reports: [[AdbDevice]] = []
        while buffer.count >= 4 {
            guard let length = Int(String(decoding: buffer.prefix(4), as: UTF8.self), radix: 16) else {
                buffer.removeAll()
                break
            }
            guard buffer.count >= 4 + length else { break }
            let body = buffer.subdata(in: buffer.startIndex + 4 ..< buffer.startIndex + 4 + length)
            buffer = Data(buffer.dropFirst(4 + length))
            reports.append(AdbDevice.parse(Substring(String(decoding: body, as: UTF8.self))))
        }
        lock.unlock()
        if let last = reports.last { onChange(last) }
    }
}

/// What the device manager shows about a physical device, read over adb once it is authorized.
struct DeviceInfo: Hashable, Sendable {
    var properties: [String: String] = [:]
    /// The display's size in its natural orientation, in pixels.
    var width = 0, height = 0
    /// Android's logical density (`wm density`) and the panel's real dots per inch, when reported.
    var density: Int?
    var xdpi: Double?
    var batteryLevel: Int?
    var charging = false
    var storageFree: Int64?
    var storageTotal: Int64?
    var memory: Int64?

    var manufacturer: String { properties["ro.product.manufacturer"] ?? "" }
    var model: String { properties["ro.product.model"] ?? "" }
    /// The name people know the device by ("Pixel 8 Pro", "Galaxy Tab S9"), else the model.
    var marketName: String {
        let candidates = ["ro.product.marketname", "ro.vendor.product.marketname", "ro.config.marketing_name",
                          "ro.product.vendor.marketname", "ro.product.odm.marketname"]
        return candidates.lazy.compactMap { self.properties[$0] }.first { !$0.isEmpty } ?? model
    }
    var codename: String { properties["ro.product.device"] ?? "" }
    var androidVersion: String { properties["ro.build.version.release"] ?? "?" }
    var sdk: Int { Int(properties["ro.build.version.sdk"] ?? "") ?? 0 }
    var serialNumber: String? { properties["ro.serialno"].flatMap { $0.isEmpty ? nil : $0 } }
    var securityPatch: String? { properties["ro.build.version.security_patch"] }
    var buildId: String? { properties["ro.build.display.id"] ?? properties["ro.build.id"] }
    var abi: String? { properties["ro.product.cpu.abi"] }
    var isTablet: Bool {
        properties["ro.build.characteristics"]?.contains("tablet") == true || min(width, height) >= 1500 && density.map { $0 < 360 } == true
    }
    var isLandscapeNatural: Bool { width > height }
    /// MIUI and HyperOS refuse input from adb (touches, keys) until "USB debugging (Security settings)" is
    /// turned on in the developer options; the screen still streams.
    var inputBlocked: Bool { properties["persist.security.adbinput"] == "0" }

    /// One shell call for everything, as each adb call costs a round trip.
    static func read(serial: String) async throws -> DeviceInfo {
        let script = "getprop; echo @@; wm size; echo @@; wm density; echo @@; dumpsys display | grep -m1 dpi; echo @@; "
            + "dumpsys battery; echo @@; df -k /data | tail -1; echo @@; head -1 /proc/meminfo"
        let output = try await Adb.shell(script, serial: serial, timeout: .seconds(15))
        let parts = output.components(separatedBy: "@@")
        var info = DeviceInfo()
        func part(_ index: Int) -> String { index < parts.count ? parts[index] : "" }

        for line in part(0).split(whereSeparator: \.isNewline) {
            // [ro.product.model]: [Pixel 8 Pro]
            guard line.hasPrefix("["), let close = line.firstIndex(of: "]"),
                  let open = line[close...].firstIndex(of: "["), line.hasSuffix("]") else { continue }
            let key = String(line[line.index(after: line.startIndex)..<close])
            info.properties[key] = String(line[line.index(after: open)..<line.index(before: line.endIndex)])
        }
        if let size = firstMatch(#"Physical size: ([0-9]+)x([0-9]+)"#, in: part(1)), size.count == 2 {
            info.width = Int(size[0]) ?? 0
            info.height = Int(size[1]) ?? 0
        }
        info.density = firstMatch(#"Physical density: ([0-9]+)"#, in: part(2)).flatMap { Int($0[0]) }
        info.xdpi = firstMatch(#"([0-9.]+) x [0-9.]+ dpi"#, in: part(3)).flatMap { Double($0[0]) }
        let battery = part(4)
        info.batteryLevel = firstMatch(#"level: ([0-9]+)"#, in: battery).flatMap { Int($0[0]) }
        info.charging = firstMatch(#"status: ([0-9]+)"#, in: battery).map { $0[0] == "2" } ?? false
        let df = part(5).split(whereSeparator: { $0 == " " || $0.isNewline }).map(String.init)
        if df.count >= 4, let total = Int64(df[1]), let free = Int64(df[3]) {
            info.storageTotal = total * 1024
            info.storageFree = free * 1024
        }
        info.memory = firstMatch(#"MemTotal:\s+([0-9]+)"#, in: part(6)).flatMap { Int64($0[0]) }.map { $0 * 1024 }
        return info
    }

    private static func firstMatch(_ pattern: String, in text: String) -> [String]? {
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) else { return nil }
        return (1..<match.numberOfRanges).compactMap { Range(match.range(at: $0), in: text).map { String(text[$0]) } }
    }
}

/// Collects a pipe's output from its readability handler.
final class DataCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var storage = Data()

    func append(_ chunk: Data) {
        guard !chunk.isEmpty else { return }
        lock.lock()
        storage.append(chunk)
        lock.unlock()
    }

    var data: Data {
        lock.lock()
        defer { lock.unlock() }
        return storage
    }
}
