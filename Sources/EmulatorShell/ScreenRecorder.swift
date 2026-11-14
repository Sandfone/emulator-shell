import Accelerate
import AVFoundation
import CoreVideo

/// Writes a device's screen, and its sound when it has some, to an MP4 file.
///
/// The frames are encoded again (H.264, in hardware) rather than copied as they come: a device that turns
/// mid-recording starts a new stream at the other size, which one MP4 track cannot hold. The video keeps the
/// size it started at, and a frame of another shape is fitted into it with black bars.
///
/// Every call may come from any thread; the work happens in order on the recorder's queue.
final class ScreenRecorder: @unchecked Sendable {
    let url: URL
    private let queue = DispatchQueue(label: "ScreenRecorder")
    private let writer: AVAssetWriter
    private let video: AVAssetWriterInput
    private let adaptor: AVAssetWriterInputPixelBufferAdaptor
    private let audio: AVAssetWriterInput?
    private let canvas: (width: Int, height: Int)
    private var started = false
    private var finished = false
    private var lastVideoTime = CMTime.negativeInfinity
    private var audioFormat: CMAudioFormatDescription?

    /// `size` is the first frame's; `withAudio` adds a sound track for 48 kHz 16-bit stereo samples.
    init(url: URL, size: CGSize, withAudio: Bool) throws {
        self.url = url
        // H.264 wants even dimensions.
        let width = max(2, Int(size.width) & ~1), height = max(2, Int(size.height) & ~1)
        canvas = (width, height)
        writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [
                // About 4 bits per pixel per second: ~9 Mbit/s for a 1080 × 2160 phone.
                AVVideoAverageBitRateKey: width * height * 4,
                AVVideoExpectedSourceFrameRateKey: 60,
                AVVideoProfileLevelKey: AVVideoProfileLevelH264HighAutoLevel,
            ] as [String: Any],
        ])
        video.expectsMediaDataInRealTime = true
        adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: video, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:] as [String: Any],
        ])
        guard writer.canAdd(video) else { throw ShellError("无法写入视频") }
        writer.add(video)
        if withAudio {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 48_000,
                AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: 160_000,
            ])
            input.expectsMediaDataInRealTime = true
            if writer.canAdd(input) {
                writer.add(input)
                audio = input
            } else {
                audio = nil
            }
        } else {
            audio = nil
        }
        guard writer.startWriting() else { throw writer.error ?? ShellError("无法开始写入 \(url.lastPathComponent)") }
    }

    /// A decoded frame (32-bit BGRA), at `time` on the host clock.
    func append(_ frame: CVPixelBuffer, at time: CMTime) {
        queue.async { [self] in
            guard !finished, writer.status == .writing else { return }
            if !started {
                writer.startSession(atSourceTime: time)
                started = true
            }
            guard time > lastVideoTime, video.isReadyForMoreMediaData else { return }
            let buffer: CVPixelBuffer
            if CVPixelBufferGetWidth(frame) == canvas.width, CVPixelBufferGetHeight(frame) == canvas.height,
               CVPixelBufferGetPixelFormatType(frame) == kCVPixelFormatType_32BGRA {
                buffer = frame
            } else {
                guard let fitted = fit(frame) else { return }
                buffer = fitted
            }
            if adaptor.append(buffer, withPresentationTime: time) { lastVideoTime = time }
        }
    }

    /// 16-bit interleaved stereo samples at 48 kHz, the first of them at `time` on the host clock.
    func appendAudio(_ samples: Data, at time: CMTime) {
        queue.async { [self] in
            guard !finished, started, let audio, audio.isReadyForMoreMediaData, time >= .zero,
                  let buffer = makeAudioBuffer(samples, at: time) else { return }
            audio.append(buffer)
        }
    }

    /// Ends the file at `time` (the last frame lasts until then) and returns once it is complete.
    func finish(at time: CMTime) async -> Bool {
        await withCheckedContinuation { continuation in
            queue.async { [self] in
                guard !finished else { return continuation.resume(returning: false) }
                finished = true
                guard started, writer.status == .writing else {
                    writer.cancelWriting()
                    try? FileManager.default.removeItem(at: url)
                    return continuation.resume(returning: false)
                }
                video.markAsFinished()
                audio?.markAsFinished()
                if time > lastVideoTime { writer.endSession(atSourceTime: time) }
                writer.finishWriting { [writer] in
                    if writer.status != .completed { Log.error("recording failed: \(writer.error.map { "\($0)" } ?? "unknown")") }
                    continuation.resume(returning: writer.status == .completed)
                }
            }
        }
    }

    /// The frame scaled to fit the video's size, centred on black.
    private func fit(_ frame: CVPixelBuffer) -> CVPixelBuffer? {
        guard CVPixelBufferGetPixelFormatType(frame) == kCVPixelFormatType_32BGRA,
              let pool = adaptor.pixelBufferPool else { return nil }
        var output: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(nil, pool, &output)
        guard let output else { return nil }
        CVPixelBufferLockBaseAddress(frame, .readOnly)
        CVPixelBufferLockBaseAddress(output, [])
        defer {
            CVPixelBufferUnlockBaseAddress(output, [])
            CVPixelBufferUnlockBaseAddress(frame, .readOnly)
        }
        guard let source = CVPixelBufferGetBaseAddress(frame), let target = CVPixelBufferGetBaseAddress(output) else { return nil }
        let rowBytes = CVPixelBufferGetBytesPerRow(output)
        var whole = vImage_Buffer(data: target, height: vImagePixelCount(canvas.height), width: vImagePixelCount(canvas.width), rowBytes: rowBytes)
        // Opaque black, in the buffer's B, G, R, A order.
        var black: [UInt8] = [0, 0, 0, 255]
        vImageBufferFill_ARGB8888(&whole, &black, vImage_Flags(kvImageNoFlags))
        let sourceWidth = CVPixelBufferGetWidth(frame), sourceHeight = CVPixelBufferGetHeight(frame)
        let scale = min(Double(canvas.width) / Double(sourceWidth), Double(canvas.height) / Double(sourceHeight))
        let width = max(1, Int(Double(sourceWidth) * scale)), height = max(1, Int(Double(sourceHeight) * scale))
        let x = (canvas.width - width) / 2, y = (canvas.height - height) / 2
        var src = vImage_Buffer(data: source, height: vImagePixelCount(sourceHeight), width: vImagePixelCount(sourceWidth),
                                rowBytes: CVPixelBufferGetBytesPerRow(frame))
        var dst = vImage_Buffer(data: target.advanced(by: y * rowBytes + x * 4), height: vImagePixelCount(height),
                                width: vImagePixelCount(width), rowBytes: rowBytes)
        vImageScale_ARGB8888(&src, &dst, nil, vImage_Flags(kvImageHighQualityResampling))
        return output
    }

    private func makeAudioBuffer(_ samples: Data, at time: CMTime) -> CMSampleBuffer? {
        if audioFormat == nil {
            var description = AudioStreamBasicDescription(
                mSampleRate: 48_000, mFormatID: kAudioFormatLinearPCM,
                mFormatFlags: kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
                mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4, mChannelsPerFrame: 2, mBitsPerChannel: 16, mReserved: 0)
            CMAudioFormatDescriptionCreate(allocator: nil, asbd: &description, layoutSize: 0, layout: nil,
                                           magicCookieSize: 0, magicCookie: nil, extensions: nil, formatDescriptionOut: &audioFormat)
        }
        let frames = samples.count / 4
        guard let audioFormat, frames > 0 else { return nil }
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: frames * 4, blockAllocator: nil,
                                                 customBlockSource: nil, offsetToData: 0, dataLength: frames * 4, flags: 0,
                                                 blockBufferOut: &block) == noErr, let block else { return nil }
        samples.withUnsafeBytes { bytes in
            _ = CMBlockBufferReplaceDataBytes(with: bytes.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: frames * 4)
        }
        var buffer: CMSampleBuffer?
        CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator: nil, dataBuffer: block, formatDescription: audioFormat,
                                                             sampleCount: frames, presentationTimeStamp: time,
                                                             packetDescriptions: nil, sampleBufferOut: &buffer)
        return buffer
    }

    /// Now on the host clock, which frames and samples are stamped with.
    static var now: CMTime { CMClockGetTime(CMClockGetHostTimeClock()) }

    /// "<name>_2026-11-14_10.32.15.<ext>" on the Desktop, as screenshots are named.
    static func desktopURL(name: String, extension ext: String) -> URL {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd_HH.mm.ss"
        let safe = name.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        let desktop = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask)[0]
        return desktop.appendingPathComponent("\(safe)_\(formatter.string(from: Date())).\(ext)")
    }
}
