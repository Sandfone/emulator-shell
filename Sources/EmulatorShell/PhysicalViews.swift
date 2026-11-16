import AppKit
import SwiftUI

/// The sidebar row of a phone or tablet.
struct PhysicalRow: View {
    let manager: DeviceManager
    let device: PhysicalDevice
    /// Red on the selection's accent colour does not read; the timer goes white there.
    var selected = false

    var body: some View {
        let recording = manager.phones.recordings[device.id]
        HStack(spacing: 10) {
            Image(systemName: device.info?.isTablet == true ? "ipad.landscape" : "iphone")
                .font(.system(size: 17))
                .frame(width: 22)
                .foregroundStyle(device.state == .offline ? .secondary : .primary)
            VStack(alignment: .leading, spacing: 1) {
                Text(device.name).lineLimit(1)
                    .foregroundStyle(device.state == .offline ? .secondary : .primary)
                if let recording {
                    TimelineView(.periodic(from: recording, by: 1)) { context in
                        Text("正在录屏 \(elapsed(since: recording, at: context.date))")
                            .font(.caption).foregroundStyle(selected ? Color.primary : Color.red).monospacedDigit().lineLimit(1)
                    }
                } else {
                    Text(subtitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer(minLength: 4)
            if let color = recording != nil ? Color.red : device.dotColor {
                Circle().fill(color).frame(width: 8, height: 8).accessibilityHidden(true)
            }
        }
        .padding(.vertical, 3)
    }

    private var subtitle: String {
        switch device.state {
        case .online:
            let link = device.isWireless ? "Wi-Fi" : "USB"
            return device.info.map { "\(link) · Android \($0.androidVersion)" } ?? link
        case .unauthorized: return "等待在手机上允许 USB 调试"
        case .connecting: return "正在连接…"
        case .offline: return device.isWireless ? "离线 · Wi-Fi" : "离线"
        case .noPermissions: return "没有 USB 访问权限"
        }
    }
}

/// The sidebar's only row under "物理设备" while none is connected.
struct ConnectHelpRow: View {
    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "iphone.gen3.badge.plus")
                .font(.system(size: 17))
                .frame(width: 22)
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text("连接 Android 设备…").lineLimit(1)
                Text("USB 或 Wi-Fi").font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 3)
    }
}

extension PhysicalDevice {
    var dotColor: Color? {
        switch state {
        case .online: .green
        case .unauthorized, .connecting, .noPermissions: .orange
        case .offline: nil
        }
    }
}

func elapsed(since start: Date, at now: Date) -> String {
    let seconds = max(0, Int(now.timeIntervalSince(start)))
    return String(format: "%02d:%02d", seconds / 60, seconds % 60)
}

/// The per-device actions of a phone, shared by its "⋯" menu and the sidebar's context menu.
struct PhysicalActions: View {
    let manager: DeviceManager
    let device: PhysicalDevice
    let includeMain: Bool

    var body: some View {
        let phones = manager.phones
        if device.isOnline {
            if includeMain {
                Button(phones.isMirroring(device.id) ? "显示投屏窗口" : "投屏") { manager.mirror(device) }
                Button("截图") { phones.screenshot(device.id) }
                Button("拷贝截图") { phones.copyScreenshot(device.id) }
                Button(phones.isRecording(device.id) ? "停止录屏" : "录屏") { phones.toggleRecording(device.id) }
                Divider()
            }
            if !device.isWireless {
                Button("切换为无线连接") { phones.switchToWireless(device.id) }
                    .disabled(phones.busy.contains(device.id))
            }
            Button("安装 APK 或传文件…") { chooseFiles() }
            Button("打开桌面（截图和录屏）") {
                NSWorkspace.shared.open(FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0])
            }
            Divider()
            if device.isWireless {
                Button("断开连接") { phones.disconnect(device.id) }
            }
            Button("重启设备") { phones.reboot(device.id) }
        } else {
            Button("重新连接") { phones.reconnect(device.id) }
                .disabled(phones.busy.contains(device.id))
        }
        if device.isWireless || device.pairedAt != nil {
            Button("忘记此设备") { phones.forget(device.id) }
        }
    }

    private func chooseFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.message = "APK 会安装到设备上，其他文件存到设备的“下载”文件夹"
        panel.prompt = "传到设备"
        guard panel.runModal() == .OK else { return }
        manager.phones.send(panel.urls, to: device.id)
    }
}

// MARK: - Detail

struct PhysicalDetailPane: View {
    let manager: DeviceManager

    var body: some View {
        if !manager.phones.adbInstalled {
            AdbMissingView(phones: manager.phones)
        } else if let device = manager.selectedPhysical {
            switch device.state {
            case .online: PhysicalDetail(manager: manager, device: device)
            case .unauthorized: UnauthorizedView(manager: manager, device: device)
            case .connecting:
                VStack(spacing: 10) {
                    ProgressView()
                    Text("正在读取“\(device.name)”的信息…").foregroundStyle(.secondary)
                }
            case .offline: OfflineView(manager: manager, device: device)
            case .noPermissions:
                MessageView(symbol: "lock", title: "这台 Mac 不能访问这个 USB 设备",
                            text: "换一根数据线或 USB 口再试。序列号 \(device.serials.first ?? "")")
            }
        } else if let selection = manager.selection, selection.hasPrefix("device:"), selection != "device:" {
            // The selected phone went away (unplugged, forgotten); it is selected again if it comes back.
            MessageView(symbol: "cable.connector.slash", title: "设备已断开",
                        text: "重新连上后会自动显示在这里。要连接新设备，插上数据线，或点工具栏的“Wi-Fi 配对”。")
        } else {
            ConnectHelpView(manager: manager)
        }
    }
}

private struct PhysicalDetail: View {
    let manager: DeviceManager
    let device: PhysicalDevice

    var body: some View {
        let phones = manager.phones
        let info = device.info ?? DeviceInfo()
        let landscape = info.isLandscapeNatural
        let recording = phones.recordings[device.id]
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .center, spacing: 26) {
                    PhonePreview(image: phones.thumbnails[device.id], info: info)
                        .frame(width: landscape ? 250 : 120, height: landscape ? 170 : 250)

                    VStack(alignment: .leading, spacing: 10) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(device.name).font(.system(size: 24, weight: .bold)).textSelection(.enabled)
                            Text(headline(info)).foregroundStyle(.secondary)
                        }
                        HStack(spacing: 7) {
                            Circle().fill(recording != nil ? Color.red : .green).frame(width: 8, height: 8)
                            if let recording {
                                TimelineView(.periodic(from: recording, by: 1)) { context in
                                    Text(status + " · 正在录屏 \(elapsed(since: recording, at: context.date))").font(.callout).monospacedDigit()
                                }
                            } else {
                                Text(status).font(.callout)
                            }
                        }
                        HStack(spacing: 8) {
                            Button { manager.mirror(device) } label: {
                                Label(phones.isMirroring(device.id) ? "显示窗口" : "投屏", systemImage: "rectangle.on.rectangle")
                            }
                            .buttonStyle(.borderedProminent)
                            .keyboardShortcut("o")
                            .help("在窗口里显示这台设备，可以直接用鼠标和键盘操作")
                            Button { phones.screenshot(device.id) } label: { Label("截图", systemImage: "camera") }
                                .help("存到桌面  ⌘S")
                            Button { phones.copyScreenshot(device.id) } label: { Label("拷贝截图", systemImage: "doc.on.clipboard") }
                                .help("截图并拷贝到剪贴板，可以直接粘贴到聊天或文档里  ⌃⌘S")
                            if let recording {
                                Button { phones.toggleRecording(device.id) } label: {
                                    TimelineView(.periodic(from: recording, by: 1)) { context in
                                        Label("停止录屏 \(elapsed(since: recording, at: context.date))", systemImage: "stop.fill").monospacedDigit()
                                    }
                                }
                                .tint(.red)
                                .buttonStyle(.borderedProminent)
                                .help("停止并存到桌面  ⇧⌘R")
                            } else {
                                Button { phones.toggleRecording(device.id) } label: { Label("录屏", systemImage: "record.circle") }
                                    .help("录成 MP4 存到桌面，不用开投屏  ⇧⌘R")
                            }
                            Menu {
                                PhysicalActions(manager: manager, device: device, includeMain: false)
                            } label: {
                                Image(systemName: "ellipsis")
                            }
                            .menuIndicator(.hidden)
                            .fixedSize()
                            .accessibilityLabel("更多操作")
                        }
                        .controlSize(.large)
                        Text("截图和录屏存到桌面 · ⌘S 截图 · ⌃⌘S 拷贝截图 · ⇧⌘R 开始或停止录屏")
                            .font(.callout).foregroundStyle(.secondary)
                        if info.inputBlocked || phones.inputRefused.contains(device.id) {
                            Label("投屏时能看不能点：这台手机的开发者选项里还没打开“USB 调试（安全设置）”，系统会拒绝来自电脑的点按和输入。截图和录屏不受影响。",
                                  systemImage: "exclamationmark.triangle.fill")
                                .font(.callout)
                                .foregroundStyle(.orange)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                }

                HStack(alignment: .top, spacing: 20) {
                    VStack(spacing: 18) {
                        InfoGroup(title: "设备", rows: deviceRows(info))
                        InfoGroup(title: "系统", rows: systemRows(info))
                    }
                    VStack(spacing: 18) {
                        InfoGroup(title: "硬件", rows: hardwareRows(info))
                        InfoGroup(title: "连接", rows: connectionRows(info))
                    }
                }
            }
            .padding(.horizontal, 28)
            .padding(.vertical, 24)
            .frame(maxWidth: 900, alignment: .leading)
        }
        .task(id: device.id) { await phones.followPreview(device.id) }
        .task(id: device.id) {
            // The battery and free space change; read them again now and then while shown.
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(30))
                await phones.refreshInfo(device.id)
            }
        }
    }

    private var status: String {
        var parts = ["已连接", device.isWireless ? "Wi-Fi" : "USB"]
        if let serial = device.serial { parts.append(serial) }
        if manager.phones.isMirroring(device.id) { parts.append("正在投屏") }
        return parts.joined(separator: " · ")
    }

    private func headline(_ info: DeviceInfo) -> String {
        let model = [info.manufacturer.capitalized, info.model].filter { !$0.isEmpty }.joined(separator: " ")
        return "\(model) · Android \(info.androidVersion) · API \(info.sdk)"
    }

    private func deviceRows(_ info: DeviceInfo) -> [(String, String)] {
        var resolution = "\(info.width) × \(info.height)"
        if let density = info.density { resolution += " · \(density) dpi" }
        return [
            ("型号", info.codename.isEmpty ? info.model : "\(info.model)（\(info.codename)）"),
            ("分辨率", resolution),
            ("方向", info.isLandscapeNatural ? "横屏为主（跟随设备）" : "竖屏为主（跟随设备）"),
        ]
    }

    private func systemRows(_ info: DeviceInfo) -> [(String, String)] {
        [
            ("Android 版本", "\(info.androidVersion)（API \(info.sdk)）"),
            ("安全补丁", info.securityPatch ?? "—"),
            ("版本号", info.buildId ?? "—"),
            ("架构", info.abi ?? "—"),
        ]
    }

    private func hardwareRows(_ info: DeviceInfo) -> [(String, String)] {
        let bytes = { (value: Int64?) in value.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) } }
        var battery = info.batteryLevel.map { "\($0)%" } ?? "—"
        if info.charging { battery += " · 正在充电" }
        let storage = bytes(info.storageFree).flatMap { free in bytes(info.storageTotal).map { "可用 \(free) / 共 \($0)" } } ?? "—"
        return [
            ("电池", battery),
            ("存储", storage),
            ("内存", bytes(info.memory) ?? "—"),
        ]
    }

    private func connectionRows(_ info: DeviceInfo) -> [(String, String)] {
        var rows = [("方式", device.isWireless ? "Wi-Fi（无线调试）" : "USB")]
        if let serial = device.serial { rows.append((device.isWireless ? "地址" : "序列号", serial)) }
        if device.serials.count > 1 { rows.append(("也连着", device.serials.filter { $0 != device.serial }.joined(separator: ", "))) }
        let mirroring = info.sdk < 21 ? "不支持（需要 Android 5.0）"
            : info.sdk >= 30 ? "支持 · 声音可转到 Mac" : "支持 · 声音需要 Android 11"
        rows.append(("投屏", mirroring))
        return rows
    }
}

/// A phone's screen in a plain dark bezel, as its mirror window shows it.
private struct PhonePreview: View {
    let image: CGImage?
    let info: DeviceInfo

    var body: some View {
        GeometryReader { geometry in
            let natural = CGSize(width: max(info.width, 1), height: max(info.height, 1))
            // The screen may be turned: the preview takes the picture's shape when there is one.
            let shape = image.map { CGSize(width: $0.width, height: $0.height) } ?? natural
            let bezel = min(geometry.size.width, geometry.size.height) * 0.035
            let scale = min((geometry.size.width - 2 * bezel) / shape.width, (geometry.size.height - 2 * bezel) / shape.height)
            let screen = CGSize(width: shape.width * scale, height: shape.height * scale)
            let radius = min(screen.width, screen.height) * 0.09
            ZStack {
                RoundedRectangle(cornerRadius: radius + bezel).fill(Color(white: 0.11))
                Group {
                    if let image {
                        Image(decorative: image, scale: 1).resizable()
                    } else {
                        Color(white: 0.06)
                    }
                }
                .frame(width: screen.width, height: screen.height)
                .clipShape(RoundedRectangle(cornerRadius: radius))
            }
            .frame(width: screen.width + 2 * bezel, height: screen.height + 2 * bezel)
            .shadow(color: .black.opacity(0.2), radius: 7, y: 4)
            .frame(width: geometry.size.width, height: geometry.size.height)
        }
    }
}

private struct UnauthorizedView: View {
    let manager: DeviceManager
    let device: PhysicalDevice

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "iphone.badge.exclamationmark")
                .font(.system(size: 48, weight: .light))
                .foregroundStyle(.orange)
            Text("在手机上允许 USB 调试").font(.title2.bold())
            Text("手机上应该弹出了“允许 USB 调试吗？”。勾选“一律允许使用这台计算机进行调试”，再点“允许”。允许后这里会自动显示设备。")
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            VStack(alignment: .leading, spacing: 4) {
                Text("没看到弹窗？").fontWeight(.semibold)
                Text("· 先解锁手机，弹窗只在解锁时出现")
                Text("· 拔下数据线再插上")
                Text("· 开发者选项里点“撤消 USB 调试授权”，然后重插")
            }
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color(nsColor: .separatorColor)))
            HStack(spacing: 10) {
                Button("重新连接") { manager.phones.reconnect(device.id) }
                    .disabled(manager.phones.busy.contains(device.id))
                Text("序列号 \(device.serials.first ?? "") · \(device.isWireless ? "Wi-Fi" : "USB")")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: 460)
        .padding(32)
    }
}

private struct OfflineView: View {
    let manager: DeviceManager
    let device: PhysicalDevice

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 14) {
                Image(systemName: "iphone.slash").font(.system(size: 40, weight: .light)).foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 4) {
                    Text(device.name).font(.title.bold())
                    HStack(spacing: 6) {
                        Circle().fill(Color.secondary).frame(width: 8, height: 8)
                        Text(device.lastSeen.map { "离线 · 上次在 \($0.formatted(date: .abbreviated, time: .shortened))" } ?? "离线")
                    }
                }
            }
            Text(device.isWireless
                 ? "设备休眠、换了网络或关了“无线调试”都会断开。配对过的设备会一直列在这里，回到同一网络后通常会自动连上。"
                 : "adb 报告这台设备离线。拔下数据线再插上，或者点“重新连接”。")
                .fixedSize(horizontal: false, vertical: true)
            InfoGroup(title: "连接", rows: [
                ("上次地址", device.address ?? "—"),
                ("配对时间", device.pairedAt.map { $0.formatted(date: .abbreviated, time: .omitted) } ?? "—"),
            ])
            HStack(spacing: 8) {
                Button("重新连接") { manager.phones.reconnect(device.id) }
                    .buttonStyle(.borderedProminent)
                    .disabled(manager.phones.busy.contains(device.id) || (device.address == nil && device.serials.isEmpty))
                if device.isWireless {
                    Button("忘记此设备", role: .destructive) { manager.phones.forget(device.id) }
                }
            }
            .controlSize(.large)
        }
        .frame(maxWidth: 520, alignment: .leading)
        .padding(32)
    }
}

private struct ConnectHelpView: View {
    let manager: DeviceManager

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("用数据线连接手机").font(.title2.bold())
            VStack(alignment: .leading, spacing: 8) {
                Text("1. 设置 › 关于手机，连续点“版本号”7 次，打开开发者选项")
                Text("2. 设置 › 系统 › 开发者选项，打开“USB 调试”")
                Text("3. 用数据线连到这台 Mac，手机上点“允许”")
            }
            Text("插上后会自动出现在左栏，不用刷新。").font(.callout).foregroundStyle(.secondary)
            Divider()
            Text("不想插线？").font(.headline)
            Text("Android 11 及以上可以通过 Wi-Fi 配对，手机和 Mac 连同一个网络即可。")
            Button("通过 Wi-Fi 配对…") { manager.sheet = .pairing }
                .buttonStyle(.borderedProminent)
        }
        .frame(maxWidth: 480, alignment: .leading)
        .padding(32)
    }
}

private struct AdbMissingView: View {
    let phones: PhysicalDeviceStore

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "apple.terminal").font(.system(size: 44, weight: .light)).foregroundStyle(.secondary)
            Text("需要 Android SDK Platform-Tools").font(.title2.bold())
            Text("发现和操作物理设备要用到其中的 adb。它会装到现在的 SDK 里，大约 10 MB。")
                .multilineTextAlignment(.center)
            if let progress = phones.installProgress {
                ProgressView(value: progress).frame(width: 240)
            } else {
                Button("安装 Platform-Tools") { phones.installPlatformTools() }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
            }
        }
        .frame(maxWidth: 420)
        .padding(32)
    }
}

private struct MessageView: View {
    let symbol: String
    let title: String
    let text: String

    var body: some View {
        ContentUnavailableView {
            Label(title, systemImage: symbol)
        } description: {
            Text(text)
        }
    }
}

/// "截图已存到桌面" and the like, in the manager window's corner for a few seconds.
struct PhysicalToast: View {
    let phones: PhysicalDeviceStore

    var body: some View {
        if let toast = phones.toast {
            HStack(spacing: 12) {
                Image(systemName: toast.isError ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
                    .foregroundStyle(toast.isError ? Color.orange : Color.green)
                    .font(.system(size: 18))
                VStack(alignment: .leading, spacing: 2) {
                    Text(toast.text).fontWeight(.medium).fixedSize(horizontal: false, vertical: true)
                    if let url = toast.url {
                        Text(url.lastPathComponent).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                    }
                }
                if let url = toast.url {
                    Button("在 Finder 中显示") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                        .controlSize(.small)
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .frame(maxWidth: 420, alignment: .leading)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color(nsColor: .separatorColor)))
            .shadow(color: .black.opacity(0.15), radius: 12, y: 4)
            .padding(16)
            .transition(.move(edge: .bottom).combined(with: .opacity))
            .task(id: toast.id) {
                try? await Task.sleep(for: .seconds(5))
                if phones.toast?.id == toast.id { withAnimation { phones.toast = nil } }
            }
        }
    }
}
