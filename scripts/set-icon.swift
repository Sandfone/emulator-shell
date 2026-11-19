// Gives a file or folder in Finder a custom icon from an image (PNG or ICNS).
// Usage: swift scripts/set-icon.swift <image> <path>
import AppKit

let arguments = CommandLine.arguments
guard arguments.count == 3, let image = NSImage(contentsOfFile: arguments[1]) else {
    FileHandle.standardError.write("usage: set-icon.swift <image> <path>\n".data(using: .utf8)!)
    exit(2)
}
if !NSWorkspace.shared.setIcon(image, forFile: arguments[2], options: []) {
    FileHandle.standardError.write("could not set the icon of \(arguments[2])\n".data(using: .utf8)!)
    exit(1)
}
