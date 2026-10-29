import AppKit
import Observation

/// What the new/edit sheet edits. Sizes are strings so half-typed values survive.
struct AvdForm {
    enum Mode: Equatable { case create, edit(String) }

    var mode: Mode = .create
    var name = ""
    /// Once the name is typed in, changing the device type no longer renames.
    var nameTouched = false
    var deviceTypeId = "pixel_10_pro"
    var imageId: String?
    var ramMB = "2048"
    var dataGB = "6"
    var sdMB = "512"
    var hostGpu = true
    var coldBoot = false
    var cameraBack = "virtualscene"
    var cameraFront = "emulated"
    var landscape = false

    var isEdit: Bool { mode != .create }
}

/// The device manager's state: AVDs, device definitions, system images, and the devices this app runs.
@MainActor
@Observable
final class DeviceManager {
    enum Status: Equatable {
        case stopped, booting, stopping
        case running(serial: String?)
        /// Started by something else (Android Studio, the command line).
        case external(serial: String?)

        var isStopped: Bool { self == .stopped }
    }

    enum Sheet: Identifiable {
        case form(AvdForm)
        case images
        var id: String {
            switch self {
            case .form: "form"
            case .images: "images"
            }
        }
    }

    enum Confirmation: Identifiable {
        case delete(Avd), wipe(Avd)
        var id: String {
            switch self {
            case let .delete(avd): "delete-\(avd.id)"
            case let .wipe(avd): "wipe-\(avd.id)"
            }
        }
    }

    var avds: [Avd] = []
    var deviceTypes: [DeviceType] = []
    var images: [SystemImage] = []
    var selection: String?
    var filter = ""
    var sheet: Sheet?
    /// The images sheet was opened from the form, which comes back when it closes.
    var formUnderImages: AvdForm?
    var confirmation: Confirmation?
    var errorMessage: String?
    /// A short line under the selected device's buttons, e.g. after a wipe.
    var note: String?
    var busy: Set<String> = []
    var sizes: [String: Int64] = [:]
    var imageSizes: [String: Int64] = [:]
    /// Download progress per image package, 0...1.
    var downloads: [String: Double] = [:]
    var loadingAvailableImages = false
    var availableImagesUpdated: Date?

    private(set) var sessionStates: [String: DeviceSession.State] = [:]
    private var external: [String: RunningEmulator] = [:]
    @ObservationIgnored private var sessions: [String: DeviceSession] = [:]
    /// Devices being restarted, with the arguments they start again with once stopped.
    @ObservationIgnored private var restarting: [String: [String]] = [:]
    @ObservationIgnored private var downloadTasks: [String: Task<Void, Never>] = [:]
    @ObservationIgnored private var availableImages: [SystemImage] = []
    @ObservationIgnored private var timer: Timer?

    private static let listCache = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("EmulatorShell/sdkmanager-list.txt")

    init() {
        if let cached = try? String(contentsOf: Self.listCache, encoding: .utf8) {
            availableImages = SystemImage.parseAvailable(cached)
            availableImagesUpdated = (try? Self.listCache.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
        }
        reload()
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshRunning() }
        }
        Task { await loadDeviceTypes() }
    }

    // MARK: - Reading

    func reload() {
        avds = Avd.loadAll()
        if selection == nil || !avds.contains(where: { $0.id == selection }) { selection = avds.first?.id }
        images = SystemImage.merge(installed: SystemImage.scanInstalled(), available: availableImages)
        refreshRunning()
        measureSizes()
    }

    var selectedAvd: Avd? { avds.first { $0.id == selection } }
    var runningSessions: [DeviceSession] { Array(sessions.values) }

    func session(for window: NSWindow?) -> DeviceSession? {
        sessions.values.first { $0.window === window && window != nil }
    }

    func status(of avd: Avd) -> Status {
        switch sessionStates[avd.id] {
        case .booting: return .booting
        case .running: return .running(serial: external[avd.id]?.serial)
        case .stopping: return .stopping
        case .stopped, nil: return external[avd.id].map { .external(serial: $0.serial) } ?? .stopped
        }
    }

    func deviceType(of avd: Avd) -> DeviceType? {
        deviceTypes.first { $0.id == avd.deviceTypeId }
    }

    func category(of avd: Avd) -> DeviceCategory {
        if let type = deviceType(of: avd) { return type.category }
        let tag = (avd.config["tag.id"] ?? "") + (avd.sysdir ?? "")
        if tag.contains("wear") { return .wear }
        if tag.contains("tv") { return .tv }
        if tag.contains("automotive") { return .auto }
        return avd.isLandscape ? .tablet : .phone
    }

    func image(of avd: Avd) -> SystemImage? {
        images.first { $0.sysdir == avd.sysdir || $0.sysdir == (avd.sysdir ?? "") + "/" }
    }

    func thumbnail(of avd: Avd) -> DeviceThumbnail? {
        sessions[avd.id]?.thumbnail()
    }

    private func refreshRunning() {
        let found = RunningEmulator.scan()
        if found != external { external = found }
    }

    private func measureSizes() {
        let folders = avds.map { ($0.id, $0.folder) }
        let installed = images.filter { $0.status == .installed }.map { ($0.id, $0.folder) }
        Task { [weak self] in
            let (avdSizes, imageSizes) = await Task.detached {
                (Dictionary(uniqueKeysWithValues: folders.map { ($0.0, allocatedSize(of: $0.1)) }),
                 Dictionary(uniqueKeysWithValues: installed.map { ($0.0, allocatedSize(of: $0.1)) }))
            }.value
            self?.sizes = avdSizes
            self?.imageSizes = imageSizes
        }
    }

    private func loadDeviceTypes() async {
        do {
            deviceTypes = try await DeviceType.load()
        } catch {
            Log.error("cannot list device definitions: \(error)")
            errorMessage = "无法读取设备类型列表：\(error.localizedDescription)"
        }
    }

    /// `sdkmanager --list` needs the network and takes a while, so the last result is shown meanwhile.
    func refreshAvailableImages() {
        guard !loadingAvailableImages else { return }
        loadingAvailableImages = true
        Task {
            defer { loadingAvailableImages = false }
            do {
                let output = try await Sdk.run("sdkmanager", ["--list"])
                try? FileManager.default.createDirectory(at: Self.listCache.deletingLastPathComponent(), withIntermediateDirectories: true)
                try? output.write(to: Self.listCache, atomically: true, encoding: .utf8)
                availableImages = SystemImage.parseAvailable(output)
                availableImagesUpdated = Date()
                images = SystemImage.merge(installed: SystemImage.scanInstalled(), available: availableImages)
            } catch {
                Log.error("sdkmanager --list failed: \(error)")
            }
        }
    }

    // MARK: - Running devices

    func start(_ avd: Avd, coldBoot: Bool = false, extraArguments: [String] = []) {
        guard status(of: avd).isStopped else { return }
        var avd = avd
        var notes: [String] = []
        if avd.config["hw.keyboard"] != "yes" {
            do {
                avd = try enableKeyboard(avd)
                notes.append("已开启键盘输入：没有它，按键和文字输入都到不了设备。硬件配置变了，这次会冷启动。")
            } catch {
                errorMessage = "无法开启“\(avd.name)”的键盘输入：\(error.localizedDescription)"
                return
            }
        }
        if coldBoot { notes.append("冷启动：忽略快照，从头开机。") }
        do {
            let session = try DeviceSession(avd: avd, grpcPort: freeGrpcPort(), coldBoot: coldBoot,
                                            extraArguments: extraArguments, cascade: sessions.count)
            sessions[avd.id] = session
            sessionStates[avd.id] = session.state
            session.onStateChange = { [weak self] state in
                guard let self else { return }
                self.sessionStates[avd.id] = state
                if state == .stopped {
                    self.sessions[avd.id] = nil
                    self.sessionStates[avd.id] = nil
                    self.refreshRunning()
                    self.measureSizes()
                    if let arguments = self.restarting.removeValue(forKey: avd.id) {
                        self.start(self.avds.first { $0.id == avd.id } ?? avd, extraArguments: arguments)
                    }
                }
            }
            session.onRestart = { [weak self, weak session] in
                guard let self, let session else { return }
                self.restarting[avd.id] = extraArguments
                session.stop()
            }
            note = notes.isEmpty ? nil : notes.joined(separator: "\n")
        } catch {
            errorMessage = "无法启动“\(avd.name)”：\(error.localizedDescription)"
        }
    }

    /// Keys and text reach the guest through the emulator's virtual keyboard, which only exists with
    /// `hw.keyboard=yes`. avdmanager leaves it off (Android Studio turns it on), and gRPC `sendKey` then
    /// succeeds without delivering anything.
    private func enableKeyboard(_ avd: Avd) throws -> Avd {
        let url = avd.folder.appendingPathComponent("config.ini")
        var config = try IniFile(contentsOf: url)
        config["hw.keyboard"] = "yes"
        try config.write(to: url)
        Log.info("enabled hw.keyboard for \(avd.id)")
        var updated = avd
        updated.config = config.values
        if let index = avds.firstIndex(where: { $0.id == avd.id }) { avds[index] = updated }
        return updated
    }

    func stop(_ avd: Avd) {
        if let session = sessions[avd.id] {
            session.stop()
        } else if let running = external[avd.id] {
            kill(running.pid, SIGINT)
        }
    }

    func showWindow(of avd: Avd) {
        sessions[avd.id]?.bringToFront()
    }

    /// The first even port from 8554 that no emulator uses and nothing else is listening on.
    private func freeGrpcPort() -> Int {
        let taken = Set(sessions.values.map(\.grpcPort) + external.values.compactMap(\.grpcPort))
        var port = 8554
        while taken.contains(port) || !Self.canBind(port) { port += 2 }
        return port
    }

    private static func canBind(_ port: Int) -> Bool {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        var address = sockaddr_in()
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = in_port_t(UInt16(port).bigEndian)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        return withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } == 0
        }
    }

    // MARK: - Creating and editing

    func newForm(deviceTypeId: String = "pixel_10_pro") -> AvdForm {
        var form = AvdForm()
        let type = deviceTypes.first { $0.id == deviceTypeId } ?? deviceTypes.first
        form.deviceTypeId = type?.id ?? deviceTypeId
        form.name = uniqueName(type?.name ?? "Pixel 10 Pro")
        form.landscape = type?.category.isLandscape ?? false
        form.imageId = defaultImage(for: type?.category ?? .phone)?.id
        return form
    }

    func editForm(for avd: Avd) -> AvdForm {
        var form = AvdForm()
        form.mode = .edit(avd.id)
        form.name = avd.name
        form.nameTouched = true
        form.deviceTypeId = avd.deviceTypeId ?? ""
        form.imageId = image(of: avd)?.id
        form.ramMB = avd.config["hw.ramSize"]?.replacingOccurrences(of: "M", with: "") ?? "2048"
        form.dataGB = (formatConfigSize(avd.config["disk.dataPartition.size"]) ?? "6").replacingOccurrences(of: " GB", with: "")
        form.sdMB = (formatConfigSize(avd.config["sdcard.size"]) ?? "512").replacingOccurrences(of: " MB", with: "")
        form.hostGpu = !["swiftshader_indirect", "swiftshader", "swangle", "swangle_indirect", "lavapipe"].contains(avd.config["hw.gpu.mode"] ?? "")
        form.coldBoot = avd.isColdBoot
        form.cameraBack = avd.config["hw.camera.back"] ?? "virtualscene"
        form.cameraFront = avd.config["hw.camera.front"] ?? "emulated"
        form.landscape = avd.isLandscape
        return form
    }

    func uniqueName(_ base: String) -> String {
        var name = base, number = 2
        while avds.contains(where: { $0.id == Avd.id(for: name) }) {
            name = "\(base) \(number)"
            number += 1
        }
        return name
    }

    /// The newest installed image a device category can run.
    func defaultImage(for category: DeviceCategory) -> SystemImage? {
        images.first { $0.family == category.family && $0.status == .installed && !$0.isPreview }
            ?? images.first { $0.family == category.family && $0.status == .installed }
    }

    func nameError(_ form: AvdForm) -> String? {
        let id = Avd.id(for: form.name)
        if form.name.trimmingCharacters(in: .whitespaces).isEmpty { return "请输入名称。" }
        if case .edit = form.mode { return nil }
        if id.isEmpty { return "名称需要包含英文字母或数字。" }
        if avds.contains(where: { $0.id == id }) { return "已有名为“\(id)”的模拟器。" }
        return nil
    }

    func imageProblem(_ form: AvdForm, category: DeviceCategory) -> String? {
        guard let image = images.first(where: { $0.id == form.imageId }), image.family == category.family else {
            return "还没有安装\(category.title)可用的系统镜像。"
        }
        if let progress = downloads[image.id] { return "正在下载 \(image.apiLabel)（\(Int(progress * 100))%）…" }
        if image.status == .broken { return "这个镜像目录是空的，安装没有完成。重新下载后才能使用。" }
        if image.status == .available { return "这个镜像还没有下载。" }
        return nil
    }

    func submit(_ form: AvdForm) {
        busy.insert("form")
        Task {
            defer { busy.remove("form") }
            do {
                switch form.mode {
                case .create:
                    let id = try await create(form)
                    reload()
                    selection = id
                    note = nil
                case let .edit(id):
                    guard let avd = avds.first(where: { $0.id == id }) else { return }
                    try apply(form, to: avd, creating: false)
                    reload()
                    note = "已保存。改动在下次启动时生效。"
                }
                sheet = nil
            } catch {
                errorMessage = error.localizedDescription
            }
        }
    }

    private func create(_ form: AvdForm) async throws -> String {
        guard let image = images.first(where: { $0.id == form.imageId }) else { throw ShellError("没有选择系统镜像") }
        let id = Avd.id(for: form.name)
        let sd = Int(form.sdMB) ?? 512
        // avdmanager asks whether to make a custom hardware profile; "no" keeps the device definition's.
        _ = try await Sdk.run("avdmanager", ["create", "avd", "-n", id, "-k", image.package, "-d", form.deviceTypeId, "-c", "\(sd)M"],
                              input: "no\n")
        guard let avd = Avd.loadAll().first(where: { $0.id == id }) else { throw ShellError("avdmanager 没有创建 \(id)") }
        try apply(form, to: avd, creating: true)
        return id
    }

    private func apply(_ form: AvdForm, to avd: Avd, creating: Bool) throws {
        let url = avd.folder.appendingPathComponent("config.ini")
        var config = try IniFile(contentsOf: url)
        config["avd.ini.displayname"] = form.name.trimmingCharacters(in: .whitespaces)
        config["hw.ramSize"] = String(Int(form.ramMB) ?? 2048)
        config["disk.dataPartition.size"] = "\(Int(form.dataGB) ?? 6)G"
        config["hw.gpu.mode"] = form.hostGpu ? "host" : "swiftshader_indirect"
        config["fastboot.forceColdBoot"] = form.coldBoot ? "yes" : "no"
        config["hw.camera.back"] = form.cameraBack
        config["hw.camera.front"] = form.cameraFront
        config["hw.initialOrientation"] = form.landscape ? "landscape" : "portrait"
        config["hw.keyboard"] = "yes"
        if let image = images.first(where: { $0.id == form.imageId }) {
            // avdmanager writes PlayStore.enabled=no even for Google Play images, which hides the Play Store.
            config["PlayStore.enabled"] = image.tag.contains("playstore") ? "true" : "false"
            if image.sysdir != avd.sysdir {
                config["image.sysdir.1"] = image.sysdir
                config["tag.id"] = image.tag
                config["abi.type"] = image.abi
            }
        }
        if creating, config["skin.path"] == nil || config["skin.name"] == nil,
           let skin = deviceTypes.first(where: { $0.id == form.deviceTypeId })?.skinPath {
            config["skin.name"] = skin.lastPathComponent
            config["skin.path"] = skin.path
            config["showDeviceFrame"] = "yes"
        }
        try config.write(to: url)
    }

    func duplicate(_ avd: Avd) {
        var form = editForm(for: avd)
        form.mode = .create
        form.name = uniqueName("\(avd.name) 副本")
        submit(form)
    }

    // MARK: - Removing

    /// Deletes the user data and snapshots, as a factory reset; the next boot starts like a new device.
    func wipe(_ avd: Avd) {
        let fm = FileManager.default
        let names = (try? fm.contentsOfDirectory(atPath: avd.folder.path)) ?? []
        for name in names where name.hasPrefix("userdata-qemu.img") || name.hasPrefix("cache.img") || name == "snapshots" {
            try? fm.removeItem(at: avd.folder.appendingPathComponent(name))
        }
        note = "数据已擦除，下次启动会像新机一样开机。"
        measureSizes()
    }

    func delete(_ avd: Avd) {
        busy.insert(avd.id)
        Task {
            defer { busy.remove(avd.id) }
            do {
                _ = try await Sdk.run("avdmanager", ["delete", "avd", "-n", avd.id])
                note = nil
                reload()
            } catch {
                errorMessage = "无法删除“\(avd.name)”：\(error.localizedDescription)"
            }
        }
    }

    func reveal(_ avd: Avd) {
        NSWorkspace.shared.activateFileViewerSelecting([avd.folder])
    }

    // MARK: - System images

    func download(_ image: SystemImage) {
        guard downloads[image.id] == nil else { return }
        downloads[image.id] = 0
        let package = image.package
        downloadTasks[image.id] = Task {
            defer {
                downloads[image.id] = nil
                downloadTasks[image.id] = nil
            }
            do {
                // An interrupted install leaves an empty folder that sdkmanager treats as installed.
                let folder = image.folder
                if image.status == .broken, (try? FileManager.default.contentsOfDirectory(atPath: folder.path))?.isEmpty == true {
                    try FileManager.default.removeItem(at: folder)
                }
                _ = try await Sdk.run("sdkmanager", ["--install", package], input: String(repeating: "y\n", count: 8)) { line in
                    guard let range = line.range(of: #"[0-9]+(?=%)"#, options: .regularExpression),
                          let percent = Double(line[range]) else { return }
                    Task { @MainActor in
                        if self.downloads[image.id] != nil { self.downloads[image.id] = percent / 100 }
                    }
                }
            } catch is CancellationError {
            } catch {
                errorMessage = "无法下载 \(image.apiLabel)：\(error.localizedDescription)"
            }
            images = SystemImage.merge(installed: SystemImage.scanInstalled(), available: availableImages)
            measureSizes()
        }
    }

    func cancelDownload(_ image: SystemImage) {
        downloadTasks[image.id]?.cancel()
    }

    func isInUse(_ image: SystemImage) -> Bool {
        avds.contains { $0.sysdir == image.sysdir }
    }

    func uninstall(_ image: SystemImage) {
        busy.insert(image.id)
        Task {
            defer { busy.remove(image.id) }
            do {
                _ = try await Sdk.run("sdkmanager", ["--uninstall", image.package])
            } catch {
                errorMessage = "无法删除 \(image.apiLabel)：\(error.localizedDescription)"
            }
            images = SystemImage.merge(installed: SystemImage.scanInstalled(), available: availableImages)
        }
    }

    // MARK: - Quitting

    var hasSessions: Bool { !sessions.isEmpty }
    /// Whether a device is between its two runs of a restart, with no window meanwhile.
    var isRestarting: Bool { !restarting.isEmpty }

    /// Shuts every device down, saving snapshots, and calls `done` once all emulators have exited.
    func stopAllSessions(then done: @escaping () -> Void) {
        DeviceSession.stopAll(Array(sessions.values), then: done)
    }

    func stopAllSessions() {
        DeviceSession.stopAll(Array(sessions.values))
    }
}
