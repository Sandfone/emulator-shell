// Draws the app icon: a dark phone on a green rounded square, written as a 1024 × 1024 PNG.
// Usage: swift scripts/make-icon.swift <output.png>
import AppKit

let size: CGFloat = 1024
let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
    // macOS icon grid: an 824 pt rounded square centred in the 1024 canvas.
    let tile = NSRect(x: 100, y: 100, width: 824, height: 824)
    let tilePath = NSBezierPath(roundedRect: tile, xRadius: 185, yRadius: 185)
    NSGradient(starting: NSColor(red: 0.24, green: 0.86, blue: 0.52, alpha: 1),
               ending: NSColor(red: 0.05, green: 0.55, blue: 0.38, alpha: 1))!.draw(in: tilePath, angle: -90)

    let body = NSRect(x: 352, y: 210, width: 320, height: 604)
    NSColor(white: 0.10, alpha: 1).setFill()
    NSBezierPath(roundedRect: body, xRadius: 58, yRadius: 58).fill()

    let screen = body.insetBy(dx: 18, dy: 18)
    NSGradient(starting: NSColor(red: 0.62, green: 0.93, blue: 1.00, alpha: 1),
               ending: NSColor(red: 0.27, green: 0.52, blue: 0.95, alpha: 1))!
        .draw(in: NSBezierPath(roundedRect: screen, xRadius: 42, yRadius: 42), angle: -90)

    NSColor(white: 0.10, alpha: 1).setFill()
    NSBezierPath(ovalIn: NSRect(x: body.midX - 13, y: screen.maxY - 44, width: 26, height: 26)).fill()

    // Power and volume keys on the right edge.
    NSBezierPath(roundedRect: NSRect(x: body.maxX - 2, y: 600, width: 14, height: 70), xRadius: 6, yRadius: 6).fill()
    NSBezierPath(roundedRect: NSRect(x: body.maxX - 2, y: 470, width: 14, height: 100), xRadius: 6, yRadius: 6).fill()
    return true
}

let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size), bitsPerSample: 8,
                              samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                              bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
image.draw(in: NSRect(x: 0, y: 0, width: size, height: size))
NSGraphicsContext.restoreGraphicsState()
try! bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[1]))
