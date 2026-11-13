#if DEBUG
using Windows.Foundation;
using PostureValue = Android.Emulation.Control.Posture.Types.PostureValue;

namespace EmulatorShell;

/// Debug builds only: `EMULATOR_SHELL_TEST_SCRIPT` runs device-window commands once the device has booted, for
/// checking the window's behaviour without driving the mouse and keyboard. Commands are separated by `;`:
/// `wait <ms>`, `rotate left|right`, `zoom actual|physical|fit|larger|smaller`, `fold`, `posture open|closed|half`,
/// `title on|off`,
/// `resize <corner 0-3> <dx> <dy>` (pixels, in 10 steps), `key <W3C name>`, `text <text>`, `touch <x> <y>`,
/// `swipe <x1> <y1> <x2> <y2>` (device pixels),
/// `screenshot`, `more`, `move <dx> <dy>`, `shot <name>` (a marker for a screen capture), `quit`.
sealed partial class DeviceSession
{
    partial void RunTestScript()
    {
        if (Environment.GetEnvironmentVariable("EMULATOR_SHELL_TEST_SCRIPT") is not { Length: > 0 } script) return;
        _ = Execute(script);

        async Task Execute(string text)
        {
            foreach (var raw in text.Split(';'))
            {
                var parts = raw.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var argument = parts.Length > 1 ? parts[1] : "";
                Log.Info($"test script: {raw.Trim()}");
                switch (parts[0])
                {
                    case "wait": await Task.Delay(int.Parse(argument)); break;
                    case "rotate": Perform(argument == "left" ? DeviceAction.RotateLeft : DeviceAction.RotateRight); break;
                    case "zoom":
                        Zoom(argument switch
                        {
                            "actual" => DeviceZoom.ActualPixels, "physical" => DeviceZoom.Physical, "fit" => DeviceZoom.Fit,
                            "larger" => DeviceZoom.Larger, _ => DeviceZoom.Smaller,
                        });
                        break;
                    case "fold": Perform(DeviceAction.ToggleFold); break;
                    case "posture":
                    {
                        var value = argument switch
                        {
                            "closed" => PostureValue.PostureClosed, "half" => PostureValue.PostureHalfOpened, _ => PostureValue.PostureOpened,
                        };
                        Enqueue(c => c.SetPosture(value));
                        break;
                    }
                    case "title": chrome?.HoldTitleBar(argument == "on"); break;
                    case "key": Run(new DeviceCommand.Key(argument)); break;
                    case "text": Enqueue(c => c.SendText(argument)); break;
                    case "touch":
                    {
                        var xy = argument.Split(' ').Select(double.Parse).ToArray();
                        Enqueue(c => c.SendTouch((int)xy[0], (int)xy[1], true));
                        Enqueue(c => c.SendTouch((int)xy[0], (int)xy[1], false));
                        break;
                    }
                    case "swipe":
                    {
                        var p = argument.Split(' ').Select(double.Parse).ToArray();
                        for (var i = 0; i <= 10; i++)
                        {
                            int x = (int)(p[0] + (p[2] - p[0]) * i / 10), y = (int)(p[1] + (p[3] - p[1]) * i / 10);
                            Enqueue(c => c.SendTouch(x, y, true));
                            await Task.Delay(25);
                        }
                        Enqueue(c => c.SendTouch((int)p[2], (int)p[3], false));
                        break;
                    }
                    case "screenshot": Perform(DeviceAction.Screenshot); break;
                    case "more": chrome?.OpenMoreForTest(); break;
                    case "move":
                    {
                        var d = argument.Split(' ').Select(double.Parse).ToArray();
                        if (Window is { } window)
                        {
                            window.SetBox(window.Box.Offset(d[0], d[1]));
                            WindowMoved();
                        }
                        break;
                    }
                    case "resize":
                    {
                        var r = argument.Split(' ').Select(double.Parse).ToArray();
                        if (Window is not { } window) break;
                        var corner = (ResizeHandle.Corner)(int)r[0];
                        bool left = corner is ResizeHandle.Corner.TopLeft or ResizeHandle.Corner.BottomLeft;
                        bool top = corner is ResizeHandle.Corner.TopLeft or ResizeHandle.Corner.TopRight;
                        var box = window.Box;
                        var start = new Point(left ? box.Left : box.Right, top ? box.Top : box.Bottom);
                        ResizeBegan(corner);
                        for (var i = 1; i <= 10; i++)
                        {
                            ResizeChanged(new Point(start.X + r[1] * i / 10, start.Y + r[2] * i / 10));
                            await Task.Delay(30);
                        }
                        ResizeEnded();
                        break;
                    }
                    // A marker for a watcher that captures the screen; it gets a moment to do so.
                    case "shot": await Task.Delay(1200); break;
                    case "quit": QuitRequested?.Invoke(); break;
                }
            }
        }
    }
}
#endif
