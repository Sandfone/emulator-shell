using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;
using Windows.UI;

namespace EmulatorShell;

/// Pairing a device over Wi-Fi (Android 11+), as Android Studio does: the device scans a QR code naming a service
/// and its password, then offers that service on the network (mDNS `_adb-tls-pairing._tcp`), and `adb pair`
/// completes it; or the device shows a six-digit code for a service it offers. Once paired, adb connects to it by
/// itself and it shows up with the other devices.
sealed partial class PairingDialog : ContentDialog
{
    private enum Mode { Qr, Code }
    private abstract record Phase
    {
        public sealed record Waiting : Phase;
        public sealed record Pairing : Phase;
        public sealed record Paired(string Host) : Phase;
        public sealed record Failed(string Message) : Phase;
    }

    private Mode mode = Mode.Qr;
    private Phase phase = new Phase.Waiting();
    /// Devices showing a pairing code, for the code page.
    private List<Adb.MdnsService> pairingServices = [];
    private Adb.MdnsService? selected;
    private string code = "";
    private string? mdnsProblem;
    private readonly string serviceName = "emulator-shell-" + Random.Shared.Next(100_000, 1_000_000);
    private readonly string password = new(Enumerable.Range(0, 10).Select(_ => "abcdefghjkmnpqrstuvwxyz23456789"[Random.Shared.Next(31)]).ToArray());
    private readonly CancellationTokenSource watching = new();
    private readonly StackPanel root = new() { Spacing = 18, Width = 560 };
    private BitmapImage? qrImage;
    /// What the code page showed last, so that a poll finding the same devices does not redraw it under the user.
    private string servicesShown = "";

    public PairingDialog()
    {
        Title = "通过 Wi-Fi 配对设备";
        CloseButtonText = "取消";
        Resources["ContentDialogMaxWidth"] = 640.0;
        Content = root;
        Opened += (_, _) => Watch();
        Closed += (_, _) => watching.Cancel();
        _ = MakeQr();
        Rebuild();
    }

    private async Task MakeQr()
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode($"WIFI:T:ADB;S:{serviceName};P:{password};;", QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        qrImage = image;
        Rebuild();
    }

    /// Watches the network for the device until the dialog closes.
    private async void Watch()
    {
        try
        {
            if ((await Adb.Text(["mdns", "check"], timeout: TimeSpan.FromSeconds(10))).Contains("unavailable"))
            {
                mdnsProblem = "adb 的局域网发现（mDNS）不可用，配对需要它。更新 Platform-Tools 后再试。";
                Rebuild();
            }
        }
        catch (Exception error)
        {
            Log.Debug($"adb mdns check: {error.Message}");
        }
        while (!watching.IsCancellationRequested)
        {
            await Poll();
            try { await Task.Delay(1000, watching.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task Poll()
    {
        List<Adb.MdnsService> services;
        try
        {
            services = await Adb.MdnsServices();
        }
        catch (Exception error)
        {
            Log.Debug($"adb mdns services: {error.Message}");
            return;
        }
        var pairing = services.Where(s => s.IsPairing).ToList();
        pairingServices = pairing.Where(s => s.Name != serviceName).ToList();
        if (selected != null && !pairingServices.Contains(selected)) selected = null;
        if (selected == null && pairingServices.Count == 1) selected = pairingServices[0];
        var shown = string.Join("|", pairingServices.Select(s => s.Address)) + "#" + selected?.Address;
        if (shown != servicesShown && mode == Mode.Code && phase is Phase.Waiting)
        {
            servicesShown = shown;
            Rebuild();
        }
        // The device that scanned the QR code offers the service named in it.
        if (mode == Mode.Qr && phase is Phase.Waiting && pairing.FirstOrDefault(s => s.Name == serviceName) is { } scanned)
            await Pair(scanned, password);
    }

    private async Task Pair(Adb.MdnsService service, string secret)
    {
        phase = new Phase.Pairing();
        Rebuild();
        try
        {
            await Adb.Pair(service.Address, secret);
            phase = new Phase.Paired(service.Host);
        }
        catch (Exception error)
        {
            phase = new Phase.Failed(error.Message);
        }
        Rebuild();
    }

    private void Rebuild()
    {
        root.Children.Clear();
        CloseButtonText = phase is Phase.Paired ? "完成" : "取消";

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (value, label) in new[] { (Mode.Qr, "二维码"), (Mode.Code, "配对码") })
        {
            var button = new Button { Content = label, MinWidth = 100, IsEnabled = phase is not Phase.Pairing };
            if (value == mode) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Click += (_, _) =>
            {
                mode = value;
                servicesShown = "";
                Rebuild();
            };
            modes.Children.Add(button);
        }
        root.Children.Add(modes);

        root.Children.Add(phase switch
        {
            Phase.Paired paired => Outcome("", Color.FromArgb(255, 0x2E, 0xB8, 0x4F), "已配对",
                                           $"设备（{paired.Host}）稍后会出现在左栏的“物理设备”里。之后回到同一网络会自动连上，不用再配对。"),
            Phase.Failed failed => FailedPage(failed.Message),
            _ => mode == Mode.Qr ? QrPage() : CodePage(),
        });
        if (mdnsProblem != null) root.Children.Add(new TextBlock { Text = "⚠ " + mdnsProblem, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
    }

    private static UIElement Outcome(string glyph, Color color, string title, string text) => new StackPanel
    {
        Spacing = 10, MinHeight = 260, VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new FontIcon { Glyph = glyph, FontSize = 44, Foreground = new SolidColorBrush(color) },
            new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = text, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, IsTextSelectionEnabled = true },
        },
    };

    private UIElement FailedPage(string message)
    {
        var page = (StackPanel)Outcome("", Color.FromArgb(255, 0xF7, 0x93, 0x0E), "配对没有成功", message);
        var retry = new Button { Content = "再试一次", HorizontalAlignment = HorizontalAlignment.Center };
        retry.Click += (_, _) =>
        {
            phase = new Phase.Waiting();
            code = "";
            Rebuild();
        };
        page.Children.Add(retry);
        return page;
    }

    private UIElement QrPage()
    {
        var waiting = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                new TextBlock { Text = phase is Phase.Pairing ? "正在配对…" : "正在等待手机扫码…", Opacity = 0.65 },
            },
        };
        var code = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Border
                {
                    Width = 220, Height = 220, Padding = new Thickness(10), CornerRadius = new CornerRadius(12),
                    Background = new SolidColorBrush(Colors.White),
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
                    Child = new Image { Source = qrImage, Stretch = Stretch.Uniform },
                },
                waiting,
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(code, "配对二维码");
        var steps = new StackPanel
        {
            Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Step("1. 手机和这台电脑连同一个 Wi-Fi"),
                Step("2. 手机上打开 设置 › 系统 › 开发者选项 › 无线调试"),
                Step("3. 点“使用二维码配对设备”，扫左边的码"),
                Note("需要 Android 11 及以上。配对成功后设备出现在左栏，之后回到同一网络会自动连上。"),
                Note("已经插着线？在设备的 ⋯ 菜单里选“切换为无线连接”，任何 Android 版本都行。"),
            },
        };
        var grid = new Grid { ColumnSpacing = 28, MinHeight = 260 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(code);
        Grid.SetColumn(steps, 1);
        grid.Children.Add(steps);
        return grid;
    }

    private static TextBlock Step(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };

    private UIElement CodePage()
    {
        var list = new ListView { Height = 120, SelectionMode = ListViewSelectionMode.Single, BorderThickness = new Thickness(1),
                                  BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], CornerRadius = new CornerRadius(8) };
        foreach (var service in pairingServices)
        {
            var item = new ListViewItem
            {
                Tag = service,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children =
                    {
                        new FontIcon { Glyph = "", FontSize = 16 },
                        new StackPanel { Children = { new TextBlock { Text = service.Name }, new TextBlock { Text = $"{service.Host} · 正在显示配对码", FontSize = 12, Opacity = 0.65 } } },
                    },
                },
            };
            list.Items.Add(item);
            if (service == selected) list.SelectedItem = item;
        }
        list.SelectionChanged += (_, _) => selected = (list.SelectedItem as ListViewItem)?.Tag as Adb.MdnsService;
        var listArea = new Grid { Children = { list } };
        if (pairingServices.Count == 0)
        {
            listArea.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { new ProgressRing { IsActive = true, Width = 16, Height = 16 }, new TextBlock { Text = "正在局域网里寻找显示配对码的设备…", Opacity = 0.65 } },
            });
        }
        var box = new TextBox { PlaceholderText = "配对码", Text = code, Width = 150, MaxLength = 6, FontSize = 20, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        var pair = new Button { Content = "配对", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        void Update() => pair.IsEnabled = selected != null && code.Length == 6 && phase is not Phase.Pairing;
        box.TextChanged += (_, _) =>
        {
            var digits = new string(box.Text.Where(char.IsDigit).Take(6).ToArray());
            if (digits != box.Text)
            {
                box.Text = digits;
                box.SelectionStart = digits.Length;
            }
            code = digits;
            Update();
        };
        list.SelectionChanged += (_, _) => Update();
        void Submit()
        {
            if (selected is { } service && code.Length == 6) _ = Pair(service, code);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Submit();
        };
        pair.Click += (_, _) => Submit();
        Update();
        var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { box, pair } };
        if (phase is Phase.Pairing) entry.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        return new StackPanel
        {
            Spacing = 12, MinHeight = 260,
            Children =
            {
                Step("1. 手机上打开 开发者选项 › 无线调试 › 使用配对码配对设备"),
                Step("2. 手机会出现在下面，选中它，输入手机上显示的 6 位配对码"),
                listArea,
                entry,
            },
        };
    }
}
