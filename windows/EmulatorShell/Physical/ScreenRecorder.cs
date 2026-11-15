using System.Runtime.InteropServices;
using System.Threading.Channels;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace EmulatorShell;

/// Writes a device's screen, and its sound when it has some, to an MP4 file with Media Foundation's sink writer
/// (H.264 and AAC).
///
/// The frames are encoded again rather than copied as they come: a device that turns mid-recording starts a new
/// stream at the other size, which one MP4 track cannot hold. The video keeps the size it started at, and a frame
/// of another shape is fitted into it with black bars.
///
/// Frames and sound may come from any thread; they are copied and written in order on the recorder's own thread,
/// and a frame that comes while two are still waiting is dropped rather than holding up the stream.
sealed class ScreenRecorder
{
    private abstract record Item;
    private sealed record Frame(byte[] Pixels, int Width, int Height, long Time) : Item;
    private sealed record Sound(byte[] Samples, long Time) : Item;
    private sealed record End(long Time, TaskCompletionSource<bool> Done) : Item;

    public string Path { get; }
    private readonly int width, height;
    private readonly bool withAudio;
    private readonly Channel<Item> items = Channel.CreateUnbounded<Item>();
    private int framesWaiting;
    private IMFSinkWriter? writer;
    private int videoStream, audioStream = -1;
    /// The time of the first frame, which the file starts at, and of the last frame written.
    private long? start;
    private long lastFrame = long.MinValue;
    private byte[]? lastPixels;

    /// `width` × `height` is the first frame's size; `withAudio` adds a sound track for 48 kHz 16-bit stereo.
    public ScreenRecorder(string path, int width, int height, bool withAudio)
    {
        Path = path;
        // H.264 wants even dimensions.
        this.width = Math.Max(2, width & ~1);
        this.height = Math.Max(2, height & ~1);
        this.withAudio = withAudio;
        // The sink writer is made and used on the recorder's own thread, in the multithreaded apartment: made on the
        // UI thread (a recording started while the device's window shows its screen) and written to from this one,
        // it ended the process a few frames in.
        var thread = new Thread(() =>
        {
            try
            {
                Open();
            }
            catch (Exception error)
            {
                Log.Error($"cannot start recording: {error.Message}");
                writer?.Dispose();
                writer = null;
            }
            WriteLoop();
        })
        { IsBackground = true, Name = "ScreenRecorder" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Open()
    {
        MediaFactory.MFStartup(false).CheckError();
        using var attributes = MediaFactory.MFCreateAttributes(2);
        // Windows' own H.264 encoder rather than the GPU's: NVIDIA's encoder MFT ended the whole process (a fail-fast
        // in the driver, which nothing here can catch) a few frames into a recording, though not in a bare test.
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 0u);
        // Frames come only when the screen changes; throttled, the sound would wait for them and back up.
        attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
        writer = MediaFactory.MFCreateSinkWriterFromURL(Path, null!, attributes);

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            // About 4 bits per pixel per second: ~9 Mbit/s for a 1080 × 2160 phone.
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(width * height * 4));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u /* progressive */);
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(60, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            videoStream = writer.AddStream(output);
        }
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(60, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            // Rows top down.
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));
            writer.SetInputMediaType(videoStream, input, null!);
        }
        if (withAudio)
        {
            try
            {
                using var output = MediaFactory.MFCreateMediaType();
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48_000u);
                output.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
                output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 20_000u);
                audioStream = writer.AddStream(output);
                using var input = MediaFactory.MFCreateMediaType();
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48_000u);
                input.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
                input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
                input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 192_000u);
                writer.SetInputMediaType(audioStream, input, null!);
            }
            catch (SharpGenException error)
            {
                Log.Error($"recording without sound: {error.Message}");
                audioStream = -1;
            }
        }
        writer.BeginWriting();
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// A decoded frame, copied before this returns.
    public void Append(VideoFrame frame)
    {
        if (Interlocked.Increment(ref framesWaiting) > 2)
        {
            Interlocked.Decrement(ref framesWaiting);
            return;
        }
        items.Writer.TryWrite(new Frame((byte[])frame.Pixels.Clone(), frame.Width, frame.Height, frame.Time));
    }

    /// 16-bit interleaved stereo samples at 48 kHz, the first of them at `time`.
    public void AppendAudio(byte[] samples, long time)
    {
        if (withAudio) items.Writer.TryWrite(new Sound(samples, time));
    }

    /// Ends the file at `time` (the last frame lasts until then); true once it is complete with something in it.
    public Task<bool> Finish(long time)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!items.Writer.TryWrite(new End(time, done))) done.TrySetResult(false);
        items.Writer.TryComplete();
        return done.Task;
    }

    private void WriteLoop()
    {
        foreach (var item in Drain())
        {
            try
            {
                switch (item)
                {
                    case Frame frame:
                        Interlocked.Decrement(ref framesWaiting);
                        WriteFrame(frame);
                        break;
                    case Sound sound:
                        WriteSound(sound);
                        break;
                    case End end:
                        end.Done.TrySetResult(Close(end.Time));
                        return;
                }
            }
            catch (Exception error)
            {
                Log.Error($"recording: {error.Message}");
                if (item is End end) end.Done.TrySetResult(false);
            }
        }
    }

    private IEnumerable<Item> Drain()
    {
        var reader = items.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var item)) yield return item;
        }
    }

    private void WriteFrame(Frame frame)
    {
        if (writer == null || frame.Time <= lastFrame) return;
        start ??= frame.Time;
        var pixels = frame.Width == width && frame.Height == height ? frame.Pixels : Fit(frame);
        // A frame lasts until the next; its duration is set once that comes.
        WriteVideo(pixels, frame.Time - start.Value);
        lastFrame = frame.Time;
        lastPixels = pixels;
    }

    private void WriteVideo(byte[] pixels, long time)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(pixels.Length);
        buffer.Lock(out var pointer, out _, out _);
        Marshal.Copy(pixels, 0, pointer, pixels.Length);
        buffer.Unlock();
        buffer.CurrentLength = pixels.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = 166_667;
        writer!.WriteSample(videoStream, sample);
    }

    private void WriteSound(Sound sound)
    {
        if (writer == null || audioStream < 0 || start is not { } first || sound.Time < first || sound.Samples.Length < 4) return;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(sound.Samples.Length);
        buffer.Lock(out var pointer, out _, out _);
        Marshal.Copy(sound.Samples, 0, pointer, sound.Samples.Length);
        buffer.Unlock();
        buffer.CurrentLength = sound.Samples.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = sound.Time - first;
        sample.SampleDuration = sound.Samples.Length / 4 * 10_000_000L / 48_000;
        writer.WriteSample(audioStream, sample);
    }

    private bool Close(long time)
    {
        if (writer == null) return false;
        var wrote = start != null;
        try
        {
            // The last frame again at the end, so it stays on screen until then.
            if (wrote && lastPixels != null && time > lastFrame + 10_000) WriteVideo(lastPixels, time - start!.Value);
            if (wrote) writer.Finalize();
        }
        finally
        {
            writer.Dispose();
            writer = null;
        }
        if (!wrote) try { File.Delete(Path); } catch { }
        return wrote;
    }

    /// The frame scaled to fit the video's size, centred on black.
    private byte[] Fit(Frame frame)
    {
        var output = new byte[width * height * 4];
        for (var i = 3; i < output.Length; i += 4) output[i] = 255;
        var scale = Math.Min((double)width / frame.Width, (double)height / frame.Height);
        int fittedWidth = Math.Max(1, (int)(frame.Width * scale)), fittedHeight = Math.Max(1, (int)(frame.Height * scale));
        int left = (width - fittedWidth) / 2, top = (height - fittedHeight) / 2;
        var source = frame.Pixels;
        Parallel.For(0, fittedHeight, y =>
        {
            var sourceRow = Math.Min(frame.Height - 1, (int)(y / scale)) * frame.Width * 4;
            var targetRow = ((top + y) * width + left) * 4;
            for (var x = 0; x < fittedWidth; x++)
            {
                var from = sourceRow + Math.Min(frame.Width - 1, (int)(x / scale)) * 4;
                var to = targetRow + x * 4;
                output[to] = source[from];
                output[to + 1] = source[from + 1];
                output[to + 2] = source[from + 2];
            }
        });
        return output;
    }

    /// "<name>_2026-11-14_10.32.15.<ext>" on the Desktop, as screenshots are named.
    public static string DesktopPath(string name, string extension)
    {
        var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return System.IO.Path.Combine(desktop, $"{safe}_{DateTime.Now:yyyy-MM-dd_HH.mm.ss}.{extension}");
    }
}
