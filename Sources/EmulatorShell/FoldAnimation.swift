import AppKit

/// A foldable folding shut or opening, played from still pictures of both states in a transparent window
/// over the device's own, whose live view is hidden meanwhile. The folding half reaches past the device
/// window's edges, which would clip it.
///
/// Laid out in the open device's natural orientation, as its skin is drawn: the outer display is on the back
/// of the half left of the hinge (Pixel Fold models), so that half swings over onto the right one, toward
/// the viewer, and past 90° its back, the outer display, comes into view where the right half was. The
/// whole is held at the device's turn, which folding keeps.
@MainActor
final class FoldAnimation {
    /// Sizes in points, upright.
    struct Geometry {
        let open: CGSize
        let closed: CGSize
        /// The hinge's distance from the open device's left edge.
        let hinge: CGFloat

        /// Where the closed device lies once folded, in the open device's coordinates (origin bottom left).
        var closedRect: CGRect {
            CGRect(x: hinge, y: (open.height - closed.height) / 2, width: closed.width, height: closed.height)
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

    static let foldDuration: CFTimeInterval = 0.45

    let closing: Bool
    /// When the animation began, in `CACurrentMediaTime()`; whatever goes along with it starts there too.
    let start: CFTimeInterval
    private let overlay: NSWindow
    /// The right half, which stays; the left one's front and back, which turn over.
    private let still = CALayer(), front = CALayer(), back = CALayer()
    /// Whether the picture of the state being entered has been replaced by a live one.
    private(set) var showsLiveTarget = false

    /// Folds (`closing`) or unfolds over `window`, with the device held at `turns`. Meanwhile the closed
    /// device's centre on screen moves from `closedCenter.from` to `closedCenter.to` and the whole grows by
    /// `growth`, which keeps the window that follows on screen and at its size; both are usually nothing.
    /// `completion` runs once it has finished, with the overlay still showing the last picture; `finish()`
    /// then takes it away.
    init(closing: Bool, geometry: Geometry, open: CGImage, closed: CGImage, closedCenter: (from: CGPoint, to: CGPoint),
         growth: CGFloat, turns: Int, over window: NSWindow, completion: @escaping () -> Void) {
        self.closing = closing
        start = CACurrentMediaTime()
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
        perspective.m34 = -1 / (2.5 * max(size.width, size.height))
        container.sublayerTransform = perspective

        let hinge = geometry.hinge / size.width
        still.frame = CGRect(x: geometry.hinge, y: 0, width: size.width - geometry.hinge, height: size.height)
        still.contents = open
        still.contentsScale = backing
        still.contentsRect = CGRect(x: hinge, y: 0, width: 1 - hinge, height: 1)

        // Turns about the hinge, its right edge.
        let flap = CATransformLayer()
        flap.anchorPoint = CGPoint(x: 1, y: 0.5)
        flap.bounds = CGRect(x: 0, y: 0, width: geometry.hinge, height: size.height)
        flap.position = CGPoint(x: geometry.hinge, y: size.height / 2)
        front.frame = flap.bounds
        front.contents = open
        front.contentsScale = backing
        front.contentsRect = CGRect(x: 0, y: 0, width: hinge, height: 1)
        front.isDoubleSided = false
        // The back lands on the right half once the flap has turned over; turned round itself, so that it
        // reads the right way then.
        back.contents = closed
        back.contentsScale = backing
        back.contentsGravity = .resize
        back.frame = CGRect(x: geometry.hinge - closedRect.width, y: closedRect.minY, width: closedRect.width, height: closedRect.height)
        back.transform = CATransform3DMakeRotation(.pi, 0, 1, 0)
        back.isDoubleSided = false
        let frontShade = Self.shade(over: front), backShade = Self.shade(over: back)
        flap.addSublayer(front)
        flap.addSublayer(back)
        container.addSublayer(still)
        container.addSublayer(flap)
        root.layer?.addSublayer(container)


        CATransaction.begin()
        CATransaction.setDisableActions(true)
        CATransaction.setCompletionBlock(completion)
        container.transform = CATransform3DScale(CATransform3DMakeRotation(CGFloat(turns) * .pi / 2, 0, 0, 1), growth, growth, 1)
        // A positive turn about the vertical axis brings the left half toward the viewer.
        flap.transform = closing ? CATransform3DMakeRotation(.pi, 0, 1, 0) : CATransform3DIdentity
        frontShade.opacity = 0
        backShade.opacity = 0
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
        let folding = CABasicAnimation(keyPath: "transform.rotation.y")
        folding.fromValue = closing ? 0 : CGFloat.pi
        folding.toValue = closing ? CGFloat.pi : 0
        Self.schedule(folding, at: start, duration: Self.foldDuration, ease: ease, on: flap)
        // Each face darkens as it turns away from the light, which is straight ahead.
        for (shade, toward) in [(frontShade, closing), (backShade, !closing)] {
            let shading = CAKeyframeAnimation(keyPath: "opacity")
            shading.values = toward ? [0, 0.55, 0.55] : [0.55, 0.55, 0]
            shading.keyTimes = toward ? [0, 0.5, 1] : [0, 0.5, 1]
            Self.schedule(shading, at: start, duration: Self.foldDuration, ease: ease, on: shade)
        }
        CATransaction.commit()

        // Not a child window: those move with their parent, which resizes and may move meanwhile.
        overlay.level = window.level
        overlay.order(.above, relativeTo: window.windowNumber)
    }

    /// Replaces the picture of the state being entered (the closed device's outer display while closing, the
    /// open one while opening) with one of its first live frames.
    func showLiveTarget(_ picture: CGImage) {
        showsLiveTarget = true
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for layer in closing ? [back] : [still, front] {
            layer.contents = picture
            layer.sublayers?.first?.mask?.contents = picture
        }
        CATransaction.commit()
    }

    /// Takes the overlay away, once the device window shows the new state.
    func finish() {
        // Closed, not just ordered out, or every fold would leave a screen-sized window behind.
        overlay.close()
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

    /// Black over the picture, kept to its outline.
    private static func shade(over layer: CALayer) -> CALayer {
        let shade = CALayer()
        shade.frame = layer.bounds
        shade.backgroundColor = .black
        let mask = CALayer()
        mask.frame = layer.bounds
        mask.contents = layer.contents
        mask.contentsRect = layer.contentsRect
        mask.contentsGravity = .resize
        shade.mask = mask
        layer.addSublayer(shade)
        return shade
    }

    /// The device as it looks: `skin` with `screen` (upright for the viewer at `screenTurns`, like a frame)
    /// in its display, or a dark display without one. `size` is in points.
    nonisolated static func picture(skin: DeviceSkin, screen: CGImage?, screenTurns: Int, size: CGSize, scale: CGFloat) -> CGImage? {
        let width = Int((size.width * scale).rounded()), height = Int((size.height * scale).rounded())
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
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
        context.draw(skin.frame, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()
    }
}
