import AppKit

/// The device window's content: the device, with room above it for the title bar and below it for the
/// button pill. That box is everything the window shows, so its size is the window's size as the user
/// sees it; the room stays reserved while the title bar is hidden so the device does not jump when it
/// appears. Around the box is the window's thin margin (`DeviceWindow.margin`), empty except where the
/// corner resize areas reach into it.
@MainActor
final class DeviceShellView: NSView {
    /// Space above the device (title bar and gap) and below it (gap and pill).
    static let topRoom = DeviceChrome.barHeight + DeviceChrome.gap
    static let bottomRoom = DeviceChrome.gap + DeviceChrome.barHeight

    let deviceView: DeviceView
    /// Set once the chrome has added its views, so the handles can go above them.
    var chrome: DeviceChrome? {
        didSet { handles.forEach { addSubview($0, positioned: .above, relativeTo: nil) } }
    }
    /// A corner drag: it began, the pointer moved to a screen point, or it ended.
    var onResize: ((ResizeHandle.Phase) -> Void)?

    private var handles: [ResizeHandle] = []
    private var resizing = false
    /// Whether this view set the resize cursor, so it puts the arrow back once the pointer leaves.
    private var showsResizeCursor = false
    private var monitors: [Any] = []

    init(deviceView: DeviceView) {
        self.deviceView = deviceView
        super.init(frame: CGRect(origin: .zero, size: Self.boxSize(device: deviceView.size(forQuarterTurns: 0))))
        wantsLayer = true
        addSubview(deviceView)
        for corner in ResizeHandle.Corner.allCases {
            let handle = ResizeHandle(corner: corner)
            handle.onPhase = { [weak self] phase in
                guard let self else { return }
                switch phase {
                case .began: self.resizing = true
                case .ended: self.resizing = false
                case .changed: break
                }
                self.onResize?(phase)
                self.pointerMoved()
            }
            handles.append(handle)
            addSubview(handle)
        }

        // Over the window's empty parts the pointer belongs to whatever is behind it, so a global monitor
        // follows it there.
        let moved: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged]
        monitors.append(NSEvent.addLocalMonitorForEvents(matching: moved) { [weak self] event in
            self?.pointerMoved()
            return event
        } as Any)
        monitors.append(NSEvent.addGlobalMonitorForEvents(matching: moved) { [weak self] _ in
            MainActor.assumeIsolated { self?.pointerMoved() }
        } as Any)
    }

    /// Removes the event monitors; for when the window closes.
    func stopTracking() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors = []
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    /// The box's size around a device of `device` points: as wide as the device, whose bars fit what room
    /// there is.
    static func boxSize(device: CGSize) -> CGSize {
        CGSize(width: device.width, height: device.height + topRoom + bottomRoom)
    }

    /// The box in this view: everything but the window's margin.
    var boxFrame: CGRect {
        bounds.insetBy(dx: DeviceWindow.margin, dy: DeviceWindow.margin)
    }

    /// The device's frame in this view.
    var deviceFrame: CGRect {
        let box = boxFrame
        let size = deviceView.size(forQuarterTurns: deviceView.quarterTurns)
        return CGRect(x: (box.midX - size.width / 2).rounded(), y: box.minY + Self.bottomRoom, width: size.width, height: size.height)
    }

    override func layout() {
        super.layout()
        let device = deviceFrame
        deviceView.frame = device
        chrome?.layout(device: device, box: boxFrame)
        for handle in handles {
            handle.place(at: device, bodyInset: deviceView.cornerInset(clockwise: handle.corner.clockwiseIndex))
        }
        pointerMoved()
    }

    /// The resize cursor exactly while the pointer is over a corner area, where a press resizes. Tracking
    /// areas missed the pointer crossing a corner from outside and never put the arrow back past the
    /// window's edge, so the pointer's position decides. No app can change the cursor while it is in the
    /// background, but a press there still resizes.
    private func pointerMoved() {
        guard let window, window.isVisible, NSApp.isActive, !resizing else { return }
        let pointer = NSEvent.mouseLocation
        if let handle = handles.first(where: { $0.contains(screenPoint: pointer) }) {
            handle.cursor.set()
            showsResizeCursor = true
        } else if showsResizeCursor {
            showsResizeCursor = false
            NSCursor.arrow.set()
        }
    }
}

/// An invisible area at a corner of the device that resizes the window. Like a standard window's corner,
/// it reaches a little outside the device's rectangle and in to just past the body's rounded corner, so it
/// sits on the corner the user sees.
///
/// Only the transparent part outside the body is drawn, faintly enough to take clicks; the body takes
/// them by itself.
@MainActor
final class ResizeHandle: NSView {
    enum Corner: CaseIterable {
        case topLeft, topRight, bottomLeft, bottomRight
        var isLeft: Bool { self == .topLeft || self == .bottomLeft }
        var isTop: Bool { self == .topLeft || self == .topRight }
        /// Steps round from the top left, clockwise.
        var clockwiseIndex: Int {
            switch self {
            case .topLeft: 0
            case .topRight: 1
            case .bottomRight: 2
            case .bottomLeft: 3
            }
        }
    }

    enum Phase {
        case began(Corner)
        /// Where the box's corner should now be, in screen coordinates.
        case changed(CGPoint)
        case ended
    }

    /// How far the area reaches outside the device's rectangle, and past the body's corner into it.
    static let outside: CGFloat = 8
    static let inside: CGFloat = 8

    let corner: Corner
    let cursor: NSCursor
    var onPhase: ((Phase) -> Void)?
    /// From the pointer to the box's corner when the drag began, so the corner does not jump to the pointer.
    private var grabOffset = CGPoint.zero
    private let shape = CAShapeLayer()
    private var bodyInset: CGFloat = -1

    init(corner: Corner) {
        self.corner = corner
        let position: NSCursor.FrameResizePosition = switch corner {
        case .topLeft: .topLeft
        case .topRight: .topRight
        case .bottomLeft: .bottomLeft
        case .bottomRight: .bottomRight
        }
        cursor = .frameResize(position: position, directions: .all)
        super.init(frame: .zero)
        wantsLayer = true
        layer?.masksToBounds = true
        shape.fillRule = .evenOdd
        // Fully transparent pixels let clicks through to the window behind; this is just above that.
        shape.fillColor = NSColor(white: 0, alpha: 0.01).cgColor
        layer?.addSublayer(shape)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    /// Puts the area at its corner of `device` (in the superview's coordinates), whose body starts `bodyInset`
    /// in from that corner along each axis.
    func place(at device: CGRect, bodyInset: CGFloat) {
        let side = Self.outside + bodyInset + Self.inside
        frame = CGRect(x: corner.isLeft ? device.minX - Self.outside : device.maxX - side + Self.outside,
                       y: corner.isTop ? device.maxY - side + Self.outside : device.minY - Self.outside,
                       width: side, height: side)
        guard bodyInset != self.bodyInset else { return }
        self.bodyInset = bodyInset
        // The body as a rounded rectangle whose curve passes `bodyInset` in along the diagonal, drawn large
        // enough to cover the area from its corner.
        let radius = bodyInset / (1 - 1 / 2.squareRoot())
        let size = max(2 * radius, 2 * side)
        let body = CGRect(x: corner.isLeft ? Self.outside : side - Self.outside - size,
                          y: corner.isTop ? side - Self.outside - size : Self.outside,
                          width: size, height: size)
        let path = CGMutablePath()
        path.addRect(CGRect(x: 0, y: 0, width: side, height: side))
        path.addPath(CGPath(roundedRect: body, cornerWidth: radius, cornerHeight: radius, transform: nil))
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        shape.path = path
        CATransaction.commit()
    }

    /// Whether `point` (screen coordinates) is in the area.
    func contains(screenPoint point: CGPoint) -> Bool {
        guard let window else { return false }
        return bounds.contains(convert(window.convertPoint(fromScreen: point), from: nil))
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        guard let window = window as? DeviceWindow else { return }
        // The click may have just brought the app forward, which is when it may set the cursor again.
        cursor.set()
        let box = window.box
        let boxCorner = CGPoint(x: corner.isLeft ? box.minX : box.maxX, y: corner.isTop ? box.maxY : box.minY)
        let pointer = NSEvent.mouseLocation
        grabOffset = CGPoint(x: boxCorner.x - pointer.x, y: boxCorner.y - pointer.y)
        onPhase?(.began(corner))
    }

    override func mouseDragged(with event: NSEvent) {
        // The pointer leaves the area when the drag outruns the window; the cursor stays the resize one.
        cursor.set()
        let pointer = NSEvent.mouseLocation
        onPhase?(.changed(CGPoint(x: pointer.x + grabOffset.x, y: pointer.y + grabOffset.y)))
    }

    override func mouseUp(with event: NSEvent) {
        onPhase?(.ended)
    }
}
