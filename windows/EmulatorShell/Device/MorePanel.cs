using Android.Emulation.Control;
using Environment = System.Environment;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmulatorShell;

/// The "More" flyout: tiles for the emulator's secondary features. Toggles and one-shot actions work on
/// the tile itself; the rest open a page inside the flyout.
sealed partial class MorePanel : UserControl
{
    private enum Page { Location, Battery, Phone, Brightness, Snapshots, Posture }

    private static string Title(Page page) => page switch
    {
        Page.Location => "位置",
        Page.Battery => "电池",
        Page.Phone => "来电与短信",
        Page.Brightness => "亮度",
        Page.Snapshots => "快照",
        _ => "折叠姿态",
    };

    private readonly DeviceControls controls;
    private Page? page;
    private readonly StackPanel root = new() { Spacing = 10, Width = 300 };

    public MorePanel(DeviceControls controls)
    {
        this.controls = controls;
        Content = root;
        controls.Changed += Rebuild;
        Unloaded += (_, _) => controls.Changed -= Rebuild;
        Rebuild();
        _ = controls.Refresh();
    }

    private void Rebuild()
    {
        root.Children.Clear();
        if (page is { } current)
        {
            var back = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "", FontSize = 12 }, new TextBlock { Text = "更多功能" } } },
                Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            };
            back.Click += (_, _) =>
            {
                page = null;
                controls.Status = null;
                Rebuild();
            };
            var header = new Grid { ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) } } };
            header.Children.Add(back);
            var title = new TextBlock { Text = Title(current), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            root.Children.Add(header);
            var body = current switch
            {
                Page.Location => LocationPage(),
                Page.Battery => BatteryPage(),
                Page.Phone => PhonePage(),
                Page.Brightness => BrightnessPage(),
                Page.Snapshots => SnapshotsPage(),
                _ => PosturePage(),
            };
            if (body is Control control) control.IsEnabled = !controls.Busy;
            root.Children.Add(body);
        }
        else
        {
            var header = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            header.Children.Add(new TextBlock { Text = "更多功能", FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock { Text = controls.DeviceName, FontSize = 12, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(header);
            root.Children.Add(Tiles());
            root.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
            // The emulator's own extended controls are Qt windows; a headless emulator has none.
            root.Children.Add(new StackPanel
            {
                Margin = new Thickness(4, 0, 4, 0),
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "全部扩展控制…", Opacity = 0.65 },
                    new TextBlock { Text = "无窗口模式下的模拟器没有自带的扩展控制窗口。", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap },
                },
            });
        }
        if (controls.Status is { } message)
        {
            var line = new TextBlock { Text = message.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0), Opacity = message.IsError ? 1 : 0.65 };
            if (message.IsError) line.Foreground = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
            root.Children.Add(line);
        }
    }

    private UIElement Tiles()
    {
        var loaded = controls.Loaded;
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var tiles = new (string Glyph, string Title, string Status, bool IsOn, bool Enabled, Action Action)[]
        {
            ("", "位置", loaded ? controls.LocationSummary : "…", false, true, () => Open(Page.Location)),
            ("", "电池", loaded ? controls.BatterySummary : "…", false, true, () => Open(Page.Battery)),
            ("", "剪贴板同步", controls.ClipboardSync ? "已开启" : "已关闭", controls.ClipboardSync, true, () =>
            {
                controls.Status = null;
                controls.ClipboardSync = !controls.ClipboardSync;
            }),
            ("", "来电与短信", "模拟", false, true, () => Open(Page.Phone)),
            ("", "指纹", "轻触一次", false, true, () => _ = controls.TouchFingerprint()),
            ("", "麦克风", controls.UsesMicrophone ? "使用电脑麦克风" : "已关闭", controls.UsesMicrophone, true, () => _ = controls.SetUsesMicrophone(!controls.UsesMicrophone)),
            ("", "亮度", loaded ? controls.BrightnessSummary : "…", false, true, () => Open(Page.Brightness)),
            ("", "快照", loaded ? $"{controls.Snapshots.Count} 个" : "…", false, true, () => Open(Page.Snapshots)),
            ("", "折叠姿态", controls.IsFoldable ? DeviceControls.PostureName(controls.Posture) : "此设备不支持", false, controls.IsFoldable, () => Open(Page.Posture)),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = Tile(tiles[i].Glyph, tiles[i].Title, tiles[i].Status, tiles[i].IsOn, tiles[i].Enabled, tiles[i].Action);
            Grid.SetColumn(tile, i % 3);
            Grid.SetRow(tile, i / 3);
            grid.Children.Add(tile);
        }
        return grid;
    }

    internal static Button Tile(string glyph, string title, string status, bool isOn, bool enabled, Action action)
    {
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var circle = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = isOn ? accent : (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = new FontIcon { Glyph = glyph, FontSize = 15 },
        };
        if (isOn) ((FontIcon)circle.Child).Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinHeight = 84,
            Padding = new Thickness(2, 6, 2, 6),
            IsEnabled = enabled,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            CornerRadius = new CornerRadius(12),
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    circle,
                    new TextBlock { Text = title, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = status, FontSize = 11, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 92 },
                },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{title}：{status}");
        button.Click += (_, _) => action();
        return button;
    }

    private void Open(Page page)
    {
        controls.Status = null;
        this.page = page;
        Rebuild();
    }

    // Pages

    private UIElement LocationPage()
    {
        var places = new MenuFlyout();
        foreach (var place in DeviceControls.Places)
        {
            var item = new MenuFlyoutItem { Text = place.Name };
            item.Click += (_, _) => _ = controls.SetLocation(place);
            places.Items.Add(item);
        }
        var latitude = new TextBox { Text = controls.Latitude, Width = 220 };
        latitude.TextChanged += (_, _) => controls.Latitude = latitude.Text;
        var longitude = new TextBox { Text = controls.Longitude, Width = 220 };
        longitude.TextChanged += (_, _) => controls.Longitude = longitude.Text;
        var apply = new Button { Content = "设置位置", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        apply.Click += (_, _) => _ = controls.ApplyLocation();
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new DropDownButton { Content = "常用地点", Flyout = places },
                Row("纬度", latitude),
                Row("经度", longitude),
                apply,
            },
        };
    }

    private UIElement BatteryPage()
    {
        var label = new TextBlock { Text = $"{(int)controls.BatteryLevel}%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = controls.BatteryLevel, Width = 190 };
        slider.ValueChanged += (_, e) =>
        {
            controls.BatteryLevel = e.NewValue;
            label.Text = $"{(int)e.NewValue}%";
        };
        // Applied when the slider is let go, as the macOS slider's editing ends.
        slider.PointerCaptureLost += (_, _) => _ = controls.ApplyBattery();
        slider.KeyUp += (_, _) => _ = controls.ApplyBattery();
        var chargers = new (string, BatteryState.Types.BatteryCharger)[]
        {
            ("未充电", BatteryState.Types.BatteryCharger.None), ("交流电", BatteryState.Types.BatteryCharger.Ac),
            ("USB", BatteryState.Types.BatteryCharger.Usb), ("无线", BatteryState.Types.BatteryCharger.Wireless),
        };
        var charger = new ComboBox { Width = 190 };
        foreach (var (name, value) in chargers)
        {
            charger.Items.Add(name);
            if (value == controls.Charger) charger.SelectedIndex = charger.Items.Count - 1;
        }
        charger.SelectionChanged += (_, _) =>
        {
            if (charger.SelectedIndex < 0) return;
            controls.Charger = chargers[charger.SelectedIndex].Item2;
            _ = controls.ApplyBattery();
        };
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Row("电量", new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, label } }),
                Row("充电", charger),
            },
        };
    }

    private UIElement PhonePage()
    {
        var number = new TextBox { Text = controls.PhoneNumber, Width = 220 };
        number.TextChanged += (_, _) => controls.PhoneNumber = number.Text;
        Button Call(string title, PhoneCall.Types.Operation operation, string? tip = null)
        {
            var button = new Button { Content = title };
            if (tip != null) ToolTipService.SetToolTip(button, tip);
            button.Click += (_, _) => _ = controls.Phone(operation);
            return button;
        }
        var sms = new TextBox { Text = controls.SmsText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, PlaceholderText = "短信内容" };
        var send = new Button { Content = "发送到设备", HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = controls.SmsText.Length > 0 };
        sms.TextChanged += (_, _) =>
        {
            controls.SmsText = sms.Text;
            send.IsEnabled = sms.Text.Length > 0;
        };
        send.Click += (_, _) => _ = controls.ReceiveSms();
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Row("号码", number),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Call("来电", PhoneCall.Types.Operation.InitCall), Call("对方接听", PhoneCall.Types.Operation.AcceptCall, "设备拨出电话后，模拟对方接听"), Call("挂断", PhoneCall.Types.Operation.DisconnectCall) },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { Call("保持", PhoneCall.Types.Operation.PlaceCallOnHold), Call("恢复", PhoneCall.Types.Operation.TakeCallOffHold) },
                },
                new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] },
                new TextBlock { Text = "收到短信", Opacity = 0.65 },
                sms,
                send,
            },
        };
    }

    private UIElement BrightnessPage()
    {
        var label = new TextBlock { Text = controls.BrightnessSummary, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 255, Value = controls.Brightness, Width = 180, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            controls.Brightness = e.NewValue;
            label.Text = controls.BrightnessSummary;
        };
        slider.PointerCaptureLost += (_, _) => _ = controls.ApplyBrightness();
        slider.KeyUp += (_, _) => _ = controls.ApplyBrightness();
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.65 },
                slider,
                new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.65 },
                label,
            },
        };
    }

    private UIElement SnapshotsPage()
    {
        var panel = new StackPanel { Spacing = 10 };
        if (controls.Snapshots.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "还没有快照。", Opacity = 0.65 });
        }
        else
        {
            var list = new StackPanel();
            foreach (var snapshot in controls.Snapshots)
            {
                if (list.Children.Count > 0) list.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
                list.Children.Add(SnapshotRow(snapshot));
            }
            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220 });
        }
        panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
        var name = new TextBox { Text = controls.NewSnapshotName, PlaceholderText = "新快照名称（可不填）", Width = 170 };
        name.TextChanged += (_, _) => controls.NewSnapshotName = name.Text;
        var save = new Button { Content = "保存当前状态" };
        save.Click += (_, _) => _ = controls.SaveSnapshot();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { name, save } });
        if (controls.Busy)
        {
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new ProgressRing { Width = 14, Height = 14, IsActive = true }, new TextBlock { Text = "正在处理快照，可能需要十几秒…", FontSize = 12, Opacity = 0.65 } },
            });
        }
        return panel;
    }

    private UIElement SnapshotRow(DeviceControls.Snapshot snapshot)
    {
        var parts = new List<string>();
        if (snapshot.Created is { } created) parts.Add(created.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        parts.Add(Sizes.Format(snapshot.Size));
        if (!snapshot.IsCompatible) parts.Add("与当前设备不兼容");
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new TextBlock { Text = snapshot.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 } } };
        if (snapshot.IsLoaded) title.Children.Add(new TextBlock { Text = "当前", FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Bottom });
        var info = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                title,
                Details(string.Join(" · ", parts), snapshot.IsCompatible),
            },
        };
        var restore = new Button { Content = "恢复", IsEnabled = snapshot.IsCompatible };
        restore.Click += (_, _) => _ = controls.LoadSnapshot(snapshot);
        var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, "删除快照");
        var confirm = new Button { Content = "删除", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = $"删除快照“{snapshot.Name}”？" }, confirm } } };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            _ = controls.DeleteSnapshot(snapshot);
        };
        delete.Flyout = flyout;
        var row = new Grid { Padding = new Thickness(0, 6, 0, 6), ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(info);
        Grid.SetColumn(restore, 1);
        row.Children.Add(restore);
        Grid.SetColumn(delete, 2);
        row.Children.Add(delete);
        return row;
    }

    private static TextBlock Details(string text, bool compatible)
    {
        var block = new TextBlock { Text = text, FontSize = 12, Opacity = compatible ? 0.65 : 1 };
        if (!compatible) block.Foreground = new SolidColorBrush(Color.FromArgb(255, 0xC8, 0x6A, 0x00));
        return block;
    }

    private UIElement PosturePage()
    {
        var radios = new RadioButtons();
        foreach (var posture in controls.Postures)
        {
            radios.Items.Add(DeviceControls.PostureName(posture));
            if (posture == controls.Posture) radios.SelectedIndex = radios.Items.Count - 1;
        }
        radios.SelectionChanged += (_, _) =>
        {
            if (radios.SelectedIndex >= 0 && controls.Postures[radios.SelectedIndex] != controls.Posture)
                _ = controls.SetPosture(controls.Postures[radios.SelectedIndex]);
        };
        return radios;
    }

    private static UIElement Row(string label, UIElement content)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn((FrameworkElement)content, 1);
        grid.Children.Add(content);
        return grid;
    }
}
