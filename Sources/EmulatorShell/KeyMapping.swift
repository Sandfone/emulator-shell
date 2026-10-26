import AppKit

/// Maps macOS key events to the W3C `key` values the emulator's `sendKey` understands.
enum KeyMapping {
    enum Output {
        case key(String)
        case text(String)
    }

    /// Esc doubles as Back, matching the stock emulator window.
    private static let specialKeys: [UInt16: String] = [
        36: "Enter", 76: "Enter", 48: "Tab", 51: "Backspace", 117: "Delete", 53: "GoBack",
        123: "ArrowLeft", 124: "ArrowRight", 125: "ArrowDown", 126: "ArrowUp",
        115: "Home", 119: "End", 116: "PageUp", 121: "PageDown",
    ]

    static func map(_ event: NSEvent) -> Output? {
        if let key = specialKeys[event.keyCode] {
            return .key(key)
        }
        guard let characters = event.characters, !characters.isEmpty,
              !event.modifierFlags.contains(.command), !event.modifierFlags.contains(.control)
        else { return nil }
        // Printable ASCII goes through `key` so Shift is synthesised; anything else is best-effort `text`.
        if characters.count == 1, let scalar = characters.unicodeScalars.first, (32..<127).contains(scalar.value) {
            return .key(characters)
        }
        return .text(characters)
    }
}
