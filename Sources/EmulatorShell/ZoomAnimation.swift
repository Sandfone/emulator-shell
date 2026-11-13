import AppKit

/// The device growing or shrinking to another size, played from a still picture in a transparent window over
/// the device's own, whose live view is hidden meanwhile. Done in the window itself, a step of the window's
/// frame and the device drawn to fit it reach the screen a frame apart, so the device jumps a few points and
/// back all the way; here the window is not resized, and Core Animation moves the picture smoothly.
@MainActor
final class ZoomAnimation {
    private let overlay: NSWindow
    /// When it starts, in `CACurrentMediaTime()`: a moment ahead, as the render server first takes in the
    /// picture, which would otherwise skip the start. The window's resizing starts then too.
    let start: CFTimeInterval
    private static let preparation: CFTimeInterval = 1.0 / 30

    /// Plays `picture` (the upright device at `to` size, drawn at `backing` pixels per point) held at `turns`
    /// counterclockwise quarter turns, its centre moving from `from.center` to `to.center` (screen
    /// coordinates) and its size from `from.size` to `to.size` (upright), over `duration`.
    init(picture: FoldAnimation.Picture, backing: CGFloat, turns: Int, from: (center: CGPoint, size: CGSize),
         to: (center: CGPoint, size: CGSize), duration: CFTimeInterval, over window: NSWindow) {
        start = CACurrentMediaTime() + Self.preparation
        let screen = window.screen ?? NSScreen.main ?? NSScreen.screens[0]
        overlay = NSWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        overlay.isOpaque = false
        overlay.backgroundColor = .clear
        overlay.hasShadow = false
        overlay.ignoresMouseEvents = true
        overlay.animationBehavior = .none
        overlay.isReleasedWhenClosed = false
        let root = NSView(frame: CGRect(origin: .zero, size: screen.frame.size))
        root.wantsLayer = true
        overlay.contentView = root

        let device = CALayer()
        device.bounds = CGRect(origin: .zero, size: to.size)
        for image in [picture.screen, picture.frame] {
            let layer = CALayer()
            layer.frame = device.bounds
            layer.contents = image
            layer.contentsScale = backing
            layer.contentsGravity = .resize
            device.addSublayer(layer)
        }
        root.layer?.addSublayer(device)

        let origin = screen.frame.origin
        let turn = CATransform3DMakeRotation(CGFloat(turns) * .pi / 2, 0, 0, 1)
        let shrink = from.size.width / to.size.width
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        device.position = CGPoint(x: to.center.x - origin.x, y: to.center.y - origin.y)
        device.transform = turn
        let ease = CAMediaTimingFunction(name: .easeInEaseOut)
        let moving = CABasicAnimation(keyPath: "position")
        moving.fromValue = CGPoint(x: from.center.x - origin.x, y: from.center.y - origin.y)
        let sizing = CABasicAnimation(keyPath: "transform")
        sizing.fromValue = CATransform3DScale(turn, shrink, shrink, 1)
        for animation in [moving, sizing] {
            animation.beginTime = start
            animation.duration = duration
            animation.timingFunction = ease
            animation.fillMode = .backwards
            device.add(animation, forKey: animation.keyPath)
        }
        CATransaction.commit()

        overlay.level = window.level
        overlay.order(.above, relativeTo: window.windowNumber)
    }

    /// Takes the overlay away, once the device window shows the device at its new size.
    func finish() {
        overlay.close()
    }
}
