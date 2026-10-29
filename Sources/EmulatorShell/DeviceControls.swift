import Foundation
import Observation

/// The "More" panel's view of one running device: values read back from the emulator, and the calls
/// that change them.
@MainActor
@Observable
final class DeviceControls {
    struct Snapshot: Identifiable {
        let id: String
        let name: String
        let created: Date?
        let size: Int64
        let isLoaded: Bool
        let isCompatible: Bool
    }

    struct Message {
        let text: String
        let isError: Bool
    }

    struct Place: Identifiable {
        let name: String
        let latitude: Double
        let longitude: Double
        var id: String { name }
    }

    static let places = [
        Place(name: "北京", latitude: 39.9042, longitude: 116.4074),
        Place(name: "上海", latitude: 31.2304, longitude: 121.4737),
        Place(name: "深圳", latitude: 22.5431, longitude: 114.0579),
        Place(name: "香港", latitude: 22.3193, longitude: 114.1694),
        Place(name: "东京", latitude: 35.6762, longitude: 139.6503),
        Place(name: "悉尼", latitude: -33.8688, longitude: 151.2093),
        Place(name: "墨尔本", latitude: -37.8136, longitude: 144.9631),
        Place(name: "伦敦", latitude: 51.5074, longitude: -0.1278),
        Place(name: "纽约", latitude: 40.7128, longitude: -74.0060),
        Place(name: "Googleplex", latitude: 37.4220, longitude: -122.0841),
    ]

    let deviceName: String
    /// Only devices with a hinge sensor (foldables) accept postures.
    let isFoldable: Bool
    /// The postures the device defines (`hw.sensor.posture_list`); the emulator refuses the others.
    let postures: [PostureValue]

    var latitude = ""
    var longitude = ""
    var batteryLevel = 100.0
    var charger: BatteryCharger = .ac
    var brightness = 128.0
    var usesMicrophone = false
    var clipboardSync: Bool {
        didSet { onClipboardSync?(clipboardSync) }
    }
    var phoneNumber = "6505551212"
    var smsText = ""
    var posture: PostureValue = .postureOpened
    var snapshots: [Snapshot] = []
    var newSnapshotName = ""
    var busy = false
    var message: Message?
    /// Whether the values above were read from the emulator, so the tiles can show them.
    var loaded = false

    /// Asks before the device restarts to switch the microphone (see `restartForMicrophone`); the value
    /// is whether it is being switched on.
    var confirmingMicrophone: Bool?

    @ObservationIgnored var onClipboardSync: ((Bool) -> Void)?
    /// Set when switching the microphone means restarting the device, with or without the emulator's own
    /// audio (see `DeviceAudio`); called with whether it is to be on.
    @ObservationIgnored var restartForMicrophone: ((Bool) -> Void)?
    @ObservationIgnored private let client: EmulatorClient

    init(client: EmulatorClient, avd: Avd, clipboardSync: Bool) {
        self.client = client
        deviceName = avd.name
        isFoldable = avd.config["hw.sensor.hinge"] == "yes"
        let listed = (avd.config["hw.sensor.posture_list"] ?? "").split(separator: ",")
            .compactMap { Int($0.trimmingCharacters(in: .whitespaces)).flatMap(PostureValue.init(rawValue:)) }
        postures = listed.isEmpty ? [.postureClosed, .postureHalfOpened, .postureOpened, .postureFlipped, .postureTent] : listed
        self.clipboardSync = clipboardSync
    }

    // MARK: - Reading

    /// Reads the current values; each one that cannot be read keeps its last value.
    func refresh() async {
        if let location = try? await client.location() {
            latitude = Self.format(location.latitude)
            longitude = Self.format(location.longitude)
        }
        if let battery = try? await client.battery() {
            batteryLevel = Double(battery.chargeLevel)
            charger = battery.charger
        }
        if let value = try? await client.brightness() { brightness = Double(value) }
        if restartForMicrophone == nil, let enabled = try? await client.usesHostMicrophone() { usesMicrophone = enabled }
        await refreshSnapshots()
        loaded = true
    }

    func refreshSnapshots() async {
        guard let list = try? await client.snapshots() else { return }
        snapshots = list.map { details in
            Snapshot(
                id: details.snapshotID,
                name: details.details.hasLogicalName && !details.details.logicalName.isEmpty ? details.details.logicalName : details.snapshotID,
                created: details.details.creationTime > 0 ? Date(timeIntervalSince1970: TimeInterval(details.details.creationTime)) : nil,
                size: Int64(details.size),
                isLoaded: details.status == .loaded,
                isCompatible: details.status != .incompatible
            )
        }.sorted { ($0.created ?? .distantPast) > ($1.created ?? .distantPast) }
    }

    var locationSummary: String {
        guard let lat = Double(latitude), let lon = Double(longitude) else { return "未读取" }
        if let place = Self.places.first(where: { abs($0.latitude - lat) < 0.001 && abs($0.longitude - lon) < 0.001 }) {
            return place.name
        }
        return String(format: "%.2f, %.2f", lat, lon)
    }

    var batterySummary: String {
        "\(Int(batteryLevel))%" + (charger == .none ? "" : " · 充电")
    }

    var brightnessSummary: String { "\(Int((brightness / 255 * 100).rounded()))%" }

    // MARK: - Changing

    func setLocation(_ place: Place) async {
        latitude = Self.format(place.latitude)
        longitude = Self.format(place.longitude)
        await applyLocation()
    }

    func applyLocation() async {
        guard let lat = Double(latitude), let lon = Double(longitude), (-90...90).contains(lat), (-180...180).contains(lon) else {
            message = Message(text: "纬度要在 -90 到 90 之间，经度在 -180 到 180 之间。", isError: true)
            return
        }
        await run("位置已设为 \(locationSummary)。") { try await $0.setLocation(latitude: lat, longitude: lon) }
    }

    func applyBattery() async {
        let level = Int(batteryLevel), charger = charger
        await run("电池已设为 \(batterySummary)。") { try await $0.setBattery(level: level, charger: charger) }
    }

    func phone(_ operation: PhoneOperation) async {
        let number = phoneNumber.trimmingCharacters(in: .whitespaces)
        let done: [PhoneOperation: String] = [
            .initCall: "\(number) 正在来电。", .acceptCall: "对方已接听。", .disconnectCall: "通话已挂断。",
            .placeCallOnHold: "通话已保持。", .takeCallOffHold: "通话已恢复。",
        ]
        await run(done[operation]) { try await $0.phone(operation, number: number) }
    }

    func receiveSms() async {
        let number = phoneNumber.trimmingCharacters(in: .whitespaces), text = smsText
        guard !text.isEmpty else { return }
        await run("已收到来自 \(number) 的短信。") { try await $0.receiveSms(from: number, text: text) }
        if message?.isError == false { smsText = "" }
    }

    /// One touch of finger 1, as when enrolling a fingerprint or unlocking.
    func touchFingerprint() async {
        await run("已触摸指纹传感器。") { client in
            try await client.touchFingerprint(true, finger: 1)
            try await Task.sleep(for: .milliseconds(250))
            try await client.touchFingerprint(false, finger: 1)
        }
    }

    func setUsesMicrophone(_ enabled: Bool) async {
        if restartForMicrophone != nil {
            confirmingMicrophone = enabled
            return
        }
        usesMicrophone = enabled
        await run(enabled ? "设备现在使用 Mac 的麦克风。" : "已断开 Mac 的麦克风。") { try await $0.setUsesHostMicrophone(enabled) }
        if message?.isError == true { usesMicrophone = !enabled }
    }

    /// Restarts the device with the microphone switched as confirmed.
    func confirmMicrophoneRestart() {
        guard let enabled = confirmingMicrophone else { return }
        confirmingMicrophone = nil
        restartForMicrophone?(enabled)
    }

    func applyBrightness() async {
        let value = Int(brightness)
        await run(nil) { try await $0.setBrightness(value) }
    }

    func setPosture(_ value: PostureValue) async {
        posture = value
        await run(nil) { try await $0.setPosture(value) }
    }

    func saveSnapshot() async {
        let name = newSnapshotName.trimmingCharacters(in: .whitespaces)
        let id = name.isEmpty ? "snap_" + Self.timestamp.string(from: Date()) : Avd.id(for: name)
        guard !id.isEmpty else {
            message = Message(text: "快照名称需要包含英文字母或数字。", isError: true)
            return
        }
        await run("已保存快照“\(id)”。") { try await $0.saveSnapshot(id) }
        newSnapshotName = ""
        await refreshSnapshots()
    }

    func loadSnapshot(_ snapshot: Snapshot) async {
        await run("已恢复到快照“\(snapshot.name)”。") { try await $0.loadSnapshot(snapshot.id) }
        await refreshSnapshots()
    }

    func deleteSnapshot(_ snapshot: Snapshot) async {
        await run("已删除快照“\(snapshot.name)”。") { try await $0.deleteSnapshot(snapshot.id) }
        await refreshSnapshots()
    }

    // MARK: - Helpers

    private func run(_ success: String?, _ operation: @escaping @Sendable (EmulatorClient) async throws -> Void) async {
        busy = true
        defer { busy = false }
        do {
            try await operation(client)
            message = success.map { Message(text: $0, isError: false) }
        } catch {
            Log.error("\(deviceName): \(error)")
            message = Message(text: (error as? ShellError)?.description ?? "操作失败：\(error.localizedDescription)", isError: true)
        }
    }

    private static func format(_ value: Double) -> String { String(format: "%.4f", value) }

    private static let timestamp: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd_HHmmss"
        return formatter
    }()
}
