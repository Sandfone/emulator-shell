using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EmulatorShell;

/// Plays a physical device's sound (48 kHz 16-bit stereo, as scrcpy sends it raw) on the default output through
/// WASAPI. Samples come from the stream's audio thread; playback starts with the first ones.
sealed class DeviceAudio : IDisposable
{
    /// More than this queued, e.g. after the window was stuck for a moment, is dropped so the sound keeps up.
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(200);

    private readonly object gate = new();
    private readonly BufferedWaveProvider buffer = new(new WaveFormat(48_000, 16, 2))
    {
        BufferDuration = TimeSpan.FromSeconds(2),
        DiscardOnBufferOverflow = true,
        ReadFully = true,
    };
    private WasapiOut? output;
    private bool disposed;

    public void Play(byte[] samples)
    {
        lock (gate)
        {
            if (disposed) return;
            if (buffer.BufferedDuration > MaxDelay) buffer.ClearBuffer();
            buffer.AddSamples(samples, 0, samples.Length);
            if (output != null) return;
            try
            {
                output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 60);
                output.Init(buffer);
                output.Play();
            }
            catch (Exception error)
            {
                Log.Error($"cannot play the device's sound: {error.Message}");
                output?.Dispose();
                output = null;
                disposed = true;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            output?.Stop();
            output?.Dispose();
            output = null;
        }
    }
}
