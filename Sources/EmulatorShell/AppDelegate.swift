import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuItemValidation {
    /// Set when launched as `EmulatorShell <avd>`: that device opens directly, without the manager window.
    private let launchAvd: String?
    private let emulatorArguments: [String]
    private let manager = DeviceManager()
    private var managerWindow: NSWindow?
    private var quitting = false

    init(launchAvd: String?, emulatorArguments: [String] = []) {
        self.launchAvd = launchAvd
        self.emulatorArguments = emulatorArguments
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.mainMenu = makeMainMenu()
        manager.listenForShowRequests()
        guard let launchAvd else {
            showManager()
            checkForwarder()
            return
        }
        guard let avd = manager.avds.first(where: { $0.id == launchAvd }) else {
            fail("没有名为 \(launchAvd) 的 AVD。")
            return
        }
        // A device another program already runs is shown instead of started a second time; one another
        // Emulator Shell shows is left to it, its window brought forward.
        if case .external = manager.status(of: avd), manager.isInOtherEmulatorShell(avd) {
            manager.showInOwner(avd)
            NSApp.terminate(nil)
            return
        } else if case .external = manager.status(of: avd) {
            manager.attach(avd)
        } else {
            manager.start(avd, extraArguments: EmulatorForwarder.forwardable(emulatorArguments))
        }
        if let message = manager.errorMessage { fail(message) }
    }

    /// Opened for a single device, the app ends with it, unless it is only restarting; otherwise the
    /// manager keeps it running.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        launchAvd != nil && !quitting && !manager.isRestarting
    }

    /// Quitting waits for the emulators to save their snapshots, which takes seconds; the windows go at
    /// once and the app stays responsive meanwhile.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        // A second request while the first waits (another ⌘Q) changes nothing.
        if quitting { return .terminateCancel }
        guard manager.hasSessions else { return .terminateNow }
        quitting = true
        managerWindow?.orderOut(nil)
        manager.stopAllSessions { sender.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { showManager() }
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        manager.stopAllSessions()
    }

    private func fail(_ message: String) {
        Log.error(message)
        let alert = NSAlert()
        alert.messageText = "Emulator Shell"
        alert.informativeText = message
        alert.runModal()
        NSApp.terminate(nil)
    }

    // MARK: - Manager window

    @objc private func showManager() {
        if managerWindow == nil {
            let hosting = NSHostingController(rootView: ManagerView(manager: manager))
            hosting.sceneBridgingOptions = [.toolbars, .title]
            let window = NSWindow(contentViewController: hosting)
            window.styleMask = [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView]
            window.toolbarStyle = .unified
            window.title = "设备"
            window.isReleasedWhenClosed = false
            window.minSize = CGSize(width: 900, height: 560)
            window.setContentSize(CGSize(width: 1120, height: 720))
            window.center()
            window.setFrameAutosaveName("DeviceManager")
            managerWindow = window
        }
        NSApp.activate()
        managerWindow?.makeKeyAndOrderFront(nil)
    }

    // MARK: - SDK forwarder

    private static let forwarderWantedKey = "forwarderWanted"

    @objc private func toggleForwarder() {
        if case .installed = EmulatorForwarder.state {
            perform("恢复 SDK 原来的模拟器") { try EmulatorForwarder.uninstall() }
            UserDefaults.standard.set(false, forKey: Self.forwarderWantedKey)
            return
        }
        let alert = NSAlert()
        alert.messageText = "让 Android Studio 等工具使用 Emulator Shell？"
        alert.informativeText = """
        Android Studio、Flutter 和 android 命令行都直接运行 SDK 里的 emulator/emulator。这会把它换成一个转发脚本：\
        启动设备时打开 Emulator Shell，其他用途（列出 AVD、查版本、Gradle 的无窗口测试）仍交给原来的模拟器，它会改名为 emulator.real。

        Android Studio 里还要关掉 Settings › Tools › Emulator ›「Launch in the Running Devices tool window」。\
        SDK 更新模拟器后需要重新开启；随时可以在这里关闭，恢复原样。
        """
        alert.addButton(withTitle: "开启")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        if perform("开启转发", { try EmulatorForwarder.install() }) {
            UserDefaults.standard.set(true, forKey: Self.forwarderWantedKey)
        }
    }

    @objc private func toggleCommand() {
        if EmulatorForwarder.isCommandInstalled {
            perform("移除命令行工具") { try EmulatorForwarder.uninstallCommand() }
        } else if perform("安装命令行工具", { _ = try EmulatorForwarder.installCommand() }), let link = EmulatorForwarder.commandLink {
            inform("已安装 \(link.path)", "在终端里运行 emulator-shell <AVD 名称> 打开设备，不带参数打开设备管理器。")
        }
    }

    /// On opening the manager: an SDK update replaces the forwarder with a new emulator, and a rebuilt or
    /// moved app leaves it pointing at a stale path.
    private func checkForwarder() {
        switch EmulatorForwarder.state {
        case .replacedBySdkUpdate where UserDefaults.standard.bool(forKey: Self.forwarderWantedKey):
            let alert = NSAlert()
            alert.messageText = "SDK 更新替换了模拟器转发"
            alert.informativeText = "Android Studio 等工具现在会启动 SDK 自带的模拟器。要重新让它们使用 Emulator Shell 吗？"
            alert.addButton(withTitle: "重新开启")
            alert.addButton(withTitle: "不用了")
            if alert.runModal() == .alertFirstButtonReturn {
                perform("重新开启转发") { try EmulatorForwarder.install() }
            } else {
                UserDefaults.standard.set(false, forKey: Self.forwarderWantedKey)
            }
        case let .installed(path) where path != EmulatorForwarder.appPath:
            perform("更新转发脚本") { try EmulatorForwarder.install() }
        default:
            break
        }
    }

    @discardableResult
    private func perform(_ action: String, _ body: () throws -> Void) -> Bool {
        do {
            try body()
            return true
        } catch {
            inform("\(action)失败", error.localizedDescription)
            return false
        }
    }

    private func inform(_ title: String, _ detail: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = detail
        alert.runModal()
    }

    // MARK: - Menus

    /// The device window in front, which the Device menu acts on.
    private var currentSession: DeviceSession? {
        manager.session(for: NSApp.keyWindow) ?? manager.session(for: NSApp.mainWindow)
    }

    private func makeMainMenu() -> NSMenu {
        let main = NSMenu()

        let appMenu = NSMenu()
        let forwarderItem = appMenu.addItem(withTitle: "让 Android Studio 等工具使用 Emulator Shell", action: #selector(toggleForwarder), keyEquivalent: "")
        forwarderItem.target = self
        let commandItem = appMenu.addItem(withTitle: "安装命令行工具 emulator-shell", action: #selector(toggleCommand), keyEquivalent: "")
        commandItem.target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "隐藏 Emulator Shell", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "退出 Emulator Shell", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        main.addItem(submenu: appMenu, title: "Emulator Shell")

        let editMenu = NSMenu(title: "编辑")
        editMenu.addItem(withTitle: "撤销", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: "重做", action: Selector(("redo:")), keyEquivalent: "Z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "拷贝", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        main.addItem(submenu: editMenu, title: "编辑")

        let deviceMenu = NSMenu(title: "设备")
        for (index, group) in DeviceCommand.menuGroups.enumerated() {
            if index > 0 { deviceMenu.addItem(.separator()) }
            for (title, command, shortcut, modifiers) in group {
                let item = NSMenuItem(title: title, action: #selector(runDeviceCommand(_:)), keyEquivalent: shortcut)
                item.keyEquivalentModifierMask = modifiers
                item.representedObject = command
                item.target = self
                deviceMenu.addItem(item)
            }
        }
        main.addItem(submenu: deviceMenu, title: "设备")

        let windowMenu = NSMenu(title: "窗口")
        let managerItem = windowMenu.addItem(withTitle: "设备管理器", action: #selector(showManager), keyEquivalent: "2")
        managerItem.keyEquivalentModifierMask = [.command, .shift]
        managerItem.target = self
        windowMenu.addItem(.separator())
        for (title, zoom, key) in DeviceZoom.menuItems {
            let item = NSMenuItem(title: title, action: #selector(zoomDevice(_:)), keyEquivalent: key)
            item.representedObject = zoom
            item.target = self
            windowMenu.addItem(item)
            if zoom == .larger {
                // ⌘= is the same key as ⌘+ without Shift.
                let alternate = NSMenuItem(title: title, action: #selector(zoomDevice(_:)), keyEquivalent: "=")
                alternate.representedObject = zoom
                alternate.target = self
                alternate.isHidden = true
                alternate.allowsKeyEquivalentWhenHidden = true
                windowMenu.addItem(alternate)
            }
        }
        windowMenu.addItem(.separator())
        windowMenu.addItem(withTitle: "最小化", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        let closeItem = windowMenu.addItem(withTitle: "关闭", action: #selector(closeFrontWindow), keyEquivalent: "w")
        closeItem.target = self
        main.addItem(submenu: windowMenu, title: "窗口")
        NSApp.windowsMenu = windowMenu
        return main
    }

    @objc private func runDeviceCommand(_ sender: NSMenuItem) {
        guard let command = sender.representedObject as? DeviceCommand else { return }
        if let session = currentSession { return session.run(command) }
        // In the manager, screenshots and recording work on the selected phone.
        guard let device = managerPhone else { return }
        switch command {
        case .action(.screenshot): manager.phones.screenshot(device.id)
        case .action(.record): manager.phones.toggleRecording(device.id)
        case .action(.sendClipboard): SendTextPrompt.show(for: device, phones: manager.phones)
        default: break
        }
    }

    /// The phone selected in the manager window, while that window is in front.
    private var managerPhone: PhysicalDevice? {
        guard NSApp.keyWindow === managerWindow, let device = manager.selectedPhysical, device.isOnline else { return nil }
        return device
    }

    @objc private func zoomDevice(_ sender: NSMenuItem) {
        guard let zoom = sender.representedObject as? DeviceZoom else { return }
        currentSession?.zoom(zoom)
    }

    /// Closing a device window shuts the device down (saving its snapshot); there is nothing to show without it.
    /// A device another program started only loses this window.
    @objc private func closeFrontWindow() {
        if let session = currentSession {
            session.close()
        } else {
            NSApp.keyWindow?.performClose(nil)
        }
    }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == #selector(toggleForwarder) {
            let state = EmulatorForwarder.state
            if case .installed = state { menuItem.state = .on } else { menuItem.state = .off }
            return state != .foreign
        }
        if menuItem.action == #selector(toggleCommand) {
            menuItem.title = EmulatorForwarder.isCommandInstalled ? "移除命令行工具 emulator-shell" : "安装命令行工具 emulator-shell"
            return EmulatorForwarder.commandLink != nil
        }
        if menuItem.action == #selector(runDeviceCommand(_:)) {
            guard let command = menuItem.representedObject as? DeviceCommand else { return false }
            guard let session = currentSession else {
                guard managerPhone != nil else { return false }
                if case .action(.screenshot) = command { return true }
                if case .action(.record) = command { return true }
                if case .action(.sendClipboard) = command { return true }
                return false
            }
            return session.state == .running && session.isAvailable(command)
        }
        if menuItem.action == #selector(zoomDevice(_:)), let zoom = menuItem.representedObject as? DeviceZoom {
            return currentSession?.canZoom(zoom) ?? false
        }
        if menuItem.action == #selector(closeFrontWindow) { return NSApp.keyWindow != nil }
        return true
    }
}

private extension NSMenu {
    func addItem(submenu: NSMenu, title: String) {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.submenu = submenu
        addItem(item)
    }
}
