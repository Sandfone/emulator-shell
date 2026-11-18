import AppKit
import CoreImage.CIFilterBuiltins
import SwiftUI

/// Pairing a device over Wi-Fi (Android 11+), as Android Studio does: the device scans a QR code naming a
/// service and its password, then offers that service on the network (mDNS `_adb-tls-pairing._tcp`), and
/// `adb pair` completes it; or the device shows a six-digit code for a service it offers. Once paired, adb
/// connects to it by itself and it shows up with the other devices.
@MainActor
@Observable
final class PairingModel {
    enum Mode: Hashable { case qr, code }
    enum Phase: Equatable { case waiting, pairing, paired(String), failed(String) }

    var mode: Mode = .qr
    private(set) var phase: Phase = .waiting
    /// Devices showing a pairing code, for the code tab.
    private(set) var pairingServices: [Adb.MdnsService] = []
    var selected: Adb.MdnsService?
    var code = ""
    private(set) var mdnsProblem: String?

    let serviceName = "emulator-shell-" + String(Int.random(in: 100_000...999_999))
    let password = String((0..<10).map { _ in "abcdefghjkmnpqrstuvwxyz23456789".randomElement()! })
    @ObservationIgnored private var task: Task<Void, Never>?

    var qrImage: NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data("WIFI:T:ADB;S:\(serviceName);P:\(password);;".utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 8, y: 8)),
              let image = CIContext().createCGImage(output, from: output.extent) else { return nil }
        return NSImage(cgImage: image, size: NSSize(width: output.extent.width / 2, height: output.extent.height / 2))
    }

    /// Watches the network for the device until the sheet closes.
    func start() {
        task?.cancel()
        task = Task { [weak self] in
            if let check = try? await Adb.text(["mdns", "check"], timeout: .seconds(10)), check.contains("unavailable") {
                self?.mdnsProblem = "adb 的局域网发现（mDNS）不可用，配对需要它。更新 Platform-Tools 后再试。"
            }
            while !Task.isCancelled {
                await self?.poll()
                try? await Task.pause(for: .seconds(1))
            }
        }
    }

    func stop() {
        task?.cancel()
        task = nil
    }

    private func poll() async {
        guard let services = try? await Adb.mdnsServices() else { return }
        let pairing = services.filter(\.isPairing)
        pairingServices = pairing.filter { $0.name != serviceName }
        if let selected, !pairingServices.contains(selected) { self.selected = nil }
        if selected == nil, pairingServices.count == 1 { selected = pairingServices.first }
        // The device that scanned the QR code offers the service named in it.
        if mode == .qr, phase == .waiting, let service = pairing.first(where: { $0.name == serviceName }) {
            await pair(service, code: password)
        }
    }

    func pairWithCode() {
        guard let selected, code.count == 6 else { return }
        Task { await pair(selected, code: code) }
    }

    private func pair(_ service: Adb.MdnsService, code: String) async {
        phase = .pairing
        do {
            try await Adb.pair(address: service.address, code: code)
            phase = .paired(service.host)
        } catch {
            phase = .failed(error.localizedDescription)
        }
    }

    func retry() {
        phase = .waiting
        code = ""
    }
}

struct PairingSheet: View {
    let manager: DeviceManager
    @State private var model = PairingModel()
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 18) {
            Text("通过 Wi-Fi 配对设备").font(.headline)
            Picker("方式", selection: $model.mode) {
                Text("二维码").tag(PairingModel.Mode.qr)
                Text("配对码").tag(PairingModel.Mode.code)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 220)
            .disabled(model.phase == .pairing)

            Group {
                switch model.phase {
                case .waiting, .pairing:
                    if model.mode == .qr { qrPage } else { codePage }
                case let .paired(host):
                    VStack(spacing: 10) {
                        Image(systemName: "checkmark.circle.fill").font(.system(size: 44)).foregroundStyle(.green)
                        Text("已配对").font(.title3.bold())
                        Text("设备（\(host)）稍后会出现在左栏的“物理设备”里。之后回到同一网络会自动连上，不用再配对。")
                            .multilineTextAlignment(.center).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, minHeight: 260)
                case let .failed(message):
                    VStack(spacing: 10) {
                        Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 40)).foregroundStyle(.orange)
                        Text("配对没有成功").font(.title3.bold())
                        Text(message).multilineTextAlignment(.center).foregroundStyle(.secondary).textSelection(.enabled)
                        Button("再试一次") { model.retry() }
                    }
                    .frame(maxWidth: .infinity, minHeight: 260)
                }
            }
            if let problem = model.mdnsProblem {
                Label(problem, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Spacer()
                if case .paired = model.phase {
                    Button("完成") { dismiss() }.keyboardShortcut(.defaultAction)
                } else {
                    Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
                }
            }
        }
        .padding(24)
        .frame(width: 600)
        .onAppear { model.start() }
        .onDisappear { model.stop() }
    }

    private var qrPage: some View {
        HStack(alignment: .center, spacing: 28) {
            VStack(spacing: 12) {
                if let image = model.qrImage {
                    Image(nsImage: image).interpolation(.none).resizable().frame(width: 200, height: 200)
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 12).fill(.white))
                        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color(nsColor: .separatorColor)))
                        .accessibilityLabel("配对二维码")
                }
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(model.phase == .pairing ? "正在配对…" : "正在等待手机扫码…").foregroundStyle(.secondary)
                }
            }
            VStack(alignment: .leading, spacing: 12) {
                Text("1. 手机和这台 Mac 连同一个 Wi-Fi")
                Text("2. 手机上打开 设置 › 系统 › 开发者选项 › **无线调试**")
                Text("3. 点“使用二维码配对设备”，扫左边的码")
                Text("需要 Android 11 及以上。配对成功后设备出现在左栏，之后回到同一网络会自动连上。")
                    .font(.callout).foregroundStyle(.secondary)
                Text("已经插着线？在设备的 ⋯ 菜单里选“切换为无线连接”，任何 Android 版本都行。")
                    .font(.callout).foregroundStyle(.secondary)
            }
            .fixedSize(horizontal: false, vertical: true)
        }
        .frame(minHeight: 260)
    }

    private var codePage: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("1. 手机上打开 开发者选项 › 无线调试 › **使用配对码配对设备**")
            Text("2. 手机会出现在下面，选中它，输入手机上显示的 6 位配对码")
            List(model.pairingServices, id: \.self, selection: $model.selected) { service in
                HStack {
                    Image(systemName: "iphone")
                    VStack(alignment: .leading) {
                        Text(service.name)
                        Text("\(service.host) · 正在显示配对码").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .tag(service)
            }
            .frame(height: 110)
            .overlay {
                if model.pairingServices.isEmpty {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text("正在局域网里寻找显示配对码的设备…").foregroundStyle(.secondary)
                    }
                }
            }
            HStack {
                TextField("配对码", text: $model.code)
                    .font(.system(size: 20, weight: .medium, design: .monospaced))
                    .frame(width: 140)
                    .onChange(of: model.code) { _, value in
                        let digits = String(value.filter(\.isNumber).prefix(6))
                        if digits != value { model.code = digits }
                    }
                    .onSubmit { model.pairWithCode() }
                Button("配对") { model.pairWithCode() }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.selected == nil || model.code.count != 6 || model.phase == .pairing)
                if model.phase == .pairing { ProgressView().controlSize(.small) }
            }
        }
        .frame(minHeight: 260)
    }
}
