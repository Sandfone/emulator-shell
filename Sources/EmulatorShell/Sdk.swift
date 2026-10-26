import Foundation

/// Where the Android SDK and its AVDs live, and how to run the SDK's command-line tools.
enum Sdk {
    static var root: URL {
        let env = ProcessInfo.processInfo.environment
        // Some launchers (the `android` CLI) pass the path with an unexpanded `~`.
        if let path = env["ANDROID_HOME"] ?? env["ANDROID_SDK_ROOT"], !path.isEmpty {
            return URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Android/sdk")
    }

    static var avdHome: URL {
        if let path = ProcessInfo.processInfo.environment["ANDROID_AVD_HOME"] {
            return URL(fileURLWithPath: path)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".android/avd")
    }

    /// Each running emulator writes `pid_<pid>.ini` here with its AVD, adb and gRPC ports.
    static var runningEmulatorsDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Caches/TemporaryItems/avd/running")
    }

    /// The real emulator launcher. With the forwarder installed (`EmulatorForwarder`) the SDK's `emulator`
    /// is a script and the original sits beside it as `emulator.real`.
    static var emulatorExecutable: URL {
        let folder = root.appendingPathComponent("emulator")
        let real = folder.appendingPathComponent("emulator.real")
        return FileManager.default.isExecutableFile(atPath: real.path) ? real : folder.appendingPathComponent("emulator")
    }

    static func tool(_ name: String) -> URL {
        root.appendingPathComponent("cmdline-tools/latest/bin/\(name)")
    }

    /// avdmanager and sdkmanager need Java. An app started from Finder has no JAVA_HOME, so look where
    /// Android Studio and Homebrew put one, then ask macOS.
    static let toolEnvironment: [String: String] = {
        var env = ProcessInfo.processInfo.environment
        env["ANDROID_HOME"] = root.path
        if env["JAVA_HOME"] == nil {
            let candidates = [
                "/Applications/Android Studio.app/Contents/jbr/Contents/Home",
                "/opt/homebrew/opt/openjdk@17/libexec/openjdk.jdk/Contents/Home",
                "/opt/homebrew/opt/openjdk/libexec/openjdk.jdk/Contents/Home",
            ]
            if let home = candidates.first(where: { FileManager.default.fileExists(atPath: $0) }) ?? systemJavaHome() {
                env["JAVA_HOME"] = home
            }
        }
        return env
    }()

    private static func systemJavaHome() -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/libexec/java_home")
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return nil }
        process.waitUntilExit()
        let path = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return process.terminationStatus == 0 && !path.isEmpty ? path : nil
    }

    /// Runs an SDK tool and returns its output. `onLine` sees each line as it arrives, including lines ended by
    /// a carriage return, which is how sdkmanager redraws its progress bar. Cancelling the task stops the tool.
    static func run(
        _ tool: String,
        _ arguments: [String],
        input: String? = nil,
        onLine: (@Sendable (String) -> Void)? = nil
    ) async throws -> String {
        let process = Process()
        process.executableURL = Self.tool(tool)
        process.arguments = arguments
        process.environment = toolEnvironment
        let output = Pipe()
        process.standardOutput = output
        process.standardError = output
        let stdin = Pipe()
        process.standardInput = stdin
        let collector = OutputCollector(onLine: onLine)
        output.fileHandleForReading.readabilityHandler = { handle in collector.append(handle.availableData) }

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
                process.terminationHandler = { process in
                    output.fileHandleForReading.readabilityHandler = nil
                    collector.append(output.fileHandleForReading.readDataToEndOfFile())
                    let text = collector.text
                    if process.terminationStatus == 0 {
                        continuation.resume(returning: text)
                    } else if process.terminationReason == .uncaughtSignal {
                        continuation.resume(throwing: CancellationError())
                    } else {
                        let tail = text.split(whereSeparator: \.isNewline).suffix(3).joined(separator: "\n")
                        continuation.resume(throwing: ShellError("\(tool) failed (\(process.terminationStatus)): \(tail)"))
                    }
                }
                do {
                    try process.run()
                    if let input { stdin.fileHandleForWriting.write(Data(input.utf8)) }
                    try? stdin.fileHandleForWriting.close()
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        } onCancel: {
            process.terminate()
        }
    }
}

/// Gathers a tool's output and hands out complete lines.
private final class OutputCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()
    private var pending = ""
    private let onLine: (@Sendable (String) -> Void)?

    init(onLine: (@Sendable (String) -> Void)?) {
        self.onLine = onLine
    }

    func append(_ chunk: Data) {
        guard !chunk.isEmpty else { return }
        lock.lock()
        data.append(chunk)
        pending += String(decoding: chunk, as: UTF8.self)
        var lines: [String] = []
        while let end = pending.firstIndex(where: { $0 == "\n" || $0 == "\r" }) {
            lines.append(String(pending[..<end]))
            pending = String(pending[pending.index(after: end)...])
        }
        lock.unlock()
        lines.filter { !$0.isEmpty }.forEach { onLine?($0) }
    }

    var text: String {
        lock.lock()
        defer { lock.unlock() }
        return String(decoding: data, as: UTF8.self)
    }
}

/// `key=value` lines such as an AVD's `config.ini`. Edits keep the other lines and their order.
struct IniFile {
    private var lines: [String]

    init(contentsOf url: URL) throws {
        lines = try String(contentsOf: url, encoding: .utf8).components(separatedBy: .newlines)
    }

    var values: [String: String] {
        var values: [String: String] = [:]
        for line in lines {
            let parts = line.split(separator: "=", maxSplits: 1)
            if parts.count == 2 {
                values[parts[0].trimmingCharacters(in: .whitespaces)] = parts[1].trimmingCharacters(in: .whitespaces)
            }
        }
        return values
    }

    subscript(key: String) -> String? {
        get { values[key] }
        set {
            let index = lines.firstIndex { $0.split(separator: "=", maxSplits: 1).first?.trimmingCharacters(in: .whitespaces) == key }
            switch (index, newValue) {
            case let (index?, value?): lines[index] = "\(key)=\(value)"
            case let (index?, nil): lines.remove(at: index)
            case let (nil, value?):
                let end = lines.last?.isEmpty == true ? lines.count - 1 : lines.count
                lines.insert("\(key)=\(value)", at: end)
            case (nil, nil): break
            }
        }
    }

    func write(to url: URL) throws {
        try lines.joined(separator: "\n").write(to: url, atomically: true, encoding: .utf8)
    }
}
