import AppKit
import SwiftUI

/// The controls around the device, inside its window: Back / Home / Recents in a pill under the device,
/// always shown, and a title bar with the remaining buttons that appears while the pointer is near the
/// device's top. The window keeps room for both, so its size is the size of everything it shows.
@MainActor
final class DeviceChrome: NSObject, NSPopoverDelegate {
    enum Action { case screenshot, rotateLeft, rotateRight }

    /// A device key went down (`true`) or up; the key is a W3C key name.
    var onKey: ((_ key: String, _ pressed: Bool) -> Void)?
    var onAction: ((Action) -> Void)?
    /// What the "More" popover shows and changes; set once the device's gRPC client exists.
    var controls: DeviceControls?

    static let barHeight: CGFloat = 44
    static let gap: CGFloat = 12
    /// Below this the title bar's buttons and name no longer fit; the window is then wider than the device.
    static let titleMinWidth: CGFloat = 300
    private static let pillWidth: CGFloat = 120
    /// How far into the device the hover band reaches; with the title bar's room it is 140 pt tall.
    private static let bandDepth: CGFloat = 84
    private static let revealOffset: CGFloat = 6

    private weak var container: NSView?
    private let deviceName: String
    private let titleBar = GlassView(cornerRadius: 14)
    private let pill = GlassView(cornerRadius: 22)
    private let hud = GlassView(cornerRadius: 14)
    private let hudLabel = NSTextField(labelWithString: "")
    private var titleButtons: [ToolbarButton] = []
    private var navButtons: [ToolbarButton] = []
    private let navStack = NSStackView()
    private let bootLabel = NSTextField(labelWithString: "正在启动…")
    private var moreButton: ToolbarButton?
    private var popover: NSPopover?
    private var popoverMonitors: [Any] = []
    private var toast: GlassView?
    private var toastTimer: Timer?
    private var hudTimer: Timer?
    private var monitors: [Any] = []
    private var showTimer: Timer?
    private var hideTimer: Timer?
    private var titleVisible = false
    /// While the window is being resized the title bar stays away and the size readout takes its place.
    private var resizing = false

    init(container: NSView, deviceName: String, subtitle: String) {
        self.container = container
        self.deviceName = deviceName
        super.init()
        buildTitleBar(subtitle: subtitle)
        buildPill()
        buildHud()
        titleBar.alphaValue = 0
        titleBar.isHidden = true
        hud.isHidden = true
        [pill, titleBar, hud].forEach(container.addSubview)
        setReady(false)

        let moved: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged]
        monitors.append(NSEvent.addLocalMonitorForEvents(matching: moved) { [weak self] event in
            self?.pointerMoved()
            return event
        } as Any)
        // Over the window's transparent room above the device the pointer belongs to whatever is behind it.
        monitors.append(NSEvent.addGlobalMonitorForEvents(matching: moved) { [weak self] _ in
            MainActor.assumeIsolated { self?.pointerMoved() }
        } as Any)
        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(appActiveChanged), name: NSApplication.didBecomeActiveNotification, object: nil)
        center.addObserver(self, selector: #selector(appActiveChanged), name: NSApplication.didResignActiveNotification, object: nil)
    }

    /// Removes the event monitors and closes the popover; the chrome is unusable afterwards.
    func close() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors = []
        NotificationCenter.default.removeObserver(self)
        [showTimer, hideTimer, toastTimer, hudTimer].forEach { $0?.invalidate() }
        popover?.close()
    }

    /// Places the bars around `device` (container coordinates); the container is the whole window.
    func layout(device: CGRect) {
        guard let bounds = container?.bounds else { return }
        titleBar.frame = titleFrame(in: bounds, visible: titleVisible)
        pill.frame = CGRect(x: (bounds.midX - Self.pillWidth / 2).rounded(), y: 0, width: Self.pillWidth, height: Self.barHeight)
        let hudWidth = max(150, ceil(hudLabel.intrinsicContentSize.width) + 28)
        hud.frame = CGRect(x: (bounds.midX - hudWidth / 2).rounded(), y: bounds.maxY - Self.barHeight, width: hudWidth, height: 32)
        if let toast {
            let width = toast.frame.width
            toast.frame = CGRect(x: (device.midX - width / 2).rounded(), y: device.maxY - 96, width: width, height: 36)
        }
    }

    /// Until the guest has booted the buttons are disabled and the pill says so.
    func setReady(_ ready: Bool) {
        (titleButtons + navButtons).forEach { $0.isEnabled = ready }
        navStack.isHidden = !ready
        bootLabel.isHidden = ready
    }

    /// Hides the title bar at once, e.g. when the screen is pressed.
    func dismiss() {
        showTimer?.invalidate()
        showTimer = nil
        if popover?.isShown != true { setTitleVisible(false) }
    }

    /// Shows `text` where the title bar goes while the window is resized; nil hides it shortly after.
    func showResizeReadout(_ text: String?) {
        hudTimer?.invalidate()
        guard let text else {
            hudTimer = Timer.scheduledTimer(withTimeInterval: 0.8, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.hud.isHidden = true
                    self?.resizing = false
                }
            }
            return
        }
        if !resizing {
            resizing = true
            popover?.close()
            setTitleVisible(false, animated: false)
        }
        hudLabel.stringValue = text
        hud.isHidden = false
        container?.needsLayout = true
    }

    /// A short note over the top of the screen; clicking it runs `onClick`.
    func showToast(_ text: String, onClick: @escaping () -> Void) {
        toast?.removeFromSuperview()
        guard let container else { return }
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 13, weight: .medium)
        let toast = GlassView(cornerRadius: 18)
        let click = ClickView { [weak self] in
            onClick()
            self?.hideToast()
        }
        toast.content.addSubview(click)
        click.frame = toast.content.bounds
        click.autoresizingMask = [.width, .height]
        toast.content.addSubview(label)
        label.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            label.centerXAnchor.constraint(equalTo: toast.content.centerXAnchor),
            label.centerYAnchor.constraint(equalTo: toast.content.centerYAnchor),
        ])
        toast.frame = CGRect(x: 0, y: 0, width: ceil(label.intrinsicContentSize.width) + 32, height: 36)
        container.addSubview(toast)
        self.toast = toast
        container.needsLayout = true
        toastTimer?.invalidate()
        toastTimer = Timer.scheduledTimer(withTimeInterval: 3, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.hideToast() }
        }
    }

    private func hideToast() {
        guard let toast else { return }
        self.toast = nil
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.2; toast.animator().alphaValue = 0 }) {
            MainActor.assumeIsolated { toast.removeFromSuperview() }
        }
    }

    // MARK: - Building

    private func buildTitleBar(subtitle: String) {
        let name = NSTextField(labelWithString: deviceName)
        name.font = .systemFont(ofSize: 13, weight: .semibold)
        name.lineBreakMode = .byTruncatingTail
        let detail = NSTextField(labelWithString: subtitle)
        detail.font = .systemFont(ofSize: 11)
        detail.textColor = .secondaryLabelColor
        detail.lineBreakMode = .byTruncatingTail
        let labels = NSStackView(views: [name, detail])
        labels.orientation = .vertical
        labels.alignment = .leading
        labels.spacing = 1
        labels.setContentHuggingPriority(.defaultLow, for: .horizontal)
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        name.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        detail.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        titleButtons = [
            actionButton("camera", "截图", "⌘S", .screenshot),
            actionButton("rotate.left", "向左旋转", "⌘←", .rotateLeft),
            actionButton("rotate.right", "向右旋转", "⌘→", .rotateRight),
            keyButton("speaker.wave.1", "音量 −", "⌥⌘↓", "AudioVolumeDown", size: 32),
            keyButton("speaker.wave.3", "音量 +", "⌥⌘↑", "AudioVolumeUp", size: 32),
            keyButton("power", "电源", "⌘L", "Power", size: 32),
        ]
        let more = ToolbarButton(symbol: "ellipsis", label: "更多功能", shortcut: nil, size: 32)
        more.onPress = { [weak self] pressed in if pressed { self?.toggleMore() } }
        moreButton = more
        titleButtons.append(more)

        let stack = NSStackView(views: [labels] + titleButtons)
        stack.orientation = .horizontal
        // The labels take up the slack so the buttons stay at the trailing edge in a landscape-wide bar.
        stack.distribution = .fill
        stack.spacing = 2
        stack.edgeInsets = NSEdgeInsets(top: 0, left: 14, bottom: 0, right: 6)
        stack.setCustomSpacing(8, after: labels)
        titleBar.content.addSubview(stack)
        pin(stack, to: titleBar.content)
    }

    private func buildPill() {
        navButtons = [
            keyButton("arrowtriangle.backward", "返回", "Esc · ⌘B", "GoBack", size: 36),
            keyButton("circle", "主屏幕", "⇧⌘H", "GoHome", size: 36),
            keyButton("square", "最近任务", "⇧⌘O", "AppSwitch", size: 36),
        ]
        navButtons.forEach(navStack.addArrangedSubview)
        navStack.orientation = .horizontal
        navStack.spacing = 2
        pill.content.addSubview(navStack)
        navStack.translatesAutoresizingMaskIntoConstraints = false

        bootLabel.font = .systemFont(ofSize: 12)
        bootLabel.textColor = .secondaryLabelColor
        pill.content.addSubview(bootLabel)
        bootLabel.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            navStack.centerXAnchor.constraint(equalTo: pill.content.centerXAnchor),
            navStack.centerYAnchor.constraint(equalTo: pill.content.centerYAnchor),
            bootLabel.centerXAnchor.constraint(equalTo: pill.content.centerXAnchor),
            bootLabel.centerYAnchor.constraint(equalTo: pill.content.centerYAnchor),
        ])
    }

    private func buildHud() {
        hudLabel.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        hudLabel.alignment = .center
        hud.content.addSubview(hudLabel)
        hudLabel.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            hudLabel.centerXAnchor.constraint(equalTo: hud.content.centerXAnchor),
            hudLabel.centerYAnchor.constraint(equalTo: hud.content.centerYAnchor),
        ])
    }

    private func keyButton(_ symbol: String, _ label: String, _ shortcut: String, _ key: String, size: CGFloat) -> ToolbarButton {
        let button = ToolbarButton(symbol: symbol, label: label, shortcut: shortcut, size: size)
        button.onPress = { [weak self] pressed in self?.onKey?(key, pressed) }
        return button
    }

    private func actionButton(_ symbol: String, _ label: String, _ shortcut: String, _ action: Action) -> ToolbarButton {
        let button = ToolbarButton(symbol: symbol, label: label, shortcut: shortcut, size: 32)
        button.onPress = { [weak self] pressed in if !pressed { self?.onAction?(action) } }
        return button
    }

    private func pin(_ view: NSView, to container: NSView) {
        view.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            view.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            view.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            view.topAnchor.constraint(equalTo: container.topAnchor),
            view.bottomAnchor.constraint(equalTo: container.bottomAnchor),
        ])
    }

    private func titleFrame(in bounds: CGRect, visible: Bool) -> CGRect {
        CGRect(x: 0, y: bounds.maxY - Self.barHeight + (visible ? 0 : Self.revealOffset), width: bounds.width, height: Self.barHeight)
    }

    // MARK: - Hover

    private var pointerInZone: Bool {
        guard let window = container?.window, NSApp.isActive, !resizing else { return false }
        if popover?.isShown == true { return true }
        // The title bar's room plus the top of the device, in screen coordinates.
        let frame = window.frame
        let band = CGRect(x: frame.minX, y: frame.maxY - Self.barHeight - Self.gap - Self.bandDepth,
                          width: frame.width, height: Self.barHeight + Self.gap + Self.bandDepth)
        return band.contains(NSEvent.mouseLocation)
    }

    /// Shows the title bar once the pointer has rested in the band for 120 ms; hides it 600 ms after it leaves.
    private func pointerMoved() {
        if pointerInZone {
            hideTimer?.invalidate()
            hideTimer = nil
            guard !titleVisible, showTimer == nil else { return }
            showTimer = Timer.scheduledTimer(withTimeInterval: 0.12, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.showTimer = nil
                    if self.pointerInZone { self.setTitleVisible(true) }
                }
            }
        } else {
            showTimer?.invalidate()
            showTimer = nil
            guard titleVisible, hideTimer == nil else { return }
            hideTimer = Timer.scheduledTimer(withTimeInterval: 0.6, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.hideTimer = nil
                    if !self.pointerInZone { self.setTitleVisible(false) }
                }
            }
        }
    }

    private func setTitleVisible(_ visible: Bool, animated: Bool = true) {
        hideTimer?.invalidate()
        hideTimer = nil
        guard visible != titleVisible, let bounds = container?.bounds else { return }
        titleVisible = visible
        // A hidden bar must not take clicks in the empty room it leaves.
        if visible { titleBar.isHidden = false }
        let target = titleFrame(in: bounds, visible: visible)
        NSAnimationContext.runAnimationGroup({ context in
            context.duration = animated ? (visible ? 0.16 : 0.12) : 0
            titleBar.animator().alphaValue = visible ? 1 : 0
            titleBar.animator().frame = target
        }, completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                if !self.titleVisible { self.titleBar.isHidden = true }
                self.container?.window?.invalidateShadow()
            }
        })
    }

    @objc private func appActiveChanged() {
        pill.alphaValue = NSApp.isActive ? 1 : 0.6
        if !NSApp.isActive {
            popover?.close()
            dismiss()
        }
    }

    // MARK: - More

    private func toggleMore() {
        if let popover, popover.isShown {
            popover.close()
            return
        }
        guard let moreButton, let controls else { return }
        let popover = NSPopover()
        // Closed here rather than by `.transient`, which took clicks inside it for ones outside while the
        // bars lived in panels that never became key; the rules below are the ones wanted either way.
        popover.behavior = .applicationDefined
        popover.delegate = self
        let hosting = NSHostingController(rootView: MorePanel(controls: controls))
        // The popover follows the panel's size as it switches between tiles and pages.
        hosting.sizingOptions = [.preferredContentSize]
        popover.contentViewController = hosting
        popover.show(relativeTo: moreButton.bounds, of: moreButton, preferredEdge: .minY)
        moreButton.isOn = true
        self.popover = popover
        popoverMonitors = [
            NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .keyDown]) { [weak self] event in
                let usedUp = MainActor.assumeIsolated { self?.closePopover(on: event) ?? false }
                return usedUp ? nil : event
            } as Any,
            NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
                MainActor.assumeIsolated { self?.popover?.close() }
            } as Any,
        ]
    }

    /// Closes the popover on Esc or a click outside it; true when the event was used up.
    private func closePopover(on event: NSEvent) -> Bool {
        guard let popover, popover.isShown, let popoverWindow = popover.contentViewController?.view.window else { return false }
        if event.type == .keyDown {
            guard event.keyCode == 53 else { return false }
            popover.close()
            return true
        }
        // The title bar's "More" button toggles the popover itself; dialogs the popover opens are part of it.
        let window = event.window
        var onTitleBar = false
        if let container, window === container.window {
            onTitleBar = titleBar.frame.contains(container.convert(event.locationInWindow, from: nil))
        }
        let inside = window === popoverWindow || onTitleBar
            || window?.sheetParent === popoverWindow || window?.parent === popoverWindow
        if !inside { popover.close() }
        return false
    }

    nonisolated func popoverDidClose(_ notification: Notification) {
        MainActor.assumeIsolated {
            popoverMonitors.forEach(NSEvent.removeMonitor)
            popoverMonitors = []
            moreButton?.isOn = false
            popover = nil
            pointerMoved()
        }
    }
}

/// A rounded glass surface inside the device window; its controls go in `content`.
final class GlassView: NSView {
    let content: NSView = DragView()

    init(cornerRadius: CGFloat) {
        super.init(frame: CGRect(x: 0, y: 0, width: 100, height: 40))
        let background: NSView
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.cornerRadius = cornerRadius
            glass.contentView = content
            background = glass
        } else {
            let effect = NSVisualEffectView()
            effect.material = .popover
            effect.blendingMode = .behindWindow
            effect.state = .active
            effect.wantsLayer = true
            effect.layer?.cornerRadius = cornerRadius
            effect.layer?.masksToBounds = true
            effect.addSubview(content)
            content.frame = effect.bounds
            content.autoresizingMask = [.width, .height]
            background = effect
        }
        background.frame = bounds
        background.autoresizingMask = [.width, .height]
        addSubview(background)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }
}

/// Background of a glass surface: dragging it moves the device window.
final class DragView: NSView {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        (window as? DeviceWindow)?.drag(with: event)
    }
}

/// Runs a closure when clicked.
final class ClickView: NSView {
    private let action: () -> Void

    init(action: @escaping () -> Void) {
        self.action = action
        super.init(frame: .zero)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseUp(with event: NSEvent) { action() }
}

/// An icon button that reports press and release separately, so device keys can be held.
final class ToolbarButton: NSView {
    var onPress: ((Bool) -> Void)?
    var isEnabled = true {
        didSet { alphaValue = isEnabled ? 1 : 0.35 }
    }
    /// Keeps the pressed look, e.g. while the button's popover is open.
    var isOn = false {
        didSet { updateBackground() }
    }

    private let label: String
    private var isHovered = false
    private var isPressed = false

    init(symbol: String, label: String, shortcut: String?, size: CGFloat) {
        self.label = label
        super.init(frame: CGRect(x: 0, y: 0, width: size, height: size))
        wantsLayer = true
        layer?.cornerRadius = size == 36 ? size / 2 : 9

        let image = NSImageView()
        image.image = NSImage(systemSymbolName: symbol, accessibilityDescription: label)
        image.symbolConfiguration = NSImage.SymbolConfiguration(pointSize: 15, weight: .regular)
        image.contentTintColor = .labelColor
        addSubview(image)
        image.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: size),
            heightAnchor.constraint(equalToConstant: size),
            image.centerXAnchor.constraint(equalTo: centerXAnchor),
            image.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
        toolTip = shortcut.map { "\(label)  \($0)" } ?? label
        setAccessibilityElement(true)
        setAccessibilityRole(.button)
        setAccessibilityLabel(label)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseEntered(with event: NSEvent) {
        isHovered = true
        updateBackground()
    }

    override func mouseExited(with event: NSEvent) {
        isHovered = false
        updateBackground()
    }

    override func mouseDown(with event: NSEvent) {
        guard isEnabled else { return }
        isPressed = true
        updateBackground()
        onPress?(true)
    }

    override func mouseUp(with event: NSEvent) {
        guard isPressed else { return }
        isPressed = false
        updateBackground()
        onPress?(false)
    }

    override func accessibilityPerformPress() -> Bool {
        guard isEnabled else { return false }
        onPress?(true)
        onPress?(false)
        return true
    }

    private func updateBackground() {
        let alpha: CGFloat = isPressed || isOn ? 0.15 : (isHovered && isEnabled ? 0.07 : 0)
        layer?.backgroundColor = NSColor.labelColor.withAlphaComponent(alpha).cgColor
    }
}
