using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace EmulatorShell;

/// Keeps the Windows clipboard and the guest clipboard in step, both ways, while enabled.
sealed partial class ClipboardSync
{
    private const string SettingsKey = "clipboardSync";

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    private bool enabled;
    public bool IsEnabled
    {
        get => enabled;
        set
        {
            enabled = value;
            Settings.Set(SettingsKey, value);
            // Catch up with whatever was copied on Windows while sync was off.
            if (value) lastSequence = 0;
        }
    }

    private readonly EmulatorClient client;
    private readonly DispatcherQueue queue;
    /// 0 makes the first poll push the Windows clipboard, which is the newer one when the device starts.
    private uint lastSequence;
    private string? lastText;
    private bool sawGuestState;
    private DispatcherQueueTimer? timer;
    private CancellationTokenSource? stream;
    private bool reading;

    public ClipboardSync(EmulatorClient client, DispatcherQueue queue)
    {
        this.client = client;
        this.queue = queue;
        enabled = Settings.GetBool(SettingsKey, true);
    }

    public void Start()
    {
        var cancellation = new CancellationTokenSource();
        stream = cancellation;
        Task.Run(async () =>
        {
            try
            {
                await client.StreamClipboard(text => queue.TryEnqueue(() => GuestCopied(text)), cancellation.Token);
            }
            catch (Exception error)
            {
                // Stopping cancels the stream; the error that comes back then is expected.
                if (!cancellation.IsCancellationRequested) Log.Error($"clipboard stream ended: {error.Message}");
            }
        });
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(0.5);
        timer.Tick += (_, _) => PollClipboard();
        timer.Start();
    }

    public void Stop()
    {
        stream?.Cancel();
        timer?.Stop();
    }

    /// The clipboard's sequence number changes with every copy, in any app; it is polled as macOS's change count is.
    private async void PollClipboard()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == lastSequence || reading) return;
        lastSequence = sequence;
        if (!enabled) return;
        reading = true;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (text == lastText) return;
            lastText = text;
            Log.Debug($"clipboard → device ({text.Length} chars)");
            await client.SetClipboard(text);
        }
        catch (Exception error)
        {
            // Another app may hold the clipboard open for a moment.
            Log.Debug($"cannot read the clipboard: {error.Message}");
            lastSequence = 0;
        }
        finally
        {
            reading = false;
        }
    }

    private void GuestCopied(string text)
    {
        // The stream opens with the guest's current clipboard, which is older than the Windows one.
        if (!sawGuestState)
        {
            sawGuestState = true;
            return;
        }
        if (!enabled || text.Length == 0 || text == lastText) return;
        lastText = text;
        Log.Debug($"clipboard ← device ({text.Length} chars)");
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            lastSequence = GetClipboardSequenceNumber();
        }
        catch (Exception error)
        {
            Log.Error($"cannot set the clipboard: {error.Message}");
        }
    }
}
