import AppKit
import CoreImage

/// A foldable folding shut or opening, played from still pictures of both states in a transparent window
/// over the device's own, whose live view is hidden meanwhile. The folding half reaches past the device
/// window's edges, which would clip it.
///
/// Laid out in the open device's natural orientation, as its skin is drawn: the outer display is on the back
/// of the half left of the hinge (Pixel Fold models), so that half swings over onto the right one, toward
/// the viewer, and past 90° its back, the outer display, comes into view exactly over the right half,
/// which stays put as if lying on a table. The hinge is stiff at first, and magnets pull the halves shut
/// for the last stretch, where they bounce once; opening, they hold for a moment before the hinge gives,
/// and it slows to a stop flat. The whole is held at the device's turn, which folding keeps.
///
/// As in Xcode's iPhone Duo, a turning display frosts over: tilted away from the viewer its picture
/// blurs, under a frame that stays sharp.
@MainActor
final class FoldAnimation {
    /// Sizes in points, upright.
    struct Geometry {
        let open: CGSize
        let closed: CGSize
        /// The hinge's distance from the open device's left edge.
        let hinge: CGFloat
        /// Where the closed device's top left corner lies once folded, from the open one's (origin top left).
        let closedOrigin: CGPoint

        /// Where the closed device lies once folded, in the open device's coordinates (origin bottom left).
        var closedRect: CGRect {
            CGRect(x: closedOrigin.x, y: open.height - closedOrigin.y - closed.height, width: closed.width, height: closed.height)
        }

        /// From the open device's centre to the closed one's, with the device held at `turns`
        /// counterclockwise quarter turns.
        func offset(turns: Int) -> CGPoint {
            let x = closedRect.midX - open.width / 2, y = closedRect.midY - open.height / 2
            switch (turns % 4 + 4) % 4 {
            case 1: return CGPoint(x: -y, y: x)
            case 2: return CGPoint(x: -x, y: -y)
            case 3: return CGPoint(x: y, y: -x)
            default: return CGPoint(x: x, y: y)
            }
        }
    }

    static let foldDuration: CFTimeInterval = 0.6
    private static let preparation: CFTimeInterval = 1.0 / 15
    /// How long a display takes to light up with its first live frame.
    static let lightDuration: CFTimeInterval = 0.2
    /// How blurred a frosted display is, for its width.
    nonisolated private static let frost: CGFloat = 0.04

    let closing: Bool
    /// When the animation began, in `CACurrentMediaTime()`; whatever goes along with it starts there too.
    let start: CFTimeInterval
    private let overlay: NSWindow
    /// A half's frame and display, drawn apart so that the display can frost over, and the display frosted.
    struct Picture {
        let frame: CGImage
        let screen: CGImage
        let frosted: CGImage
    }

    /// A half on screen: its display, frosted over it, under its frame.
    private struct Face {
        let layer = CALayer(), screen = CALayer(), frosted = CALayer(), frame = CALayer()
    }

    /// The right half, which stays; the left one's front and back, which turn over.
    private let still = Face(), front = Face(), back = Face()
    /// Whether the dark display of the state being entered has been given a live frame, and whether that
    /// has finished lighting up.
    private(set) var hasLiveTarget = false
    private(set) var showsLiveTarget = false

    /// Folds (`closing`) or unfolds over `window`, with the device held at `turns`. Meanwhile the closed
    /// device's centre on screen moves from `closedCenter.from` to `closedCenter.to` and the whole grows by
    /// `growth`, which keeps the window that follows on screen and at its size; both are usually nothing.
    /// `completion` runs once it has finished, with the overlay still showing the last picture; `finish()`
    /// then takes it away.
    init(closing: Bool, geometry: Geometry, open: Picture, closed: Picture, closedCenter: (from: CGPoint, to: CGPoint),
         growth: CGFloat, turns: Int, over window: NSWindow, completion: @escaping () -> Void) {
        self.closing = closing
        // A moment ahead: the render server first takes in the pictures, which would otherwise skip the start.
        start = CACurrentMediaTime() + Self.preparation
        let screen = window.screen ?? NSScreen.main ?? NSScreen.screens[0]
        overlay = NSWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        overlay.isOpaque = false
        overlay.backgroundColor = .clear
        overlay.hasShadow = false
        overlay.ignoresMouseEvents = true
        // Shown and taken away at once: AppKit's own window animations would add a fade, and while one cannot
        // run (the screen locked) the window lingers in the window server.
        overlay.animationBehavior = .none
        overlay.isReleasedWhenClosed = false
        let root = NSView(frame: CGRect(origin: .zero, size: screen.frame.size))
        root.wantsLayer = true
        overlay.contentView = root
        // The pictures carry the pixels for the size they grow to.
        let backing = window.backingScaleFactor * max(1, growth)

        let size = geometry.open
        let container = CALayer()
        container.bounds = CGRect(origin: .zero, size: size)
        let closedRect = geometry.closedRect
        container.anchorPoint = CGPoint(x: closedRect.midX / size.width, y: closedRect.midY / size.height)
        let origin = screen.frame.origin
        let from = CGPoint(x: closedCenter.from.x - origin.x, y: closedCenter.from.y - origin.y)
        let to = CGPoint(x: closedCenter.to.x - origin.x, y: closedCenter.to.y - origin.y)
        container.position = to
        // Some depth, about as a real device seen from a little way off.
        var perspective = CATransform3DIdentity
        perspective.m34 = -1 / (2.2 * max(size.width, size.height))
        container.sublayerTransform = perspective

        let hinge = geometry.hinge / size.width
        Self.setUp(still, picture: open, frame: CGRect(x: geometry.hinge, y: 0, width: size.width - geometry.hinge, height: size.height),
                   part: CGRect(x: hinge, y: 0, width: 1 - hinge, height: 1), scale: backing)

        // Turns about the hinge, its right edge.
        let flap = CATransformLayer()
        flap.anchorPoint = CGPoint(x: 1, y: 0.5)
        flap.bounds = CGRect(x: 0, y: 0, width: geometry.hinge, height: size.height)
        flap.position = CGPoint(x: geometry.hinge, y: size.height / 2)
        Self.setUp(front, picture: open, frame: flap.bounds, part: CGRect(x: 0, y: 0, width: hinge, height: 1), scale: backing)
        front.layer.isDoubleSided = false
        // The back lands where the closed device lies once the flap has turned over, mirrored about the
        // hinge until then; turned round itself, so that it reads the right way then.
        Self.setUp(back, picture: closed, frame: CGRect(x: 2 * geometry.hinge - closedRect.maxX, y: closedRect.minY,
                                                        width: closedRect.width, height: closedRect.height),
                   part: CGRect(x: 0, y: 0, width: 1, height: 1), scale: backing)
        back.layer.transform = CATransform3DMakeRotation(.pi, 0, 1, 0)
        back.layer.isDoubleSided = false
        flap.addSublayer(front.layer)
        flap.addSublayer(back.layer)
        container.addSublayer(still.layer)
        container.addSublayer(flap)
        root.layer?.addSublayer(container)

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        CATransaction.setCompletionBlock(completion)
        container.transform = CATransform3DScale(CATransform3DMakeRotation(CGFloat(turns) * .pi / 2, 0, 0, 1), growth, growth, 1)
        // A positive turn about the vertical axis brings the left half toward the viewer.
        flap.transform = closing ? CATransform3DMakeRotation(.pi, 0, 1, 0) : CATransform3DIdentity
        for layer in [still.frosted, front.frosted, back.frosted] { layer.opacity = 0 }
        let ease = CAMediaTimingFunction(name: .easeInEaseOut)
        if growth != 1 {
            let growing = CABasicAnimation(keyPath: "transform.scale")
            growing.fromValue = 1
            growing.toValue = growth
            Self.schedule(growing, at: start, duration: Self.foldDuration, ease: ease, on: container)
        }
        if from != to {
            let moving = CABasicAnimation(keyPath: "position")
            moving.fromValue = from
            moving.toValue = to
            Self.schedule(moving, at: start, duration: Self.foldDuration, ease: ease, on: container)
        }
        // The motion is sampled finely enough to play back as straight steps between the samples.
        let steps = 60
        let progress = (0...steps).map { Double($0) / Double(steps) }
        let folded = progress.map { CGFloat(Self.folded(at: $0, closing: closing)) * .pi }
        // Each turning display frosts over as it tilts.
        let motions: [(CALayer, String, [CGFloat])] = [
            (flap, "transform.rotation.y", folded),
            (front.frosted, "opacity", folded.map { Self.frosting(facing: $0) }),
            (back.frosted, "opacity", folded.map { Self.frosting(facing: $0 - .pi) }),
        ]
        for (layer, keyPath, values) in motions {
            let motion = CAKeyframeAnimation(keyPath: keyPath)
            motion.values = values
            motion.keyTimes = progress.map { NSNumber(value: $0) }
            Self.schedule(motion, at: start, duration: Self.foldDuration, ease: CAMediaTimingFunction(name: .linear), on: layer)
        }
        CATransaction.commit()

        // Not a child window: those move with their parent, which resizes and may move meanwhile.
        overlay.level = window.level
        overlay.order(.above, relativeTo: window.windowNumber)
    }

    /// Lights up the dark display of the state being entered (the closed device's outer one, or the open
    /// one's halves) with one of its first live frames (`screen` of a `Picture`), fading it in as a display
    /// turning on. `completion` runs once it is fully lit.
    func showLiveTarget(_ picture: CGImage, completion: @escaping () -> Void) {
        hasLiveTarget = true
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        CATransaction.setCompletionBlock { [weak self] in
            self?.showsLiveTarget = true
            completion()
        }
        for layer in closing ? [back.screen] : [still.screen, front.screen] {
            let lighting = CABasicAnimation(keyPath: "contents")
            lighting.fromValue = layer.contents
            lighting.toValue = picture
            lighting.duration = Self.lightDuration
            layer.contents = picture
            layer.add(lighting, forKey: "lighting")
        }
        CATransaction.commit()
    }

    /// Takes the overlay away, once the device window shows the new state.
    func finish() {
        // Closed, not just ordered out, or every fold would leave a screen-sized window behind.
        overlay.close()
    }

    /// How far shut (0 open, 1 shut) the device is `progress` of the way through folding or unfolding.
    nonisolated static func folded(at progress: Double, closing: Bool) -> Double {
        let p = min(max(progress, 0), 1)
        if !closing {
            // The magnets hold for a moment, then the hinge swings open and slows to a stop flat.
            return pow(1 - pow(p, 1.3), 3)
        }
        let caught = 0.82, shut = 0.9
        if p < caught {
            // Stiff at first, then steadily on, until the magnets catch it nine tenths shut.
            let x = p / caught
            return 0.9 * (x + x * x * (3 - 2 * x)) / 2
        }
        if p < shut {
            // Pulled shut, faster and faster, from the speed it had.
            let y = (p - caught) / (shut - caught), speed = 0.9 / 2 / caught * (shut - caught)
            return 0.9 + speed * y + (0.1 - speed) * y * y
        }
        // The halves meet and bounce apart a little once.
        return 1 - 0.025 * sin(.pi * (p - shut) / (1 - shut))
    }

    /// How far a display turned `angle` from the viewer has frosted over: from a little way off facing, fully
    /// by 60°.
    private static func frosting(facing angle: CGFloat) -> CGFloat {
        let tilt = min(1, max(0, (1 - cos(angle)) / 0.5))
        return tilt * tilt * (3 - 2 * tilt)
    }

    private static func schedule(_ animation: CAPropertyAnimation, at start: CFTimeInterval, duration: CFTimeInterval,
                                 ease: CAMediaTimingFunction, on layer: CALayer) {
        animation.beginTime = start
        animation.duration = duration
        animation.timingFunction = ease
        // Shows its first value until it starts.
        animation.fillMode = .backwards
        layer.add(animation, forKey: animation.keyPath)
    }

    /// Lays out a half: `part` of `picture` in `frame`, the frosted display over the sharp one.
    private static func setUp(_ face: Face, picture: Picture, frame: CGRect, part: CGRect, scale: CGFloat) {
        face.layer.frame = frame
        for (layer, image, parent) in [(face.screen, picture.screen, face.layer), (face.frosted, picture.frosted, face.screen),
                                       (face.frame, picture.frame, face.layer)] {
            layer.frame = parent.bounds
            layer.contents = image
            layer.contentsScale = scale
            layer.contentsRect = part
            layer.contentsGravity = .resize
            parent.addSublayer(layer)
        }
    }

    /// The device as it looks: `skin`'s frame, and `screen` (upright for the viewer at `screenTurns`, like a
    /// frame) in its display, or a dark display without one; each the device's size, transparent elsewhere.
    /// `size` is in points.
    nonisolated static func picture(skin: DeviceSkin, screen: CGImage?, screenTurns: Int, size: CGSize, scale: CGFloat) -> Picture? {
        let width = Int((size.width * scale).rounded()), height = Int((size.height * scale).rounded())
        let makeContext = {
            CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                      space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        }
        guard let context = makeContext(), let frameContext = makeContext() else { return nil }
        context.interpolationQuality = .high
        let sx = CGFloat(width) / skin.size.width, sy = CGFloat(height) / skin.size.height
        let display = CGRect(x: skin.display.minX * sx, y: CGFloat(height) - skin.display.maxY * sy,
                             width: skin.display.width * sx, height: skin.display.height * sy)
        context.saveGState()
        context.clip(to: display, mask: skin.screenClip)
        context.setFillColor(.black)
        context.fill(display)
        if let screen {
            // Frames are upright for the viewer, so inside the upright device they are turned back.
            let turns = (screenTurns % 4 + 4) % 4
            context.translateBy(x: display.midX, y: display.midY)
            context.rotate(by: -CGFloat(turns) * .pi / 2)
            let drawn = turns % 2 == 0 ? display.size : CGSize(width: display.height, height: display.width)
            context.draw(screen, in: CGRect(x: -drawn.width / 2, y: -drawn.height / 2, width: drawn.width, height: drawn.height))
        }
        context.restoreGState()
        frameContext.interpolationQuality = .high
        frameContext.draw(skin.frame, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let screenImage = context.makeImage(), let frameImage = frameContext.makeImage(),
              let frostedImage = frosted(screenImage, radius: CGFloat(width) * frost) else { return nil }
        return Picture(frame: frameImage, screen: screenImage, frosted: frostedImage)
    }

    /// `screen` blurred, keeping its outline: blurred with its edges carried on outward, so that they do not
    /// fade, then cut back to the outline. Blurred, it loses nothing drawn at a quarter of the size, which
    /// is much quicker; the layer showing it scales it back up.
    nonisolated private static func frosted(_ screen: CGImage, radius: CGFloat) -> CGImage? {
        let reduction: CGFloat = 4
        let image = CIImage(cgImage: screen).transformed(by: CGAffineTransform(scaleX: 1 / reduction, y: 1 / reduction))
        let blurred = image.clampedToExtent().applyingGaussianBlur(sigma: Double(radius / reduction)).cropped(to: image.extent)
        let outline = CIFilter(name: "CIBlendWithAlphaMask", parameters: [
            kCIInputImageKey: blurred, kCIInputBackgroundImageKey: CIImage.empty(), kCIInputMaskImageKey: image,
        ])?.outputImage
        return outline.flatMap { frostContext.createCGImage($0, from: image.extent) }
    }

    nonisolated private static let frostContext = CIContext(options: [.cacheIntermediates: false])

    /// Gets the blur ready ahead of the first fold, which would otherwise wait a few hundred milliseconds for
    /// it to set up.
    nonisolated static func prepare() {
        DispatchQueue.global(qos: .utility).async {
            let size = 64
            guard let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                                          space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
                  let image = context.makeImage() else { return }
            _ = frosted(image, radius: CGFloat(size) * frost)
        }
    }
}
