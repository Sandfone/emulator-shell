import Accelerate
import CoreGraphics
import Foundation
import ImageIO

/// An emulator skin folder (`layout` + frame image + screen mask), read in the device's natural orientation.
struct DeviceSkin {
    /// Frame artwork with the mask's bezel corners and camera cutout drawn in, at the skin's full size;
    /// transparent over the visible screen.
    let frame: CGImage
    /// Alpha mask for the display layer that hides its square corners where they poke out of the frame.
    let screenClip: CGImage
    /// Size of the whole skin in skin pixels.
    let size: CGSize
    /// Display rectangle in skin pixels, top-left origin. Its size equals the device resolution.
    let display: CGRect
    /// Hardware buttons drawn on the frame's edge, clickable like the real ones.
    let sideButtons: [SideButton]

    /// The AVD's skin, or a plain rounded bezel when it has none: many device definitions ship no artwork.
    static func load(avd: Avd) throws -> DeviceSkin {
        if let folder = avd.skinFolder { return try load(folder: folder) }
        guard let width = avd.config["hw.lcd.width"].flatMap(Double.init),
              let height = avd.config["hw.lcd.height"].flatMap(Double.init)
        else {
            throw ShellError("\(avd.id) has neither a skin nor hw.lcd.width/height")
        }
        return frameless(display: CGSize(width: width, height: height))
    }

    static func load(folder: URL) throws -> DeviceSkin {
        let layout = try LayoutNode.parse(String(contentsOf: folder.appendingPathComponent("layout"), encoding: .utf8))

        // Tablets and other landscape-first devices only define a landscape layout, which is then their
        // natural orientation: the one the emulator calls unrotated.
        guard let displayNode = layout["parts"]?["device"]?["display"],
              let displayWidth = displayNode.number("width"),
              let displayHeight = displayNode.number("height"),
              let mainLayout = layout["layouts"]?["portrait"] ?? layout["layouts"]?["landscape"],
              let skinWidth = mainLayout.number("width"),
              let skinHeight = mainLayout.number("height"),
              let devicePart = mainLayout.children.first(where: { $0.value["name"]?.text == "device" })?.value,
              let displayX = devicePart.number("x"),
              let displayY = devicePart.number("y"),
              let portraitPart = layout["parts"]?["portrait"],
              let frameName = portraitPart["background"]?["image"]?.text
        else {
            throw ShellError("unsupported layout in \(folder.path)")
        }

        let display = CGRect(x: displayX, y: displayY, width: displayWidth, height: displayHeight)
        let skinSize = CGSize(width: skinWidth, height: skinHeight)
        let background = try loadImage(folder.appendingPathComponent(frameName))
        // Like the emulator, the image keeps its own pixel size; stretching it to the layout size shifts the
        // frame's edge away from the mask's near the far corners.
        let frameRect = Self.frameRect(layout: mainLayout, image: CGSize(width: background.width, height: background.height))
        let mask = try portraitPart["foreground"]?["mask"]?.text.map { try loadImage(folder.appendingPathComponent($0)) }

        // The mask draws the bezel's corners and the camera cutout over the display, partly repeating the
        // frame's edge. Drawn into the frame here, both are resampled together; as separate layers each is
        // scaled to a slightly different rectangle and the edge they share no longer lines up.
        let flipped = { (rect: CGRect) in CGRect(x: rect.minX, y: skinHeight - rect.maxY, width: rect.width, height: rect.height) }
        let frameOnly = render(skinSize) { context in context.draw(background, in: flipped(frameRect)) }
        let frame = mask.map { mask in
            render(skinSize) { context in
                context.draw(mask, in: flipped(display))
                context.draw(frameOnly, in: CGRect(origin: .zero, size: skinSize))
            }
        } ?? frameOnly

        let screenClip: CGImage
        if let mask {
            screenClip = try makeScreenClip(mask: mask, displaySize: display.size)
        } else {
            let radius = displayNode.number("corner_radius") ?? 0
            screenClip = render(display.size) { context in
                context.addPath(CGPath(roundedRect: CGRect(origin: .zero, size: display.size),
                                       cornerWidth: radius, cornerHeight: radius, transform: nil))
                context.setFillColor(.white)
                context.fillPath()
            }
        }

        return DeviceSkin(
            frame: frame,
            screenClip: screenClip,
            size: skinSize,
            display: display,
            sideButtons: try findSideButtons(frame: frameOnly, display: display)
        )
    }

    /// Where the background image goes in skin pixels: at the layout's background part, at the image's
    /// own size. Some skins' images differ from the layout by a few pixels (Pixel 10 Pro: 1410 × 2968
    /// against a 1408 × 2965 layout), and Wear OS ones are smaller and offset.
    static func frameRect(layout: LayoutNode, image: CGSize) -> CGRect {
        let part = layout.children.first { $0.value["name"]?.text == "portrait" }?.value
        return CGRect(origin: CGPoint(x: part?.number("x") ?? 0, y: part?.number("y") ?? 0), size: image)
    }

    /// A dark rounded bezel around a display of the given pixel size, for devices without skin artwork.
    static func frameless(display size: CGSize) -> DeviceSkin {
        let short = min(size.width, size.height)
        let bezel = (short * 0.035).rounded()
        let outerRadius = short * 0.09
        let innerRadius = outerRadius - bezel
        let skinSize = CGSize(width: size.width + 2 * bezel, height: size.height + 2 * bezel)
        let display = CGRect(origin: CGPoint(x: bezel, y: bezel), size: size)

        let frame = render(skinSize) { context in
            let bounds = CGRect(origin: .zero, size: skinSize)
            context.addPath(CGPath(roundedRect: bounds, cornerWidth: outerRadius, cornerHeight: outerRadius, transform: nil))
            context.addPath(CGPath(roundedRect: display, cornerWidth: innerRadius, cornerHeight: innerRadius, transform: nil))
            context.setFillColor(CGColor(gray: 0.11, alpha: 1))
            context.fillPath(using: .evenOdd)
            context.addPath(CGPath(roundedRect: bounds.insetBy(dx: 1.5, dy: 1.5),
                                   cornerWidth: outerRadius - 1.5, cornerHeight: outerRadius - 1.5, transform: nil))
            context.setStrokeColor(CGColor(gray: 0.32, alpha: 1))
            context.setLineWidth(3)
            context.strokePath()
        }
        let screenClip = render(size) { context in
            context.addPath(CGPath(roundedRect: CGRect(origin: .zero, size: size),
                                   cornerWidth: innerRadius, cornerHeight: innerRadius, transform: nil))
            context.setFillColor(.white)
            context.fillPath()
        }
        return DeviceSkin(frame: frame, screenClip: screenClip,
                          size: skinSize, display: display, sideButtons: [])
    }

    private static func render(_ size: CGSize, _ draw: (CGContext) -> Void) -> CGImage {
        let context = CGContext(
            data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!
        draw(context)
        return context.makeImage()!
    }

    private static func loadImage(_ url: URL) throws -> CGImage {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil)
        else {
            throw ShellError("cannot decode \(url.path)")
        }
        return image
    }

    /// Skins carry no button metadata, so the buttons are found as bumps on the frame's right edge:
    /// the longer one is the volume rocker (top half up, bottom half down), the shorter one power.
    private static func findSideButtons(frame: CGImage, display: CGRect) throws -> [SideButton] {
        let pixels = try RGBABuffer(image: frame, width: frame.width, height: frame.height)
        func rightEdge(_ y: Int) -> Int {
            var x = pixels.width - 1
            while x > 0 && pixels.alpha(x: x, y: y) < 128 { x -= 1 }
            return x
        }
        let edges = (0..<pixels.height).map(rightEdge)
        // The median over the display's rows: any single row may be one of the buttons.
        let body = edges[Int(display.minY)..<Int(display.maxY)].sorted()[Int(display.height) / 2]
        var bumps: [Range<Int>] = []
        var start: Int?
        for y in 0..<pixels.height {
            let out = edges[y] > body + 2
            if out, start == nil { start = y }
            if !out, let first = start {
                bumps.append(first..<y)
                start = nil
            }
        }
        Log.debug("side buttons: frame edge at x \(body), bumps at rows \(bumps)")
        guard (1...2).contains(bumps.count) else { return [] }

        // A hit area reaching into the bezel, since the drawn bumps are only a few pixels wide.
        func rect(_ rows: Range<Int>) -> CGRect {
            CGRect(x: body - 24, y: rows.lowerBound, width: pixels.width - body + 24, height: rows.count)
        }
        let sorted = bumps.sorted { $0.count < $1.count }
        var buttons = [SideButton(key: "Power", label: "电源", rect: rect(sorted[0]))]
        if sorted.count == 2 {
            let rocker = sorted[1]
            let middle = rocker.lowerBound + rocker.count / 2
            buttons.append(SideButton(key: "AudioVolumeUp", label: "音量 +", rect: rect(rocker.lowerBound..<middle)))
            buttons.append(SideButton(key: "AudioVolumeDown", label: "音量 −", rect: rect(middle..<rocker.upperBound)))
        }
        return buttons
    }

    /// Where the screen shows: the mask's opening, reaching a little under the bezel around it.
    ///
    /// The display rectangle's square corners stick out past the rounded frame on some skins. The stock
    /// emulator hides them by shaping its window; here they are flood-filled from each corner through the
    /// mask's transparent pixels and left out. The screen must not run under the whole bezel either: it
    /// would show through the bezel's soft outer edge, and the screen layer sits on a rectangle rounded out
    /// to whole pixels, so it can stick out past that edge.
    private static func makeScreenClip(mask: CGImage, displaySize: CGSize) throws -> CGImage {
        let width = Int(displaySize.width)
        let height = Int(displaySize.height)
        let pixels = try RGBABuffer(image: mask, width: width, height: height)

        let corners = [(0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1)]
        var outside = [Bool](repeating: false, count: width * height)
        var stack = corners.filter { pixels.alpha(x: $0.0, y: $0.1) < 128 }.map { $0.1 * width + $0.0 }
        var filled = 0
        while let index = stack.popLast() {
            if outside[index] || pixels.bytes[index * 4 + 3] >= 128 { continue }
            outside[index] = true
            filled += 1
            let x = index % width, y = index / width
            if x > 0 { stack.append(index - 1) }
            if x < width - 1 { stack.append(index + 1) }
            if y > 0 { stack.append(index - width) }
            if y < height - 1 { stack.append(index + width) }
        }
        // A fill this large means the mask is open to the screen area; keep the corners rather than lose the screen.
        if filled > width * height / 100 {
            Log.error("corner flood reached \(filled) px; skipping corner clipping")
            outside = [Bool](repeating: false, count: width * height)
        }

        // The opening, grown by a few skin pixels so it runs under the bezel's soft inner edge at any scale
        // while staying far inside the bezel's outer edge.
        let reach = 4
        var opening = [UInt8](repeating: 0, count: width * height)
        for index in 0..<(width * height) where !outside[index] && pixels.bytes[index * 4 + 3] < 128 {
            opening[index] = 255
        }
        var grown = [UInt8](repeating: 0, count: width * height)
        let side = vImagePixelCount(2 * reach + 1)
        let error = opening.withUnsafeMutableBytes { source in
            grown.withUnsafeMutableBytes { destination in
                var input = vImage_Buffer(data: source.baseAddress, height: vImagePixelCount(height), width: vImagePixelCount(width), rowBytes: width)
                var output = vImage_Buffer(data: destination.baseAddress, height: vImagePixelCount(height), width: vImagePixelCount(width), rowBytes: width)
                return vImageMax_Planar8(&input, &output, nil, 0, 0, side, side, vImage_Flags(kvImageEdgeExtend))
            }
        }
        guard error == kvImageNoError else { throw ShellError("cannot grow the screen clip (\(error))") }

        var clip = RGBABuffer(width: width, height: height)
        for index in 0..<(width * height) where grown[index] != 0 && !outside[index] {
            clip.bytes.replaceSubrange(index * 4..<(index * 4 + 4), with: [255, 255, 255, 255])
        }
        return try clip.makeImage()
    }
}

struct SideButton {
    /// W3C key name sent while the button is held.
    let key: String
    let label: String
    /// Hit area in skin pixels, top-left origin.
    let rect: CGRect
}

/// Premultiplied RGBA8 pixels, top row first.
struct RGBABuffer {
    let width: Int
    let height: Int
    var bytes: [UInt8]

    init(width: Int, height: Int) {
        self.width = width
        self.height = height
        bytes = [UInt8](repeating: 0, count: width * height * 4)
    }

    init(image: CGImage, width: Int, height: Int) throws {
        self.init(width: width, height: height)
        let drawn = bytes.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = Self.context(buffer.baseAddress, width: width, height: height) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        if !drawn { throw ShellError("cannot create bitmap context") }
    }

    func alpha(x: Int, y: Int) -> UInt8 {
        bytes[(y * width + x) * 4 + 3]
    }

    func makeImage() throws -> CGImage {
        var copy = bytes
        let image = copy.withUnsafeMutableBytes { buffer -> CGImage? in
            Self.context(buffer.baseAddress, width: width, height: height)?.makeImage()
        }
        guard let image else { throw ShellError("cannot create image") }
        return image
    }

    private static func context(_ data: UnsafeMutableRawPointer?, width: Int, height: Int) -> CGContext? {
        CGContext(
            data: data,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: width * 4,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )
    }
}

/// Where a skin's frame image and display are, read from its `layout` alone: enough for small previews.
struct SkinGeometry {
    let frameURL: URL
    /// Where the frame image goes, in skin pixels.
    let frameRect: CGRect
    /// Size of the whole skin in skin pixels, in the device's natural orientation.
    let size: CGSize
    /// Display rectangle in skin pixels, top-left origin.
    let display: CGRect
    let cornerRadius: CGFloat

    static func read(_ folder: URL) -> SkinGeometry? {
        guard let text = try? String(contentsOf: folder.appendingPathComponent("layout"), encoding: .utf8),
              let layout = try? LayoutNode.parse(text),
              let displayNode = layout["parts"]?["device"]?["display"],
              let main = layout["layouts"]?["portrait"] ?? layout["layouts"]?["landscape"],
              let device = main.children.first(where: { $0.value["name"]?.text == "device" })?.value,
              let frameName = layout["parts"]?["portrait"]?["background"]?["image"]?.text,
              let width = main.number("width"), let height = main.number("height"),
              let source = CGImageSourceCreateWithURL(folder.appendingPathComponent(frameName) as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let imageWidth = properties[kCGImagePropertyPixelWidth] as? Double,
              let imageHeight = properties[kCGImagePropertyPixelHeight] as? Double
        else { return nil }
        return SkinGeometry(
            frameURL: folder.appendingPathComponent(frameName),
            frameRect: DeviceSkin.frameRect(layout: main, image: CGSize(width: imageWidth, height: imageHeight)),
            size: CGSize(width: width, height: height),
            display: CGRect(x: device.number("x") ?? 0, y: device.number("y") ?? 0,
                            width: displayNode.number("width") ?? 0, height: displayNode.number("height") ?? 0),
            cornerRadius: displayNode.number("corner_radius") ?? 0
        )
    }
}

/// The emulator skin `layout` format: whitespace-separated `key value` pairs and `key { ... }` blocks.
struct LayoutNode {
    var text: String?
    var children: [(key: String, value: LayoutNode)] = []

    subscript(key: String) -> LayoutNode? {
        children.first { $0.key == key }?.value
    }

    func number(_ key: String) -> Double? {
        self[key]?.text.flatMap(Double.init)
    }

    static func parse(_ source: String) throws -> LayoutNode {
        var tokens = source.split(whereSeparator: \.isWhitespace).map(String.init)[...]
        let root = try parseBlock(&tokens)
        guard tokens.isEmpty else { throw ShellError("unexpected '}' in layout") }
        return root
    }

    private static func parseBlock(_ tokens: inout ArraySlice<String>) throws -> LayoutNode {
        var node = LayoutNode()
        while let key = tokens.first, key != "}" {
            tokens.removeFirst()
            guard let next = tokens.first else { throw ShellError("layout ends after '\(key)'") }
            tokens.removeFirst()
            if next == "{" {
                let child = try parseBlock(&tokens)
                guard tokens.first == "}" else { throw ShellError("unclosed block '\(key)' in layout") }
                tokens.removeFirst()
                node.children.append((key, child))
            } else {
                node.children.append((key, LayoutNode(text: next)))
            }
        }
        return node
    }
}

extension DeviceSkin {
    /// How far in from each corner of the skin the device's body starts, measured along the diagonal (in skin
    /// pixels per axis): where a rounded phone's visible corner is. Clockwise from the top left.
    func cornerInsets() -> [CGFloat] {
        guard let buffer = try? RGBABuffer(image: frame, width: frame.width, height: frame.height) else { return [0, 0, 0, 0] }
        let right = buffer.width - 1, bottom = buffer.height - 1
        let corners = [(0, 0, 1, 1), (right, 0, -1, 1), (right, bottom, -1, -1), (0, bottom, 1, -1)]
        let limit = min(buffer.width, buffer.height) / 2
        return corners.map { x, y, dx, dy in
            let steps = (0..<limit).first { buffer.alpha(x: x + $0 * dx, y: y + $0 * dy) > 128 } ?? 0
            return CGFloat(steps) * size.width / CGFloat(buffer.width)
        }
    }
}
