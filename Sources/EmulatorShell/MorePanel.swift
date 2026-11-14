import SwiftUI

/// The "More" popover: tiles for the emulator's secondary features. Toggles and one-shot actions work on
/// the tile itself; the rest open a page inside the popover.
struct MorePanel: View {
    @Bindable var controls: DeviceControls
    @State private var page: Page?

    enum Page: String {
        case location = "位置", battery = "电池", phone = "来电与短信", brightness = "亮度", snapshots = "快照", posture = "折叠姿态"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let page {
                HStack(spacing: 4) {
                    Button {
                        withAnimation(.easeOut(duration: 0.15)) { self.page = nil }
                        controls.message = nil
                    } label: {
                        Label("更多功能", systemImage: "chevron.left")
                    }
                    .buttonStyle(.borderless)
                    Spacer()
                    Text(page.rawValue).font(.headline)
                    Spacer()
                    Color.clear.frame(width: 70, height: 1)
                }
                Group {
                    switch page {
                    case .location: LocationPage(controls: controls)
                    case .battery: BatteryPage(controls: controls)
                    case .phone: PhonePage(controls: controls)
                    case .brightness: BrightnessPage(controls: controls)
                    case .snapshots: SnapshotsPage(controls: controls)
                    case .posture: PosturePage(controls: controls)
                    }
                }
                .disabled(controls.busy)
            } else {
                HStack(alignment: .firstTextBaseline) {
                    Text("更多功能").font(.headline)
                    Spacer()
                    Text(controls.deviceName).font(.caption).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 4)
                tiles
                if controls.restartForMicrophone != nil && controls.usesMicrophone {
                    Label("麦克风开着时，声音由模拟器自己播放，偶尔会让模拟器卡死并退出。", systemImage: "exclamationmark.triangle")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, 4)
                }
                Divider()
                // The emulator's own extended controls are Qt windows; a headless emulator has none, and asking
                // UiController to show them makes it crash.
                VStack(alignment: .leading, spacing: 2) {
                    Text("全部扩展控制…").foregroundStyle(.secondary)
                    Text("无窗口模式下的模拟器没有自带的扩展控制窗口。").font(.caption).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 4)
            }
            if let message = controls.message {
                Text(message.text)
                    .font(.caption)
                    .foregroundStyle(message.isError ? Color.red : Color.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 4)
            }
        }
        .padding(12)
        .frame(width: 300)
        .task { await controls.refresh() }
        .alert(controls.confirmingMicrophone == true ? "打开麦克风需要重启设备" : "关闭麦克风需要重启设备",
               isPresented: Binding(get: { controls.confirmingMicrophone != nil },
                                    set: { if !$0 { controls.confirmingMicrophone = nil } })) {
            Button(controls.confirmingMicrophone == true ? "重启并打开" : "重启并关闭") { controls.confirmMicrophoneRestart() }
            Button("取消", role: .cancel) { controls.confirmingMicrophone = nil }
        } message: {
            Text(controls.confirmingMicrophone == true
                 ? "设备会保存当前状态后重新启动。麦克风开着时，声音改由模拟器自己播放，这种方式偶尔会让模拟器卡死并退出。"
                 : "设备会保存当前状态后重新启动，之后声音由 Emulator Shell 播放。")
        }
    }

    private var tiles: some View {
        let loaded = controls.loaded
        return LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: 3), spacing: 4) {
            Tile(symbol: "location", title: "位置", status: loaded ? controls.locationSummary : "…") { open(.location) }
            Tile(symbol: "battery.100percent.bolt", title: "电池", status: loaded ? controls.batterySummary : "…") { open(.battery) }
            Tile(symbol: "doc.on.clipboard", title: "剪贴板同步", status: controls.clipboardSync ? "已开启" : "已关闭", isOn: controls.clipboardSync) {
                controls.clipboardSync.toggle()
                controls.message = nil
            }
            Tile(symbol: "phone", title: "来电与短信", status: "模拟") { open(.phone) }
            Tile(symbol: "touchid", title: "指纹", status: "轻触一次") {
                Task { await controls.touchFingerprint() }
            }
            Tile(symbol: "mic", title: "麦克风", status: controls.usesMicrophone ? "使用 Mac 麦克风" : "已关闭", isOn: controls.usesMicrophone) {
                Task { await controls.setUsesMicrophone(!controls.usesMicrophone) }
            }
            Tile(symbol: "sun.max", title: "亮度", status: loaded ? controls.brightnessSummary : "…") { open(.brightness) }
            Tile(symbol: "clock.arrow.circlepath", title: "快照", status: loaded ? "\(controls.snapshots.count) 个" : "…") { open(.snapshots) }
            Tile(symbol: "rectangle.split.2x1", title: "折叠姿态", status: controls.isFoldable ? postureName(controls.posture) : "此设备不支持") { open(.posture) }
                .disabled(!controls.isFoldable)
        }
    }

    private func open(_ page: Page) {
        controls.message = nil
        withAnimation(.easeOut(duration: 0.15)) { self.page = page }
    }
}

struct Tile: View {
    let symbol: String
    let title: String
    let status: String
    var isOn = false
    let action: () -> Void
    @State private var hovered = false
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Image(systemName: symbol)
                    .font(.system(size: 15))
                    .foregroundStyle(isOn ? Color.white : Color.primary)
                    .frame(width: 36, height: 36)
                    .background(Circle().fill(isOn ? Color.accentColor : Color.primary.opacity(0.07)))
                Text(title).font(.system(size: 12))
                Text(status).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
            }
            .frame(maxWidth: .infinity, minHeight: 84)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(hovered && isEnabled ? 0.06 : 0)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .opacity(isEnabled ? 1 : 0.45)
        .onHover { hovered = $0 }
        .accessibilityLabel(title)
        .accessibilityValue(status)
    }
}

private func postureName(_ posture: PostureValue) -> String {
    switch posture {
    case .postureClosed: "合上"
    case .postureHalfOpened: "半开"
    case .postureOpened: "展开"
    case .postureFlipped: "翻转"
    case .postureTent: "帐篷"
    default: "未知"
    }
}

// MARK: - Pages

private struct LocationPage: View {
    @Bindable var controls: DeviceControls

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Menu("常用地点") {
                ForEach(DeviceControls.places) { place in
                    Button(place.name) { Task { await controls.setLocation(place) } }
                }
            }
            Grid(alignment: .leading, horizontalSpacing: 8, verticalSpacing: 8) {
                GridRow {
                    Text("纬度").foregroundStyle(.secondary)
                    TextField("纬度", text: $controls.latitude).labelsHidden()
                }
                GridRow {
                    Text("经度").foregroundStyle(.secondary)
                    TextField("经度", text: $controls.longitude).labelsHidden()
                }
            }
            HStack {
                Spacer()
                Button("设置位置") { Task { await controls.applyLocation() } }
                    .keyboardShortcut(.defaultAction)
            }
        }
    }
}

private struct BatteryPage: View {
    @Bindable var controls: DeviceControls

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("电量").foregroundStyle(.secondary)
                Slider(value: $controls.batteryLevel, in: 0...100, step: 1) { editing in
                    if !editing { Task { await controls.applyBattery() } }
                }
                Text("\(Int(controls.batteryLevel))%").monospacedDigit().frame(width: 40, alignment: .trailing)
            }
            Picker("充电", selection: $controls.charger) {
                Text("未充电").tag(BatteryCharger.none)
                Text("交流电").tag(BatteryCharger.ac)
                Text("USB").tag(BatteryCharger.usb)
                Text("无线").tag(BatteryCharger.wireless)
            }
            .onChange(of: controls.charger) { Task { await controls.applyBattery() } }
        }
    }
}

private struct PhonePage: View {
    @Bindable var controls: DeviceControls

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("号码").foregroundStyle(.secondary)
                TextField("号码", text: $controls.phoneNumber).labelsHidden()
            }
            HStack(spacing: 6) {
                Button("来电") { Task { await controls.phone(.initCall) } }
                Button("对方接听") { Task { await controls.phone(.acceptCall) } }
                    .help("设备拨出电话后，模拟对方接听")
                Button("挂断") { Task { await controls.phone(.disconnectCall) } }
            }
            HStack(spacing: 6) {
                Button("保持") { Task { await controls.phone(.placeCallOnHold) } }
                Button("恢复") { Task { await controls.phone(.takeCallOffHold) } }
            }
            Divider()
            Text("收到短信").foregroundStyle(.secondary)
            TextField("短信内容", text: $controls.smsText, axis: .vertical)
                .lineLimit(2...4)
                .labelsHidden()
            HStack {
                Spacer()
                Button("发送到设备") { Task { await controls.receiveSms() } }
                    .disabled(controls.smsText.isEmpty)
            }
        }
    }
}

private struct BrightnessPage: View {
    @Bindable var controls: DeviceControls

    var body: some View {
        HStack {
            Image(systemName: "sun.min").foregroundStyle(.secondary)
            Slider(value: $controls.brightness, in: 0...255) { editing in
                if !editing { Task { await controls.applyBrightness() } }
            }
            Image(systemName: "sun.max").foregroundStyle(.secondary)
            Text(controls.brightnessSummary).monospacedDigit().frame(width: 40, alignment: .trailing)
        }
    }
}

private struct SnapshotsPage: View {
    @Bindable var controls: DeviceControls
    @State private var deleting: DeviceControls.Snapshot?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if controls.snapshots.isEmpty {
                Text("还没有快照。").foregroundStyle(.secondary)
            } else {
                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(controls.snapshots) { snapshot in
                            row(snapshot)
                            if snapshot.id != controls.snapshots.last?.id { Divider() }
                        }
                    }
                }
                .frame(maxHeight: 220)
            }
            Divider()
            HStack {
                TextField("新快照名称（可不填）", text: $controls.newSnapshotName).labelsHidden()
                Button("保存当前状态") { Task { await controls.saveSnapshot() } }
            }
            if controls.busy {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("正在处理快照，可能需要十几秒…").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .confirmationDialog("删除快照“\(deleting?.name ?? "")”？", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
            Button("删除", role: .destructive) {
                if let snapshot = deleting { Task { await controls.deleteSnapshot(snapshot) } }
            }
        }
    }

    private func row(_ snapshot: DeviceControls.Snapshot) -> some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    Text(snapshot.name).lineLimit(1)
                    if snapshot.isLoaded { Text("当前").font(.caption2).foregroundStyle(.secondary) }
                }
                Text(details(snapshot)).font(.caption).foregroundStyle(snapshot.isCompatible ? Color.secondary : Color.orange)
            }
            Spacer(minLength: 4)
            Button("恢复") { Task { await controls.loadSnapshot(snapshot) } }
                .disabled(!snapshot.isCompatible)
            Button {
                deleting = snapshot
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(.borderless)
            .accessibilityLabel("删除快照")
        }
        .padding(.vertical, 6)
    }

    private func details(_ snapshot: DeviceControls.Snapshot) -> String {
        var parts: [String] = []
        if let created = snapshot.created { parts.append(created.formatted(date: .abbreviated, time: .shortened)) }
        parts.append(ByteCountFormatter.string(fromByteCount: snapshot.size, countStyle: .file))
        if !snapshot.isCompatible { parts.append("与当前设备不兼容") }
        return parts.joined(separator: " · ")
    }
}

private struct PosturePage: View {
    @Bindable var controls: DeviceControls

    var body: some View {
        Picker("姿态", selection: Binding(get: { controls.posture }, set: { value in Task { await controls.setPosture(value) } })) {
            ForEach(controls.postures, id: \.self) {
                Text(postureName($0)).tag($0)
            }
        }
        .pickerStyle(.radioGroup)
        .labelsHidden()
    }
}
