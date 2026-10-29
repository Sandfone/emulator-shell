import AVFoundation

/// Plays the device's sound on the Mac. The emulator runs with its own audio switched off (`-audio none`):
/// its CoreAudio backend can deadlock, the guest starting a sound while it holds the lock CoreAudio's
/// playback thread is waiting for, and the emulator's hang detector then kills it. The sound comes over
/// gRPC instead and is played here.
///
/// Packets arrive about every 10 ms, silent ones too, while the device runs. The engine runs only while
/// there is something to hear, since running audio keeps the Mac from idling.
final class DeviceAudio: @unchecked Sendable {
    static let sampleRate = 48_000

    /// Behind this much (the stream opens with a burst, or the Mac's output stalled), packets are dropped
    /// until the sound catches up, so it never lags the screen by more.
    private static let maxQueued = sampleRate / 10
    /// Laid down before the first packet after a gap, so the next ones arrive before it runs out.
    private static let lead = sampleRate * 40 / 1000
    /// After this long without a sound the guest has stopped playing.
    private static let idle = DispatchTimeInterval.seconds(2)

    private let queue = DispatchQueue(label: "DeviceAudio")
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let format = AVAudioFormat(standardFormatWithSampleRate: Double(sampleRate), channels: 2)!
    /// Frames handed to the player since it last started.
    private var scheduled: AVAudioFramePosition = 0
    private var lastSound = DispatchTime.now()
    private var idleCheck: DispatchWorkItem?
    private var observer: NSObjectProtocol?

    init() {
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: format)
        // A new output device stops the engine; the next packet starts it again on the new one.
        observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine,
                                                          queue: nil) { [weak self] _ in
            self?.queue.async { self?.halt() }
        }
    }

    deinit {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        engine.stop()
    }

    /// Queues 16-bit interleaved stereo samples.
    func play(_ samples: Data) {
        queue.async { self.schedule(samples) }
    }

    func stop() {
        queue.async { self.halt() }
    }

    private func schedule(_ samples: Data) {
        let frames = samples.count / 4
        guard frames > 0 else { return }
        if Self.isSilent(samples) {
            guard engine.isRunning else { return }
        } else {
            lastSound = .now()
        }
        if !engine.isRunning {
            do {
                try engine.start()
            } catch {
                Log.error("audio output failed to start: \(error)")
                return
            }
        }
        if !player.isPlaying {
            player.play()
            scheduled = 0
        }
        let queued = scheduled - played
        if queued > Self.maxQueued { return }
        if queued <= 0, let silence = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(Self.lead)) {
            // Behind already (the start, or a gap): the player's clock has run on, so start from where it is.
            scheduled = played
            silence.frameLength = silence.frameCapacity
            player.scheduleBuffer(silence)
            scheduled += AVAudioFramePosition(Self.lead)
        }
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
              let channels = buffer.floatChannelData else { return }
        buffer.frameLength = AVAudioFrameCount(frames)
        samples.withUnsafeBytes { raw in
            let source = raw.bindMemory(to: Int16.self)
            for frame in 0..<frames {
                channels[0][frame] = Float(Int16(littleEndian: source[2 * frame])) / 32768
                channels[1][frame] = Float(Int16(littleEndian: source[2 * frame + 1])) / 32768
            }
        }
        player.scheduleBuffer(buffer)
        scheduled += AVAudioFramePosition(frames)
        watchForIdle()
    }

    /// Frames the player has played since it started.
    private var played: AVAudioFramePosition {
        guard let now = player.lastRenderTime, let time = player.playerTime(forNodeTime: now) else { return 0 }
        return time.sampleTime
    }

    private func watchForIdle() {
        guard idleCheck == nil else { return }
        let check = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.idleCheck = nil
            if DispatchTime.now() >= self.lastSound + Self.idle {
                self.halt()
            } else {
                self.watchForIdle()
            }
        }
        idleCheck = check
        queue.asyncAfter(deadline: lastSound + Self.idle, execute: check)
    }

    /// Nothing but the faintest noise, which some players leave in their silence.
    private static func isSilent(_ samples: Data) -> Bool {
        samples.withUnsafeBytes { raw in
            raw.bindMemory(to: Int16.self).allSatisfy { abs(Int(Int16(littleEndian: $0))) <= 4 }
        }
    }

    private func halt() {
        idleCheck?.cancel()
        idleCheck = nil
        player.stop()
        engine.stop()
        scheduled = 0
    }
}
