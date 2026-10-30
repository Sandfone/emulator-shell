import AppKit
import QuartzCore

/// Renders the device: the live display under the skin's frame, both composited by Core Animation
/// with real alpha so every edge is antialiased against whatever is behind the window.
final class DeviceView: NSView {
    var onTouch: ((_ devicePoint: CGPoint, _ pressed: Bool) -> Void)?
    var onKey: ((NSEvent) -> Void)?
    /// A side button went down (`true`) or up; the key is a W3C key name.
    var onSideButton: ((_ key: String, _ pressed: Bool) -> Void)?
    /// The mouse went down on the screen, before that press becomes a touch.
    var onScreenPress: (() -> Void)?
    var contextMenuProvider: (() -> NSMenu)?

    private var skin: DeviceSkin
    /// `DeviceSkin.cornerInsets()`, measured once per skin.
    private var skinCornerInsets: [CGFloat]
    /// Size of the upright device in whole points, so a quarter turn about its centre keeps pixels on the grid.
    private(set) var deviceSize: CGSize
    /// Points per skin pixel the layers are drawn for.
    private(set) var scale: CGFloat
    /// Points per skin pixel shown; differs from `scale` only while the window is being resized.
    private(set) var liveScale: CGFloat
    /// Points per skin pixel, per axis (they differ by the rounding of `deviceSize`).
    private var scaleX: CGFloat
    private var scaleY: CGFloat
    /// Counterclockwise quarter turns the device is drawn at.
    private(set) var quarterTurns = 0
    /// Quarter turns of the frame last shown, which the emulator already turned upright.
    private var contentTurns = 0

    /// Holds everything that turns with the device; its own coordinates are the upright device's.
    private let deviceLayer = CALayer()
    private let screenLayer = CALayer()
    private let screenClipLayer = CALayer()
    private let contentLayer = CALayer()
    private let flashLayer = CALayer()
    private let frameLayer = CALayer()
    private let highlightLayer = CALayer()
    /// Holds the display and frame; masked to the right of the hinge while a foldable is half open, when
    /// `flapLayer` shows the half left of it, turned toward the viewer about the hinge.
    private let bodyLayer = CALayer()
    private let bodyMask = CALayer()
    /// Gives the turned half its depth, about as the fold animation's (`FoldAnimation`).
    private let flapSpace = CALayer()
    private let flapLayer = CALayer()
    private let flapMask = CALayer()
    /// The flap's own display (content and clip) and frame, showing what the body's do.
    private let flapScreen = CALayer()
    private let flapClip = CALayer()
    private let flapContent = CALayer()
    private let flapFrame = CALayer()
    /// How far the half left of the hinge is turned toward the viewer, and where the hinge is, in skin pixels
    /// from the left.
    private(set) var foldAngle: CGFloat = 0
    private var hinge: CGFloat = 0
    private var isTouching = false
    private var pressedButton: SideButton?

    init(skin: DeviceSkin, scale: CGFloat) {
        self.skin = skin
        skinCornerInsets = skin.cornerInsets()
        self.scale = scale
        liveScale = scale
        deviceSize = CGSize(width: (skin.size.width * scale).rounded(), height: (skin.size.height * scale).rounded())
        scaleX = deviceSize.width / skin.size.width
        scaleY = deviceSize.height / skin.size.height
        super.init(frame: CGRect(origin: .zero, size: deviceSize))
        wantsLayer = true
        layer?.backgroundColor = .clear

        screenLayer.backgroundColor = .black
        screenLayer.mask = screenClipLayer
        contentLayer.contentsGravity = .resize
        contentLayer.magnificationFilter = .linear
        contentLayer.minificationFilter = .trilinear
        flashLayer.backgroundColor = .white
        flashLayer.opacity = 0
        highlightLayer.backgroundColor = NSColor(red: 0, green: 0.4, blue: 0.84, alpha: 0.35).cgColor
        highlightLayer.cornerRadius = 3
        highlightLayer.isHidden = true

        flapScreen.backgroundColor = .black
        flapScreen.mask = flapClip
        flapContent.contentsGravity = .resize
        flapContent.magnificationFilter = .linear
        flapContent.minificationFilter = .trilinear
        bodyMask.backgroundColor = .black
        flapMask.backgroundColor = .black
        flapLayer.mask = flapMask
        flapSpace.isHidden = true

        let still: [String: CAAction] = ["contents": NSNull(), "bounds": NSNull(), "position": NSNull(),
                                         "transform": NSNull(), "hidden": NSNull(), "anchorPoint": NSNull(),
                                         "sublayerTransform": NSNull()]
        for sublayer in [deviceLayer, screenLayer, screenClipLayer, contentLayer, flashLayer, frameLayer, highlightLayer,
                         bodyLayer, bodyMask, flapSpace, flapLayer, flapMask, flapScreen, flapClip, flapContent, flapFrame] {
            sublayer.actions = still
        }
        screenLayer.addSublayer(contentLayer)
        screenLayer.addSublayer(flashLayer)
        for sublayer in [screenLayer, frameLayer, highlightLayer] {
            bodyLayer.addSublayer(sublayer)
        }
        flapScreen.addSublayer(flapContent)
        flapLayer.addSublayer(flapScreen)
        flapLayer.addSublayer(flapFrame)
        flapSpace.addSublayer(flapLayer)
        deviceLayer.addSublayer(bodyLayer)
        deviceLayer.addSublayer(flapSpace)
        deviceLayer.bounds = CGRect(origin: .zero, size: deviceSize)
        layer?.addSublayer(deviceLayer)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    /// The view's size with the device at `turns` quarter turns and `scale` points per skin pixel
    /// (by default the one shown).
    func size(forQuarterTurns turns: Int, skin: DeviceSkin? = nil, scale: CGFloat? = nil) -> CGSize {
        let scale = scale ?? liveScale, skin = skin ?? self.skin
        let size = CGSize(width: (skin.size.width * scale).rounded(), height: (skin.size.height * scale).rounded())
        return turns % 2 == 0 ? size : CGSize(width: size.height, height: size.width)
    }

    /// How far in from the corner of this view, per axis, the device's body starts at the shown size, for the
    /// corner `clockwise` steps round from the top left.
    func cornerInset(clockwise corner: Int) -> CGFloat {
        // A counterclockwise quarter turn brings the skin's next corner clockwise to each corner of the view.
        skinCornerInsets[(corner + quarterTurns) % 4] * liveScale
    }

    /// The skin's size in skin pixels with the device at `turns` quarter turns.
    func skinSize(forQuarterTurns turns: Int) -> CGSize {
        turns % 2 == 0 ? skin.size : CGSize(width: skin.size.height, height: skin.size.width)
    }

    /// While the window is resized: stretches what is already drawn, which keeps up with the pointer.
    func setLiveScale(_ scale: CGFloat) {
        liveScale = scale
        deviceLayer.removeAnimation(forKey: "turn")
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        deviceLayer.transform = shownTransform
        CATransaction.commit()
        needsLayout = true
    }

    /// Once resizing ends: draws the skin again at the new size so it is sharp.
    func commitScale() {
        guard liveScale != scale else { return }
        scale = liveScale
        deviceSize = size(forQuarterTurns: 0, scale: scale)
        scaleX = deviceSize.width / skin.size.width
        scaleY = deviceSize.height / skin.size.height
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        deviceLayer.bounds = CGRect(origin: .zero, size: deviceSize)
        deviceLayer.transform = shownTransform
        CATransaction.commit()
        rebuildLayers()
        needsLayout = true
    }

    /// Draws another skin at the same scale, e.g. a foldable's outer display once it is closed; the window is
    /// resized separately.
    func setSkin(_ skin: DeviceSkin) {
        self.skin = skin
        // Another skin's hinge is not this one's.
        foldAngle = 0
        flapLayer.removeAnimation(forKey: "fold")
        showsFold(false)
        // The last frame is of the other display; dark until the first of this one's.
        contentLayer.contents = nil
        skinCornerInsets = skin.cornerInsets()
        deviceLayer.removeAnimation(forKey: "turn")
        deviceSize = size(forQuarterTurns: 0, scale: scale)
        scaleX = deviceSize.width / skin.size.width
        scaleY = deviceSize.height / skin.size.height
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        deviceLayer.bounds = CGRect(origin: .zero, size: deviceSize)
        deviceLayer.transform = shownTransform
        CATransaction.commit()
        highlight(nil)
        rebuildLayers()
        needsLayout = true
    }

    /// The device's turn, and while resizing the stretch from the drawn size to the shown one.
    private var shownTransform: CATransform3D {
        let stretch = liveScale / scale
        return CATransform3DScale(Self.rotation(quarterTurns), stretch, stretch, 1)
    }

    /// Display rect in device points (bottom-left origin, upright device).
    private var displayRect: CGRect {
        CGRect(
            x: skin.display.minX * scaleX,
            y: (skin.size.height - skin.display.maxY) * scaleY,
            width: skin.display.width * scaleX,
            height: skin.display.height * scaleY
        )
    }

    /// Where the display layers sit: `displayRect` grown to whole backing pixels. If the screen only half-covered
    /// the pixel under the frame's antialiased hole edge, the two partial coverages would leave a faint see-through
    /// line; grown, the screen is opaque there and the frame edge blends against it.
    private var screenRect: CGRect {
        let backing = window?.backingScaleFactor ?? 2
        let rect = displayRect
        let minX = (rect.minX * backing).rounded(.down) / backing
        let minY = (rect.minY * backing).rounded(.down) / backing
        let maxX = (rect.maxX * backing).rounded(.up) / backing
        let maxY = (rect.maxY * backing).rounded(.up) / backing
        return CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
    }

    /// Pixel size of the upright display on the backing store.
    var displayPixelSize: CGSize {
        let backing = window?.backingScaleFactor ?? 2
        return CGSize(width: screenRect.width * backing, height: screenRect.height * backing)
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        rebuildLayers()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        rebuildLayers()
    }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        deviceLayer.position = CGPoint(x: bounds.midX, y: bounds.midY)
        CATransaction.commit()
        updateToolTips()
    }

    /// While set, frames are not shown: the view keeps its last one, e.g. until a fold animation takes over.
    var freezesFrames = false

    func show(frame surface: IOSurface, rotation: Int) {
        guard !freezesFrames else { return }
        if rotation != contentTurns {
            contentTurns = rotation
            layoutContent()
        }
        contentLayer.contents = surface
        if !flapSpace.isHidden { flapContent.contents = surface }
    }

    /// How long the half left of the hinge takes to turn between open and half open.
    static let foldDuration: TimeInterval = 0.45

    /// Turns the half left of `hinge` (in skin pixels from the left) by `angle` toward the viewer, as a
    /// foldable half open; 0 lays it flat again.
    func setFold(_ angle: CGFloat, hinge: CGFloat, animated: Bool) {
        let from = flapSpace.isHidden ? 0 : (flapLayer.presentation()?.value(forKeyPath: "transform.rotation.y") as? CGFloat ?? foldAngle)
        foldAngle = angle
        self.hinge = hinge
        layoutFold()
        guard animated, from != angle else {
            if angle == 0 { showsFold(false) }
            return
        }
        CATransaction.begin()
        // Laid flat, the body shows the whole device again once the half has come down.
        CATransaction.setCompletionBlock { [weak self] in
            guard let self, self.foldAngle == 0 else { return }
            self.showsFold(false)
        }
        let turning = CABasicAnimation(keyPath: "transform.rotation.y")
        turning.fromValue = from
        turning.toValue = angle
        turning.duration = Self.foldDuration
        turning.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
        flapLayer.add(turning, forKey: "fold")
        CATransaction.commit()
    }

    /// Splits the device at the hinge, the half left of it turned by `foldAngle`.
    private func layoutFold() {
        let size = deviceLayer.bounds.size
        let x = (hinge * scaleX).rounded()
        guard foldAngle != 0 || !flapSpace.isHidden, x > 0, x < size.width else { return showsFold(false) }
        showsFold(true)
        bodyMask.frame = CGRect(x: x, y: 0, width: size.width - x, height: size.height)
        flapMask.frame = CGRect(x: 0, y: 0, width: x, height: size.height)
        // The depth is seen from in front of the right half, as in the fold animation.
        flapSpace.bounds = CGRect(origin: .zero, size: size)
        flapSpace.anchorPoint = CGPoint(x: (x + size.width) / 2 / size.width, y: 0.5)
        flapSpace.position = CGPoint(x: (x + size.width) / 2, y: size.height / 2)
        var perspective = CATransform3DIdentity
        perspective.m34 = -1 / (2.2 * max(size.width, size.height))
        flapSpace.sublayerTransform = perspective
        flapLayer.bounds = CGRect(origin: .zero, size: size)
        flapLayer.anchorPoint = CGPoint(x: x / size.width, y: 0.5)
        flapLayer.position = CGPoint(x: x, y: size.height / 2)
        // A positive turn about the vertical axis brings the left half toward the viewer.
        flapLayer.transform = CATransform3DMakeRotation(foldAngle, 0, 1, 0)
    }

    private func showsFold(_ shown: Bool) {
        guard shown == flapSpace.isHidden else { return }
        flapSpace.isHidden = !shown
        bodyLayer.mask = shown ? bodyMask : nil
        flapContent.contents = shown ? contentLayer.contents : nil
    }

    /// Turns the device to `turns` counterclockwise quarter turns; the window is resized separately.
    func setQuarterTurns(_ turns: Int, animated: Bool) {
        setQuarterTurns(turns, scale: scale, animated: animated)
    }

    /// How long a turn takes; the window resizes along with it.
    static let turnDuration: TimeInterval = 0.3

    /// Turns the device to `turns` and draws it at `newScale` (it may fit the screen at another size turned),
    /// animating both from how it looks now, so that the turn is never cut short by the new size.
    func setQuarterTurns(_ turns: Int, scale newScale: CGFloat, animated: Bool) {
        guard turns != quarterTurns || newScale != scale else { return }
        var from = deviceLayer.presentation()?.transform ?? deviceLayer.transform
        if newScale != scale {
            // Drawn anew at the new size: the old look is the new layer stretched back by the ratio.
            let ratio = scale / newScale
            from = CATransform3DScale(from, ratio, ratio, 1)
            liveScale = newScale
            commitScale()
        }
        quarterTurns = turns
        deviceLayer.removeAnimation(forKey: "turn")
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        deviceLayer.transform = shownTransform
        CATransaction.commit()
        if animated {
            let animation = CABasicAnimation(keyPath: "transform")
            animation.fromValue = from
            animation.toValue = deviceLayer.transform
            animation.duration = Self.turnDuration
            animation.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            deviceLayer.add(animation, forKey: "turn")
        }
        needsLayout = true
    }

    /// Briefly whitens the screen, as a screenshot confirmation.
    func flash() {
        let animation = CABasicAnimation(keyPath: "opacity")
        animation.fromValue = 0.85
        animation.toValue = 0
        animation.duration = 0.3
        flashLayer.add(animation, forKey: "flash")
    }

    private static func rotation(_ turns: Int) -> CATransform3D {
        CATransform3DMakeRotation(CGFloat(turns) * .pi / 2, 0, 0, 1)
    }

    private func rebuildLayers() {
        let backing = window?.backingScaleFactor ?? 2
        let screen = screenRect

        bodyLayer.frame = deviceLayer.bounds
        frameLayer.frame = deviceLayer.bounds
        frameLayer.contentsScale = backing
        frameLayer.contents = Self.resample(skin.frame, to: deviceSize, scale: backing)

        screenLayer.frame = screen
        screenClipLayer.frame = screenLayer.bounds
        screenClipLayer.contentsScale = backing
        screenClipLayer.contents = Self.resample(skin.screenClip, to: screen.size, scale: backing)
        flashLayer.frame = screenLayer.bounds

        flapFrame.frame = frameLayer.frame
        flapFrame.contentsScale = backing
        flapFrame.contents = frameLayer.contents
        flapScreen.frame = screen
        flapClip.frame = screenClipLayer.frame
        flapClip.contentsScale = backing
        flapClip.contents = screenClipLayer.contents
        layoutContent()
        layoutFold()
        window?.invalidateShadow()
    }

    /// The emulator sends frames upright for the viewer, so inside the turned device they are turned back.
    private func layoutContent() {
        let size = screenLayer.bounds.size
        contentLayer.bounds = contentTurns % 2 == 0
            ? CGRect(origin: .zero, size: size)
            : CGRect(x: 0, y: 0, width: size.height, height: size.width)
        contentLayer.position = CGPoint(x: size.width / 2, y: size.height / 2)
        contentLayer.transform = Self.rotation(-contentTurns)
        flapContent.bounds = contentLayer.bounds
        flapContent.position = contentLayer.position
        flapContent.transform = contentLayer.transform
    }

    /// Downsamples skin artwork once with high-quality filtering instead of letting the compositor minify ~3×.
    private static func resample(_ image: CGImage, to size: CGSize, scale: CGFloat) -> CGImage? {
        let width = Int((size.width * scale).rounded())
        let height = Int((size.height * scale).rounded())
        guard let context = Self.context(width: width, height: height) else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()
    }

    private static func context(width: Int, height: Int) -> CGContext? {
        CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )
    }

    // MARK: - Side buttons

    /// A side button's hit area in upright device points.
    private func localRect(of button: SideButton) -> CGRect {
        CGRect(
            x: button.rect.minX * scaleX,
            y: (skin.size.height - button.rect.maxY) * scaleY,
            width: button.rect.width * scaleX,
            height: button.rect.height * scaleY
        )
    }

    private func sideButton(at local: CGPoint) -> SideButton? {
        skin.sideButtons.first { localRect(of: $0).contains(local) }
    }

    private func highlight(_ button: SideButton?) {
        if let button {
            highlightLayer.frame = localRect(of: button)
        }
        highlightLayer.isHidden = button == nil
    }

    private func updateToolTips() {
        removeAllToolTips()
        guard let root = layer else { return }
        for button in skin.sideButtons {
            addToolTip(deviceLayer.convert(localRect(of: button), to: root), owner: button.label as NSString, userData: nil)
        }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(
            rect: .zero,
            options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self
        ))
    }

    override func mouseMoved(with event: NSEvent) {
        if pressedButton == nil { highlight(sideButton(at: localPoint(for: event))) }
    }

    override func mouseExited(with event: NSEvent) {
        if pressedButton == nil { highlight(nil) }
    }

    // MARK: - Input

    /// The event's location in upright device points; on the half left of the hinge while it is turned, where
    /// on that half it is.
    private func localPoint(for event: NSEvent) -> CGPoint {
        let point = convert(event.locationInWindow, from: nil)
        guard let root = layer else { return point }
        let local = deviceLayer.convert(point, from: root)
        guard !flapSpace.isHidden, local.x < flapMask.frame.maxX else { return local }
        return flapLayer.convert(point, from: root)
    }

    /// Device pixel under the cursor, clamped to the display so a drag that leaves the screen still ends cleanly.
    /// Touch coordinates are the panel's own, so they do not change when the device is turned.
    private func devicePoint(for event: NSEvent) -> CGPoint {
        let local = localPoint(for: event)
        let display = displayRect
        let x = (min(max(local.x, display.minX), display.maxX) - display.minX) / scaleX
        let y = (display.maxY - min(max(local.y, display.minY), display.maxY)) / scaleY
        return CGPoint(
            x: min(x.rounded(.down), skin.display.width - 1),
            y: min(y.rounded(.down), skin.display.height - 1)
        )
    }

    override func mouseDown(with event: NSEvent) {
        let local = localPoint(for: event)
        if displayRect.contains(local) {
            onScreenPress?()
            isTouching = true
            onTouch?(devicePoint(for: event), true)
        } else if let button = sideButton(at: local) {
            pressedButton = button
            highlight(button)
            onSideButton?(button.key, true)
        } else {
            (window as? DeviceWindow)?.drag(with: event)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard isTouching else { return }
        onTouch?(devicePoint(for: event), true)
    }

    override func mouseUp(with event: NSEvent) {
        if let button = pressedButton {
            pressedButton = nil
            onSideButton?(button.key, false)
            highlight(sideButton(at: localPoint(for: event)))
        }
        guard isTouching else { return }
        isTouching = false
        onTouch?(devicePoint(for: event), false)
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        contextMenuProvider?()
    }

    override func keyDown(with event: NSEvent) {
        onKey?(event)
    }
}

/// The device's borderless window. Borderless windows refuse key status by default, which would swallow
/// all keyboard input; and the system does not keep them on screen, so dragging does.
///
/// What the user sees and sizes is the box: the device with its bars. The window reaches `margin` further
/// on every side, where the corner resize areas extend past the box as a standard window's do.
final class DeviceWindow: NSWindow {
    static let margin: CGFloat = ResizeHandle.outside

    /// The box, in screen coordinates.
    var box: CGRect { frame.insetBy(dx: Self.margin, dy: Self.margin) }

    func setBox(_ box: CGRect, display: Bool, animate: Bool = false) {
        setFrame(box.insetBy(dx: -Self.margin, dy: -Self.margin), display: display, animate: animate)
    }

    /// Moves and resizes to `box` over `duration` from `start` (a `CACurrentMediaTime()`), eased like the
    /// device's own Core Animation animations, which it goes along with; `completion` runs at the end.
    ///
    /// Stepped on each refresh of the display, at the point in time the device's animation has reached, so
    /// the two stay together: an AppKit animator runs on its own clock, falls behind when this thread is
    /// busy and lands after the device does, nudging the window once the live view is back.
    func setBox(_ box: CGRect, duration: TimeInterval, start: CFTimeInterval = CACurrentMediaTime(), completion: (() -> Void)? = nil) {
        stopBoxAnimation()
        let frame = box.insetBy(dx: -Self.margin, dy: -Self.margin)
        guard frame != self.frame else {
            completion?()
            return
        }
        boxAnimation = (from: self.frame, to: frame, start: start, duration: duration, completion: completion)
        // The screen's link rather than the window's: that one stops while the window is covered, as it is by
        // the fold animation's overlay.
        let link = (screen ?? NSScreen.main)?.displayLink(target: self, selector: #selector(stepBoxAnimation))
            ?? displayLink(target: self, selector: #selector(stepBoxAnimation))
        link.add(to: .main, forMode: .common)
        boxLink = link
        // Display links pause while the screen is locked or asleep; the animation still ends, on time.
        boxAnimationID += 1
        let id = boxAnimationID
        DispatchQueue.main.asyncAfter(deadline: .now() + duration + 0.1) { [weak self] in
            guard let self, self.boxAnimationID == id, self.boxAnimation != nil else { return }
            self.stepBoxAnimation()
        }
    }

    /// Ends a `setBox(_:duration:)` animation where it is, without its completion.
    func stopBoxAnimation() {
        boxLink?.invalidate()
        boxLink = nil
        boxAnimation = nil
    }

    private var boxAnimation: (from: CGRect, to: CGRect, start: CFTimeInterval, duration: TimeInterval, completion: (() -> Void)?)?
    private var boxLink: CADisplayLink?
    private var boxAnimationID = 0

    @objc private func stepBoxAnimation() {
        guard let animation = boxAnimation else { return stopBoxAnimation() }
        let progress = min(max((CACurrentMediaTime() - animation.start) / animation.duration, 0), 1)
        let eased = Self.easeInOut(progress)
        func mix(_ a: CGFloat, _ b: CGFloat) -> CGFloat { (a + (b - a) * eased).rounded() }
        let frame = progress >= 1 ? animation.to : CGRect(x: mix(animation.from.minX, animation.to.minX),
                                                           y: mix(animation.from.minY, animation.to.minY),
                                                           width: mix(animation.from.width, animation.to.width),
                                                           height: mix(animation.from.height, animation.to.height))
        if frame != self.frame { setFrame(frame, display: true) }
        guard progress >= 1 else { return }
        stopBoxAnimation()
        animation.completion?()
    }

    /// `CAMediaTimingFunction(name: .easeInEaseOut)`: the cubic Bézier through (0.42, 0) and (0.58, 1).
    static func easeInOut(_ time: Double) -> Double {
        // Finds the curve's parameter whose x is `time` by bisection (x rises with it), then its y.
        var low = 0.0, high = 1.0, s = time
        for _ in 0..<20 {
            s = (low + high) / 2
            let x = 3 * (1 - s) * (1 - s) * s * 0.42 + 3 * (1 - s) * s * s * 0.58 + s * s * s
            if x < time { low = s } else { high = s }
        }
        return 3 * s * s - 2 * s * s * s
    }

    /// `contentRect` is the box's.
    override init(contentRect: NSRect, styleMask: NSWindow.StyleMask, backing: NSWindow.BackingStoreType, defer flag: Bool) {
        super.init(contentRect: contentRect.insetBy(dx: -Self.margin, dy: -Self.margin), styleMask: styleMask, backing: backing, defer: flag)
        // The cursor follows the pointer over the corners, which needs its moves outside any view's tracking area.
        acceptsMouseMovedEvents = true
    }

    /// Called once a drag that moved the window ends.
    var onMoved: (() -> Void)?
    /// Called with `true` once a drag first brings the box's top against the top of the screen, and with
    /// `false` when that drag ends.
    var onScreenTopReached: ((Bool) -> Void)?
    private var dragEndTimer: Timer?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    /// The Window menu's ⌘M; without a title bar of its own the window would only beep.
    override func performMiniaturize(_ sender: Any?) { miniaturize(sender) }

    /// Placement keeps the box (not the empty margin around it) on screen, so AppKit must not push the
    /// margin out from under the menu bar.
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }

    /// Moves the window with the pointer, then brings any part dragged off the screen back.
    func drag(with event: NSEvent) {
        let before = frame.origin
        performDrag(with: event)
        // The window server moves the window and `performDrag` returns at once, so the drag's end is found by
        // watching for the button to come up.
        dragEndTimer?.invalidate()
        var reachedTop = false
        dragEndTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] timer in
            MainActor.assumeIsolated {
                guard let self else { return }
                guard NSEvent.pressedMouseButtons & 1 == 0 else {
                    if !reachedTop, self.atScreenTop {
                        reachedTop = true
                        self.onScreenTopReached?(true)
                    }
                    return
                }
                timer.invalidate()
                self.dragEndTimer = nil
                if reachedTop { self.onScreenTopReached?(false) }
                guard self.frame.origin != before else { return }
                let fitted = Self.fitted(self.box, on: self.screen ?? NSScreen.main)
                if fitted != self.box { self.setBox(fitted, display: true, animate: true) }
                self.onMoved?()
            }
        }
    }

    /// Whether the box's top is at or above its screen's visible top, with no other screen above to move on to.
    private var atScreenTop: Bool {
        guard let screen, box.maxY >= screen.visibleFrame.maxY - 1 else { return false }
        let above = CGPoint(x: box.midX, y: screen.frame.maxY + 1)
        return !NSScreen.screens.contains { $0 != screen && $0.frame.contains(above) }
    }

    /// The box `frame` moved (not resized) so it lies within the screen's visible area where it can.
    static func fitted(_ frame: CGRect, on screen: NSScreen?) -> CGRect {
        guard let visible = screen?.visibleFrame else { return frame }
        var fitted = frame
        fitted.origin.x = min(max(frame.minX, visible.minX), max(visible.minX, visible.maxX - frame.width))
        fitted.origin.y = min(max(frame.minY, visible.minY), max(visible.minY, visible.maxY - frame.height))
        return fitted
    }
}
