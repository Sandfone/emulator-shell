using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace EmulatorShell;

/// Decodes scrcpy's H.264 stream with Windows' own decoder (the Media Foundation H.264 decoder MFT, in low-latency
/// mode so it holds no frames back) into NV12, which is turned into BGRA here, rows in parallel, into the stream's
/// buffers. Runs on the stream's video thread.
sealed class H264Decoder : IDisposable
{
    private static readonly Guid DecoderClsid = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Lazy<bool> Started = new(() =>
    {
        MediaFactory.MFStartup(false).CheckError();
        return true;
    });

    private readonly ScrcpySinks sinks;
    private readonly IMFTransform decoder;
    private readonly HostClockMapper clock = new();
    /// The SPS and PPS of a config packet, sent along with the next frame as scrcpy's own client does.
    private byte[]? config;
    /// The decoded picture: its rows and their padding, the part shown, and how its colours are coded.
    private (int Width, int Height, int Stride, int VisibleWidth, int VisibleHeight, bool Bt709, bool FullRange) format;
    private bool disposed;

    public H264Decoder(ScrcpySinks sinks)
    {
        _ = Started.Value;
        this.sinks = sinks;
        decoder = new IMFTransform(IntPtr.Zero);
        ComUtilities.CreateComInstance(DecoderClsid, ComContext.InprocServer, typeof(IMFTransform).GUID, decoder);
        decoder.Attributes.Set(LowLatency, 1u);
        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        decoder.SetInputType(0, input, 0);
        ChooseOutput();
        decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    /// A new encoder session (the device turned): frames still inside the decoder belong to the old one.
    public void Reset()
    {
        clock.Reset();
        try { decoder.ProcessMessage(TMessageType.MessageCommandFlush, 0); } catch (SharpGenException) { }
    }

    /// A config packet: the SPS and PPS, Annex B.
    public void Configure(byte[] data) => config = data;

    public void Decode(byte[] data, long pts)
    {
        if (config != null)
        {
            data = [.. config, .. data];
            config = null;
        }
        using var sample = MediaFactory.MFCreateSample();
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length))
        {
            buffer.Lock(out var pointer, out _, out _);
            Marshal.Copy(data, 0, pointer, data.Length);
            buffer.Unlock();
            buffer.CurrentLength = data.Length;
            sample.AddBuffer(buffer);
        }
        sample.SampleTime = pts * 10;
        try
        {
            decoder.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException error) when (error.ResultCode.Code == unchecked((int)0xC00D36B5))
        {
            // MF_E_NOTACCEPTING: output waiting first.
            Drain();
            decoder.ProcessInput(0, sample, 0);
        }
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            var info = decoder.GetOutputStreamInfo(0);
            var provides = ((OutputStreamInfoFlags)info.Flags & (OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
            IMFSample? given = null;
            if (!provides)
            {
                given = MediaFactory.MFCreateSample();
                using var buffer = MediaFactory.MFCreateMemoryBuffer(info.Size);
                given.AddBuffer(buffer);
            }
            var output = new OutputDataBuffer { StreamID = 0, Sample = given };
            var result = decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            try
            {
                if (result == ResultCode.TransformNeedMoreInput) return;
                if (result == ResultCode.TransformStreamChange)
                {
                    ChooseOutput();
                    continue;
                }
                if (result.Failure)
                {
                    Log.Debug($"H.264 decoder: 0x{result.Code:X8}");
                    return;
                }
                if (output.Sample is { } decoded) Deliver(decoded);
            }
            finally
            {
                if (output.Sample != null && !ReferenceEquals(output.Sample, given)) output.Sample.Dispose();
                given?.Dispose();
            }
        }
    }

    /// NV12 out, at whatever size the stream now has.
    private void ChooseOutput()
    {
        for (var i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                type = decoder.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                throw new ShellException("H.264 解码器不能输出 NV12");
            }
            using (type)
            {
                if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                decoder.SetOutputType(0, type, 0);
                ReadFormat(type);
                return;
            }
        }
    }

    private void ReadFormat(IMFMediaType type)
    {
        var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        int width = (int)(size >> 32), height = (int)(size & 0xFFFF_FFFF);
        var stride = type.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var s).Success ? (int)s : width;
        int visibleWidth = width, visibleHeight = height;
        // MFVideoArea: OffsetX and OffsetY (16.16 fixed point), then the area's width and height.
        if (type.GetBlobSize(MediaTypeAttributeKeys.MinimumDisplayAperture, out var blobSize).Success && blobSize >= 16)
        {
            var area = new byte[blobSize];
            type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture, area);
            visibleWidth = Math.Min(width, BitConverter.ToInt32(area, 8));
            visibleHeight = Math.Min(height, BitConverter.ToInt32(area, 12));
        }
        var matrix = type.GetUInt32(MediaTypeAttributeKeys.YuvMatrix, out var m).Success ? m : 0;
        var range = type.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange, out var r).Success ? r : 0;
        // MFVideoTransferMatrix_BT709 is 1; MFNominalRange_0_255 is 1.
        format = (width, height, Math.Abs(stride), visibleWidth, visibleHeight, matrix == 1, range == 1);
        Log.Debug($"H.264 output {width}×{height} (shown {visibleWidth}×{visibleHeight}), stride {stride}, {(matrix == 1 ? "BT.709" : "BT.601")}");
    }

    private unsafe void Deliver(IMFSample sample)
    {
        var time = clock.HostTime(sample.SampleTime / 10);
        using var buffer = sample.ConvertToContiguousBuffer();
        var (width, height, stride, shownWidth, shownHeight, bt709, fullRange) = format;
        if (shownWidth <= 0 || shownHeight <= 0) return;
        var pixels = sinks.Rent(shownWidth * shownHeight * 4);
        using var planes = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        nint scan0;
        int pitch;
        if (planes != null)
        {
            planes.Lock2D(out scan0, out pitch);
        }
        else
        {
            buffer.Lock(out scan0, out _, out _);
            pitch = stride;
        }
        try
        {
            ConvertNv12((byte*)scan0, (byte*)scan0 + (long)pitch * height, pitch, pixels, shownWidth, shownHeight, bt709, fullRange);
        }
        finally
        {
            if (planes != null) planes.Unlock2D(); else buffer.Unlock();
        }
        sinks.Deliver(new VideoFrame(pixels, shownWidth, shownHeight, time));
    }

    /// NV12 (a Y plane, then interleaved U and V at half resolution) to BGRA, in fixed point with 10 fraction bits.
    private static unsafe void ConvertNv12(byte* yPlane, byte* uvPlane, int pitch, byte[] output, int width, int height, bool bt709, bool fullRange)
    {
        // Y scale and offset, and the colour factors for R from V, G from U and V, and B from U.
        int yScale = fullRange ? 1024 : 1192, yOffset = fullRange ? 0 : 16;
        int rv, gu, gv, bu;
        if (bt709)
        {
            (rv, gu, gv, bu) = fullRange ? (1613, 192, 479, 1900) : (1836, 218, 546, 2163);
        }
        else
        {
            (rv, gu, gv, bu) = fullRange ? (1436, 352, 731, 1815) : (1634, 401, 833, 2066);
        }
        fixed (byte* target = output)
        {
            var targetAddress = (nint)target;
            Parallel.For(0, (height + 1) / 2, pair =>
            {
                var bgra = (byte*)targetAddress;
                var uvRow = uvPlane + (long)pair * pitch;
                for (var row = pair * 2; row < Math.Min(pair * 2 + 2, height); row++)
                {
                    var yRow = yPlane + (long)row * pitch;
                    var pixel = bgra + (long)row * width * 4;
                    for (var x = 0; x < width; x++)
                    {
                        var y = (yRow[x] - yOffset) * yScale;
                        var u = uvRow[x & ~1] - 128;
                        var v = uvRow[(x & ~1) + 1] - 128;
                        var r = (y + rv * v + 512) >> 10;
                        var g = (y - gu * u - gv * v + 512) >> 10;
                        var b = (y + bu * u + 512) >> 10;
                        pixel[0] = (byte)Math.Clamp(b, 0, 255);
                        pixel[1] = (byte)Math.Clamp(g, 0, 255);
                        pixel[2] = (byte)Math.Clamp(r, 0, 255);
                        pixel[3] = 255;
                        pixel += 4;
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { decoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0); } catch (SharpGenException) { }
        decoder.Dispose();
    }
}
