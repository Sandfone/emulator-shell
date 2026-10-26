import AppKit

/// The device window's content: the device, with room above it for the title bar and below it for the
/// button pill. The window is exactly this box, so its size is everything it shows; the room stays
/// reserved while the title bar is hidden so the device does not jump when it appears.
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

    init(deviceView: DeviceView) {
        self.deviceView = deviceView
        super.init(frame: CGRect(origin: .zero, size: Self.boxSize(device: deviceView.size(forQuarterTurns: 0))))
        wantsLayer = true
        addSubview(deviceView)
        for corner in ResizeHandle.Corner.allCases {
            let handle = ResizeHandle(corner: corner)
            handle.onPhase = { [weak self] phase in self?.onResize?(phase) }
            handles.append(handle)
            addSubview(handle)
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    /// The window size around a device of `device` points.
    static func boxSize(device: CGSize) -> CGSize {
        CGSize(width: max(device.width, DeviceChrome.titleMinWidth), height: device.height + topRoom + bottomRoom)
    }

    /// The device's frame in this view.
    var deviceFrame: CGRect {
        let size = deviceView.size(forQuarterTurns: deviceView.quarterTurns)
        return CGRect(x: ((bounds.width - size.width) / 2).rounded(), y: Self.bottomRoom, width: size.width, height: size.height)
    }

    override func layout() {
        super.layout()
        let device = deviceFrame
        deviceView.frame = device
        chrome?.layout(device: device)
        let side = ResizeHandle.side
        for handle in handles {
            let x = handle.corner.isLeft ? 0 : bounds.width - side
            let y = handle.corner.isTop ? bounds.height - side : 0
            handle.frame = CGRect(x: x, y: y, width: side, height: side)
        }
    }
}

/// An invisible square at a corner of the device window that resizes it.
@MainActor
final class ResizeHandle: NSView {
    enum Corner: CaseIterable {
        case topLeft, topRight, bottomLeft, bottomRight
        var isLeft: Bool { self == .topLeft || self == .bottomLeft }
        var isTop: Bool { self == .topLeft || self == .topRight }
    }

    enum Phase {
        case began(Corner)
        /// Where the corner should now be, in screen coordinates.
        case changed(CGPoint)
        case ended
    }

    static let side: CGFloat = 20

    let corner: Corner
    var onPhase: ((Phase) -> Void)?
    /// From the pointer to the window's corner when the drag began, so the corner does not jump to the pointer.
    private var grabOffset = CGPoint.zero

    init(corner: Corner) {
        self.corner = corner
        super.init(frame: CGRect(x: 0, y: 0, width: Self.side, height: Self.side))
        wantsLayer = true
        // Fully transparent pixels let clicks through to the window behind; this is just above that.
        layer?.backgroundColor = NSColor(white: 0, alpha: 0.01).cgColor
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        let position: NSCursor.FrameResizePosition = switch corner {
        case .topLeft: .topLeft
        case .topRight: .topRight
        case .bottomLeft: .bottomLeft
        case .bottomRight: .bottomRight
        }
        addCursorRect(bounds, cursor: .frameResize(position: position, directions: .all))
    }

    override func mouseDown(with event: NSEvent) {
        guard let window else { return }
        let frame = window.frame
        let windowCorner = CGPoint(x: corner.isLeft ? frame.minX : frame.maxX, y: corner.isTop ? frame.maxY : frame.minY)
        let pointer = NSEvent.mouseLocation
        grabOffset = CGPoint(x: windowCorner.x - pointer.x, y: windowCorner.y - pointer.y)
        onPhase?(.began(corner))
    }

    override func mouseDragged(with event: NSEvent) {
        let pointer = NSEvent.mouseLocation
        onPhase?(.changed(CGPoint(x: pointer.x + grabOffset.x, y: pointer.y + grabOffset.y)))
    }

    override func mouseUp(with event: NSEvent) {
        onPhase?(.ended)
    }
}
