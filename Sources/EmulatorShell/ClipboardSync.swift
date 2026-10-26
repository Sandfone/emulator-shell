import AppKit

/// Keeps the Mac pasteboard and the guest clipboard in step, both ways, while enabled.
@MainActor
final class ClipboardSync {
    private static let defaultsKey = "clipboardSync"

    var isEnabled: Bool {
        didSet {
            UserDefaults.standard.set(isEnabled, forKey: Self.defaultsKey)
            // Catch up with whatever was copied on the Mac while sync was off.
            if isEnabled { lastChangeCount = -1 }
        }
    }

    private let client: EmulatorClient
    private let pasteboard = NSPasteboard.general
    /// -1 makes the first poll push the Mac's clipboard, which is the newer one when the device starts.
    private var lastChangeCount = -1
    private var lastText: String?
    private var sawGuestState = false
    private var timer: Timer?
    private var stream: Task<Void, Never>?

    init(client: EmulatorClient) {
        self.client = client
        isEnabled = UserDefaults.standard.object(forKey: Self.defaultsKey) as? Bool ?? true
    }

    func start() {
        let deliver: @Sendable (String) -> Void = { [weak self] text in
            Task { @MainActor in self?.guestCopied(text) }
        }
        stream = Task.detached { [client] in
            do {
                try await client.streamClipboard(onText: deliver)
            } catch {
                // Stopping cancels the stream; the error that comes back then is expected.
                if !Task.isCancelled { Log.error("clipboard stream ended: \(error)") }
            }
        }
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.pollPasteboard() }
        }
    }

    func stop() {
        stream?.cancel()
        timer?.invalidate()
    }

    /// NSPasteboard has no change notification, so its change count is polled.
    private func pollPasteboard() {
        guard pasteboard.changeCount != lastChangeCount else { return }
        lastChangeCount = pasteboard.changeCount
        guard isEnabled, let text = pasteboard.string(forType: .string), text != lastText else { return }
        lastText = text
        Log.debug("clipboard → device (\(text.count) chars)")
        Task { [client] in await client.setClipboard(text) }
    }

    private func guestCopied(_ text: String) {
        // The stream opens with the guest's current clipboard, which is older than the Mac's.
        guard sawGuestState else {
            sawGuestState = true
            return
        }
        guard isEnabled, !text.isEmpty, text != lastText else { return }
        lastText = text
        Log.debug("clipboard ← device (\(text.count) chars)")
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        lastChangeCount = pasteboard.changeCount
    }
}
