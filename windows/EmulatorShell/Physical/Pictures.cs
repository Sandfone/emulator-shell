using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EmulatorShell;

/// PNG in and out of `VideoFrame`s (BGRA), with Windows' own codecs.
static class Pictures
{
    public static async Task<byte[]> EncodePng(VideoFrame frame)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96,
                             frame.Pixels.AsSpan(0, frame.Width * frame.Height * 4).ToArray());
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    /// The picture, shrunk so its long side is at most `maxSide` pixels.
    public static async Task<VideoFrame?> DecodePng(byte[] png, int maxSide)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1, (double)maxSide / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                                                   ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new VideoFrame(data.DetachPixelData(), (int)width, (int)height, HostClock.Now);
    }

    /// Whether every pixel of a 16 × 16 reduction is all but black, as a screen that forbids capture comes through.
    public static async Task<bool> IsBlack(byte[] png)
    {
        if (await DecodePng(png, 16) is not { } small) return false;
        for (var i = 0; i < small.Width * small.Height * 4; i += 4)
        {
            if (Math.Max(small.Pixels[i], Math.Max(small.Pixels[i + 1], small.Pixels[i + 2])) >= 6) return false;
        }
        return true;
    }

    /// A copy of the frame shrunk so its long side is at most `maxSide` pixels (nearest pixel, for previews).
    public static VideoFrame Shrunk(VideoFrame frame, int maxSide)
    {
        var scale = Math.Min(1, (double)maxSide / Math.Max(frame.Width, frame.Height));
        int width = Math.Max(1, (int)(frame.Width * scale)), height = Math.Max(1, (int)(frame.Height * scale));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = Math.Min(frame.Height - 1, (int)(y / scale)) * frame.Width;
            for (var x = 0; x < width; x++)
            {
                var from = (sourceRow + Math.Min(frame.Width - 1, (int)(x / scale))) * 4;
                var to = (y * width + x) * 4;
                pixels[to] = frame.Pixels[from];
                pixels[to + 1] = frame.Pixels[from + 1];
                pixels[to + 2] = frame.Pixels[from + 2];
                pixels[to + 3] = 255;
            }
        }
        return new VideoFrame(pixels, width, height, frame.Time);
    }

    /// The frame as an image source for XAML.
    public static WriteableBitmap Bitmap(VideoFrame frame)
    {
        var bitmap = new WriteableBitmap(frame.Width, frame.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(frame.Pixels, 0, frame.Width * frame.Height * 4);
        }
        bitmap.Invalidate();
        return bitmap;
    }
}
