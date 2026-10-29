import Foundation

/// Follows the guest's log for lines matching a pattern, from when it starts, through `adb logcat`: the
/// emulator's own `streamLogcat` sends nothing. The filtering happens in the guest, so only matching lines
/// cross over. adb gives up while the device is not up yet, so it is run again after a pause whenever it
/// ends, until `stop()`.
final class GuestLogWatch: @unchecked Sendable {
    private let queue = DispatchQueue(label: "GuestLogWatch")
    private let serial: @Sendable () -> String?
    private let pattern: String
    private let onLine: @Sendable (String) -> Void
    private var process: Process?
    private var stopped = false

    /// `serial` is asked again on each run: the emulator writes it down only once it is up.
    init(serial: @escaping @Sendable () -> String?, pattern: String, onLine: @escaping @Sendable (String) -> Void) {
        self.serial = serial
        self.pattern = pattern
        self.onLine = onLine
        queue.async { self.run() }
    }

    func stop() {
        queue.async {
            self.stopped = true
            self.process?.terminate()
            self.process = nil
        }
    }

    private func run() {
        guard !stopped else { return }
        guard let serial = serial() else { return retry() }
        let process = Process()
        process.executableURL = Sdk.root.appendingPathComponent("platform-tools/adb")
        // -T 1: from the newest line on, not what the log already holds.
        process.arguments = ["-s", serial, "logcat", "-T", "1", "-v", "brief", "-e", pattern]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        var pending = ""
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard let self, !data.isEmpty else { return }
            pending += String(decoding: data, as: UTF8.self)
            while let newline = pending.firstIndex(of: "\n") {
                let line = String(pending[..<newline])
                pending.removeSubrange(...newline)
                // -T 1 shows the one newest line too, whatever it is.
                if line.range(of: self.pattern, options: .regularExpression) != nil { self.onLine(line) }
            }
        }
        process.terminationHandler = { [weak self] _ in
            output.fileHandleForReading.readabilityHandler = nil
            self?.queue.async { self?.retry() }
        }
        do {
            try process.run()
            self.process = process
        } catch {
            Log.error("cannot run adb logcat: \(error)")
        }
    }

    private func retry() {
        guard !stopped else { return }
        process = nil
        queue.asyncAfter(deadline: .now() + 3) { self.run() }
    }
}
