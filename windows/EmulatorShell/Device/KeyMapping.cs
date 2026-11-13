using Windows.System;

namespace EmulatorShell;

/// Maps Windows key events to the W3C `key` values the emulator's `sendKey` understands.
static class KeyMapping
{
    /// Esc doubles as Back, matching the stock emulator window.
    private static readonly Dictionary<VirtualKey, string> SpecialKeys = new()
    {
        [VirtualKey.Enter] = "Enter", [VirtualKey.Tab] = "Tab", [VirtualKey.Back] = "Backspace", [VirtualKey.Delete] = "Delete",
        [VirtualKey.Escape] = "GoBack",
        [VirtualKey.Left] = "ArrowLeft", [VirtualKey.Right] = "ArrowRight", [VirtualKey.Down] = "ArrowDown", [VirtualKey.Up] = "ArrowUp",
        [VirtualKey.Home] = "Home", [VirtualKey.End] = "End", [VirtualKey.PageUp] = "PageUp", [VirtualKey.PageDown] = "PageDown",
    };

    /// A key that is not text: its W3C name, or null when it is text (that comes as characters) or a shortcut.
    public static string? Special(VirtualKey key, bool control, bool alt) =>
        control || alt ? null : SpecialKeys.GetValueOrDefault(key);

    /// Printable ASCII goes through `key` so Shift is synthesised; anything else is best-effort `text`.
    public static (string? Key, string? Text) Character(string characters) =>
        characters.Length == 1 && characters[0] is >= ' ' and < '\x7f' ? (characters, null) : (null, characters);
}
