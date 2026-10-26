import Foundation

/// Makes Android Studio, Flutter and the `android` CLI open devices in this app. They all run the SDK's
/// `emulator/emulator`, with no setting for another program, so that file is replaced by a small script:
/// device launches go to this app, everything else (`-list-avds`, `-version`, headless `-no-window` runs
/// such as Gradle Managed Devices) to the original, kept beside it as `emulator.real`.
enum EmulatorForwarder {
    enum State: Equatable {
        case notInstalled
        case installed(appPath: String)
        /// An SDK update put a new `emulator` in place; `emulator.real` is the old one.
        case replacedBySdkUpdate
        /// `emulator` is a script this app did not write; it is left alone.
        case foreign
    }

    /// Set for emulators this app starts itself, so a forwarder that still receives the call runs the real one.
    static let passThroughVariable = "EMULATOR_SHELL_PASSTHROUGH"

    private static let marker = "# Emulator Shell forwarder"
    private static var folder: URL { Sdk.root.appendingPathComponent("emulator") }
    private static var launcher: URL { folder.appendingPathComponent("emulator") }
    private static var real: URL { folder.appendingPathComponent("emulator.real") }

    /// This executable, where the forwarder points.
    static var appPath: String {
        (Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0])).resolvingSymlinksInPath().path
    }

    static var state: State {
        let head = (try? FileHandle(forReadingFrom: launcher)).flatMap { try? $0.read(upToCount: 4096) } ?? Data()
        guard head.starts(with: Array("#!".utf8)), let script = String(data: head, encoding: .utf8) else {
            // Not text: the SDK's own binary.
            return FileManager.default.fileExists(atPath: real.path) ? .replacedBySdkUpdate : .notInstalled
        }
        guard script.contains(marker) else { return .foreign }
        let path = script.split(separator: "\n").first { $0.hasPrefix("app=") }
            .map { String($0.dropFirst("app=".count)).trimmingCharacters(in: CharacterSet(charactersIn: "\"")) }
        return .installed(appPath: path ?? "")
    }

    static func install() throws {
        let fm = FileManager.default
        switch state {
        case .foreign:
            throw ShellError("\(launcher.path) 已经被别的脚本替换过，没有改动它。")
        case .notInstalled, .replacedBySdkUpdate:
            // After an SDK update the new binary replaces the old `emulator.real`, so launcher and qemu match.
            if fm.fileExists(atPath: real.path) { try fm.removeItem(at: real) }
            try fm.moveItem(at: launcher, to: real)
        case .installed:
            break
        }
        try script.write(to: launcher, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: launcher.path)
        Log.info("installed the emulator forwarder in \(folder.path)")
    }

    static func uninstall() throws {
        guard case .installed = state else { return }
        let fm = FileManager.default
        try fm.removeItem(at: launcher)
        try fm.moveItem(at: real, to: launcher)
        Log.info("restored the SDK's emulator in \(folder.path)")
    }

    private static var script: String {
        """
        #!/bin/zsh
        \(marker), installed by \(appPath).
        # Device launches open in Emulator Shell; everything else runs the original emulator, emulator.real.
        # Remove it from Emulator Shell's app menu, or move emulator.real back over this file.
        app="\(appPath)"
        real="${0:A:h}/emulator.real"
        print -r -- "$(date '+%F %T') $*" >> ~/Library/Logs/EmulatorShell-forwarder.log 2>/dev/null
        [[ -n $\(passThroughVariable) || ! -x $app ]] && exec "$real" "$@"
        avd=""
        rest=()
        args=("$@")
        for (( i = 1; i <= $#args; i++ )); do
          case $args[i] in
            -avd) avd=$args[i+1]; (( i++ )) ;;
            @*) avd=${args[i]#@} ;;
            -no-window|-list-avds|-version|-help*|-accel-check|-snapshot-list|-check-snapshot-loadable|-read-only) exec "$real" "$@" ;;
            *) rest+=("$args[i]") ;;
          esac
        done
        [[ -z $avd ]] && exec "$real" "$@"
        exec "$app" "$avd" "${rest[@]}"

        """
    }

    /// The emulator arguments a launcher passed that this app can hand on. It runs the emulator without
    /// a window and with its own gRPC port and GPU mode, so those flags (and Studio's tool-window ones) go.
    static func forwardable(_ arguments: [String]) -> [String] {
        let dropped: Set<String> = ["-no-window", "-qt-hide-window", "-grpc-use-token", "-grpc-use-jwt"]
        let droppedWithValue: Set<String> = ["-grpc", "-gpu", "-idle-grpc-timeout", "-avd"]
        var result: [String] = []
        var skipNext = false
        for argument in arguments {
            if skipNext { skipNext = false; continue }
            if droppedWithValue.contains(argument) { skipNext = true; continue }
            if dropped.contains(argument) || argument.hasPrefix("@") || argument == "--" { continue }
            result.append(argument)
        }
        return result
    }

    // MARK: - Command-line tool

    /// `emulator-shell <avd>` for the terminal, linked into the first writable bin folder on the PATH.
    static var commandLink: URL? {
        let candidates = ["/opt/homebrew/bin", "/usr/local/bin", NSHomeDirectory() + "/.local/bin"]
        return candidates.first { FileManager.default.isWritableFile(atPath: $0) }
            .map { URL(fileURLWithPath: $0).appendingPathComponent("emulator-shell") }
    }

    static var isCommandInstalled: Bool {
        guard let link = commandLink, let target = try? FileManager.default.destinationOfSymbolicLink(atPath: link.path) else { return false }
        return target == appPath
    }

    static func installCommand() throws -> URL {
        guard let link = commandLink else { throw ShellError("找不到可写的 bin 目录（/opt/homebrew/bin、/usr/local/bin 或 ~/.local/bin）。") }
        let fm = FileManager.default
        if let existing = try? fm.destinationOfSymbolicLink(atPath: link.path), existing != appPath {
            try fm.removeItem(at: link)
        } else if fm.fileExists(atPath: link.path), (try? fm.destinationOfSymbolicLink(atPath: link.path)) == nil {
            throw ShellError("\(link.path) 已经存在，而且不是链接，没有改动它。")
        }
        if !isCommandInstalled { try fm.createSymbolicLink(atPath: link.path, withDestinationPath: appPath) }
        return link
    }

    static func uninstallCommand() throws {
        guard isCommandInstalled, let link = commandLink else { return }
        try FileManager.default.removeItem(at: link)
    }
}
