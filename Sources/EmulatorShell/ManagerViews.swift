import AppKit
import SwiftUI

/// The device manager window: AVDs grouped by category on the left, the selected one's details on the right.
struct ManagerView: View {
    @Bindable var manager: DeviceManager

    var body: some View {
        NavigationSplitView {
            Sidebar(manager: manager)
                .navigationSplitViewColumnWidth(min: 220, ideal: 272, max: 360)
        } detail: {
            DetailPane(manager: manager)
        }
        .navigationTitle("设备")
        // AVDs and images may have been changed by Android Studio or the command line meanwhile.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            manager.reload()
        }
        .onChange(of: manager.phones.devices) { _, _ in manager.followPhysicalSelection() }
        .onChange(of: manager.phones.adbInstalled) { _, _ in manager.followPhysicalSelection() }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    manager.sheet = .pairing
                } label: {
                    Label("Wi-Fi 配对", systemImage: "wifi")
                }
                .labelStyle(.titleAndIcon)
                .keyboardShortcut("n", modifiers: [.command, .shift])
                .help("通过 Wi-Fi 配对 Android 11 及以上的设备  ⇧⌘N")
            }
            ToolbarItem(placement: .primaryAction) {
                Button {
                    manager.sheet = .images
                } label: {
                    Label("系统镜像", systemImage: "arrow.down.circle")
                }
                .labelStyle(.titleAndIcon)
                .help("下载或删除系统镜像")
            }
        }
        .sheet(item: $manager.sheet) { sheet in
            switch sheet {
            case let .form(form): AvdFormSheet(manager: manager, form: form)
            case .images: ImagesSheet(manager: manager)
            case .pairing: PairingSheet(manager: manager)
            }
        }
        .alert(
            confirmationTitle,
            isPresented: Binding(get: { manager.confirmation != nil }, set: { if !$0 { manager.confirmation = nil } }),
            presenting: manager.confirmation
        ) { confirmation in
            switch confirmation {
            case let .delete(avd): Button("删除", role: .destructive) { manager.delete(avd) }
            case let .wipe(avd): Button("擦除", role: .destructive) { manager.wipe(avd) }
            }
            Button("取消", role: .cancel) {}
        } message: { confirmation in
            switch confirmation {
            case .delete: Text("它的数据和快照会一起删除，无法撤销。系统镜像会保留。")
            case .wipe: Text("已安装的应用、账号和快照都会被清除，相当于恢复出厂设置。")
            }
        }
        .alert("操作没有完成", isPresented: Binding(get: { manager.errorMessage != nil }, set: { if !$0 { manager.errorMessage = nil } })) {
            Button("好") {}
        } message: {
            Text(manager.errorMessage ?? "")
        }
    }

    private var confirmationTitle: String {
        switch manager.confirmation {
        case let .delete(avd)?: "删除“\(avd.name)”？"
        case let .wipe(avd)?: "擦除“\(avd.name)”的数据？"
        case nil: ""
        }
    }
}

// MARK: - Sidebar

private struct Sidebar: View {
    @Bindable var manager: DeviceManager

    private var sections: [(category: DeviceCategory, avds: [Avd])] {
        let filter = manager.filter.trimmingCharacters(in: .whitespaces)
        let visible = manager.avds.filter { filter.isEmpty || $0.name.localizedCaseInsensitiveContains(filter) }
        return DeviceCategory.allCases.compactMap { category in
            let avds = visible.filter { manager.category(of: $0) == category }
            return avds.isEmpty ? nil : (category, avds)
        }
    }

    private var phones: [PhysicalDevice] {
        let filter = manager.filter.trimmingCharacters(in: .whitespaces)
        return manager.phones.devices.filter { filter.isEmpty || $0.name.localizedCaseInsensitiveContains(filter) }
    }

    var body: some View {
        List(selection: $manager.selection) {
            // The section is there only while it lists a device (pairing is in the toolbar), or, without adb, to
            // offer installing it.
            if !phones.isEmpty || manager.showsPhysicalHelp {
                Section("物理设备") {
                    ForEach(phones) { device in
                        PhysicalRow(manager: manager, device: device, selected: manager.selection == device.id).tag(device.id)
                    }
                    if manager.showsPhysicalHelp {
                        ConnectHelpRow().tag(DeviceManager.physicalHelpTag)
                    }
                }
            }
            ForEach(sections, id: \.category) { section in
                Section(section.category.title) {
                    ForEach(section.avds) { avd in
                        SidebarRow(manager: manager, avd: avd).tag(avd.id)
                    }
                }
            }
        }
        .contextMenu(forSelectionType: String.self) { ids in
            if let avd = manager.avds.first(where: { $0.id == ids.first }) {
                AvdActions(manager: manager, avd: avd, includeStart: true)
            } else if let device = manager.phones.devices.first(where: { $0.id == ids.first }) {
                PhysicalActions(manager: manager, device: device, includeMain: true)
            }
        } primaryAction: { ids in
            if let device = manager.phones.devices.first(where: { $0.id == ids.first }), device.isOnline {
                manager.mirror(device)
            } else if let avd = manager.avds.first(where: { $0.id == ids.first }) {
                if manager.status(of: avd).isStopped { manager.start(avd) } else { manager.showWindow(of: avd) }
            }
        }
        .overlay {
            if !manager.filter.isEmpty && sections.isEmpty && phones.isEmpty && !manager.avds.isEmpty {
                Text("没有名称包含“\(manager.filter)”的模拟器").font(.callout).foregroundStyle(.secondary).padding()
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            VStack(spacing: 0) {
                Divider()
                HStack(spacing: 2) {
                    Button {
                        manager.sheet = .form(manager.newForm())
                    } label: {
                        Image(systemName: "plus").frame(width: 24, height: 22)
                    }
                    .help("新建模拟器")
                    .accessibilityLabel("新建模拟器")
                    .keyboardShortcut("n")
                    Button {
                        if let avd = manager.selectedAvd { manager.confirmation = .delete(avd) }
                    } label: {
                        Image(systemName: "minus").frame(width: 24, height: 22)
                    }
                    .help("删除所选模拟器")
                    .accessibilityLabel("删除所选模拟器")
                    .keyboardShortcut(.delete, modifiers: .command)
                    .disabled(manager.selectedAvd.map { !manager.status(of: $0).isStopped } ?? true)
                    TextField("筛选", text: $manager.filter)
                        .textFieldStyle(.roundedBorder)
                        .padding(.leading, 6)
                }
                .buttonStyle(.borderless)
                .padding(.horizontal, 8)
                .padding(.vertical, 8)
            }
        }
    }
}

extension Sidebar {
}

private struct SidebarRow: View {
    let manager: DeviceManager
    let avd: Avd

    var body: some View {
        let status = manager.status(of: avd)
        HStack(spacing: 10) {
            Image(systemName: manager.category(of: avd).symbol)
                .font(.system(size: 17))
                .frame(width: 22)
            VStack(alignment: .leading, spacing: 1) {
                Text(avd.name).lineLimit(1)
                Text(subtitle(status)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 4)
            if let color = status.dotColor {
                Circle().fill(color).frame(width: 8, height: 8).accessibilityHidden(true)
            }
        }
        .padding(.vertical, 3)
    }

    private func subtitle(_ status: DeviceManager.Status) -> String {
        if let text = status.label { return text }
        guard let image = manager.image(of: avd) else { return "系统镜像缺失" }
        return "\(image.apiLabel) · \(image.kindLabel.components(separatedBy: " · ")[0])"
    }
}

private extension DeviceManager.Status {
    var label: String? {
        switch self {
        case .stopped: nil
        case .booting: "正在启动…"
        case .stopping: "正在停止…"
        case .running: "运行中"
        case .external: "在其他程序中运行"
        }
    }

    var dotColor: Color? {
        switch self {
        case .stopped: nil
        case .booting, .stopping: .orange
        case .running, .external: .green
        }
    }
}

// MARK: - Detail

private struct DetailPane: View {
    @Bindable var manager: DeviceManager

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .overlay(alignment: .bottomTrailing) { PhysicalToast(phones: manager.phones) }
            .animation(.easeOut(duration: 0.2), value: manager.phones.toast)
    }

    @ViewBuilder
    private var content: some View {
        if manager.selection?.hasPrefix("device:") == true {
            PhysicalDetailPane(manager: manager)
        } else if manager.avds.isEmpty {
            ContentUnavailableView {
                Label("还没有模拟器", systemImage: "iphone")
            } description: {
                Text("新建一个模拟器，选择设备类型和系统镜像即可开始。")
            } actions: {
                Button("新建模拟器") { manager.sheet = .form(manager.newForm()) }
                    .buttonStyle(.borderedProminent)
            }
        } else if let avd = manager.selectedAvd {
            AvdDetail(manager: manager, avd: avd)
        } else {
            Text("选择一个模拟器查看详情").foregroundStyle(.secondary)
        }
    }
}

private struct AvdDetail: View {
    let manager: DeviceManager
    let avd: Avd

    var body: some View {
        let status = manager.status(of: avd)
        let landscape = manager.category(of: avd).isLandscape
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .center, spacing: 26) {
                    Group {
                        if case .running = status {
                            TimelineView(.periodic(from: .now, by: 1)) { _ in
                                DevicePreview(avd: avd, deviceType: manager.deviceType(of: avd), thumbnail: manager.thumbnail(of: avd))
                            }
                        } else {
                            DevicePreview(avd: avd, deviceType: manager.deviceType(of: avd), thumbnail: nil)
                        }
                    }
                    .frame(width: landscape ? 250 : 120, height: landscape ? 170 : 250)

                    VStack(alignment: .leading, spacing: 10) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(avd.name).font(.system(size: 24, weight: .bold)).textSelection(.enabled)
                            Text(headline).foregroundStyle(.secondary)
                        }
                        HStack(spacing: 7) {
                            Circle().fill(status.dotColor ?? Color.secondary).frame(width: 8, height: 8)
                            Text(statusText(status)).font(.callout)
                        }
                        HStack(spacing: 8) {
                            primaryButtons(status)
                            Button("编辑…") { manager.sheet = .form(manager.editForm(for: avd)) }
                                .keyboardShortcut("e")
                                .disabled(!status.isStopped)
                            Menu {
                                AvdActions(manager: manager, avd: avd, includeStart: false)
                            } label: {
                                Image(systemName: "ellipsis")
                            }
                            .menuIndicator(.hidden)
                            .fixedSize()
                            .accessibilityLabel("更多操作")
                        }
                        .controlSize(.large)
                        if let note = manager.note {
                            Text(note).font(.callout).foregroundStyle(.secondary)
                        }
                    }
                }

                HStack(alignment: .top, spacing: 20) {
                    VStack(spacing: 18) {
                        InfoGroup(title: "设备", rows: deviceRows)
                        InfoGroup(title: "系统", rows: systemRows)
                    }
                    VStack(spacing: 18) {
                        InfoGroup(title: "硬件", rows: hardwareRows)
                        InfoGroup(title: "存储", rows: storageRows)
                    }
                }
            }
            .padding(.horizontal, 28)
            .padding(.vertical, 24)
            .frame(maxWidth: 900, alignment: .leading)
        }
    }

    @ViewBuilder
    private func primaryButtons(_ status: DeviceManager.Status) -> some View {
        switch status {
        case .stopped:
            Button { manager.start(avd) } label: { Label("启动", systemImage: "play.fill") }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut("o")
        case .booting:
            Button("正在启动…") {}.buttonStyle(.borderedProminent).disabled(true)
        case .stopping:
            Button("正在停止…") {}.disabled(true)
        case .running:
            Button { manager.stop(avd) } label: { Label("停止", systemImage: "stop.fill") }
            Button("显示窗口") { manager.showWindow(of: avd) }
        case .external where manager.isInOtherEmulatorShell(avd):
            Button { manager.stop(avd) } label: { Label("停止", systemImage: "stop.fill") }
            Button("显示窗口") { manager.showInOwner(avd) }
                .help("切换到显示这台设备的另一个 Emulator Shell")
        case .external:
            let problem = manager.attachProblem(avd)
            Button { manager.attach(avd) } label: { Label("在本 App 中显示", systemImage: "macwindow") }
                .buttonStyle(.borderedProminent)
                .disabled(problem != nil)
                .help(problem ?? "用本 App 的窗口显示和操作这台设备；关掉窗口设备继续运行")
            if let owner = manager.owner(of: avd) {
                Button("切换到 \(owner.localizedName ?? "启动它的程序")") { manager.showInOwner(avd) }
                    .help("把启动这台设备的程序切换到前台")
            }
            Button { manager.stop(avd) } label: { Label("停止", systemImage: "stop.fill") }
        }
    }

    private func statusText(_ status: DeviceManager.Status) -> String {
        switch status {
        case .stopped:
            FileManager.default.fileExists(atPath: avd.folder.appendingPathComponent("snapshots/default_boot").path)
                ? "已停止 · 有快速启动快照" : "已停止"
        case .booting: "正在启动…"
        case .stopping: "正在停止，正在保存快照…"
        case let .running(serial): "运行中" + (serial.map { " · \($0)" } ?? "")
        case let .external(serial):
            (manager.isInOtherEmulatorShell(avd) ? "在另一个 Emulator Shell 中运行" : "在其他程序中运行")
                + (serial.map { " · \($0)" } ?? "")
        }
    }

    private var modelName: String {
        let type = manager.deviceType(of: avd)?.name ?? avd.deviceTypeId ?? "未知设备"
        return [avd.manufacturer, type].filter { !$0.isEmpty }.joined(separator: " ")
    }

    private var headline: String {
        guard let image = manager.image(of: avd) else { return modelName }
        return "\(modelName) · \(image.apiLabel) · \(image.kindLabel)"
    }

    private var deviceRows: [(String, String)] {
        [
            ("型号", modelName),
            ("分辨率", avd.resolution ?? "—"),
            ("初始方向", avd.isLandscape ? "横屏" : "竖屏"),
        ]
    }

    private var systemRows: [(String, String)] {
        guard let image = manager.image(of: avd) else {
            return [("系统镜像", "缺失：\(avd.sysdir ?? "未设置")")]
        }
        return [
            ("系统镜像", image.apiLabel + (image.extensionLevel.map { "（扩展级别 \($0)）" } ?? "")),
            ("版本类型", image.kindLabel),
            ("架构", avd.config["abi.type"] ?? image.abi),
        ]
    }

    private var hardwareRows: [(String, String)] {
        let ram = avd.config["hw.ramSize"].flatMap { Double($0.replacingOccurrences(of: "M", with: "")) }
        let gpu = avd.config["hw.gpu.mode"] ?? "auto"
        let cameras = ["virtualscene": "虚拟场景", "emulated": "模拟", "webcam0": "Mac 摄像头", "none": "无"]
        return [
            ("内存", ram.map { String(format: "%g GB", $0 / 1024) } ?? "—"),
            ("内部存储 / SD 卡", "\(formatConfigSize(avd.config["disk.dataPartition.size"]) ?? "—") / \(formatConfigSize(avd.config["sdcard.size"]) ?? "无")"),
            ("图形", ["host", "auto"].contains(gpu) ? "主机 GPU" : "软件渲染"),
            ("摄像头", "后置 \(cameras[avd.config["hw.camera.back"] ?? "none"] ?? "无") · 前置 \(cameras[avd.config["hw.camera.front"] ?? "none"] ?? "无")"),
        ]
    }

    private var storageRows: [(String, String)] {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return [
            ("启动方式", avd.isColdBoot ? "每次冷启动" : "快速启动"),
            ("占用空间", manager.sizes[avd.id].map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) } ?? "计算中…"),
            ("位置", avd.folder.path.replacingOccurrences(of: home, with: "~")),
        ]
    }
}

/// The per-device actions, shared by the "⋯" menu and the sidebar's context menu.
private struct AvdActions: View {
    let manager: DeviceManager
    let avd: Avd
    let includeStart: Bool

    var body: some View {
        let stopped = manager.status(of: avd).isStopped
        if includeStart {
            if stopped {
                Button("启动") { manager.start(avd) }
            } else {
                Button("停止") { manager.stop(avd) }
            }
        }
        Button("冷启动") { manager.start(avd, coldBoot: true) }.disabled(!stopped)
        if includeStart {
            Button("编辑…") { manager.sheet = .form(manager.editForm(for: avd)) }.disabled(!stopped)
        }
        Button("复制") { manager.duplicate(avd) }
        Button("在 Finder 中显示") { manager.reveal(avd) }
        Divider()
        Button("擦除数据…") { manager.confirmation = .wipe(avd) }.disabled(!stopped)
        Button("删除…") { manager.confirmation = .delete(avd) }.disabled(!stopped)
    }
}

struct InfoGroup: View {
    let title: String
    let rows: [(String, String)]

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.leading, 4)
            VStack(spacing: 0) {
                ForEach(Array(rows.enumerated()), id: \.offset) { index, row in
                    if index > 0 { Divider().padding(.leading, 12) }
                    HStack(alignment: .firstTextBaseline, spacing: 16) {
                        Text(row.0).foregroundStyle(.secondary)
                        Spacer(minLength: 0)
                        // A path too long for the column keeps its start and its folder's name.
                        Text(row.1).multilineTextAlignment(.trailing).truncationMode(.middle).textSelection(.enabled)
                            .help(row.1)
                            .layoutPriority(1)
                    }
                    .padding(.horizontal, 12)
                    .padding(.vertical, 7)
                }
            }
            .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color(nsColor: .separatorColor)))
        }
        .frame(maxWidth: .infinity)
    }
}

// MARK: - Device preview

/// A skin's frame image and where its display sits, for small previews. Cached per skin folder.
struct SkinPreview {
    let frame: NSImage
    let frameRect: CGRect
    let size: CGSize
    let display: CGRect
    let cornerRadius: CGFloat

    @MainActor private static var cache: [URL: SkinPreview] = [:]

    @MainActor static func load(_ folder: URL) -> SkinPreview? {
        if let cached = cache[folder] { return cached }
        guard let geometry = SkinGeometry.read(folder), let frame = NSImage(contentsOf: geometry.frameURL) else { return nil }
        let preview = SkinPreview(frame: frame, frameRect: geometry.frameRect, size: geometry.size, display: geometry.display, cornerRadius: geometry.cornerRadius)
        cache[folder] = preview
        return preview
    }
}

/// The device's frame with its screen: the live picture while running, dark otherwise.
struct DevicePreview: View {
    let skinFolder: URL?
    /// Width over height of the display, for devices without a skin.
    let aspect: CGFloat
    /// Upright for the viewer.
    let screen: CGImage?
    /// Counterclockwise quarter turns the device is held at.
    let quarterTurns: Int

    init(avd: Avd, deviceType: DeviceType?, thumbnail: DeviceThumbnail?) {
        skinFolder = thumbnail?.skinFolder ?? avd.skinFolder ?? deviceType?.skinFolder
        let width = avd.config["hw.lcd.width"].flatMap(Double.init) ?? 1080
        let height = avd.config["hw.lcd.height"].flatMap(Double.init) ?? 2400
        aspect = width / height
        screen = thumbnail?.screen
        quarterTurns = thumbnail?.quarterTurns ?? 0
    }

    init(deviceType: DeviceType?, landscape: Bool) {
        skinFolder = deviceType?.skinFolder
        aspect = landscape ? 16.0 / 10.0 : 9.0 / 19.5
        screen = nil
        quarterTurns = 0
    }

    var body: some View {
        GeometryReader { geometry in
            let box = geometry.size
            if let folder = skinFolder, let skin = SkinPreview.load(folder) {
                // Drawn upright and turned as a whole like the device window; the screen, already upright for
                // the viewer, is turned back inside it.
                let sideways = quarterTurns % 2 == 1
                let shown = sideways ? CGSize(width: skin.size.height, height: skin.size.width) : skin.size
                let scale = min(box.width / shown.width, box.height / shown.height)
                let display = CGSize(width: skin.display.width * scale, height: skin.display.height * scale)
                ZStack(alignment: .topLeading) {
                    Group {
                        if let screen {
                            Image(decorative: screen, scale: 1).resizable()
                                .frame(width: sideways ? display.height : display.width,
                                       height: sideways ? display.width : display.height)
                                .rotationEffect(.degrees(Double(quarterTurns) * 90))
                        } else {
                            Color(white: 0.06)
                        }
                    }
                    .frame(width: display.width, height: display.height)
                    .clipShape(RoundedRectangle(cornerRadius: skin.cornerRadius * scale))
                    .offset(x: skin.display.minX * scale, y: skin.display.minY * scale)
                    Image(nsImage: skin.frame).resizable()
                        .frame(width: skin.frameRect.width * scale, height: skin.frameRect.height * scale)
                        .offset(x: skin.frameRect.minX * scale, y: skin.frameRect.minY * scale)
                }
                .frame(width: skin.size.width * scale, height: skin.size.height * scale)
                .rotationEffect(.degrees(Double(quarterTurns) * -90))
                .frame(width: shown.width * scale, height: shown.height * scale)
                .shadow(color: .black.opacity(0.2), radius: 7, y: 4)
                .frame(width: box.width, height: box.height)
            } else {
                // No artwork to turn: the box takes the screen's shape as the viewer sees it.
                let aspect = quarterTurns % 2 == 1 ? 1 / aspect : aspect
                let width = min(box.width, box.height * aspect)
                let height = width / aspect
                ZStack {
                    RoundedRectangle(cornerRadius: min(width, height) * 0.12)
                        .fill(screen == nil ? Color(white: 0.11) : Color.black)
                    if let screen {
                        Image(decorative: screen, scale: 1).resizable()
                            .clipShape(RoundedRectangle(cornerRadius: min(width, height) * 0.09))
                            .padding(min(width, height) * 0.035)
                    } else {
                        Text("没有外观图").font(.caption2).foregroundStyle(.white.opacity(0.7))
                    }
                }
                .frame(width: width, height: height)
                .frame(width: box.width, height: box.height)
            }
        }
        .accessibilityHidden(true)
    }
}

// MARK: - New / edit sheet

private struct AvdFormSheet: View {
    let manager: DeviceManager
    @State private var form: AvdForm
    @State private var showAdvanced = false

    init(manager: DeviceManager, form: AvdForm) {
        self.manager = manager
        _form = State(initialValue: form)
    }

    private var deviceType: DeviceType? { manager.deviceTypes.first { $0.id == form.deviceTypeId } }
    private var category: DeviceCategory {
        if let deviceType { return deviceType.category }
        if case let .edit(id) = form.mode, let avd = manager.avds.first(where: { $0.id == id }) { return manager.category(of: avd) }
        return .phone
    }
    private var compatibleImages: [SystemImage] {
        manager.images.filter { $0.family == category.family && $0.status != .available }
    }
    private var selectedImage: SystemImage? { manager.images.first { $0.id == form.imageId } }

    var body: some View {
        let nameError = manager.nameError(form)
        let imageProblem = manager.imageProblem(form, category: category)
        VStack(spacing: 0) {
            Text(form.isEdit ? "编辑模拟器" : "新建模拟器")
                .font(.headline)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 22)
                .padding(.top, 18)

            HStack(alignment: .top, spacing: 22) {
                DevicePreview(deviceType: deviceType, landscape: category.isLandscape)
                    .frame(width: 120, height: 250)
                    .padding(.top, 6)

                Form {
                    LabeledContent("名称") {
                        VStack(alignment: .leading, spacing: 4) {
                            TextField("名称", text: Binding(get: { form.name }, set: { name in
                                // SwiftUI also calls this with the unchanged text, which must not count as an edit.
                                guard name != form.name else { return }
                                form.name = name
                                form.nameTouched = true
                            }))
                                .labelsHidden()
                            if let nameError {
                                Text(nameError).font(.caption).foregroundStyle(.red)
                            } else if !form.isEdit {
                                Text("AVD 名称：\(Avd.id(for: form.name))").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                    }

                    LabeledContent("设备类型") {
                        VStack(alignment: .leading, spacing: 4) {
                            if form.isEdit {
                                Text(deviceType?.name ?? form.deviceTypeId)
                            } else if manager.deviceTypes.isEmpty {
                                HStack { ProgressView().controlSize(.small); Text("正在读取设备类型…").foregroundStyle(.secondary) }
                            } else {
                                Picker("设备类型", selection: Binding(get: { form.deviceTypeId }, set: changeDeviceType)) {
                                    ForEach(DeviceCategory.allCases) { category in
                                        let types = manager.deviceTypes.filter { $0.category == category }
                                        if !types.isEmpty {
                                            Section(category.title) {
                                                ForEach(types) { Text($0.name).tag($0.id) }
                                            }
                                        }
                                    }
                                }
                                .labelsHidden()
                            }
                            Text(typeHint).font(.caption).foregroundStyle(.secondary)
                        }
                    }

                    LabeledContent("系统镜像") {
                        VStack(alignment: .leading, spacing: 6) {
                            Picker("系统镜像", selection: $form.imageId) {
                                if compatibleImages.isEmpty {
                                    Text("没有已安装的\(category.title)镜像").tag(String?.none)
                                }
                                ForEach(compatibleImages) { image in
                                    Text("\(image.apiLabel) · \(image.kindLabel)" + (image.status == .broken ? "（安装不完整）" : ""))
                                        .tag(Optional(image.id))
                                }
                            }
                            .labelsHidden()
                            if let imageProblem {
                                imageProblemBox(imageProblem)
                            } else if let image = selectedImage {
                                Text([image.extensionLevel.map { "扩展级别 \($0)" }, manager.imageSizes[image.id].map { "已安装 " + ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) }]
                                    .compactMap { $0 }.joined(separator: " · "))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            Button("下载其他系统镜像…") {
                                manager.formUnderImages = form
                                manager.sheet = .images
                            }
                            .buttonStyle(.link)
                            .font(.caption)
                        }
                    }

                    DisclosureGroup("高级设置", isExpanded: $showAdvanced) {
                        LabeledContent("内存") { numberField($form.ramMB, label: "内存", unit: "MB") }
                        LabeledContent("内部存储") { numberField($form.dataGB, label: "内部存储", unit: "GB") }
                        LabeledContent("SD 卡") { numberField($form.sdMB, label: "SD 卡", unit: "MB").disabled(form.isEdit) }
                        Picker("图形", selection: $form.hostGpu) {
                            Text("主机 GPU（推荐）").tag(true)
                            Text("软件渲染").tag(false)
                        }
                        Picker("启动方式", selection: $form.coldBoot) {
                            Text("快速启动（保存快照）").tag(false)
                            Text("每次冷启动").tag(true)
                        }
                        .pickerStyle(.radioGroup)
                        Picker("后置摄像头", selection: $form.cameraBack) {
                            Text("虚拟场景").tag("virtualscene")
                            Text("模拟").tag("emulated")
                            Text("Mac 摄像头").tag("webcam0")
                            Text("无").tag("none")
                        }
                        Picker("前置摄像头", selection: $form.cameraFront) {
                            Text("模拟").tag("emulated")
                            Text("Mac 摄像头").tag("webcam0")
                            Text("无").tag("none")
                        }
                        Picker("初始方向", selection: $form.landscape) {
                            Text("竖屏").tag(false)
                            Text("横屏").tag(true)
                        }
                    }
                }
                .formStyle(.columns)
            }
            .padding(.horizontal, 22)
            .padding(.vertical, 16)

            Divider()
            HStack {
                if manager.busy.contains("form") {
                    ProgressView().controlSize(.small)
                    Text(form.isEdit ? "正在保存…" : "正在创建…").foregroundStyle(.secondary)
                }
                Spacer()
                Button("取消") { manager.sheet = nil }
                    .keyboardShortcut(.cancelAction)
                Button(form.isEdit ? "保存" : "创建") { manager.submit(form) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(nameError != nil || imageProblem != nil || manager.busy.contains("form"))
            }
            .padding(.horizontal, 22)
            .padding(.vertical, 14)
        }
        .frame(width: 660)
    }

    private var typeHint: String {
        var parts = [category.title]
        if let folder = deviceType?.skinFolder, let skin = SkinPreview.load(folder) {
            parts.append("\(Int(skin.display.width)) × \(Int(skin.display.height))")
        } else if !form.isEdit {
            parts.append("没有外观图，按屏幕比例显示")
        }
        return parts.joined(separator: " · ")
    }

    private func changeDeviceType(_ id: String) {
        form.deviceTypeId = id
        guard let type = manager.deviceTypes.first(where: { $0.id == id }) else { return }
        if !form.nameTouched { form.name = manager.uniqueName(type.name) }
        form.landscape = type.category.isLandscape
        if selectedImage?.family != type.category.family {
            form.imageId = manager.defaultImage(for: type.category)?.id
        }
    }

    private func numberField(_ value: Binding<String>, label: String, unit: String) -> some View {
        HStack(spacing: 6) {
            TextField(label, text: value, prompt: Text(unit)).labelsHidden().frame(width: 90)
            Text(unit).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func imageProblemBox(_ problem: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            Text(problem).font(.callout).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            if let image = selectedImage, image.family == category.family, image.status == .broken, manager.downloads[image.id] == nil {
                Button("重新下载") { manager.download(image) }.controlSize(.small)
            } else if selectedImage == nil || selectedImage?.family != category.family {
                Button("下载…") {
                    manager.formUnderImages = form
                    manager.sheet = .images
                }
                .controlSize(.small)
            }
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.orange.opacity(0.12)))
    }
}

// MARK: - System images

private struct ImagesSheet: View {
    @Bindable var manager: DeviceManager
    @State private var family: ImageFamily?
    @State private var showPreview = false

    private var rows: [SystemImage] {
        manager.images.filter { (family == nil || $0.family == family) && (showPreview || !$0.isPreview) && $0.family != .other }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                Text("系统镜像").font(.headline)
                Spacer()
                if manager.loadingAvailableImages {
                    ProgressView().controlSize(.small)
                    Text("正在获取可下载列表…").font(.caption).foregroundStyle(.secondary)
                } else {
                    Text("SDK 位置：\(Sdk.root.path.replacingOccurrences(of: FileManager.default.homeDirectoryForCurrentUser.path, with: "~")) · 只列出 arm64 镜像")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            HStack {
                Picker("类别", selection: $family) {
                    Text("全部").tag(ImageFamily?.none)
                    ForEach([ImageFamily.phone, .wear, .tv, .auto]) { Text($0.title).tag(Optional($0)) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                Spacer()
                Toggle("显示预览版和 Canary", isOn: $showPreview)
            }
            Table(rows) {
                TableColumn("版本") { image in Text(image.apiLabel).fontWeight(.semibold) }
                    .width(min: 120, ideal: 150)
                TableColumn("类型") { image in Text(image.kindLabel) }
                TableColumn("状态") { image in status(image) }
                    .width(min: 170, ideal: 210)
                TableColumn("") { image in action(image) }
                    .width(min: 80, ideal: 90)
            }
            .frame(height: 420)
            HStack {
                Text("正被模拟器使用的镜像不能删除。").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("完成") { close() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 820)
        .onAppear { manager.refreshAvailableImages() }
    }

    @ViewBuilder
    private func status(_ image: SystemImage) -> some View {
        if let progress = manager.downloads[image.id] {
            HStack(spacing: 8) {
                ProgressView(value: progress)
                Text("\(Int(progress * 100))%").font(.caption).monospacedDigit().frame(width: 34, alignment: .trailing)
            }
        } else {
            switch image.status {
            case .installed:
                Text((["已安装"] + [manager.imageSizes[image.id].map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) },
                                     manager.isInUse(image) ? "使用中" : nil].compactMap { $0 }).joined(separator: " · "))
            case .broken:
                Text("安装不完整").foregroundStyle(.orange)
            case .available:
                Text("可下载").foregroundStyle(.secondary)
            }
        }
    }

    @ViewBuilder
    private func action(_ image: SystemImage) -> some View {
        if manager.downloads[image.id] != nil {
            Button("取消") { manager.cancelDownload(image) }
        } else {
            switch image.status {
            case .installed:
                Button("删除") { manager.uninstall(image) }
                    .disabled(manager.isInUse(image) || manager.busy.contains(image.id))
            case .broken:
                Button("重新下载") { manager.download(image) }
            case .available:
                Button("下载") { manager.download(image) }
            }
        }
    }

    private func close() {
        if var form = manager.formUnderImages {
            manager.formUnderImages = nil
            let category = manager.deviceTypes.first { $0.id == form.deviceTypeId }?.category ?? .phone
            if form.imageId == nil { form.imageId = manager.defaultImage(for: category)?.id }
            manager.sheet = .form(form)
        } else {
            manager.sheet = nil
        }
    }
}
