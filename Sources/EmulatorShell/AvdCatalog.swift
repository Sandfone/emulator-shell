import Foundation

/// How the device manager groups AVDs and device definitions.
enum DeviceCategory: String, CaseIterable, Identifiable {
    case phone, fold, tablet, wear, tv, auto, desktop

    var id: String { rawValue }

    var title: String {
        switch self {
        case .phone: "手机"
        case .fold: "可折叠"
        case .tablet: "平板"
        case .wear: "Wear OS"
        case .tv: "电视"
        case .auto: "汽车"
        case .desktop: "桌面"
        }
    }

    var symbol: String {
        switch self {
        case .phone, .fold: "iphone"
        case .tablet: "ipad.landscape"
        case .wear: "applewatch"
        case .tv: "tv"
        case .auto: "car"
        case .desktop: "desktopcomputer"
        }
    }

    /// The kind of system image devices in this category run.
    var family: ImageFamily {
        switch self {
        case .phone, .fold, .tablet, .desktop: .phone
        case .wear: .wear
        case .tv: .tv
        case .auto: .auto
        }
    }

    var isLandscape: Bool { [.tablet, .tv, .auto, .desktop].contains(self) }
}

enum ImageFamily: String, CaseIterable, Identifiable {
    case phone, wear, tv, auto, other

    var id: String { rawValue }

    var title: String {
        switch self {
        case .phone: "手机和平板"
        case .wear: "Wear OS"
        case .tv: "电视"
        case .auto: "汽车"
        case .other: "其他"
        }
    }
}

/// A hardware profile from `avdmanager list device`.
struct DeviceType: Identifiable, Hashable {
    let id: String
    let name: String
    let oem: String
    let category: DeviceCategory

    /// The SDK's skin for this device, as an AVD's `skin.path` names it.
    var skinPath: URL? {
        let folder = Sdk.root.appendingPathComponent("skins/\(id)")
        return Sdk.skinArtwork(in: folder) != nil ? folder : nil
    }

    /// Where that skin's `layout` and images are.
    var skinFolder: URL? { skinPath.flatMap(Sdk.skinArtwork) }

    static func load() async throws -> [DeviceType] {
        parse(try await Sdk.run("avdmanager", ["list", "device"]))
    }

    /// Blocks of `id: N or "pixel_9_pro"`, `Name: …`, `OEM : …`, `Tag : …`, separated by dashes.
    static func parse(_ output: String) -> [DeviceType] {
        var types: [(DeviceType, Int)] = []
        var fields: [String: String] = [:]
        func flush() {
            defer { fields = [:] }
            guard let id = fields["id"], let name = fields["Name"],
                  let category = category(id: id, name: name, tag: fields["Tag"] ?? "")
            else { return }
            types.append((DeviceType(id: id, name: name, oem: fields["OEM"] ?? "", category: category), types.count))
        }
        for line in output.split(whereSeparator: \.isNewline) {
            let text = line.trimmingCharacters(in: .whitespaces)
            if text.hasPrefix("id:") {
                flush()
                if let open = text.firstIndex(of: "\""), let close = text.lastIndex(of: "\""), open < close {
                    fields["id"] = String(text[text.index(after: open)..<close])
                }
            } else if text.hasPrefix("---") {
                flush()
            } else if let colon = text.firstIndex(of: ":") {
                fields[text[..<colon].trimmingCharacters(in: .whitespaces)] = text[text.index(after: colon)...].trimmingCharacters(in: .whitespaces)
            }
        }
        flush()
        // Newest Pixels first, then everything else in avdmanager's order.
        return types.sorted { a, b in
            let (ga, gb) = (pixelGeneration(a.0.name), pixelGeneration(b.0.name))
            return ga != gb ? ga > gb : a.1 < b.1
        }.map(\.0)
    }

    private static func pixelGeneration(_ name: String) -> Int {
        guard name.hasPrefix("Pixel") else { return -1 }
        let digits = name.dropFirst("Pixel".count).drop(while: { $0 == " " }).prefix(while: \.isNumber)
        return Int(digits) ?? 1
    }

    /// avdmanager tags only some categories; phones, foldables and tablets are told apart by name.
    /// XR and glasses profiles need images this app cannot show, so they are left out.
    private static func category(id: String, name: String, tag: String) -> DeviceCategory? {
        let tag = tag.lowercased(), lower = (id + " " + name).lowercased()
        if tag.contains("xr") || tag.contains("glasses") { return nil }
        if tag.contains("wear") { return .wear }
        if tag.contains("tv") { return .tv }
        if tag.contains("automotive") { return .auto }
        if tag.contains("desktop") { return .desktop }
        if ["fold", "flip", "rollable"].contains(where: lower.contains) { return .fold }
        if ["tablet", "freeform", "pixel_c", "nexus 7", "nexus 9", "nexus 10"].contains(where: lower.contains) {
            return .tablet
        }
        return .phone
    }
}

/// An arm64 system image, installed or offered by sdkmanager.
struct SystemImage: Identifiable, Hashable {
    enum Status { case installed, broken, available }

    /// `android-37.1`, `android-35-ext15`, `android-canary-20261007`.
    let apiFolder: String
    let tag: String
    let abi: String
    var status: Status
    var description: String
    var revision: String?
    var extensionLevel: String?
    var sizeBytes: Int64?

    var id: String { package }
    /// The sdkmanager package path.
    var package: String { "system-images;\(apiFolder);\(tag);\(abi)" }
    /// The value of `image.sysdir.1` in an AVD using this image.
    var sysdir: String { "system-images/\(apiFolder)/\(tag)/\(abi)/" }
    var folder: URL { Sdk.root.appendingPathComponent(sysdir) }

    var apiLabel: String {
        var version = apiFolder.replacingOccurrences(of: "android-", with: "")
        // `android-CANARY` is the rolling canary; dated ones are `android-canary-20261007`.
        if version.lowercased() == "canary" { return "Canary" }
        if version.hasPrefix("canary-") {
            let date = version.dropFirst("canary-".count)
            guard date.count == 8 else { return "Canary \(date)" }
            return "Canary \(date.prefix(4))-\(date.dropFirst(4).prefix(2))-\(date.suffix(2))"
        }
        if let range = version.range(of: "-ext") {
            let ext = version[range.upperBound...]
            version = String(version[..<range.lowerBound])
            return "API \(version) · 扩展 \(ext)"
        }
        if let range = version.range(of: "-beta") {
            return "API \(version[..<range.lowerBound]) Beta \(version[range.upperBound...])"
        }
        return "API \(version)"
    }

    var kindLabel: String {
        let base = tag.replacingOccurrences(of: "_ps16k", with: "").replacingOccurrences(of: "-ps16k", with: "")
        let page = base == tag ? "" : " · 16 KB 页"
        let names = [
            "google_apis_playstore": "Google Play", "google_apis": "Google APIs", "default": "AOSP",
            "aosp_atd": "AOSP ATD", "google_atd": "Google APIs ATD",
            "google_apis_playstore_tablet": "Google Play 平板", "google_apis_tablet": "Google APIs 平板",
            "android-tv": "Android TV", "google-tv": "Google TV",
            "android-automotive-playstore": "Android Automotive · Google Play", "android-automotive": "Android Automotive",
        ]
        if let name = names[base] { return name + page }
        if base.contains("wear"), let range = description.range(of: #"Wear OS [0-9.]+"#, options: .regularExpression) {
            return String(description[range]) + page
        }
        return description.isEmpty ? base : description
    }

    var family: ImageFamily {
        if tag.contains("wear") { return .wear }
        if tag.contains("tv") { return .tv }
        if tag.contains("automotive") { return .auto }
        if tag.contains("glasses") || tag.contains("xr") { return .other }
        return .phone
    }

    var isPreview: Bool { ["beta", "canary", "preview"].contains(where: apiFolder.lowercased().contains) }

    /// Installed images under `system-images/*/*/arm64-v8a`. A folder without `source.properties` is an
    /// interrupted install: sdkmanager lists it as version "unknown" and the emulator cannot boot it.
    static func scanInstalled() -> [SystemImage] {
        let base = Sdk.root.appendingPathComponent("system-images")
        let fm = FileManager.default
        var images: [SystemImage] = []
        for api in (try? fm.contentsOfDirectory(atPath: base.path)) ?? [] where api.hasPrefix("android-") {
            for tag in (try? fm.contentsOfDirectory(atPath: base.appendingPathComponent(api).path)) ?? [] {
                let folder = base.appendingPathComponent("\(api)/\(tag)/arm64-v8a")
                guard fm.fileExists(atPath: folder.path) else { continue }
                let properties = try? IniFile(contentsOf: folder.appendingPathComponent("source.properties"))
                images.append(SystemImage(
                    apiFolder: api, tag: tag, abi: "arm64-v8a",
                    status: properties == nil ? .broken : .installed,
                    description: properties?["Pkg.Desc"] ?? "",
                    revision: properties?["Pkg.Revision"],
                    extensionLevel: properties?["AndroidVersion.ExtensionLevel"]
                ))
            }
        }
        return images
    }

    /// Rows of the "Available packages:" section of `sdkmanager --list`: path, version and description
    /// in space-padded columns, with `/` where package paths use `;`.
    static func parseAvailable(_ output: String) -> [SystemImage] {
        var images: [SystemImage] = []
        var inAvailable = false
        for line in output.split(whereSeparator: \.isNewline) {
            if !line.hasPrefix(" ") {
                inAvailable = line.hasPrefix("Available packages")
                continue
            }
            guard inAvailable, !line.contains("->") else { continue }
            let columns = line.trimmingCharacters(in: .whitespaces)
                .components(separatedBy: "  ").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            let parts = columns.first?.split(separator: "/").map(String.init) ?? []
            guard parts.count == 4, parts[0] == "system-images", parts[3] == "arm64-v8a" else { continue }
            images.append(SystemImage(
                apiFolder: parts[1], tag: parts[2], abi: parts[3], status: .available,
                description: columns.count > 2 ? columns[2] : "", revision: columns.count > 1 ? columns[1] : nil
            ))
        }
        return images
    }

    /// Installed and broken images, plus offered ones not on disk, newest first.
    static func merge(installed: [SystemImage], available: [SystemImage]) -> [SystemImage] {
        var byId = Dictionary(installed.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        for image in available {
            if var existing = byId[image.id] {
                if existing.description.isEmpty { existing.description = image.description }
                byId[image.id] = existing
            } else {
                byId[image.id] = image
            }
        }
        return byId.values.sorted { a, b in
            a.apiFolder != b.apiFolder
                ? a.apiFolder.compare(b.apiFolder, options: .numeric) == .orderedDescending
                : a.tag < b.tag
        }
    }
}

/// An Android Virtual Device as described by `~/.android/avd/<id>.ini` and its `config.ini`.
struct Avd: Identifiable, Hashable {
    let id: String
    let folder: URL
    var config: [String: String]

    var name: String { config["avd.ini.displayname"] ?? id.replacingOccurrences(of: "_", with: " ") }
    var deviceTypeId: String? { config["hw.device.name"] }
    var manufacturer: String { config["hw.device.manufacturer"] ?? "" }
    var sysdir: String? { config["image.sysdir.1"] }
    var isColdBoot: Bool { config["fastboot.forceColdBoot"] == "yes" }
    var isLandscape: Bool { config["hw.initialOrientation"] == "landscape" }
    var resolution: String? {
        guard let width = config["hw.lcd.width"], let height = config["hw.lcd.height"] else { return nil }
        return "\(width) × \(height)" + (config["hw.lcd.density"].map { " · \($0) dpi" } ?? "")
    }
    /// Where the skin's `layout` and images are.
    var skinFolder: URL? {
        guard let path = config["skin.path"] else { return nil }
        return Sdk.skinArtwork(in: path.hasPrefix("/") ? URL(fileURLWithPath: path) : Sdk.root.appendingPathComponent(path))
    }
    /// A foldable skin's artwork for the outer display, shown while the device is closed: `closed` beside
    /// the `default` folder (Pixel Fold models).
    var closedSkinFolder: URL? {
        guard let folder = skinFolder, folder.lastPathComponent == "default" else { return nil }
        let closed = folder.deletingLastPathComponent().appendingPathComponent("closed")
        return Sdk.skinArtwork(in: closed) == closed ? closed : nil
    }

    static func loadAll() -> [Avd] {
        let fm = FileManager.default
        let files = (try? fm.contentsOfDirectory(atPath: Sdk.avdHome.path)) ?? []
        return files.filter { $0.hasSuffix(".ini") }.compactMap { file -> Avd? in
            let id = String(file.dropLast(".ini".count))
            let pointer = try? IniFile(contentsOf: Sdk.avdHome.appendingPathComponent(file))
            let folder = pointer?["path"].map { URL(fileURLWithPath: $0) } ?? Sdk.avdHome.appendingPathComponent("\(id).avd")
            guard let config = try? IniFile(contentsOf: folder.appendingPathComponent("config.ini")) else { return nil }
            return Avd(id: id, folder: folder, config: config.values)
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// The AVD name avdmanager accepts for a display name: letters, digits, `.`, `_` and `-`.
    static func id(for name: String) -> String {
        name.trimmingCharacters(in: .whitespaces)
            .replacingOccurrences(of: "[^A-Za-z0-9._-]+", with: "_", options: .regularExpression)
            .trimmingCharacters(in: CharacterSet(charactersIn: "_"))
    }
}

/// An emulator process found through the files emulators write while they run, whoever started them.
struct RunningEmulator: Hashable {
    let pid: Int32
    let avdId: String
    let serialPort: Int?
    let grpcPort: Int?
    /// Set when the emulator runs with `-grpc-use-token`.
    let grpcToken: String?
    /// Set when it runs with `-grpc-use-jwt` (newer Android Studio): calls then need a token signed with a
    /// key registered in that folder, which this app does not do.
    let usesJwt: Bool

    var serial: String? { serialPort.map { "emulator-\($0)" } }

    static func scan() -> [String: RunningEmulator] {
        let folder = Sdk.runningEmulatorsDirectory
        var found: [String: RunningEmulator] = [:]
        for file in (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? [] {
            guard file.hasPrefix("pid_"), file.hasSuffix(".ini"),
                  let pid = Int32(file.dropFirst("pid_".count).dropLast(".ini".count)),
                  kill(pid, 0) == 0 || errno == EPERM,
                  let info = try? IniFile(contentsOf: folder.appendingPathComponent(file)),
                  let avdId = info["avd.id"]
            else { continue }
            found[avdId] = RunningEmulator(pid: pid, avdId: avdId, serialPort: info["port.serial"].flatMap(Int.init),
                                           grpcPort: info["grpc.port"].flatMap(Int.init), grpcToken: info["grpc.token"],
                                           usesJwt: info["grpc.jwks"] != nil)
        }
        return found
    }
}

/// Bytes a folder takes on disk.
func allocatedSize(of folder: URL) -> Int64 {
    let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .isRegularFileKey]
    guard let files = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: keys) else { return 0 }
    var total: Int64 = 0
    for case let file as URL in files {
        if let values = try? file.resourceValues(forKeys: Set(keys)), values.isRegularFile == true {
            total += Int64(values.totalFileAllocatedSize ?? 0)
        }
    }
    return total
}

/// "6G" → "6 GB", "512M" → "512 MB"; plain numbers are bytes.
func formatConfigSize(_ value: String?) -> String? {
    guard let value, !value.isEmpty else { return nil }
    let units = ["K": "KB", "M": "MB", "G": "GB", "T": "TB"]
    if let unit = units[String(value.suffix(1)).uppercased()] { return "\(value.dropLast()) \(unit)" }
    guard let bytes = Int64(value) else { return value }
    return ByteCountFormatter.string(fromByteCount: bytes, countStyle: .binary)
}
