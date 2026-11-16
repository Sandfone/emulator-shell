using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;

namespace EmulatorShell;

/// The manager's physical devices: their sidebar section, their detail pane in its states, the toast in the
/// pane's corner and the per-device menu.
sealed partial class ManagerWindow
{
    private static readonly Color Red = Color.FromArgb(255, 0xE5, 0x39, 0x35);
    private static readonly Color Orange = Color.FromArgb(255, 0xF7, 0x93, 0x0E);
    private static readonly Color Green = Color.FromArgb(255, 0x2E, 0xB8, 0x4F);

    private readonly Grid toastHost = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16) };
    private Guid? shownToast;
    private DispatcherQueueTimerHolder? toastTimer;
    /// The device whose preview is followed, the task following it and the picture it updates.
    private string? previewDevice;
    private CancellationTokenSource? previewFollow;
    private Image? previewImage;
    private Border? previewBezel;
    private Grid? previewArea;
    private (int Width, int Height) previewShape;
    /// Labels showing how long a recording has run, updated every second: the sidebar's and the pane's.
    private readonly List<(DateTime Since, TextBlock Label, string Format)> sidebarClocks = [], detailClocks = [];
    private DispatcherQueueTimerHolder? physicalTimer;
    private int physicalTicks;

    private PhysicalDeviceStore Phones => manager.Phones;

    private void StartPhysical()
    {
        Phones.StartTracking();
        Phones.ThumbnailChanged += ShowThumbnail;
        toastTimer = new DispatcherQueueTimerHolder(this);
        physicalTimer = new DispatcherQueueTimerHolder(this);
        physicalTimer.StartRepeating(TimeSpan.FromSeconds(1), PhysicalTick);
        Closed += (_, _) =>
        {
            Phones.ThumbnailChanged -= ShowThumbnail;
            physicalTimer.Stop();
            toastTimer.Stop();
            StopPreview();
        };
    }

    private static string Elapsed(DateTime since)
    {
        var seconds = Math.Max(0, (int)(DateTime.Now - since).TotalSeconds);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private void PhysicalTick()
    {
        foreach (var (since, label, format) in sidebarClocks.Concat(detailClocks)) label.Text = string.Format(format, Elapsed(since));
        // The battery and free space change; read them again now and then while shown.
        if (++physicalTicks % 30 == 0 && manager.SelectedPhysical is { IsOnline: true } device) _ = Phones.RefreshInfo(device.Id);
    }

    // Sidebar

    private List<PhysicalDevice> VisiblePhones
    {
        get
        {
            var text = filter.Text.Trim();
            return Phones.Devices.Where(d => text.Length == 0 || d.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
    }

    private string PhysicalSignature =>
        string.Join("|", VisiblePhones.Select(d => $"{d.Id}:{d.Name}:{d.State}:{d.IsWireless}:{d.Info?.AndroidVersion}:{Phones.Recordings.ContainsKey(d.Id)}"))
        + $"#{Phones.Devices.Count}";

    /// The section is there only while it lists a device (pairing is in the toolbar), or, without adb, to offer
    /// installing it.
    private void AddPhysicalItems()
    {
        sidebarClocks.Clear();
        var phones = VisiblePhones;
        if (phones.Count == 0 && !manager.ShowsPhysicalHelp) return;
        list.Items.Add(SectionHeader("物理设备"));
        foreach (var device in phones)
        {
            var item = new ListViewItem { Tag = device.Id, Content = PhysicalRow(device), Padding = new Thickness(12, 4, 12, 4) };
            item.ContextFlyout = PhysicalMenu(device, includeMain: true);
            list.Items.Add(item);
            if (device.Id == manager.Selection) list.SelectedItem = item;
        }
        if (manager.ShowsPhysicalHelp)
        {
            var help = new ListViewItem { Tag = DeviceManager.PhysicalHelpId, Content = HelpRow(), Padding = new Thickness(12, 4, 12, 4) };
            list.Items.Add(help);
            if (manager.Selection == DeviceManager.PhysicalHelpId) list.SelectedItem = help;
        }
    }

    private static ListViewItem SectionHeader(string title) => new()
    {
        IsEnabled = false,
        MinHeight = 28,
        Padding = new Thickness(16, 10, 12, 2),
        Content = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.7 },
    };

    private UIElement PhysicalRow(PhysicalDevice device)
    {
        var offline = device.State == PhysicalState.Offline;
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = device.Info?.IsTablet == true ? "" : "", FontSize = 17, Opacity = offline ? 0.5 : 1 });
        var subtitle = new TextBlock { FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
        if (Phones.Recordings.TryGetValue(device.Id, out var since))
        {
            subtitle.Opacity = 1;
            subtitle.Foreground = new SolidColorBrush(Red);
            subtitle.Text = $"正在录屏 {Elapsed(since)}";
            sidebarClocks.Add((since, subtitle, "正在录屏 {0}"));
        }
        else
        {
            subtitle.Text = PhysicalSubtitle(device);
        }
        var text = new StackPanel
        {
            Spacing = 1,
            Children = { new TextBlock { Text = device.Name, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = offline ? 0.6 : 1 }, subtitle },
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if ((Phones.Recordings.ContainsKey(device.Id) ? Red : DotColor(device)) is { } color)
        {
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }
        return grid;
    }

    private static UIElement HelpRow()
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new FontIcon { Glyph = "", FontSize = 17, Opacity = 0.6 });
        var text = new StackPanel
        {
            Spacing = 1,
            Children = { new TextBlock { Text = "连接 Android 设备…" }, new TextBlock { Text = "需要安装 Platform-Tools", FontSize = 12, Opacity = 0.65 } },
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static string PhysicalSubtitle(PhysicalDevice device) => device.State switch
    {
        PhysicalState.Online => (device.IsWireless ? "Wi-Fi" : "USB") + (device.Info is { } info ? $" · Android {info.AndroidVersion}" : ""),
        PhysicalState.Unauthorized => "等待在手机上允许 USB 调试",
        PhysicalState.Connecting => "正在连接…",
        PhysicalState.Offline => device.IsWireless ? "离线 · Wi-Fi" : "离线",
        _ => "没有 USB 访问权限",
    };

    private static Color? DotColor(PhysicalDevice device) => device.State switch
    {
        PhysicalState.Online => Green,
        PhysicalState.Offline => null,
        _ => Orange,
    };

    /// The per-device actions of a phone, shared by its "⋯" menu and the sidebar's context menu.
    private MenuFlyout PhysicalMenu(PhysicalDevice shown, bool includeMain)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var device = Phones.Device(shown.Id) ?? shown;
            void Add(string text, Action action, bool enabled = true, string? glyph = null)
            {
                var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
                if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            var busy = Phones.Busy.Contains(device.Id);
            if (device.IsOnline)
            {
                if (includeMain)
                {
                    Add(Phones.IsMirroring(device.Id) ? "显示投屏窗口" : "投屏", () => manager.Mirror(device), glyph: "");
                    Add("截图", () => Phones.Screenshot(device.Id), glyph: "");
                    Add(Phones.IsRecording(device.Id) ? "停止录屏" : "录屏", () => Phones.ToggleRecording(device.Id), glyph: "");
                    Add("发送文字到剪贴板…", () => _ = AskAndSendText(device), glyph: "\uE8C8");
                    menu.Items.Add(new MenuFlyoutSeparator());
                }
                if (!device.IsWireless) Add("切换为无线连接", () => Phones.SwitchToWireless(device.Id), !busy);
                Add("安装 APK 或传文件…", () => ChooseFilesFor(device.Id));
                Add("打开桌面（截图和录屏）", PhysicalDeviceStore.RevealDesktop);
                menu.Items.Add(new MenuFlyoutSeparator());
                if (device.IsWireless) Add("断开连接", () => Phones.Disconnect(device.Id));
                Add("重启设备", () => Phones.Reboot(device.Id));
            }
            else
            {
                Add("重新连接", () => Phones.Reconnect(device.Id), !busy);
            }
            if (device.IsWireless || device.PairedAt != null) Add("忘记此设备", () => Phones.Forget(device.Id));
        };
        return menu;
    }

    private async void ChooseFilesFor(string id)
    {
        var picker = new FileOpenPicker { CommitButtonText = "传到设备" };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) Phones.Send(files.Select(f => f.Path).ToList(), id);
    }

    // Detail

    private string PhysicalDetailSignature
    {
        get
        {
            var device = manager.SelectedPhysical;
            var info = device?.Info;
            return $"phys:{manager.Selection}:{Phones.AdbInstalled}:{Phones.InstallProgress:0.00}:{device?.State}:{device?.Name}:{device?.Serial}:"
                   + $"{string.Join(",", device?.Serials ?? [])}:{device?.LastSeen}:{info?.BatteryLevel}:{info?.Charging}:{info?.StorageFree}:"
                   + $"{info?.InputBlocked}:{Phones.IsMirroring(manager.Selection ?? "")}:{Phones.Recordings.ContainsKey(manager.Selection ?? "")}:"
                   + $"{Phones.Busy.Contains(manager.Selection ?? "")}:{Phones.InputRefused.Contains(manager.Selection ?? "")}";
        }
    }

    private void RebuildPhysicalDetail()
    {
        detailClocks.Clear();
        var device = manager.SelectedPhysical;
        if (device?.Id != previewDevice || device?.IsOnline != true) StopPreview();
        UIElement content;
        if (!Phones.AdbInstalled) content = AdbMissingView();
        // The selected phone went away (unplugged, forgotten); it is selected again if it comes back.
        else if (device == null) content = Message("", "设备已断开", "重新连上后会自动显示在这里。要连接新设备，插上数据线，或点上方的“Wi-Fi 配对”。");
        else
        {
            content = device.State switch
            {
                PhysicalState.Online => PhysicalDetail(device),
                PhysicalState.Unauthorized => UnauthorizedView(device),
                PhysicalState.Connecting => Centered(new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new ProgressRing { IsActive = true, Width = 28, Height = 28 },
                        new TextBlock { Text = $"正在读取“{device.Name}”的信息…", Opacity = 0.65 },
                    },
                }),
                PhysicalState.Offline => OfflineView(device),
                _ => Message("", "这台电脑不能访问这个 USB 设备", $"换一根数据线或 USB 口再试。序列号 {device.Serials.FirstOrDefault()}"),
            };
        }
        detail.Children.Add(content);
    }

    private static UIElement Centered(UIElement child) => new Grid
    {
        Children = { new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = child } },
    };

    private static UIElement Message(string glyph, string title, string text) => Centered(new StackPanel
    {
        Spacing = 10, MaxWidth = 460,
        Children =
        {
            new FontIcon { Glyph = glyph, FontSize = 40, Opacity = 0.5 },
            new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = text, Opacity = 0.65, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        },
    });

    private static Button Labeled(string glyph, string text, bool accent = false)
    {
        var button = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 13 }, new TextBlock { Text = text } } } };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        return button;
    }

    /// Asks for text and puts it on the device's clipboard; the toast says how it went.
    private async Task AskAndSendText(PhysicalDevice device)
    {
        var (dialog, text) = SendTextDialog.Create(device.Name);
        await ShowDialog(dialog);
        if (text() is { } typed) Phones.SendToClipboard(typed, device.Id);
    }

    private UIElement PhysicalDetail(PhysicalDevice device)
    {
        var info = device.Info ?? new DeviceInfo();
        var recording = Phones.Recordings.TryGetValue(device.Id, out var since) ? since : (DateTime?)null;

        var preview = PhonePreview(device, info);

        var mirror = Labeled("", Phones.IsMirroring(device.Id) ? "显示窗口" : "投屏", accent: true);
        ToolTipService.SetToolTip(mirror, "在窗口里显示这台设备，可以直接用鼠标和键盘操作  Ctrl+O");
        mirror.Click += (_, _) => manager.Mirror(device);
        var screenshot = Labeled("", "截图");
        ToolTipService.SetToolTip(screenshot, "存到桌面  Ctrl+S");
        screenshot.Click += (_, _) => Phones.Screenshot(device.Id);
        Button record;
        if (recording is { } started)
        {
            var label = new TextBlock { Text = $"停止录屏 {Elapsed(started)}" };
            detailClocks.Add((started, label, "停止录屏 {0}"));
            record = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "", FontSize = 13 }, label } },
                Background = new SolidColorBrush(Red),
                Foreground = new SolidColorBrush(Colors.White),
            };
            ToolTipService.SetToolTip(record, "停止并存到桌面  Ctrl+Shift+R");
        }
        else
        {
            record = Labeled("", "录屏");
            ToolTipService.SetToolTip(record, "录成 MP4 存到桌面，不用开投屏  Ctrl+Shift+R");
        }
        record.Click += (_, _) => Phones.ToggleRecording(device.Id);
        var sendText = Labeled("\uE8C8", "发送文字");
        ToolTipService.SetToolTip(sendText, "输入一段文字，添加到设备的剪贴板，在设备上长按输入框粘贴  Ctrl+Shift+V");
        sendText.IsEnabled = !Phones.SendingClipboard.Contains(device.Id);
        sendText.Click += (_, _) => _ = AskAndSendText(device);
        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Flyout = PhysicalMenu(device, includeMain: false) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "更多操作");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { mirror, screenshot, record, sendText, more } };

        var status = new TextBlock { Text = PhysicalStatus(device) };
        if (recording is { } recordingSince)
        {
            status.Text = $"{PhysicalStatus(device)} · 正在录屏 {Elapsed(recordingSince)}";
            detailClocks.Add((recordingSince, status, PhysicalStatus(device).Replace("{", "{{").Replace("}", "}}") + " · 正在录屏 {0}"));
        }
        var heading = new StackPanel
        {
            Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = device.Name, FontSize = 24, FontWeight = FontWeights.Bold, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = Headline(info), Opacity = 0.65, TextWrapping = TextWrapping.Wrap },
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 7,
                    Children =
                    {
                        new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
                                     Background = new SolidColorBrush(recording != null ? Red : Green) },
                        status,
                    },
                },
                buttons,
                new TextBlock { Text = "截图和录屏存到桌面 · Ctrl+S 截图 · Ctrl+Shift+R 开始或停止录屏 · Ctrl+Shift+V 发送文字到剪贴板", Opacity = 0.65, TextWrapping = TextWrapping.Wrap },
            },
        };
        if (info.InputBlocked || Phones.InputRefused.Contains(device.Id))
        {
            heading.Children.Add(new TextBlock
            {
                Text = "⚠ 投屏时能看不能点：这台手机的开发者选项里还没打开“USB 调试（安全设置）”，系统会拒绝来自电脑的点按和输入。截图和录屏不受影响。",
                Foreground = new SolidColorBrush(Orange),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var top = new Grid { ColumnSpacing = 26 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(preview);
        Grid.SetColumn(heading, 1);
        top.Children.Add(heading);

        var groups = new Grid { ColumnSpacing = 20 };
        groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Spacing = 18, Children = { InfoGroup("设备", DeviceRows(info)), InfoGroup("系统", SystemRows(info)) } };
        var right = new StackPanel { Spacing = 18, Children = { InfoGroup("硬件", HardwareRows(info)), InfoGroup("连接", ConnectionRows(device, info)) } };
        groups.Children.Add(left);
        Grid.SetColumn(right, 1);
        groups.Children.Add(right);

        var column = new Grid { Padding = new Thickness(28, 24, 28, 24) };
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 900 });
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        column.Children.Add(new StackPanel { Spacing = 22, Children = { top, groups } });
        return new ScrollViewer { Content = column };
    }

    private string PhysicalStatus(PhysicalDevice device)
    {
        var parts = new List<string> { "已连接", device.IsWireless ? "Wi-Fi" : "USB" };
        if (device.Serial is { } serial) parts.Add(serial);
        if (Phones.IsMirroring(device.Id)) parts.Add("正在投屏");
        return string.Join(" · ", parts);
    }

    private static string Headline(DeviceInfo info)
    {
        var manufacturer = info.Manufacturer.Length > 0 ? char.ToUpperInvariant(info.Manufacturer[0]) + info.Manufacturer[1..] : "";
        var model = string.Join(" ", new[] { manufacturer, info.Model }.Where(s => s.Length > 0));
        return $"{model} · Android {info.AndroidVersion} · API {info.Sdk}";
    }

    private static (string, string)[] DeviceRows(DeviceInfo info)
    {
        var resolution = $"{info.Width} × {info.Height}" + (info.Density is { } density ? $" · {density} dpi" : "");
        return
        [
            ("型号", info.Codename.Length == 0 ? info.Model : $"{info.Model}（{info.Codename}）"),
            ("分辨率", resolution),
            ("方向", info.IsLandscapeNatural ? "横屏为主（跟随设备）" : "竖屏为主（跟随设备）"),
        ];
    }

    private static (string, string)[] SystemRows(DeviceInfo info) =>
    [
        ("Android 版本", $"{info.AndroidVersion}（API {info.Sdk}）"),
        ("安全补丁", info.SecurityPatch ?? "—"),
        ("版本号", info.BuildId ?? "—"),
        ("架构", info.Abi ?? "—"),
    ];

    private static (string, string)[] HardwareRows(DeviceInfo info)
    {
        var battery = info.BatteryLevel is { } level ? $"{level}%" : "—";
        if (info.Charging) battery += " · 正在充电";
        var storage = info.StorageFree is { } free && info.StorageTotal is { } total ? $"可用 {Sizes.Format(free)} / 共 {Sizes.Format(total)}" : "—";
        return
        [
            ("电池", battery),
            ("存储", storage),
            ("内存", info.Memory is { } memory ? Sizes.Format(memory) : "—"),
        ];
    }

    private static (string, string)[] ConnectionRows(PhysicalDevice device, DeviceInfo info)
    {
        var rows = new List<(string, string)> { ("方式", device.IsWireless ? "Wi-Fi（无线调试）" : "USB") };
        if (device.Serial is { } serial) rows.Add((device.IsWireless ? "地址" : "序列号", serial));
        if (device.Serials.Count > 1) rows.Add(("也连着", string.Join(", ", device.Serials.Where(s => s != device.Serial))));
        rows.Add(("投屏", info.Sdk < 21 ? "不支持（需要 Android 5.0）" : info.Sdk >= 30 ? "支持 · 声音可转到电脑" : "支持 · 声音需要 Android 11"));
        return [.. rows];
    }

    /// A phone's screen in a plain dark bezel, as its mirror window shows it, kept current while shown.
    private UIElement PhonePreview(PhysicalDevice device, DeviceInfo info)
    {
        var landscape = info.IsLandscapeNatural;
        var area = new Grid { Width = landscape ? 250 : 120, Height = landscape ? 170 : 250 };
        previewArea = area;
        previewImage = new Image { Stretch = Stretch.Fill };
        previewBezel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 28, 28, 28)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 15, 15, 15)), Child = previewImage },
        };
        area.Children.Add(previewBezel);
        previewShape = (Math.Max(info.Width, 1), Math.Max(info.Height, 1));
        LayoutPreview();
        if (Phones.Thumbnails.TryGetValue(device.Id, out var picture)) SetPreview(picture);
        if (previewDevice != device.Id || previewFollow == null)
        {
            StopPreview();
            previewDevice = device.Id;
            previewFollow = new CancellationTokenSource();
            _ = Phones.FollowPreview(device.Id, previewFollow.Token);
        }
        return area;
    }

    /// The bezel around the screen at the shape of the latest picture (the screen may be turned).
    private void LayoutPreview()
    {
        // The area is remembered rather than found as the bezel's parent, which is unset until the pane is on screen.
        if (previewArea is not { } area || previewBezel?.Child is not Border screen) return;
        var bezel = Math.Min(area.Width, area.Height) * 0.035;
        var scale = Math.Min((area.Width - 2 * bezel) / previewShape.Width, (area.Height - 2 * bezel) / previewShape.Height);
        double width = previewShape.Width * scale, height = previewShape.Height * scale;
        var radius = Math.Min(width, height) * 0.09;
        screen.Width = width;
        screen.Height = height;
        screen.CornerRadius = new CornerRadius(radius);
        previewBezel.Padding = new Thickness(bezel);
        previewBezel.CornerRadius = new CornerRadius(radius + bezel);
    }

    private void SetPreview(VideoFrame picture)
    {
        if (previewImage == null) return;
        previewImage.Source = Pictures.Bitmap(picture);
        if ((picture.Width, picture.Height) != previewShape)
        {
            previewShape = (picture.Width, picture.Height);
            LayoutPreview();
        }
    }

    private void ShowThumbnail(string id)
    {
        if (id == previewDevice && Phones.Thumbnails.TryGetValue(id, out var picture)) SetPreview(picture);
    }

    private void StopPreview()
    {
        previewFollow?.Cancel();
        previewFollow = null;
        previewDevice = null;
    }

    private UIElement UnauthorizedView(PhysicalDevice device)
    {
        var reconnect = new Button { Content = "重新连接", IsEnabled = !Phones.Busy.Contains(device.Id) };
        reconnect.Click += (_, _) => Phones.Reconnect(device.Id);
        var tips = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "没看到弹窗？", FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "· 先解锁手机，弹窗只在解锁时出现", Opacity = 0.65 },
                new TextBlock { Text = "· 拔下数据线再插上", Opacity = 0.65 },
                new TextBlock { Text = "· 开发者选项里点“撤消 USB 调试授权”，然后重插", Opacity = 0.65 },
            },
        };
        return Centered(new StackPanel
        {
            Spacing = 14, MaxWidth = 460,
            Children =
            {
                new FontIcon { Glyph = "", FontSize = 44, Foreground = new SolidColorBrush(Orange) },
                new TextBlock { Text = "在手机上允许 USB 调试", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Text = "手机上应该弹出了“允许 USB 调试吗？”。勾选“一律允许使用这台计算机进行调试”，再点“允许”。允许后这里会自动显示设备。",
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                },
                Card(tips),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children =
                    {
                        reconnect,
                        new TextBlock { Text = $"序列号 {device.Serials.FirstOrDefault()} · {(device.IsWireless ? "Wi-Fi" : "USB")}", FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            },
        });
    }

    private static Border Card(UIElement child) => new()
    {
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(8),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        Child = child,
    };

    private UIElement OfflineView(PhysicalDevice device)
    {
        var reconnect = new Button
        {
            Content = "重新连接",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            IsEnabled = !Phones.Busy.Contains(device.Id) && (device.Address != null || device.Serials.Count > 0),
        };
        reconnect.Click += (_, _) => Phones.Reconnect(device.Id);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { reconnect } };
        if (device.IsWireless)
        {
            var forget = new Button { Content = "忘记此设备" };
            forget.Click += (_, _) => Phones.Forget(device.Id);
            buttons.Children.Add(forget);
        }
        var panel = new StackPanel
        {
            Spacing = 16, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(32),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 14,
                    Children =
                    {
                        new FontIcon { Glyph = "", FontSize = 40, Opacity = 0.5 },
                        new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                new TextBlock { Text = device.Name, FontSize = 24, FontWeight = FontWeights.Bold },
                                new TextBlock { Text = device.LastSeen is { } seen ? $"离线 · 上次在 {seen:yyyy-MM-dd HH:mm}" : "离线", Opacity = 0.65 },
                            },
                        },
                    },
                },
                new TextBlock
                {
                    Text = device.IsWireless
                        ? "设备休眠、换了网络或关了“无线调试”都会断开。配对过的设备会一直列在这里，回到同一网络后通常会自动连上。"
                        : "adb 报告这台设备离线。拔下数据线再插上，或者点“重新连接”。",
                    TextWrapping = TextWrapping.Wrap,
                },
                InfoGroup("连接", [("上次地址", device.Address ?? "—"), ("配对时间", device.PairedAt is { } paired ? $"{paired:yyyy-MM-dd}" : "—")]),
                buttons,
            },
        };
        return new ScrollViewer { Content = panel };
    }

    private UIElement AdbMissingView()
    {
        var panel = new StackPanel
        {
            Spacing = 14, MaxWidth = 420,
            Children =
            {
                new FontIcon { Glyph = "", FontSize = 44, Opacity = 0.5 },
                new TextBlock { Text = "需要 Android SDK Platform-Tools", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "发现和操作物理设备要用到其中的 adb。它会装到现在的 SDK 里，大约 10 MB。", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            },
        };
        if (Phones.InstallProgress is { } progress)
        {
            panel.Children.Add(new ProgressBar { Value = progress * 100, Maximum = 100, Width = 240 });
        }
        else
        {
            var install = new Button { Content = "安装 Platform-Tools", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Center };
            install.Click += (_, _) => Phones.InstallPlatformTools();
            panel.Children.Add(install);
        }
        return Centered(panel);
    }

    // Toast

    /// "截图已存到桌面" and the like, in the pane's corner for a few seconds.
    private void UpdateToast()
    {
        var toast = Phones.Toast;
        if (toast?.Id == shownToast) return;
        shownToast = toast?.Id;
        toastHost.Children.Clear();
        toastTimer?.Stop();
        if (toast == null) return;
        var icon = new FontIcon { Glyph = toast.IsError ? "" : "", FontSize = 18, Foreground = new SolidColorBrush(toast.IsError ? Orange : Green), VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
        text.Children.Add(new TextBlock { Text = toast.Text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (toast.Path is { } path) text.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { icon, text } };
        if (toast.Path is { } file)
        {
            var reveal = new Button { Content = "在文件夹中显示", VerticalAlignment = VerticalAlignment.Center };
            reveal.Click += (_, _) => PhysicalDeviceStore.Reveal(file);
            row.Children.Add(reveal);
        }
        toastHost.Children.Add(new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
            BorderThickness = new Thickness(1),
            Child = row,
        });
        var id = toast.Id;
        toastTimer?.StartOnce(TimeSpan.FromSeconds(5), () =>
        {
            if (Phones.Toast?.Id == id) Phones.Toast = null;
        });
    }

    // Pairing

    private Task ShowPairing() => ShowDialog(new PairingDialog());
}
