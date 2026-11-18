using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace EmulatorShell;

/// A decoded frame: 32-bit BGRA pixels, top-down rows `Width * 4` bytes apart, and when it arrived on the host
/// clock (`HostClock`). The pixels belong to the stream: a sink copies what it keeps before returning.
sealed class VideoFrame(byte[] pixels, int width, int height, long time)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public long Time { get; } = time;
}

/// Time on a steady clock in 100 ns units, which frames, sound and recordings are stamped with.
static class HostClock
{
    public static long Now => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
}

/// A physical device's screen, sound and input through scrcpy-server (`vendor/scrcpy`), which runs on the device
/// with `app_process` as the shell user: no app is installed.
///
/// One adb forward tunnel carries three sockets, connected in this order: video (H.264 from the device's encoder,
/// decoded here with Media Foundation), audio (raw 48 kHz 16-bit stereo, Android 11+) and control (input from here,
/// the device's clipboard back). All integers on them are big-endian. The protocol is scrcpy 5.0.1's; the server
/// refuses a client of another version.
///
/// The stream outlives its connections: `Restart` reconnects with other options, and the sinks (a device window,
/// a recording) keep receiving. Its events come on the UI thread.
sealed class ScrcpyStream
{
    public const string ServerVersion = "5.0.1";

    public sealed record Options
    {
        /// The video's long side at most, in pixels; 0 for the device's own size.
        public int MaxSize { get; init; } = 1920;
        public int BitRate { get; init; } = 8_000_000;
        public bool Audio { get; init; } = true;
        public bool StayAwake { get; init; } = true;
        public bool ShowTouches { get; init; }
        public bool ClipboardSync { get; init; } = true;
        /// Turns the screen on as the stream starts.
        public bool PowerOn { get; init; } = true;
    }

    public enum Phase { Connecting, Streaming, Ended }

    /// `Message` says why a stream ended other than by being stopped.
    public sealed record State(Phase Phase, string? Message = null)
    {
        public static readonly State Connecting = new(Phase.Connecting);
        public static readonly State Streaming = new(Phase.Streaming);
    }

    public string Serial { get; }
    public Options Settings { get; private set; }
    private State state = State.Connecting;
    public State CurrentState
    {
        get => state;
        private set
        {
            state = value;
            StateChanged?.Invoke(value);
        }
    }
    public event Action<State>? StateChanged;
    /// The video's size, as the latest session reported it; touch positions are given in it.
    public Size VideoSize { get; private set; }
    public event Action<Size>? VideoSizeChanged;
    /// The device's clipboard changed (with clipboard sync on).
    public event Action<string>? Clipboard;
    /// Whether the device sends its sound: Android 11 and later, with `Options.Audio`.
    public bool HasAudio { get; private set; }
    public event Action<bool>? AudioAvailableChanged;
    /// The device refused an input event from adb (MIUI and HyperOS until "USB debugging (Security settings)" is on).
    public event Action? InputRefused;

    private ScrcpyConnection? connection;
    private readonly ScrcpySinks sinks = new();
    /// What to call when the device acknowledges a clipboard, by the sequence number sent with it.
    private readonly Dictionary<ulong, Action> clipboardAcks = [];
    private ulong clipboardSequence;
    private readonly DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();

    public ScrcpyStream(string serial, Options options)
    {
        Serial = serial;
        Settings = options;
    }

    public bool IsStreaming => CurrentState.Phase == Phase.Streaming;

    public void Start()
    {
        if (connection != null) return;
        CurrentState = State.Connecting;
        var started = new ScrcpyConnection(Serial, Settings, sinks);
        started.Event += e => queue.TryEnqueue(() =>
        {
            if (connection == started) Handle(e);
        });
        connection = started;
        started.Start();
    }

    public void Stop()
    {
        connection?.Stop();
        connection = null;
        if (CurrentState.Phase != Phase.Ended) CurrentState = new State(Phase.Ended);
    }

    /// Reconnects with other options (a moment without frames); sinks stay.
    public void Restart(Options options)
    {
        Settings = options;
        connection?.Stop();
        connection = null;
        Start();
    }

    private void Handle(ScrcpyConnection.Message message)
    {
        switch (message)
        {
            case ScrcpyConnection.Message.Streaming:
                CurrentState = State.Streaming;
                break;
            case ScrcpyConnection.Message.VideoSize size:
                VideoSize = size.Size;
                VideoSizeChanged?.Invoke(size.Size);
                break;
            case ScrcpyConnection.Message.Audio audio:
                HasAudio = audio.Available;
                AudioAvailableChanged?.Invoke(audio.Available);
                break;
            case ScrcpyConnection.Message.ClipboardText clipboard:
                Clipboard?.Invoke(clipboard.Text);
                break;
            case ScrcpyConnection.Message.ClipboardAck ack:
                if (clipboardAcks.Remove(ack.Sequence, out var acknowledged)) acknowledged();
                break;
            case ScrcpyConnection.Message.InputRefused:
                InputRefused?.Invoke();
                break;
            case ScrcpyConnection.Message.Ended ended:
                connection = null;
                HasAudio = false;
                clipboardAcks.Clear();
                CurrentState = new State(Phase.Ended, ended.Reason);
                break;
        }
    }

    // Sinks

    /// Receives each decoded frame on the stream's own thread.
    public void AddFrameSink(object key, Action<VideoFrame> sink) => sinks.SetFrame(key, sink);

    /// Receives the sound as 16-bit interleaved stereo at 48 kHz, with the time of its first sample.
    public void AddAudioSink(object key, Action<byte[], long> sink) => sinks.SetAudio(key, sink);

    public void RemoveSinks(object key) => sinks.Remove(key);

    /// A copy of the latest frame, for the device manager's preview and screenshots.
    public VideoFrame? LastFrame() => sinks.LastFrameCopy();

    // Control

    public enum KeyAction : byte { Down = 0, Up = 1 }
    public enum TouchAction : byte { Down = 0, Up = 1, Move = 2 }

    /// The finger a single touch is; scrcpy's "generic finger".
    public const ulong FingerPointer = 0xFFFF_FFFF_FFFF_FFFE;
    /// The mirrored second finger of a pinch.
    public const ulong VirtualFingerPointer = 0xFFFF_FFFF_FFFF_FFFD;

    public void InjectKey(int keycode, KeyAction action, int metaState = 0)
    {
        var message = new ControlMessage(0);
        message.U8((byte)action);
        message.I32(keycode);
        message.U32(0);
        message.I32(metaState);
        Send(message);
    }

    /// Text the device types as if from a keyboard; ASCII only, 300 bytes at most per message.
    public void InjectText(string text)
    {
        for (var i = 0; i < text.Length; i += 300)
        {
            var message = new ControlMessage(1);
            message.String(text.Substring(i, Math.Min(300, text.Length - i)));
            Send(message);
        }
    }

    /// A touch at `point`, in the current video's pixels.
    public void InjectTouch(TouchAction action, Point point, ulong pointer = FingerPointer)
    {
        if (Position(point) is not { } position) return;
        var message = new ControlMessage(2);
        message.U8((byte)action);
        message.U64(pointer);
        message.Bytes(position);
        message.U16(action == TouchAction.Up ? (ushort)0 : (ushort)0xFFFF);
        message.I32(0);
        message.I32(0);
        Send(message);
    }

    /// Scrolling at `point` by wheel ticks (positive: right / up).
    public void InjectScroll(Point point, double horizontal, double vertical)
    {
        if (Position(point) is not { } position) return;
        static ushort Fixed(double value)
        {
            var clamped = Math.Clamp(value / 16, -1, 1);
            return unchecked((ushort)(short)Math.Clamp((int)(clamped * 32768), short.MinValue, short.MaxValue));
        }
        var message = new ControlMessage(3);
        message.Bytes(position);
        message.U16(Fixed(horizontal));
        message.U16(Fixed(vertical));
        message.I32(0);
        Send(message);
    }

    /// Sets the device's clipboard; `paste` also pastes it where the cursor is (Android 7+).
    public void SetClipboard(string text, bool paste) => SetClipboard(text, paste, 0);

    /// Sets the device's clipboard and calls `acknowledged` once the device has; not if the stream ends first.
    public void SetClipboard(string text, Action acknowledged)
    {
        clipboardSequence++;
        clipboardAcks[clipboardSequence] = acknowledged;
        SetClipboard(text, false, clipboardSequence);
    }

    /// A non-zero `sequence` asks the device to acknowledge it.
    private void SetClipboard(string text, bool paste, ulong sequence)
    {
        var message = new ControlMessage(9);
        message.U64(sequence);
        message.U8(paste ? (byte)1 : (byte)0);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 262_130) bytes = bytes[..262_130];
        message.U32((uint)bytes.Length);
        message.Bytes(bytes);
        Send(message);
    }

    /// Turns the device's own display off or on; the stream goes on either way.
    public void SetDisplayPower(bool on)
    {
        var message = new ControlMessage(10);
        message.U8(on ? (byte)1 : (byte)0);
        Send(message);
    }

    private byte[]? Position(Point point)
    {
        if (VideoSize.Width <= 0) return null;
        var message = new ControlMessage(0);
        message.Clear();
        message.I32((int)Math.Floor(point.X));
        message.I32((int)Math.Floor(point.Y));
        message.U16((ushort)VideoSize.Width);
        message.U16((ushort)VideoSize.Height);
        return message.ToArray();
    }

    private void Send(ControlMessage message) => connection?.Send(message.ToArray());
}

/// A control message, built field by field in big-endian order.
sealed class ControlMessage
{
    private readonly List<byte> data;

    public ControlMessage(byte type) => data = [type];

    public void Clear() => data.Clear();
    public void U8(byte value) => data.Add(value);
    public void U16(ushort value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, value); data.AddRange(b); }
    public void U32(uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); data.AddRange(b); }
    public void I32(int value) => U32(unchecked((uint)value));
    public void U64(ulong value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, value); data.AddRange(b); }
    public void Bytes(byte[] value) => data.AddRange(value);
    public void String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        U32((uint)bytes.Length);
        data.AddRange(bytes);
    }
    public byte[] ToArray() => [.. data];
}

/// Where decoded frames and sound go; read from the stream's threads, changed from the UI thread. It also owns
/// the buffers frames are decoded into, so that the latest one is never written while it is being copied.
sealed class ScrcpySinks
{
    private readonly object gate = new();
    private readonly Dictionary<object, Action<VideoFrame>> frames = [];
    private readonly Dictionary<object, Action<byte[], long>> audio = [];
    private readonly byte[]?[] buffers = new byte[3][];
    private VideoFrame? last;

    public void SetFrame(object key, Action<VideoFrame> sink) { lock (gate) frames[key] = sink; }
    public void SetAudio(object key, Action<byte[], long> sink) { lock (gate) audio[key] = sink; }
    public void Remove(object key) { lock (gate) { frames.Remove(key); audio.Remove(key); } }

    /// A buffer of `length` bytes to decode the next frame into: never the one the latest frame is in.
    public byte[] Rent(int length)
    {
        lock (gate)
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] is { } buffer && buffer.Length == length && !ReferenceEquals(buffer, last?.Pixels))
                {
                    // Rotate so the same one is not handed out twice in a row.
                    (buffers[i], buffers[^1]) = (buffers[^1], buffers[i]);
                    return buffers[^1]!;
                }
            }
            var fresh = new byte[length];
            var slot = Array.FindIndex(buffers, b => b == null || b.Length != length);
            if (slot < 0) slot = Array.FindIndex(buffers, b => !ReferenceEquals(b, last?.Pixels));
            buffers[slot] = fresh;
            return fresh;
        }
    }

    public void Deliver(VideoFrame frame)
    {
        Action<VideoFrame>[] targets;
        lock (gate)
        {
            last = frame;
            targets = [.. frames.Values];
        }
        foreach (var target in targets) target(frame);
    }

    public void DeliverAudio(byte[] samples, long time)
    {
        Action<byte[], long>[] targets;
        lock (gate) targets = [.. audio.Values];
        foreach (var target in targets) target(samples, time);
    }

    public VideoFrame? LastFrameCopy()
    {
        lock (gate)
        {
            return last == null ? null : new VideoFrame((byte[])last.Pixels.Clone(), last.Width, last.Height, last.Time);
        }
    }
}

/// One run of scrcpy-server: pushing it, starting it, its sockets and the threads reading them.
sealed class ScrcpyConnection(string serial, ScrcpyStream.Options options, ScrcpySinks sinks)
{
    public abstract record Message
    {
        public sealed record Streaming : Message;
        public sealed record VideoSize(Size Size) : Message;
        public sealed record Audio(bool Available) : Message;
        public sealed record ClipboardText(string Text) : Message;
        public sealed record ClipboardAck(ulong Sequence) : Message;
        public sealed record InputRefused : Message;
        public sealed record Ended(string? Reason) : Message;
    }

    public event Action<Message>? Event;

    private readonly object gate = new();
    private bool stopped;
    private bool ended;
    private Process? server;
    private readonly List<Socket> sockets = [];
    private NetworkStream? control;
    private readonly object controlGate = new();
    /// The server's last log lines, for the message when it fails.
    private readonly Queue<string> serverLog = new();
    private bool inputRefused;

    public void Start()
    {
        new Thread(() =>
        {
            try
            {
                Run();
            }
            catch (Exception error)
            {
                Finish(IsStopped ? null : error.Message);
            }
        })
        { IsBackground = true, Name = $"scrcpy {serial}" }.Start();
    }

    public void Stop()
    {
        Socket[] open;
        Process? running;
        lock (gate)
        {
            stopped = true;
            open = [.. sockets];
            running = server;
        }
        // Closing the sockets makes the server exit; one that does not (a sleeping device) is killed.
        foreach (var socket in open) ShutDown(socket);
        if (running != null) _ = Task.Delay(1000).ContinueWith(_ => Kill(running));
    }

    public void Send(byte[] data)
    {
        // Writes are short and ordered; a lock keeps two messages from interleaving.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (controlGate)
            {
                NetworkStream? stream;
                lock (gate) stream = control;
                if (stream == null) return;
                try { stream.Write(data); } catch (Exception error) { Log.Debug($"scrcpy control write failed: {error.Message}"); }
            }
        });
    }

    private bool IsStopped
    {
        get { lock (gate) return stopped; }
    }

    private void Finish(string? message)
    {
        Socket[] open;
        Process? running;
        string[] log;
        lock (gate)
        {
            if (ended) return;
            ended = true;
            stopped = true;
            open = [.. sockets];
            running = server;
            log = [.. serverLog];
        }
        foreach (var socket in open) ShutDown(socket);
        if (running != null) _ = Task.Delay(1000).ContinueWith(_ => Kill(running));
        // The server's own error says more than a closed socket; so does what the runtime prints when it kills the
        // server ("stack corruption detected (-fstack-protector)"), which is not one of the server's own lines.
        var serverError = log.LastOrDefault(l => l.Contains("ERROR") || l.Contains("Exception"))
            ?? log.FirstOrDefault(l => !l.StartsWith("[server]") && !string.IsNullOrWhiteSpace(l));
        var reason = message == null ? null : serverError ?? message;
        if (reason != null) Log.Error($"scrcpy {serial}: {reason}");
        Event?.Invoke(new Message.Ended(reason));
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); } catch { }
    }

    private static void ShutDown(Socket socket)
    {
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Close(); } catch { }
    }

    private void Run()
    {
        if (ServerJar is not { } jar) throw new ShellException("找不到 scrcpy-server（应在 App 的文件夹或源码的 vendor\\scrcpy 里）");
        const string remote = "/data/local/tmp/scrcpy-server.jar";
        // The server deletes its jar as it starts (`cleanup`), so it is pushed every time.
        AdbSync(["push", jar, remote], 30);
        if (IsStopped) return;

        var scid = Random.Shared.Next(0, 0x7FFF_FFFF).ToString("x8");
        var port = Forward($"scrcpy_{scid}");
        try
        {
            // Only what differs from the server's defaults (H.264, sound and control on, clipboard sync and power-on
            // on, stay-awake and touches off), to keep its command line short: One UI's media framework reads the
            // process's command line into a fixed buffer as it sets up a video encoder, and from 265 bytes on that
            // aborts the server with "stack corruption detected" (Galaxy S10+, Android 12). With every option
            // spelled out the line was 250 bytes without sound and exactly 265 with it; this way it is 226 at most.
            var arguments = new List<string>
            {
                $"CLASSPATH={remote}", "app_process", "/", "com.genymobile.scrcpy.Server", ScrcpyStream.ServerVersion,
                $"scid={scid}", "log_level=info", "tunnel_forward=true",
                $"max_size={options.MaxSize}", $"video_bit_rate={options.BitRate}",
                options.Audio ? "audio_codec=raw" : "audio=false",
            };
            if (options.StayAwake) arguments.Add("stay_awake=true");
            if (options.ShowTouches) arguments.Add("show_touches=true");
            if (!options.ClipboardSync) arguments.Add("clipboard_autosync=false");
            if (!options.PowerOn) arguments.Add("power_on=false");
            LaunchServer(arguments);

            // adb accepts the connection even before the server listens; the server's first byte says it does.
            Socket? video = null;
            for (var i = 0; i < 100 && video == null; i++)
            {
                if (IsStopped) return;
                try
                {
                    var socket = Connect(port);
                    var first = new byte[1];
                    if (socket.Receive(first) == 1)
                    {
                        video = socket;
                    }
                    else
                    {
                        socket.Close();
                        Thread.Sleep(100);
                    }
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }
            if (video == null) throw new ShellException("scrcpy-server 没有响应");
            Register(video);
            var audio = options.Audio ? Connect(port) : null;
            if (audio != null) Register(audio);
            var controlSocket = Connect(port);
            controlSocket.NoDelay = true;
            Register(controlSocket);
            lock (gate) control = new NetworkStream(controlSocket, ownsSocket: false);
        }
        finally
        {
            // Every socket is connected, or the attempt failed: the tunnel is no longer needed.
            try { AdbSync(["forward", "--remove", $"tcp:{port}"], 5); } catch { }
        }

        Socket[] connected;
        lock (gate) connected = [.. sockets];
        var videoStream = new NetworkStream(connected[0], ownsSocket: false);
        var name = Read(videoStream, 64);
        Log.Info($"scrcpy {serial}: streaming {Encoding.UTF8.GetString(name).TrimEnd('\0')}");
        Event?.Invoke(new Message.Streaming());

        if (options.Audio)
        {
            var audioStream = new NetworkStream(connected[1], ownsSocket: false);
            new Thread(() => ReadAudio(audioStream)) { IsBackground = true, Name = $"scrcpy audio {serial}" }.Start();
        }
        var controlStream = new NetworkStream(connected[^1], ownsSocket: false);
        new Thread(() => ReadDeviceMessages(controlStream)) { IsBackground = true, Name = $"scrcpy control {serial}" }.Start();
        ReadVideo(videoStream);
    }

    private void Register(Socket socket)
    {
        bool isStopped;
        lock (gate)
        {
            sockets.Add(socket);
            isStopped = stopped;
        }
        if (isStopped) ShutDown(socket);
    }

    private static Socket Connect(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(IPAddress.Loopback, port);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static byte[] Read(Stream stream, int count)
    {
        var data = new byte[count];
        stream.ReadExactly(data);
        return data;
    }

    private static uint ReadU32(Stream stream) => BinaryPrimitives.ReadUInt32BigEndian(Read(stream, 4));

    // Video

    private void ReadVideo(Stream stream)
    {
        var codec = ReadU32(stream);
        if (codec != 0x6832_3634) throw new ShellException($"视频编码不是 H.264（{codec:x}）");
        using var decoder = new H264Decoder(sinks);
        var header = new byte[12];
        while (true)
        {
            stream.ReadExactly(header);
            var first = BinaryPrimitives.ReadUInt32BigEndian(header);
            if ((first & 0x8000_0000) != 0)
            {
                // A new session: the encoder restarted, e.g. the device turned; its size comes with it.
                var size = new Size(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)), BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
                decoder.Reset();
                Event?.Invoke(new Message.VideoSize(size));
                continue;
            }
            var flags = BinaryPrimitives.ReadUInt64BigEndian(header);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            var payload = Read(stream, length);
            if ((flags & (1UL << 62)) != 0) decoder.Configure(payload);
            else decoder.Decode(payload, (long)(flags & ((1UL << 61) - 1)));
        }
    }

    // Audio

    private void ReadAudio(Stream stream)
    {
        try
        {
            var codec = ReadU32(stream);
            if (codec != 0x0072_6177)
            {
                // 0: the device cannot capture its sound (Android 10 or older); 1: it failed to.
                Event?.Invoke(new Message.Audio(false));
                return;
            }
            Event?.Invoke(new Message.Audio(true));
            var clock = new HostClockMapper();
            var header = new byte[12];
            while (true)
            {
                stream.ReadExactly(header);
                var flags = BinaryPrimitives.ReadUInt64BigEndian(header);
                var payload = Read(stream, (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
                if ((flags & (1UL << 62)) != 0) continue;
                sinks.DeliverAudio(payload, clock.HostTime((long)(flags & ((1UL << 61) - 1))));
            }
        }
        catch
        {
            // The audio socket closing is not fatal; the video ends the connection.
            Event?.Invoke(new Message.Audio(false));
        }
    }

    // Device messages

    private void ReadDeviceMessages(Stream stream)
    {
        try
        {
            while (true)
            {
                var type = Read(stream, 1)[0];
                switch (type)
                {
                    case 0:
                        var length = (int)ReadU32(stream);
                        Event?.Invoke(new Message.ClipboardText(Encoding.UTF8.GetString(Read(stream, length))));
                        break;
                    case 1:
                        Event?.Invoke(new Message.ClipboardAck(BinaryPrimitives.ReadUInt64BigEndian(Read(stream, 8))));
                        break;
                    case 2:
                        Read(stream, 2);
                        var size = BinaryPrimitives.ReadUInt16BigEndian(Read(stream, 2));
                        Read(stream, size);
                        break;
                    default:
                        throw new ShellException($"unknown device message {type}");
                }
            }
        }
        catch
        {
            Finish(IsStopped ? null : "控制通道断开");
        }
    }

    // adb

    private void LaunchServer(List<string> arguments)
    {
        var start = new ProcessStartInfo(Adb.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-s", serial, "shell" }.Concat(arguments)) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void Line(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Log.Debug($"scrcpy-server {serial}: {line}");
            bool refused;
            lock (gate)
            {
                serverLog.Enqueue(line);
                if (serverLog.Count > 20) serverLog.Dequeue();
                // The server says why an injected event failed; some devices set no property that would say so first.
                refused = !inputRefused && (line.Contains("INJECT_EVENTS") || line.Contains("USB debugging (Security Settings)"));
                if (refused) inputRefused = true;
            }
            if (refused) Event?.Invoke(new Message.InputRefused());
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Exited += (_, _) => Finish(IsStopped ? null : "scrcpy-server 退出了");
        process.Start();
        ChildProcesses.Adopt(process);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (gate) server = process;
    }

    /// `adb forward` on the first free port from 27183, scrcpy's range.
    private int Forward(string socket)
    {
        for (var port = 27183; port <= 27299; port++)
        {
            if (!CanBind(port)) continue;
            try
            {
                AdbSync(["forward", $"tcp:{port}", $"localabstract:{socket}"], 5);
                return port;
            }
            catch { }
        }
        throw new ShellException("没有空闲的本地端口");
    }

    private void AdbSync(string[] arguments, int seconds) =>
        Adb.Run(arguments, serial, TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();

    private static bool CanBind(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// The server jar beside the app, or in the source tree's `vendor\scrcpy` for a build run from `bin`.
    public static string? ServerJar
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "scrcpy-server");
            if (File.Exists(beside)) return beside;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            {
                var source = Path.Combine(folder.FullName, "vendor", "scrcpy", "scrcpy-server");
                if (File.Exists(source)) return source;
            }
            return null;
        }
    }
}

/// Maps a stream's timestamps (microseconds on the device's clock) onto the host clock, anchored at the first
/// packet; re-anchored if they drift more than half a second from when packets arrive.
sealed class HostClockMapper
{
    private (long Pts, long Host)? anchor;

    public long HostTime(long pts)
    {
        var now = HostClock.Now;
        if (anchor is { } a)
        {
            var time = a.Host + (pts - a.Pts) * 10;
            if (Math.Abs(time - now) < 5_000_000) return time;
        }
        anchor = (pts, now);
        return now;
    }

    public void Reset() => anchor = null;
}

/// Android key codes for the key names device windows use (the emulator's W3C names).
static class AndroidKey
{
    private static readonly Dictionary<string, int> Codes = new()
    {
        ["GoBack"] = 4, ["GoHome"] = 3, ["AppSwitch"] = 187, ["Power"] = 26,
        ["AudioVolumeUp"] = 24, ["AudioVolumeDown"] = 25,
        ["Enter"] = 66, ["Tab"] = 61, ["Backspace"] = 67, ["Delete"] = 112,
        ["ArrowLeft"] = 21, ["ArrowRight"] = 22, ["ArrowUp"] = 19, ["ArrowDown"] = 20,
        ["Home"] = 122, ["End"] = 123, ["PageUp"] = 92, ["PageDown"] = 93, ["Escape"] = 111,
    };

    public static int? Code(string key) => Codes.TryGetValue(key, out var code) ? code : null;

    public const int Paste = 279;
}
