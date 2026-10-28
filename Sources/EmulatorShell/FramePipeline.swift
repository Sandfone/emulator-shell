import Accelerate
import CoreGraphics
import CoreVideo
import Foundation
import IOSurface

/// One display frame as delivered by the emulator: RGBA8888, top row first.
struct RawFrame {
    let width: Int
    let height: Int
    /// Emulator-side sequence number; gaps are frames the emulator skipped because we were behind.
    let seq: UInt32
    let timestampUs: UInt64
    /// Quarter turns the device is held at (`Rotation.SkinRotation`); the pixels are already upright for the viewer.
    let rotation: Int
    /// Whether a foldable is folded and this is its outer display.
    let folded: Bool
    /// Only valid for the duration of the callback it is passed to.
    let pixels: UnsafeRawPointer
}

/// A file-backed region the emulator writes frames into (`ImageTransport.MMAP`), so pixels skip gRPC entirely.
final class SharedFrameBuffer: @unchecked Sendable {
    let url: URL
    let size: Int
    let base: UnsafeRawPointer

    /// The `ImageTransport.handle` value: a `file://` URL makes the emulator mmap the file rather than `shm_open` it.
    var handle: String { "file://" + url.path }

    init(url: URL, size: Int) throws {
        let fd = open(url.path, O_RDWR | O_CREAT | O_TRUNC, 0o600)
        guard fd >= 0 else { throw ShellError("cannot create \(url.path): \(String(cString: strerror(errno)))") }
        defer { close(fd) }
        guard ftruncate(fd, off_t(size)) == 0 else {
            throw ShellError("cannot size \(url.path): \(String(cString: strerror(errno)))")
        }
        guard let base = mmap(nil, size, PROT_READ, MAP_SHARED, fd, 0), base != MAP_FAILED else {
            throw ShellError("cannot map \(url.path): \(String(cString: strerror(errno)))")
        }
        self.url = url
        self.size = size
        self.base = UnsafeRawPointer(base)
    }

    deinit {
        munmap(UnsafeMutableRawPointer(mutating: base), size)
        unlink(url.path)
    }
}

/// Copies frames into a small ring of IOSurfaces that Core Animation can display without another upload.
/// Called from the streaming task only; the surfaces it returns are handed to the main thread.
final class SurfacePool: @unchecked Sendable {
    private var surfaces: [IOSurface] = []
    private var next = 0
    private var width = 0
    private var height = 0

    /// Writes `frame` into a surface the window server is not reading, or returns nil to drop the frame.
    func write(_ frame: RawFrame) -> IOSurface? {
        if frame.width != width || frame.height != height {
            surfaces = (0..<4).compactMap { _ in Self.makeSurface(width: frame.width, height: frame.height) }
            width = frame.width
            height = frame.height
        }
        guard let index = surfaces.indices.lazy.map({ (self.next + $0) % self.surfaces.count })
            .first(where: { !self.surfaces[$0].isInUse })
        else { return nil }
        next = index + 1
        let surface = surfaces[index]

        surface.lock(options: [], seed: nil)
        defer { surface.unlock(options: [], seed: nil) }
        var source = vImage_Buffer(
            data: UnsafeMutableRawPointer(mutating: frame.pixels),
            height: vImagePixelCount(frame.height),
            width: vImagePixelCount(frame.width),
            rowBytes: frame.width * 4
        )
        var destination = vImage_Buffer(
            data: surface.baseAddress,
            height: vImagePixelCount(frame.height),
            width: vImagePixelCount(frame.width),
            rowBytes: surface.bytesPerRow
        )
        // RGBA → BGRA, forcing alpha opaque since the guest framebuffer's alpha is not meaningful.
        let rgbaToBGRA: [UInt8] = [2, 1, 0, 3]
        let opaque: [UInt8] = [0, 0, 0, 255]
        vImagePermuteChannelsWithMaskedInsert_ARGB8888(&source, &destination, rgbaToBGRA, 0x1, opaque, vImage_Flags(kvImageNoFlags))
        return surface
    }

    private static func makeSurface(width: Int, height: Int) -> IOSurface? {
        let surface = IOSurface(properties: [
            .width: width,
            .height: height,
            .bytesPerElement: 4,
            .pixelFormat: kCVPixelFormatType_32BGRA,
        ])
        // Tag as sRGB so Core Animation colour-matches it like the CGImages it replaces.
        if let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)?.copyPropertyList() {
            surface?.setAttachment(colorSpace, forKey: kIOSurfaceColorSpace as String)
        }
        return surface
    }
}
