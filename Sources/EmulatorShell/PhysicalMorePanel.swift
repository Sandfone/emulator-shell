import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// What a physical device's "More" popover shows and changes: the stream's options, kept per device.
@MainActor
@Observable
final class PhysicalControls {
    let id: String
    let name: String
    let info: DeviceInfo
    let isWireless: Bool
    /// Whether the device's sound reaches the Mac now.
    var audioAvailable: Bool
    @ObservationIgnored private weak var store: PhysicalDeviceStore?

    init(id: String, name: String, info: DeviceInfo, isWireless: Bool, store: PhysicalDeviceStore) {
        self.id = id
        self.name = name
        self.info = info
        self.isWireless = isWireless
        self.store = store
        audioAvailable = store.audioDevices.contains(id)
    }

    /// The options live in the defaults, which observation does not see; this changes with them.
    private var revision = 0

    var options: ScrcpyStream.Options {
        get {
            _ = revision
            return store?.options(for: id) ?? .init()
        }
        set {
            store?.setOptions(newValue, for: id)
            revision += 1
        }
    }

    var canPlaySound: Bool { info.sdk >= 30 }
    var screenOff: Bool { store?.screensOff.contains(id) ?? false }
    var isRecording: Bool { store?.isRecording(id) ?? false }

    func toggleScreenOff() {
        store?.setScreenOff(!screenOff, for: id)
    }

    func chooseFiles() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.message = "APK 会安装到手机上，其他文件存到手机的“下载”文件夹"
        panel.prompt = "传到手机"
        guard panel.runModal() == .OK, !panel.urls.isEmpty else { return }
        store?.send(panel.urls, to: id)
    }

    /// The quality presets: the video's long side at most, and its bit rate.
    static let qualities: [(name: String, maxSize: Int, bitRate: Int)] = [
        ("流畅", 1280, 4_000_000), ("均衡", 1920, 8_000_000), ("原始", 0, 16_000_000),
    ]
}

/// The "More" popover of a physical device's window.
struct PhysicalMorePanel: View {
    @Bindable var controls: PhysicalControls

    var body: some View {
        let options = controls.options
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text("更多功能").font(.headline)
                Spacer()
                Text(controls.name).font(.caption).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 4)
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: 3), spacing: 4) {
                Tile(symbol: "speaker.wave.2", title: "声音",
                     status: !controls.canPlaySound ? "需要 Android 11" : options.audio ? "在 Mac 上播放" : "手机自己播放",
                     isOn: controls.canPlaySound && options.audio) {
                    controls.options.audio.toggle()
                }
                .disabled(!controls.canPlaySound)
                Tile(symbol: "iphone.slash", title: "熄灭手机屏幕", status: controls.screenOff ? "已熄灭" : "亮着", isOn: controls.screenOff) {
                    controls.toggleScreenOff()
                }
                Tile(symbol: "sun.max", title: "保持唤醒", status: options.stayAwake ? "投屏时不锁屏" : "已关闭", isOn: options.stayAwake) {
                    controls.options.stayAwake.toggle()
                }
                Tile(symbol: "doc.on.clipboard", title: "剪贴板同步", status: options.clipboardSync ? "已开启" : "已关闭", isOn: options.clipboardSync) {
                    controls.options.clipboardSync.toggle()
                }
                Tile(symbol: "hand.tap", title: "显示触摸点", status: options.showTouches ? "已开启" : "已关闭", isOn: options.showTouches) {
                    controls.options.showTouches.toggle()
                }
                Tile(symbol: "arrow.down.doc", title: "传文件", status: "APK 或文件") {
                    controls.chooseFiles()
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text("画质")
                    Spacer()
                    Text(qualityNote(options)).font(.caption).foregroundStyle(.secondary)
                }
                Picker("画质", selection: Binding(
                    get: { PhysicalControls.qualities.firstIndex { $0.maxSize == options.maxSize } ?? 1 },
                    set: { index in
                        let quality = PhysicalControls.qualities[index]
                        controls.options.maxSize = quality.maxSize
                        controls.options.bitRate = quality.bitRate
                    })) {
                    ForEach(PhysicalControls.qualities.indices, id: \.self) { Text(PhysicalControls.qualities[$0].name).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
            }
            .padding(.horizontal, 4)
            Divider()
            VStack(alignment: .leading, spacing: 3) {
                Text("把 APK 拖到设备上即安装，其他文件存到手机的“下载”文件夹。⌥ 拖动是双指缩放。")
                Text("\(controls.isWireless ? "Wi-Fi" : "USB") · Android \(controls.info.androidVersion) · H.264 硬件解码")
                if controls.isRecording {
                    Text("录屏中改画质或声音会让画面断开一下，录屏继续。")
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 4)
        }
        .padding(12)
        .frame(width: 300)
    }

    private func qualityNote(_ options: ScrcpyStream.Options) -> String {
        let size = options.maxSize == 0 ? "原始分辨率" : "最长边 \(options.maxSize)"
        return "\(size) · \(options.bitRate / 1_000_000) Mbit/s"
    }
}
