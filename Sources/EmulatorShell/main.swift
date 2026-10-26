import AppKit

// Usage: EmulatorShell [avd-name [emulator arguments…]]
// With an AVD name the device opens directly, the remaining arguments going to the emulator (this is how the
// SDK forwarder hands over launches from Android Studio and Flutter); without one the device manager opens.
let arguments = Array(CommandLine.arguments.dropFirst())

// Writing to a pipe its reader has closed must not end the app (see `Log`).
signal(SIGPIPE, SIG_IGN)

MainActor.assumeIsolated {
    let app = NSApplication.shared
    let delegate = AppDelegate(launchAvd: arguments.first, emulatorArguments: Array(arguments.dropFirst()))
    app.delegate = delegate
    app.setActivationPolicy(.regular)
    app.activate()
    app.run()
}
