// Draws the installer window's artwork: the background, at 1x and 2x, and the icon of the environment check.
// The window is drawn as a landscape Android screen, with a status bar and the gesture pill, and the arrow from
// the app to Applications is a swipe: a touch ripple where the finger lands and a trail of dots.
// Usage: swift scripts/make-dmg-art.swift <output folder> <version>
// Writes background.png, background@2x.png and check-icon.png (1024 × 1024).
import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments[1])
let version = CommandLine.arguments.count > 2 ? CommandLine.arguments[2] : ""

// Window content size in points; make-dmg.sh uses the same numbers for the icon positions.
let width: CGFloat = 660, height: CGFloat = 500
let appCenter = NSPoint(x: 180, y: 222), applicationsCenter = NSPoint(x: 480, y: 222)
let checkCenter = NSPoint(x: 120, y: 384)

let green = NSColor(red: 0.13, green: 0.70, blue: 0.45, alpha: 1)
let ink = NSColor(red: 0.11, green: 0.13, blue: 0.16, alpha: 1)
let muted = NSColor(red: 0.40, green: 0.44, blue: 0.49, alpha: 1)

func text(_ string: String, size: CGFloat, weight: NSFont.Weight, color: NSColor) -> NSAttributedString {
    NSAttributedString(string: string, attributes: [.font: NSFont.systemFont(ofSize: size, weight: weight),
                                                    .foregroundColor: color])
}

func drawBackground() {
    // Screen: a pale, slightly green wallpaper.
    NSGradient(starting: NSColor(red: 0.97, green: 0.99, blue: 0.98, alpha: 1),
               ending: NSColor(red: 0.90, green: 0.96, blue: 0.93, alpha: 1))!
        .draw(in: NSRect(x: 0, y: 0, width: width, height: height), angle: -90)

    // A soft wash of colour behind the two icons, like a home screen's light.
    for (center, color) in [(appCenter, green), (applicationsCenter, NSColor.systemBlue)] {
        NSGradient(colors: [color.withAlphaComponent(0.13), color.withAlphaComponent(0)])!
            .draw(fromCenter: center, radius: 0, toCenter: center, radius: 120, options: [])
    }

    // Status bar: version where the clock goes, Wi-Fi and battery on the right.
    text(version.isEmpty ? "Emulator Shell" : version, size: 12, weight: .semibold, color: ink)
        .draw(at: NSPoint(x: 22, y: 9))
    ink.setFill(); ink.setStroke()
    let wifi = NSPoint(x: width - 64, y: 23)
    for (i, r) in [CGFloat(10), 6.5, 3].enumerated() {
        let arc = NSBezierPath()
        if i == 2 {
            arc.appendOval(in: NSRect(x: wifi.x - 2, y: wifi.y - 3, width: 4, height: 4)); arc.fill(); continue
        }
        arc.appendArc(withCenter: wifi, radius: r, startAngle: 225, endAngle: 315, clockwise: false)
        arc.lineWidth = 1.8; arc.lineCapStyle = .round; arc.stroke()
    }
    let battery = NSRect(x: width - 46, y: 12, width: 22, height: 11)
    let shell = NSBezierPath(roundedRect: battery, xRadius: 3, yRadius: 3); shell.lineWidth = 1.2; shell.stroke()
    NSBezierPath(roundedRect: battery.insetBy(dx: 2, dy: 2).divided(atDistance: 14, from: .minXEdge).slice,
                 xRadius: 1.5, yRadius: 1.5).fill()
    NSBezierPath(roundedRect: NSRect(x: battery.maxX + 1, y: battery.midY - 2.5, width: 2, height: 5),
                 xRadius: 1, yRadius: 1).fill()

    // Title.
    let title = text("Emulator Shell", size: 26, weight: .bold, color: ink)
    title.draw(at: NSPoint(x: (width - title.size().width) / 2, y: 50))
    let subtitle = text("把图标滑到「应用程序」，就装好了", size: 14, weight: .regular, color: muted)
    subtitle.draw(at: NSPoint(x: (width - subtitle.size().width) / 2, y: 88))

    // The swipe: ripple where the finger touches down, then dots that grow towards the target, then a chevron.
    let y = appCenter.y
    let start = appCenter.x + 78, end = applicationsCenter.x - 82
    for (radius, alpha) in [(CGFloat(17), 0.10), (11, 0.20)] {
        green.withAlphaComponent(alpha).setFill()
        NSBezierPath(ovalIn: NSRect(x: start - radius, y: y - radius, width: radius * 2, height: radius * 2)).fill()
    }
    green.setFill()
    NSBezierPath(ovalIn: NSRect(x: start - 6, y: y - 6, width: 12, height: 12)).fill()
    let dots = 7
    for i in 0..<dots {
        let t = CGFloat(i + 1) / CGFloat(dots + 1)
        let x = start + 18 + (end - start - 44) * t
        let r = 2 + 2.2 * t
        green.withAlphaComponent(0.25 + 0.7 * t).setFill()
        NSBezierPath(ovalIn: NSRect(x: x - r, y: y - r, width: r * 2, height: r * 2)).fill()
    }
    let chevron = NSBezierPath()
    chevron.move(to: NSPoint(x: end - 14, y: y - 12))
    chevron.line(to: NSPoint(x: end, y: y))
    chevron.line(to: NSPoint(x: end - 14, y: y + 12))
    chevron.lineWidth = 4; chevron.lineCapStyle = .round; chevron.lineJoinStyle = .round
    green.setStroke(); chevron.stroke()

    // The card for the environment check, like a notification on the screen.
    let card = NSRect(x: 40, y: 322, width: width - 80, height: 140)
    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = NSColor.black.withAlphaComponent(0.08); shadow.shadowBlurRadius = 14
    shadow.shadowOffset = NSSize(width: 0, height: -3); shadow.set()
    NSColor.white.withAlphaComponent(0.85).setFill()
    NSBezierPath(roundedRect: card, xRadius: 20, yRadius: 20).fill()
    NSGraphicsContext.restoreGraphicsState()
    let left = checkCenter.x + 62
    text("装好后，先检查一下开发环境", size: 15, weight: .semibold, color: ink).draw(at: NSPoint(x: left, y: card.minY + 38))
    let body = NSMutableParagraphStyle(); body.lineSpacing = 3
    NSAttributedString(string: "双击左边的「检查开发环境」：Android SDK、模拟器、Java、\n你的 AVD 和连着的手机是否就绪，缺什么、怎么装都会告诉你。",
                       attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: muted, .paragraphStyle: body])
        .draw(at: NSPoint(x: left, y: card.minY + 64))

    // Gesture pill.
    ink.withAlphaComponent(0.55).setFill()
    NSBezierPath(roundedRect: NSRect(x: width / 2 - 54, y: height - 16, width: 108, height: 5), xRadius: 2.5, yRadius: 2.5).fill()
}

func drawCheckIcon(size: CGFloat) {
    // Same tile as the app icon, in blue, with a phone and a green tick badge.
    let s = size / 1024
    let tile = NSBezierPath(roundedRect: NSRect(x: 100 * s, y: 100 * s, width: 824 * s, height: 824 * s),
                            xRadius: 185 * s, yRadius: 185 * s)
    NSGradient(starting: NSColor(red: 0.36, green: 0.62, blue: 1.00, alpha: 1),
               ending: NSColor(red: 0.20, green: 0.36, blue: 0.86, alpha: 1))!.draw(in: tile, angle: -90)
    // Checklist lines.
    NSColor.white.setFill()
    for (i, w) in [CGFloat(380), 300, 340].enumerated() {
        let rowY = (660 - CGFloat(i) * 150) * s
        NSBezierPath(ovalIn: NSRect(x: 230 * s, y: rowY, width: 70 * s, height: 70 * s)).fill()
        NSBezierPath(roundedRect: NSRect(x: 340 * s, y: rowY + 18 * s, width: w * s, height: 34 * s),
                     xRadius: 17 * s, yRadius: 17 * s).fill()
    }
    // Badge.
    let badge = NSRect(x: 560 * s, y: 160 * s, width: 300 * s, height: 300 * s)
    NSColor.white.setFill(); NSBezierPath(ovalIn: badge.insetBy(dx: -16 * s, dy: -16 * s)).fill()
    NSColor(red: 0.13, green: 0.70, blue: 0.45, alpha: 1).setFill(); NSBezierPath(ovalIn: badge).fill()
    let tick = NSBezierPath()
    tick.move(to: NSPoint(x: badge.minX + 80 * s, y: badge.midY))
    tick.line(to: NSPoint(x: badge.minX + 130 * s, y: badge.midY - 52 * s))
    tick.line(to: NSPoint(x: badge.maxX - 70 * s, y: badge.midY + 62 * s))
    tick.lineWidth = 34 * s; tick.lineCapStyle = .round; tick.lineJoinStyle = .round
    NSColor.white.setStroke(); tick.stroke()
}

func render(pixelsWide: Int, pixelsHigh: Int, scale: CGFloat, flipped: Bool, to name: String, draw: () -> Void) {
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixelsWide, pixelsHigh: pixelsHigh,
                                  bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                  colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    let context = NSGraphicsContext(bitmapImageRep: bitmap)!
    NSGraphicsContext.current = context
    let cg = context.cgContext
    if flipped {
        cg.translateBy(x: 0, y: CGFloat(pixelsHigh)); cg.scaleBy(x: scale, y: -scale)
        // Text draws upside down in a flipped CG context unless AppKit knows it is flipped.
        NSGraphicsContext.current = NSGraphicsContext(cgContext: cg, flipped: true)
    } else {
        cg.scaleBy(x: scale, y: scale)
    }
    draw()
    NSGraphicsContext.restoreGraphicsState()
    bitmap.size = NSSize(width: CGFloat(pixelsWide) / scale, height: CGFloat(pixelsHigh) / scale)
    try! bitmap.representation(using: .png, properties: [:])!.write(to: out.appendingPathComponent(name))
}

// The background is laid out top-down, as Finder places icons.
render(pixelsWide: Int(width), pixelsHigh: Int(height), scale: 1, flipped: true, to: "background.png", draw: drawBackground)
render(pixelsWide: Int(width * 2), pixelsHigh: Int(height * 2), scale: 2, flipped: true, to: "background@2x.png", draw: drawBackground)
render(pixelsWide: 1024, pixelsHigh: 1024, scale: 1, flipped: false, to: "check-icon.png") { drawCheckIcon(size: 1024) }
