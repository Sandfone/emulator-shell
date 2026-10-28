// swift-tools-version: 6.0
import Foundation
import PackageDescription

/// The macOS SDK of the selected developer tools, for the link. SwiftPM links through clang with `--sysroot`,
/// from which clang does not read the SDK's version, so the binary records the deployment target (15.0) as the
/// SDK it was built with, and AppKit draws the app as it did on macOS 15. With `-isysroot` it records the SDK's.
let sdkPath: String? = {
    let developer = ProcessInfo.processInfo.environment["DEVELOPER_DIR"]
        ?? (try? FileManager.default.destinationOfSymbolicLink(atPath: "/var/db/xcode_select_link"))
    guard let developer else { return nil }
    return ["Platforms/MacOSX.platform/Developer/SDKs/MacOSX.sdk", "SDKs/MacOSX.sdk"]
        .map { "\(developer)/\($0)" }
        .first { FileManager.default.fileExists(atPath: "\($0)/SDKSettings.json") }
}()

let package = Package(
    name: "EmulatorShell",
    platforms: [.macOS(.v15)],
    dependencies: [
        .package(url: "https://github.com/grpc/grpc-swift-2.git", from: "2.4.4"),
        .package(url: "https://github.com/grpc/grpc-swift-nio-transport.git", from: "2.11.0"),
        .package(url: "https://github.com/grpc/grpc-swift-protobuf.git", from: "2.5.0"),
        .package(url: "https://github.com/apple/swift-protobuf.git", from: "1.30.0"),
    ],
    targets: [
        .executableTarget(
            name: "EmulatorShell",
            dependencies: [
                .product(name: "GRPCCore", package: "grpc-swift-2"),
                .product(name: "GRPCNIOTransportHTTP2", package: "grpc-swift-nio-transport"),
                .product(name: "GRPCProtobuf", package: "grpc-swift-protobuf"),
                .product(name: "SwiftProtobuf", package: "swift-protobuf"),
            ],
            linkerSettings: sdkPath.map { [.unsafeFlags(["-Xclang-linker", "-isysroot", "-Xclang-linker", $0])] } ?? [],
            plugins: [
                .plugin(name: "GRPCProtobufGenerator", package: "grpc-swift-protobuf"),
            ]
        ),
    ],
    swiftLanguageModes: [.v5]
)
