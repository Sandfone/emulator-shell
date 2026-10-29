import Foundation

/// Launches `emulator -avd <name> -no-window -gpu <mode> -grpc <port>` and stops it on request.
final class EmulatorProcess: @unchecked Sendable {
    let avdName: String
    let grpcPort: Int
    private let gpu: String
    private let extraArguments: [String]
    private var process: Process?

    /// How long an emulator may take to save its quick-boot snapshot and exit before it is considered stuck.
    /// Saving has taken over 30 s here.
    static let shutdownTimeout = 90

    /// Called on an arbitrary thread when the emulator exits, however that happens.
    var onExit: (@Sendable () -> Void)?

    init(avdName: String, grpcPort: Int, gpu: String = "host", extraArguments: [String] = []) {
        self.avdName = avdName
        self.grpcPort = grpcPort
        self.gpu = gpu
        self.extraArguments = extraArguments
    }

    func start(logFile: URL) throws {
        let emulator = Sdk.emulatorExecutable
        guard FileManager.default.isExecutableFile(atPath: emulator.path) else {
            throw ShellError("emulator binary not found at \(emulator.path)")
        }
        FileManager.default.createFile(atPath: logFile.path, contents: nil)
        let log = try FileHandle(forWritingTo: logFile)

        let process = Process()
        process.executableURL = emulator
        process.arguments = ["-avd", avdName, "-no-window", "-gpu", gpu, "-grpc", String(grpcPort)] + extraArguments
        process.environment = ProcessInfo.processInfo.environment.merging([
            // Tells the forwarder, should this still reach it, to run the real emulator instead of this app.
            EmulatorForwarder.passThroughVariable: "1",
            // After SIGINT the emulator kills itself after 20 s by default, even in the middle of saving the
            // snapshot, which is then lost.
            "ANDROID_EMULATOR_WAIT_TIME_BEFORE_KILL": String(Self.shutdownTimeout),
        ]) { $1 }
        process.standardOutput = log
        process.standardError = log
        process.terminationHandler = { [weak self] _ in self?.onExit?() }
        try process.run()
        self.process = process
    }

    var isRunning: Bool { process?.isRunning ?? false }

    /// SIGINT lets the emulator shut down the same way as closing its window (quick-boot snapshot included).
    /// It goes to the emulator process alone: `Process.interrupt()` would signal the child's whole
    /// process group, netsimd included (the Bluetooth/Wi-Fi daemon the emulator starts, which other running
    /// emulators share too): netsimd then quits while qemu is still saving the snapshot, and qemu's attempt to
    /// reconnect to it crashes in gRPC (`bad_function_call`), losing the snapshot.
    func interrupt() {
        guard let process, process.isRunning else { return }
        kill(process.processIdentifier, SIGINT)
    }

    func terminate() {
        guard let process, process.isRunning else { return }
        process.terminate()
    }

    /// Interrupts and waits up to `timeout` for the snapshot to be saved, then kills the process.
    func stop(timeout: TimeInterval = TimeInterval(shutdownTimeout)) {
        guard let process, process.isRunning else { return }
        interrupt()
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.1)
        }
        if process.isRunning {
            process.terminate()
        }
    }
}

struct ShellError: Error, CustomStringConvertible, LocalizedError {
    let description: String
    init(_ description: String) { self.description = description }
    var errorDescription: String? { description }
}
