import AppKit
import ObjectiveC
import SwiftUI

/// The controls around the device, inside its window: Back / Home / Recents in a pill under the device,
/// always shown, and a title bar with the remaining buttons that appears while the pointer is near the
/// device's top; for a foldable also a rail at its right with its postures, always shown. The window keeps
/// room for them all, so its size is the size of everything it shows.
@MainActor
final class DeviceChrome: NSObject, NSPopoverDelegate {
    enum Action { case close, minimize, screenshot, rotateLeft, rotateRight, toggleFold, posture(PostureValue) }

    /// A device key went down (`true`) or up; the key is a W3C key name.
    var onKey: ((_ key: String, _ pressed: Bool) -> Void)?
    var onAction: ((Action) -> Void)?
    /// What the "More" popover shows and changes; set once the device's gRPC client exists.
    var controls: DeviceControls?

    static let barHeight: CGFloat = 44
    static let gap: CGFloat = 12
    private static let pillWidth: CGFloat = 120
    /// How far into the device the hover band reaches; with the title bar's room it is 140 pt tall.
    private static let bandDepth: CGFloat = 84
    private static let revealOffset: CGFloat = 6

    private weak var container: NSView?
    private let deviceName: String
    private let titleBar = GlassView(cornerRadius: 14)
    private let pill = GlassView(cornerRadius: 22)
    /// A foldable's postures, open, closed and half open, the one it is in marked.
    private let rail = GlassView(cornerRadius: 22)
    private var postureButtons: [(PostureValue, ToolbarButton)] = []
    private let hud = GlassView(cornerRadius: 14)
    private let hudLabel = NSTextField(labelWithString: "")
    private var titleButtons: [ToolbarButton] = []
    /// The device's name and details at the title bar's leading edge.
    private var titleLabels: [NSTextField] = []
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
    /// Shown whatever the pointer does, while a drag holds the window against the top of the screen.
    private var titleHeld = false
    /// While the window is being resized the title bar stays away and the size readout takes its place.
    private var resizing = false
    /// The box the bars are placed in, and the device in it, in the container's coordinates.
    private var box = CGRect.zero
    private var device = CGRect.zero
    private let foldable: Bool
    /// The title bar's buttons that may be left out on a narrow device, in the order they go.
    private var droppableButtons: [[ToolbarButton]] = []

    /// `foldable` adds the posture rail.
    init(container: NSView, deviceName: String, subtitle: String, foldable: Bool = false) {
        self.container = container
        self.deviceName = deviceName
        self.foldable = foldable
        super.init()
        buildTitleBar(subtitle: subtitle)
        buildPill()
        buildHud()
        if foldable { buildRail() }
        titleBar.alphaValue = 0
        titleBar.isHidden = true
        hud.isHidden = true
        [pill, titleBar, hud].forEach(container.addSubview)
        if foldable { container.addSubview(rail) }
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

    /// Places the bars in `box` around `device` (both in the container's coordinates): the title bar and the
    /// pill as wide as the device, the rail beside it.
    func layout(device: CGRect, box: CGRect) {
        self.box = box
        self.device = device
        fitTitleBar(width: device.width)
        titleBar.frame = titleFrame(visible: titleVisible)
        pill.frame = CGRect(x: (device.midX - Self.pillWidth / 2).rounded(), y: box.minY, width: Self.pillWidth, height: Self.barHeight)
        let hudWidth = min(max(150, ceil(hudLabel.intrinsicContentSize.width) + 28), device.width)
        hud.frame = CGRect(x: (device.midX - hudWidth / 2).rounded(), y: box.maxY - Self.barHeight, width: hudWidth, height: 32)
        let railHeight = Self.railHeight(buttons: postureButtons.count)
        rail.frame = CGRect(x: device.maxX + Self.gap, y: (device.midY - railHeight / 2).rounded(), width: Self.barHeight, height: railHeight)
        if let toast {
            let width = toast.frame.width
            toast.frame = CGRect(x: (device.midX - width / 2).rounded(), y: device.maxY - 96, width: width, height: 36)
        }
    }

    /// Until the guest has booted the buttons are disabled and the pill says so.
    func setReady(_ ready: Bool) {
        (titleButtons + navButtons + postureButtons.map(\.1)).forEach { $0.isEnabled = ready }
        navStack.isHidden = !ready
        bootLabel.isHidden = ready
    }

    /// Hides the title bar at once, e.g. when the screen is pressed.
    func dismiss() {
        showTimer?.invalidate()
        showTimer = nil
        if popover?.isShown != true { setTitleVisible(false) }
    }

    /// Shows the title bar while a drag has the window's top against the top of the screen: the window goes no
    /// higher, so the pointer cannot reach the band that shows it, and the bar would otherwise go unnoticed.
    /// Released, it hides unless the pointer is in that band.
    func holdTitleBar(_ hold: Bool) {
        guard hold != titleHeld else { return }
        titleHeld = hold
        showTimer?.invalidate()
        showTimer = nil
        setTitleVisible(pointerInZone)
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
        titleLabels = [name, detail]
        let labels = NSStackView(views: [name, detail])
        labels.orientation = .vertical
        labels.alignment = .leading
        labels.spacing = 1
        labels.setContentHuggingPriority(.defaultLow, for: .horizontal)
        labels.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        name.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        detail.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let screenshot = actionButton("camera", "截图", "⌘S", .screenshot)
        let rotate = [actionButton("rotate.left", "向左旋转", "⌘←", .rotateLeft),
                      actionButton("rotate.right", "向右旋转", "⌘→", .rotateRight)]
        let volume = [keyButton("speaker.wave.1", "音量 −", "⌥⌘↓", "AudioVolumeDown", size: 32),
                      keyButton("speaker.wave.3", "音量 +", "⌥⌘↑", "AudioVolumeUp", size: 32)]
        let power = keyButton("power", "电源", "⌘L", "Power", size: 32)
        titleButtons = [screenshot] + rotate + volume + [power]
        droppableButtons = [volume, [power], rotate, [screenshot]]
        let more = ToolbarButton(symbol: "ellipsis", label: "更多功能", shortcut: nil, size: 32)
        more.onPress = { [weak self] pressed in if pressed { self?.toggleMore() } }
        moreButton = more
        titleButtons.append(more)
        // At the leading edge, where a window's are; enabled while the device boots too.
        let windowButtons = WindowButtons { [weak self] action in self?.onAction?(action) }

        let stack = NSStackView(views: [windowButtons, labels] + titleButtons)
        stack.orientation = .horizontal
        // The labels take up the slack so the buttons stay at the trailing edge in a landscape-wide bar.
        stack.distribution = .fill
        stack.spacing = 2
        stack.edgeInsets = NSEdgeInsets(top: 0, left: 14, bottom: 0, right: 6)
        stack.setCustomSpacing(12, after: windowButtons)
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

    private func buildRail() {
        let postures: [(PostureValue, String, String)] = [
            (.postureOpened, "rectangle.split.2x1", "展开"),
            (.postureClosed, "rectangle.portrait", "合上"),
            (.postureHalfOpened, "book", "半开"),
        ]
        postureButtons = postures.map { posture, symbol, label in
            let button = ToolbarButton(symbol: symbol, label: label, shortcut: nil, size: 36)
            button.onPress = { [weak self] pressed in if !pressed { self?.onAction?(.posture(posture)) } }
            return (posture, button)
        }
        let stack = NSStackView(views: postureButtons.map(\.1))
        stack.orientation = .vertical
        stack.spacing = 2
        rail.content.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: rail.content.centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: rail.content.centerYAnchor),
        ])
    }

    /// The rail's height for `buttons` of the pill's size, with the pill's inset at the ends.
    private static func railHeight(buttons: Int) -> CGFloat {
        CGFloat(buttons) * 36 + CGFloat(max(buttons - 1, 0)) * 2 + 2 * 4
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

    /// The bar is as wide as the device, so on a small one what does not fit is left out: first the name,
    /// then the volume keys and power (also on the frame's side buttons and in the Device menu), then the
    /// rotate buttons, then screenshot. The window buttons and "More" always stay. The labels'
    /// stack stays as the flexible space that keeps the buttons at the trailing edge.
    private func fitTitleBar(width: CGFloat) {
        var hidden: [ToolbarButton] = []
        var showsLabels = true
        func needed() -> CGFloat {
            let buttons = CGFloat(titleButtons.count - hidden.count)
            return 14 + WindowButtons.width + 12 + (showsLabels ? 50 + 8 : 0) + buttons * 32 + (buttons - 1) * 2 + 6
        }
        if needed() > width { showsLabels = false }
        for group in droppableButtons where needed() > width { hidden += group }
        titleLabels.forEach { $0.isHidden = !showsLabels }
        for button in titleButtons { button.isHidden = hidden.contains { $0 === button } }
    }

    /// Marks the rail's button for the posture the device is in (none for postures it does not offer).
    func showPosture(_ posture: PostureValue) {
        for (value, button) in postureButtons { button.isOn = value == posture }
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

    private func titleFrame(visible: Bool) -> CGRect {
        CGRect(x: device.minX, y: box.maxY - Self.barHeight + (visible ? 0 : Self.revealOffset), width: device.width, height: Self.barHeight)
    }

    // MARK: - Hover

    private var pointerInZone: Bool {
        if titleHeld { return true }
        guard let container, let window = container.window, NSApp.isActive, !resizing else { return false }
        if popover?.isShown == true { return true }
        // The title bar's room plus the top of the device, in screen coordinates.
        let frame = window.convertToScreen(container.convert(CGRect(x: device.minX, y: box.minY, width: device.width, height: box.height), to: nil))
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
        guard visible != titleVisible else { return }
        titleVisible = visible
        // A hidden bar must not take clicks in the empty room it leaves.
        if visible { titleBar.isHidden = false }
        let target = titleFrame(visible: visible)
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

/// The standard close, minimize and zoom buttons, as at the leading edge of a titled window's title bar. Zoom
/// is greyed out: a device window has no full screen, and its sizes are in the Window menu.
final class WindowButtons: NSView {
    static let width: CGFloat = 14 * 3 + 2 * spacing
    private static let spacing: CGFloat = 6

    private let onAction: (DeviceChrome.Action) -> Void
    private var isPointerInside = false
    private var buttons: [NSButton] = []

    init(onAction: @escaping (DeviceChrome.Action) -> Void) {
        self.onAction = onAction
        super.init(frame: .zero)
        let kinds: [(NSWindow.ButtonType, Selector?)] = [
            (.closeButton, #selector(close)), (.miniaturizeButton, #selector(minimize)), (.zoomButton, nil),
        ]
        var x: CGFloat = 0
        for (kind, action) in kinds {
            guard let button = NSWindow.standardWindowButton(kind, for: [.titled, .closable, .miniaturizable, .resizable]) else { continue }
            button.target = self
            button.action = action
            button.isEnabled = action != nil
            Self.showSymbolsOnHover(button)
            button.setFrameOrigin(CGPoint(x: x, y: 0))
            x += button.frame.width + Self.spacing
            addSubview(button)
            buttons.append(button)
        }
        let height = buttons.map(\.frame.height).max() ?? 16
        translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: Self.width),
            heightAnchor.constraint(equalToConstant: height),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    @objc private func close() { onAction(.close) }
    @objc private func minimize() { onAction(.minimize) }

    /// Before macOS 26 the buttons ask their superview through this whether to show their symbols, which a
    /// title bar does while the pointer is over any of them.
    @objc func _mouseInGroup(_ button: NSButton) -> Bool { isPointerInside }

    /// From macOS 26 a button's cell asks the title bar it sits in instead (`_bezelInteractionState`, through
    /// `+[NSThemeFrame containingThemeFrameFromView:]`), and outside one the answer is never "rolled over". So the
    /// cell becomes a subclass that gives that state itself while the pointer is over the group: 4 shows the
    /// symbols, 2 is pressed. A disabled button keeps the system's answer, so zoom stays grey as in a title bar.
    private static func showSymbolsOnHover(_ button: NSButton) {
        let selector = NSSelectorFromString("_bezelInteractionState")
        guard let cell = button.cell, let base = object_getClass(cell),
              let method = class_getInstanceMethod(base, selector) else { return }
        let name = "EmulatorShell_" + String(cString: class_getName(base))
        if let subclass = objc_lookUpClass(name) {
            object_setClass(cell, subclass)
            return
        }
        guard let subclass = objc_allocateClassPair(base, name, 0) else { return }
        let system = unsafeBitCast(method_getImplementation(method), to: (@convention(c) (AnyObject, Selector) -> Int).self)
        let state: @convention(block) (NSButtonCell) -> Int = { cell in
            MainActor.assumeIsolated {
                guard cell.isEnabled, (cell.controlView?.superview as? WindowButtons)?.isPointerInside == true else {
                    return system(cell, selector)
                }
                return cell.isHighlighted ? 2 : 4
            }
        }
        class_addMethod(subclass, selector, imp_implementationWithBlock(state), method_getTypeEncoding(method))
        objc_registerClassPair(subclass)
        object_setClass(cell, subclass)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseEntered(with event: NSEvent) { setPointerInside(true) }
    override func mouseExited(with event: NSEvent) { setPointerInside(false) }

    private func setPointerInside(_ inside: Bool) {
        isPointerInside = inside
        // The cell keeps the look it worked out until something about the button changes.
        for button in buttons {
            let enabled = button.isEnabled
            button.isEnabled = !enabled
            button.isEnabled = enabled
            button.needsDisplay = true
        }
    }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
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

    private var label: String
    private let imageView = NSImageView()
    private var isHovered = false
    private var isPressed = false

    init(symbol: String, label: String, shortcut: String?, size: CGFloat) {
        self.label = label
        super.init(frame: CGRect(x: 0, y: 0, width: size, height: size))
        wantsLayer = true
        layer?.cornerRadius = size == 36 ? size / 2 : 9

        let image = imageView
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

    /// Shows another symbol and name, for a button whose action changes with the device's state.
    func update(symbol: String, label: String, shortcut: String?) {
        self.label = label
        imageView.image = NSImage(systemSymbolName: symbol, accessibilityDescription: label)
        toolTip = shortcut.map { "\(label)  \($0)" } ?? label
        setAccessibilityLabel(label)
    }

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
