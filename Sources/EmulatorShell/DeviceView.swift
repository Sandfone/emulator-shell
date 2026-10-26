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

    private let skin: DeviceSkin
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
    private var isTouching = false
    private var pressedButton: SideButton?

    init(skin: DeviceSkin, scale: CGFloat) {
        self.skin = skin
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

        let still: [String: CAAction] = ["contents": NSNull(), "bounds": NSNull(), "position": NSNull(),
                                         "transform": NSNull(), "hidden": NSNull()]
        for sublayer in [deviceLayer, screenLayer, screenClipLayer, contentLayer, flashLayer,
                         frameLayer, highlightLayer] {
            sublayer.actions = still
        }
        screenLayer.addSublayer(contentLayer)
        screenLayer.addSublayer(flashLayer)
        for sublayer in [screenLayer, frameLayer, highlightLayer] {
            deviceLayer.addSublayer(sublayer)
        }
        deviceLayer.bounds = CGRect(origin: .zero, size: deviceSize)
        layer?.addSublayer(deviceLayer)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    /// The view's size with the device at `turns` quarter turns and `scale` points per skin pixel
    /// (by default the one shown).
    func size(forQuarterTurns turns: Int, scale: CGFloat? = nil) -> CGSize {
        let scale = scale ?? liveScale
        let size = CGSize(width: (skin.size.width * scale).rounded(), height: (skin.size.height * scale).rounded())
        return turns % 2 == 0 ? size : CGSize(width: size.height, height: size.width)
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

    func show(frame surface: IOSurface, rotation: Int) {
        if rotation != contentTurns {
            contentTurns = rotation
            layoutContent()
        }
        contentLayer.contents = surface
    }

    /// Turns the device to `turns` counterclockwise quarter turns; the window is resized separately.
    func setQuarterTurns(_ turns: Int, animated: Bool) {
        guard turns != quarterTurns else { return }
        let from = deviceLayer.presentation()?.transform ?? deviceLayer.transform
        quarterTurns = turns
        deviceLayer.transform = shownTransform
        if animated {
            let animation = CABasicAnimation(keyPath: "transform")
            animation.fromValue = from
            animation.toValue = deviceLayer.transform
            animation.duration = 0.3
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

        frameLayer.frame = deviceLayer.bounds
        frameLayer.contentsScale = backing
        frameLayer.contents = Self.resample(skin.frame, to: deviceSize, scale: backing)

        screenLayer.frame = screen
        screenClipLayer.frame = screenLayer.bounds
        screenClipLayer.contentsScale = backing
        screenClipLayer.contents = Self.resample(skin.screenClip, to: screen.size, scale: backing)
        flashLayer.frame = screenLayer.bounds
        layoutContent()
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

    /// The event's location in upright device points.
    private func localPoint(for event: NSEvent) -> CGPoint {
        let point = convert(event.locationInWindow, from: nil)
        guard let root = layer else { return point }
        return deviceLayer.convert(point, from: root)
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
final class DeviceWindow: NSWindow {
    /// Called once a drag that moved the window ends.
    var onMoved: (() -> Void)?
    private var dragEndTimer: Timer?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    /// Moves the window with the pointer, then brings any part dragged off the screen back.
    func drag(with event: NSEvent) {
        let before = frame.origin
        performDrag(with: event)
        // The window server moves the window and `performDrag` returns at once, so the drag's end is found by
        // watching for the button to come up.
        dragEndTimer?.invalidate()
        dragEndTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] timer in
            MainActor.assumeIsolated {
                guard NSEvent.pressedMouseButtons & 1 == 0, let self else { return }
                timer.invalidate()
                self.dragEndTimer = nil
                guard self.frame.origin != before else { return }
                let fitted = Self.fitted(self.frame, on: self.screen ?? NSScreen.main)
                if fitted != self.frame { self.setFrame(fitted, display: true, animate: true) }
                self.onMoved?()
            }
        }
    }

    /// `frame` moved (not resized) so it lies within the screen's visible area where it can.
    static func fitted(_ frame: CGRect, on screen: NSScreen?) -> CGRect {
        guard let visible = screen?.visibleFrame else { return frame }
        var fitted = frame
        fitted.origin.x = min(max(frame.minX, visible.minX), max(visible.minX, visible.maxX - frame.width))
        fitted.origin.y = min(max(frame.minY, visible.minY), max(visible.minY, visible.maxY - frame.height))
        return fitted
    }
}
