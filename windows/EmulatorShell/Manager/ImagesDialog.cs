using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmulatorShell;

/// The system images: installed, broken and downloadable ones, for this computer's processor or, with
/// 显示全部平台的镜像 on, for every one, each with its platform.
sealed partial class ImagesDialog : ContentDialog
{
    private readonly DeviceManager manager;
    private ImageFamily? family;
    private bool showPreview;
    /// Off, only images this computer's emulator runs are listed.
    private bool showAllPlatforms;
    private readonly Grid table = new();
    private readonly TextBlock footer = new() { FontSize = 12, Opacity = 0.65 };
    private readonly StackPanel rows = new();
    private readonly TextBlock header = new() { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing loading = new() { Width = 14, Height = 14, IsActive = true };

    public ImagesDialog(DeviceManager manager)
    {
        this.manager = manager;
        Title = "系统镜像";
        CloseButtonText = "完成";
        DefaultButton = ContentDialogButton.Close;

        var families = new SelectorBar();
        foreach (var (title, value) in new (string, ImageFamily?)[] { ("全部", null), (ImageFamily.Phone.Title(), ImageFamily.Phone), (ImageFamily.Wear.Title(), ImageFamily.Wear),
                                                                       (ImageFamily.Tv.Title(), ImageFamily.Tv), (ImageFamily.Auto.Title(), ImageFamily.Auto) })
        {
            var item = new SelectorBarItem { Text = title, Tag = value };
            families.Items.Add(item);
            if (value == null) families.SelectedItem = item;
        }
        families.SelectionChanged += (_, _) =>
        {
            family = families.SelectedItem?.Tag as ImageFamily?;
            Rebuild();
        };
        var preview = new ToggleSwitch { Header = null, OnContent = "显示预览版和 Canary", OffContent = "显示预览版和 Canary", HorizontalAlignment = HorizontalAlignment.Right };
        preview.Toggled += (_, _) =>
        {
            showPreview = preview.IsOn;
            Rebuild();
        };
        var platforms = new ToggleSwitch { Header = null, OnContent = "显示全部平台的镜像", OffContent = "显示全部平台的镜像", HorizontalAlignment = HorizontalAlignment.Right };
        platforms.Toggled += (_, _) =>
        {
            showAllPlatforms = platforms.IsOn;
            Rebuild();
        };
        var filters = new Grid { ColumnSpacing = 24 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.Children.Add(families);
        Grid.SetColumn(platforms, 1);
        filters.Children.Add(platforms);
        Grid.SetColumn(preview, 2);
        filters.Children.Add(preview);

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { loading, header } };
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(420) });
        table.Children.Add(HeaderRow());
        table.Children.Add(WithRow(new ScrollViewer { Content = rows }, 1));
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = table,
        };
        Content = new StackPanel
        {
            Width = 820,
            Spacing = 12,
            Children =
            {
                top,
                filters,
                card,
                footer,
            },
        };
        manager.Changed += Rebuild;
        Closed += (_, _) => manager.Changed -= Rebuild;
        Opened += (_, _) => manager.RefreshAvailableImages();
        Rebuild();
    }

    private static FrameworkElement WithRow(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        return element;
    }

    /// Version, kind, the platform when every one is listed, status and the row's button.
    private Grid Columns()
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 8, 14, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (showAllPlatforms) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        return grid;
    }

    private UIElement HeaderRow()
    {
        var grid = Columns();
        string[] titles = showAllPlatforms ? ["版本", "类型", "平台", "状态", ""] : ["版本", "类型", "状态", ""];
        for (var i = 0; i < titles.Length; i++)
        {
            var text = new TextBlock { Text = titles[i], FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.65 };
            Grid.SetColumn(text, i);
            grid.Children.Add(text);
        }
        return grid;
    }

    private void Rebuild()
    {
        loading.Visibility = manager.LoadingAvailableImages ? Visibility.Visible : Visibility.Collapsed;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        header.Text = manager.LoadingAvailableImages ? "正在获取可下载列表…"
            : $"SDK 位置：{Sdk.Root.Replace(home, "~")} · 本机：{Sdk.HostLabel}";
        footer.Text = "正被模拟器使用的镜像不能删除。" + (showAllPlatforms ? $"这台电脑只能运行 {Sdk.HostLabel} 平台的镜像。" : "");
        table.Children[0] = HeaderRow();
        rows.Children.Clear();
        var visible = manager.Images.Where(i => (family == null || i.Family == family) && (showPreview || !i.IsPreview) && i.Family != ImageFamily.Other
                                                 && (showAllPlatforms || i.RunsHere));
        foreach (var image in visible)
        {
            if (rows.Children.Count > 0) rows.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
            var row = Columns();
            row.Children.Add(new TextBlock { Text = image.ApiLabel, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var kind = new TextBlock { Text = image.KindLabel, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(kind, 1);
            row.Children.Add(kind);
            var next = 2;
            if (showAllPlatforms)
            {
                var platform = Platform(image);
                Grid.SetColumn(platform, next++);
                row.Children.Add(platform);
            }
            var status = Status(image);
            Grid.SetColumn((FrameworkElement)status, next++);
            row.Children.Add(status);
            var action = Action(image);
            Grid.SetColumn(action, next);
            row.Children.Add(action);
            rows.Children.Add(row);
        }
    }

    /// The image's processor; one this computer's emulator cannot run is marked.
    private static FrameworkElement Platform(SystemImage image)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Border
        {
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(9),
            Background = (Brush)Application.Current.Resources[image.RunsHere ? "AccentFillColorSelectedTextBackgroundBrush" : "ControlAltFillColorTertiaryBrush"],
            Child = new TextBlock
            {
                Text = image.PlatformLabel, FontSize = 12, FontFamily = new FontFamily("Consolas"),
                Foreground = image.RunsHere ? new SolidColorBrush(Microsoft.UI.Colors.White) : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            },
        });
        if (!image.RunsHere)
        {
            var warning = new FontIcon { Glyph = "\uE7BA", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF7, 0x93, 0x0E)) };
            ToolTipService.SetToolTip(warning, $"这台电脑是 {Sdk.HostLabel}，运行不了 {image.PlatformLabel} 平台的镜像。");
            panel.Children.Add(warning);
        }
        return panel;
    }

    private UIElement Status(SystemImage image)
    {
        if (manager.Downloads.TryGetValue(image.Id, out var progress))
        {
            var grid = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.Children.Add(new ProgressBar { Value = progress * 100, VerticalAlignment = VerticalAlignment.Center });
            var percent = new TextBlock { Text = $"{(int)(progress * 100)}%", FontSize = 12, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(percent, 1);
            grid.Children.Add(percent);
            return grid;
        }
        return image.Status switch
        {
            ImageStatus.Installed => new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Text = string.Join(" · ", new[] { "已安装", manager.ImageSizes.TryGetValue(image.Id, out var size) ? Sizes.Format(size) : null, manager.IsInUse(image) ? "使用中" : null }.OfType<string>()),
            },
            ImageStatus.Broken => new TextBlock { Text = "安装不完整", VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromArgb(255, 0xC8, 0x6A, 0x00)) },
            _ => new TextBlock { Text = "可下载", Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private Button Action(SystemImage image)
    {
        Button Make(string title, Action action, bool enabled = true)
        {
            var button = new Button { Content = title, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += (_, _) => action();
            return button;
        }
        if (manager.Downloads.ContainsKey(image.Id)) return Make("取消", () => manager.CancelDownload(image));
        return image.Status switch
        {
            ImageStatus.Installed => Make("删除", () => manager.Uninstall(image), !manager.IsInUse(image) && !manager.Busy.Contains(image.Id)),
            ImageStatus.Broken => Make("重新下载", () => manager.Download(image)),
            _ => Make("下载", () => manager.Download(image)),
        };
    }
}
