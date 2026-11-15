using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace EmulatorShell;

/// What a physical device's "More" flyout shows and changes: the stream's options, kept per device.
sealed class PhysicalControls(string id, string name, DeviceInfo info, bool isWireless, PhysicalDeviceStore store, Func<nint> owner)
{
    public event Action? Changed;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public DeviceInfo Info { get; } = info;
    public bool IsWireless { get; } = isWireless;

    private bool audioAvailable = store.AudioDevices.Contains(id);
    /// Whether the device's sound reaches this PC now.
    public bool AudioAvailable
    {
        get => audioAvailable;
        set
        {
            audioAvailable = value;
            Changed?.Invoke();
        }
    }

    public ScrcpyStream.Options Options
    {
        get => store.OptionsFor(Id);
        set
        {
            store.SetOptions(value, Id);
            Changed?.Invoke();
        }
    }

    public bool CanPlaySound => Info.Sdk >= 30;
    public bool ScreenOff => store.ScreensOff.Contains(Id);
    public bool IsRecording => store.IsRecording(Id);

    public void ToggleScreenOff()
    {
        store.SetScreenOff(!ScreenOff, Id);
        Changed?.Invoke();
    }

    public async void ChooseFiles()
    {
        var picker = new FileOpenPicker { CommitButtonText = "传到手机" };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner());
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) store.Send(files.Select(f => f.Path).ToList(), Id);
    }

    /// The quality presets: the video's long side at most, and its bit rate.
    public static readonly (string Name, int MaxSize, int BitRate)[] Qualities =
        [("流畅", 1280, 4_000_000), ("均衡", 1920, 8_000_000), ("原始", 0, 16_000_000)];
}

/// The "More" flyout of a physical device's window.
sealed partial class PhysicalMorePanel : UserControl
{
    private readonly PhysicalControls controls;
    private readonly StackPanel root = new() { Spacing = 10, Width = 300 };

    public PhysicalMorePanel(PhysicalControls controls)
    {
        this.controls = controls;
        Content = root;
        controls.Changed += Rebuild;
        Unloaded += (_, _) => controls.Changed -= Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        var options = controls.Options;
        root.Children.Clear();
        var header = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        header.Children.Add(new TextBlock { Text = "更多功能", FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = controls.Name, FontSize = 12, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);

        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var tiles = new (string Glyph, string Title, string Status, bool IsOn, bool Enabled, Action Action)[]
        {
            ("", "声音", !controls.CanPlaySound ? "需要 Android 11" : options.Audio ? "在电脑上播放" : "手机自己播放",
             controls.CanPlaySound && options.Audio, controls.CanPlaySound, () => controls.Options = options with { Audio = !options.Audio }),
            ("", "熄灭手机屏幕", controls.ScreenOff ? "已熄灭" : "亮着", controls.ScreenOff, true, controls.ToggleScreenOff),
            ("", "保持唤醒", options.StayAwake ? "投屏时不锁屏" : "已关闭", options.StayAwake, true,
             () => controls.Options = options with { StayAwake = !options.StayAwake }),
            ("", "剪贴板同步", options.ClipboardSync ? "已开启" : "已关闭", options.ClipboardSync, true,
             () => controls.Options = options with { ClipboardSync = !options.ClipboardSync }),
            ("", "显示触摸点", options.ShowTouches ? "已开启" : "已关闭", options.ShowTouches, true,
             () => controls.Options = options with { ShowTouches = !options.ShowTouches }),
            ("", "传文件", "APK 或文件", false, true, controls.ChooseFiles),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = MorePanel.Tile(tiles[i].Glyph, tiles[i].Title, tiles[i].Status, tiles[i].IsOn, tiles[i].Enabled, tiles[i].Action);
            Grid.SetColumn(tile, i % 3);
            Grid.SetRow(tile, i / 3);
            grid.Children.Add(tile);
        }
        root.Children.Add(grid);

        // Quality: three presets side by side, the current one marked.
        var quality = new Grid { Margin = new Thickness(4, 0, 4, 0), RowSpacing = 6 };
        quality.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        quality.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        quality.Children.Add(new TextBlock { Text = "画质" });
        quality.Children.Add(new TextBlock { Text = QualityNote(options), FontSize = 12, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        var presets = new Grid { ColumnSpacing = 4 };
        var current = Array.FindIndex(PhysicalControls.Qualities, q => q.MaxSize == options.MaxSize);
        if (current < 0) current = 1;
        for (var i = 0; i < PhysicalControls.Qualities.Length; i++)
        {
            var preset = PhysicalControls.Qualities[i];
            presets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new Button { Content = preset.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (i == current) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            button.Click += (_, _) => controls.Options = options with { MaxSize = preset.MaxSize, BitRate = preset.BitRate };
            Grid.SetColumn(button, i);
            presets.Children.Add(button);
        }
        Grid.SetRow(presets, 1);
        quality.Children.Add(presets);
        root.Children.Add(quality);

        root.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
        var notes = new StackPanel { Margin = new Thickness(4, 0, 4, 0), Spacing = 3 };
        void Note(string text) => notes.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        Note("把 APK 拖到设备上即安装，其他文件存到手机的“下载”文件夹。按住 Alt 拖动是双指缩放。");
        Note($"{(controls.IsWireless ? "Wi-Fi" : "USB")} · Android {controls.Info.AndroidVersion} · H.264");
        if (controls.IsRecording) Note("录屏中改画质或声音会让画面断开一下，录屏继续。");
        root.Children.Add(notes);
    }

    private static string QualityNote(ScrcpyStream.Options options) =>
        $"{(options.MaxSize == 0 ? "原始分辨率" : $"最长边 {options.MaxSize}")} · {options.BitRate / 1_000_000} Mbit/s";
}
