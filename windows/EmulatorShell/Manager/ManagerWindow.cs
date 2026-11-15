using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace EmulatorShell;

/// The device manager window: phones and tablets connected over USB or Wi-Fi, then AVDs grouped by category on the
/// left, the selected one's details on the right.
sealed partial class ManagerWindow : Window
{
    /// The dialog showing, for the debug test script.
    private ContentDialog? currentDialog;

    public event Action? QuitRequested;

    private readonly DeviceManager manager;
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(0, 4, 0, 4) };
    private readonly TextBox filter = new() { PlaceholderText = "筛选" };
    private readonly Button removeButton;
    private readonly Grid detail = new();
    private readonly TextBlock emptyFilter = new() { Opacity = 0.65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), Visibility = Visibility.Collapsed };
    private readonly SemaphoreSlim dialogs = new(1, 1);
    private string sidebarSignature = "";
    private string detailSignature = "";
    private bool updatingList;
    private DevicePreview? livePreview;
    private readonly DispatcherQueueTimerHolder previewTimer;

    private const string ForwarderWantedKey = "forwarderWanted";
    private const string PlacementKey = "managerWindow";

    public ManagerWindow(DeviceManager manager)
    {
        this.manager = manager;
        previewTimer = new DispatcherQueueTimerHolder(this);
        Title = "设备";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        // Title bar: the app's name where it can be dragged, then the toolbar.
        var dragArea = new Grid { Padding = new Thickness(16, 0, 0, 0) };
        dragArea.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png"))), Width = 18, Height = 18 },
                new TextBlock { Text = "设备", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center },
            },
        });
        var pairing = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "\uE701", FontSize = 14 }, new TextBlock { Text = "Wi-Fi 配对" } } },
            Margin = new Thickness(0, 0, 6, 0),
        };
        ToolTipService.SetToolTip(pairing, "通过 Wi-Fi 配对 Android 11 及以上的设备  Ctrl+Shift+N");
        pairing.Click += (_, _) => _ = ShowPairing();
        var images = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "", FontSize = 14 }, new TextBlock { Text = "系统镜像" } } },
            Margin = new Thickness(0, 0, 6, 0),
        };
        ToolTipService.SetToolTip(images, "下载或删除系统镜像");
        images.Click += (_, _) => _ = ShowImages(null);
        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Flyout = AppMenu() };
        ToolTipService.SetToolTip(more, "Emulator Shell");
        var titleBar = new Grid { Height = 48 };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
        titleBar.Children.Add(dragArea);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { pairing, images, more } };
        Grid.SetColumn(toolbar, 1);
        titleBar.Children.Add(toolbar);
        SetTitleBar(dragArea);

        // Sidebar
        var add = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        ToolTipService.SetToolTip(add, "新建模拟器  Ctrl+N");
        add.Click += (_, _) => _ = ShowForm(manager.NewForm());
        removeButton = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        ToolTipService.SetToolTip(removeButton, "删除所选模拟器");
        removeButton.Click += (_, _) =>
        {
            if (manager.SelectedAvd is { } avd) _ = ConfirmDelete(avd);
        };
        filter.TextChanged += (_, _) => RebuildSidebar(force: true);
        var bottomBar = new Grid { Padding = new Thickness(8), ColumnSpacing = 4 };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.Children.Add(add);
        Grid.SetColumn(removeButton, 1);
        bottomBar.Children.Add(removeButton);
        Grid.SetColumn(filter, 2);
        bottomBar.Children.Add(filter);
        list.SelectionChanged += (_, _) =>
        {
            if (updatingList || list.SelectedItem is not ListViewItem { Tag: string id }) return;
            manager.Selection = id;
            manager.Note = null;
            Render();
        };
        list.DoubleTapped += (_, _) =>
        {
            if (manager.SelectedPhysical is { IsOnline: true } phone)
            {
                manager.Mirror(phone);
                return;
            }
            if (manager.SelectedAvd is not { } avd) return;
            if (manager.Status(avd).IsStopped) manager.Start(avd); else manager.ShowWindow(avd);
        };
        var sidebar = new Grid { Width = 272 };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.Children.Add(list);
        sidebar.Children.Add(emptyFilter);
        Grid.SetRow(bottomBar, 1);
        sidebar.Children.Add(bottomBar);

        var detailCard = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(8, 0, 0, 0),
            Child = new Grid { Children = { detail, toastHost } },
        };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(sidebar);
        Grid.SetColumn(detailCard, 1);
        body.Children.Add(detailCard);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(titleBar);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.N, () => _ = ShowForm(manager.NewForm())));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.E, () =>
        {
            if (manager.SelectedAvd is { } avd && manager.Status(avd).IsStopped) _ = ShowForm(manager.EditForm(avd));
        }));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.O, () =>
        {
            if (manager.SelectedPhysical is { IsOnline: true } phone) manager.Mirror(phone);
            else if (manager.SelectedAvd is { } avd) manager.Start(avd);
        }));
        // In the manager, screenshots and recording work on the selected phone.
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.S, () =>
        {
            if (manager.SelectedPhysical is { IsOnline: true } phone) manager.Phones.Screenshot(phone.Id);
        }));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.R, () =>
        {
            if (manager.SelectedPhysical is { IsOnline: true } phone) manager.Phones.ToggleRecording(phone.Id);
        }, shift: true));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.N, () => _ = ShowPairing(), shift: true));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Delete, () =>
        {
            if (manager.SelectedAvd is { } avd && manager.Status(avd).IsStopped) _ = ConfirmDelete(avd);
        }));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.Q, () => QuitRequested?.Invoke()));
        root.KeyboardAccelerators.Add(Accelerator(VirtualKey.W, Close));
        Content = root;

        manager.Changed += Render;
        manager.Error += message => _ = Alert("操作没有完成", message);
        // AVDs and images may have been changed by Android Studio or the command line meanwhile.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) manager.Reload();
        };
        Closed += (_, _) =>
        {
            manager.Changed -= Render;
            previewTimer.Stop();
            SavePlacement();
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 560;
        }
        RestorePlacement();
        StartPhysical();
        Render();
        RunTestScript();
    }

    /// See ManagerWindow.Debug.cs.
    partial void RunTestScript();

    private static KeyboardAccelerator Accelerator(VirtualKey key, Action action, bool shift = false)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control | (shift ? VirtualKeyModifiers.Shift : 0) };
        accelerator.Invoked += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return accelerator;
    }

    private void RestorePlacement()
    {
        var scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (Settings.Get(PlacementKey) is JsonObject saved && saved["x"] is { } x && saved["y"] is { } y && saved["width"] is { } w && saved["height"] is { } h)
        {
            var rect = new Windows.Graphics.RectInt32((int)x.GetValue<double>(), (int)y.GetValue<double>(), (int)w.GetValue<double>(), (int)h.GetValue<double>());
            if (Screen.All.Any(s => s.WorkArea.Contains(rect.X + 40, rect.Y + 20)))
            {
                AppWindow.MoveAndResize(rect);
                return;
            }
        }
        var work = Screen.Primary.WorkArea;
        var size = new Windows.Graphics.SizeInt32((int)(1120 * scale), (int)(720 * scale));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32((int)(work.MidX - size.Width / 2.0), (int)(work.MidY - size.Height / 2.0), size.Width, size.Height));
    }

    private void SavePlacement()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
        Settings.Set(PlacementKey, new JsonObject
        {
            ["x"] = AppWindow.Position.X, ["y"] = AppWindow.Position.Y, ["width"] = AppWindow.Size.Width, ["height"] = AppWindow.Size.Height,
        });
    }

    private void Render()
    {
        RebuildSidebar(force: false);
        RebuildDetail();
        UpdateToast();
    }

    // Sidebar

    private List<(DeviceCategory Category, List<Avd> Avds)> Sections
    {
        get
        {
            var text = filter.Text.Trim();
            var visible = manager.Avds.Where(a => text.Length == 0 || a.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
            return CategoryInfo.All
                .Select(c => (c, visible.Where(a => manager.CategoryOf(a) == c).ToList()))
                .Where(s => s.Item2.Count > 0).ToList();
        }
    }

    private void RebuildSidebar(bool force)
    {
        var sections = Sections;
        var signature = PhysicalSignature + "/" + string.Join("|", sections.SelectMany(s => s.Avds.Select(a => $"{s.Category}:{a.Id}:{a.Name}:{Subtitle(a)}:{manager.Status(a)}")))
                        + "#" + manager.Selection;
        if (!force && signature == sidebarSignature) return;
        sidebarSignature = signature;
        updatingList = true;
        list.Items.Clear();
        AddPhysicalItems();
        foreach (var (category, avds) in sections)
        {
            list.Items.Add(new ListViewItem
            {
                IsEnabled = false,
                MinHeight = 28,
                Padding = new Thickness(16, 10, 12, 2),
                Content = new TextBlock { Text = category.Title(), FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.7 },
            });
            foreach (var avd in avds)
            {
                var item = new ListViewItem { Tag = avd.Id, Content = Row(avd), Padding = new Thickness(12, 4, 12, 4) };
                item.ContextFlyout = ActionsMenu(avd, includeStart: true);
                list.Items.Add(item);
                if (avd.Id == manager.Selection) list.SelectedItem = item;
            }
        }
        updatingList = false;
        emptyFilter.Text = $"没有名称包含“{filter.Text}”的模拟器";
        emptyFilter.Visibility = filter.Text.Length > 0 && sections.Count == 0 && VisiblePhones.Count == 0 && manager.Avds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        removeButton.IsEnabled = manager.SelectedAvd is { } selected && manager.Status(selected).IsStopped;
    }

    private UIElement Row(Avd avd)
    {
        var status = manager.Status(avd);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = manager.CategoryOf(avd).Glyph(), FontSize = 17 });
        var text = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = avd.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = Subtitle(avd), FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (DotColor(status) is { } color)
        {
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);
        }
        return grid;
    }

    private string Subtitle(Avd avd)
    {
        if (Label(manager.Status(avd)) is { } label) return label;
        if (manager.ImageOf(avd) is not { } image) return "系统镜像缺失";
        return $"{image.ApiLabel} · {image.KindLabel.Split(" · ")[0]}";
    }

    private static string? Label(AvdStatus status) => status.Kind switch
    {
        AvdStatusKind.Booting => "正在启动…",
        AvdStatusKind.Stopping => "正在停止…",
        AvdStatusKind.Running => "运行中",
        AvdStatusKind.External => "在其他程序中运行",
        _ => null,
    };

    private static Color? DotColor(AvdStatus status) => status.Kind switch
    {
        AvdStatusKind.Booting or AvdStatusKind.Stopping => Color.FromArgb(255, 0xF7, 0x93, 0x0E),
        AvdStatusKind.Running or AvdStatusKind.External => Color.FromArgb(255, 0x2E, 0xB8, 0x4F),
        _ => null,
    };

    /// The per-device actions, shared by the "⋯" menu and the sidebar's context menu.
    private MenuFlyout ActionsMenu(Avd avd, bool includeStart)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var stopped = manager.Status(avd).IsStopped;
            void Add(string text, bool enabled, Action action, string? glyph = null)
            {
                var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
                if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            if (includeStart)
            {
                if (stopped) Add("启动", true, () => manager.Start(avd), "");
                else Add("停止", true, () => manager.Stop(avd), "");
            }
            Add("冷启动", stopped, () => manager.Start(avd, coldBoot: true));
            if (includeStart) Add("编辑…", stopped, () => _ = ShowForm(manager.EditForm(avd)), "");
            Add("复制", true, () => manager.Duplicate(avd), "");
            Add("在资源管理器中显示", true, () => manager.Reveal(avd), "");
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("擦除数据…", stopped, () => _ = ConfirmWipe(avd));
            Add("删除…", stopped, () => _ = ConfirmDelete(avd), "");
        };
        return menu;
    }

    // Detail

    private void RebuildDetail()
    {
        if (manager.Selection?.StartsWith("device:") == true)
        {
            var physicalSignature = PhysicalDetailSignature;
            if (physicalSignature == detailSignature) return;
            detailSignature = physicalSignature;
            detail.Children.Clear();
            livePreview = null;
            previewTimer.Stop();
            RebuildPhysicalDetail();
            return;
        }
        StopPreview();
        detailClocks.Clear();
        var avd = manager.SelectedAvd;
        var status = avd == null ? null : manager.Status(avd);
        var signature = avd == null ? $"none:{manager.Avds.Count}"
            : $"{avd.Id}:{avd.GetHashCode()}:{string.Join(",", avd.Config.Select(c => c.Key + c.Value))}:{status}:{manager.Note}:{manager.Sizes.GetValueOrDefault(avd.Id)}:{manager.DeviceTypes.Count}:{manager.Images.Count}";
        if (signature == detailSignature) return;
        detailSignature = signature;
        detail.Children.Clear();
        livePreview = null;
        previewTimer.Stop();

        if (manager.Avds.Count == 0)
        {
            var create = new Button { Content = "新建模拟器", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Center };
            create.Click += (_, _) => _ = ShowForm(manager.NewForm());
            detail.Children.Add(new StackPanel
            {
                Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 40, Opacity = 0.5 },
                    new TextBlock { Text = "还没有模拟器", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "新建一个模拟器，选择设备类型和系统镜像即可开始。", Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center },
                    create,
                },
            });
            return;
        }
        if (avd == null || status == null)
        {
            detail.Children.Add(new TextBlock { Text = "选择一个模拟器查看详情", Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return;
        }

        var landscape = manager.CategoryOf(avd).IsLandscape();
        var preview = new DevicePreview { Width = landscape ? 250 : 120, Height = landscape ? 170 : 250 };
        preview.Show(avd, manager.DeviceTypeOf(avd), null);
        if (status.Kind == AvdStatusKind.Running)
        {
            livePreview = preview;
            void Refresh()
            {
                if (livePreview == preview) preview.Show(avd, manager.DeviceTypeOf(avd), manager.Thumbnail(avd));
            }
            Refresh();
            previewTimer.StartRepeating(TimeSpan.FromSeconds(1), Refresh);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in PrimaryButtons(avd, status)) buttons.Children.Add(button);
        var edit = new Button { Content = "编辑…", IsEnabled = status.IsStopped };
        edit.Click += (_, _) => _ = ShowForm(manager.EditForm(avd));
        buttons.Children.Add(edit);
        var more = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Flyout = ActionsMenu(avd, includeStart: false) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, "更多操作");
        buttons.Children.Add(more);

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
                        new TextBlock { Text = avd.Name, FontSize = 24, FontWeight = FontWeights.Bold, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = Headline(avd), Opacity = 0.65, TextWrapping = TextWrapping.Wrap },
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 7,
                    Children =
                    {
                        new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
                                     Background = new SolidColorBrush(DotColor(status) ?? Color.FromArgb(255, 0x8A, 0x8A, 0x8A)) },
                        new TextBlock { Text = StatusText(avd, status) },
                    },
                },
                buttons,
            },
        };
        if (manager.Note is { } note) heading.Children.Add(new TextBlock { Text = note, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });

        var top = new Grid { ColumnSpacing = 26 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(preview);
        Grid.SetColumn(heading, 1);
        top.Children.Add(heading);

        var groups = new Grid { ColumnSpacing = 20 };
        groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Spacing = 18, Children = { InfoGroup("设备", DeviceRows(avd)), InfoGroup("系统", SystemRows(avd)) } };
        var right = new StackPanel { Spacing = 18, Children = { InfoGroup("硬件", HardwareRows(avd)), InfoGroup("存储", StorageRows(avd)) } };
        groups.Children.Add(left);
        Grid.SetColumn(right, 1);
        groups.Children.Add(right);

        // As wide as the pane, up to 900 DIPs, from its leading edge.
        var column = new Grid { Padding = new Thickness(28, 24, 28, 24) };
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 900 });
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        column.Children.Add(new StackPanel { Spacing = 22, Children = { top, groups } });
        detail.Children.Add(new ScrollViewer { Content = column });
    }

    private IEnumerable<Button> PrimaryButtons(Avd avd, AvdStatus status)
    {
        Button Labeled(string glyph, string text, bool accent = false)
        {
            var button = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 13 }, new TextBlock { Text = text } } } };
            if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            return button;
        }
        switch (status.Kind)
        {
            case AvdStatusKind.Stopped:
                var start = Labeled("", "启动", accent: true);
                start.Click += (_, _) => manager.Start(avd);
                yield return start;
                break;
            case AvdStatusKind.Booting:
                yield return new Button { Content = "正在启动…", IsEnabled = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                break;
            case AvdStatusKind.Stopping:
                yield return new Button { Content = "正在停止…", IsEnabled = false };
                break;
            case AvdStatusKind.Running:
                var stop = Labeled("", "停止");
                stop.Click += (_, _) => manager.Stop(avd);
                yield return stop;
                var show = new Button { Content = "显示窗口" };
                show.Click += (_, _) => manager.ShowWindow(avd);
                yield return show;
                break;
            case AvdStatusKind.External when manager.IsInOtherEmulatorShell(avd):
                var stopShown = Labeled("", "停止");
                stopShown.Click += (_, _) => manager.Stop(avd);
                yield return stopShown;
                var showOwner = new Button { Content = "显示窗口" };
                ToolTipService.SetToolTip(showOwner, "切换到显示这台设备的另一个 Emulator Shell");
                showOwner.Click += (_, _) => manager.ShowInOwner(avd);
                yield return showOwner;
                break;
            case AvdStatusKind.External:
                var problem = manager.AttachProblem(avd);
                var attach = Labeled("", "在本 App 中显示", accent: true);
                attach.IsEnabled = problem == null;
                ToolTipService.SetToolTip(attach, problem ?? "用本 App 的窗口显示和操作这台设备；关掉窗口设备继续运行");
                attach.Click += (_, _) => manager.Attach(avd);
                yield return attach;
                if (manager.Owner(avd) is { } owner)
                {
                    var switchTo = new Button { Content = $"切换到 {owner.DisplayName}" };
                    ToolTipService.SetToolTip(switchTo, "把启动这台设备的程序切换到前台");
                    switchTo.Click += (_, _) => manager.ShowInOwner(avd);
                    yield return switchTo;
                }
                var stopExternal = Labeled("", "停止");
                stopExternal.Click += (_, _) => manager.Stop(avd);
                yield return stopExternal;
                break;
        }
    }

    private string StatusText(Avd avd, AvdStatus status) => status.Kind switch
    {
        AvdStatusKind.Stopped => Directory.Exists(Path.Combine(avd.Folder, "snapshots", "default_boot")) ? "已停止 · 有快速启动快照" : "已停止",
        AvdStatusKind.Booting => "正在启动…",
        AvdStatusKind.Stopping => "正在停止，正在保存快照…",
        AvdStatusKind.Running => "运行中" + (status.Serial is { } s ? $" · {s}" : ""),
        _ => (manager.IsInOtherEmulatorShell(avd) ? "在另一个 Emulator Shell 中运行" : "在其他程序中运行")
             + (status.Serial is { } serial ? $" · {serial}" : ""),
    };

    private string ModelName(Avd avd)
    {
        var type = manager.DeviceTypeOf(avd)?.Name ?? avd.DeviceTypeId ?? "未知设备";
        return string.Join(" ", new[] { avd.Manufacturer, type }.Where(s => s.Length > 0));
    }

    private string Headline(Avd avd) =>
        manager.ImageOf(avd) is { } image ? $"{ModelName(avd)} · {image.ApiLabel} · {image.KindLabel}" : ModelName(avd);

    private (string, string)[] DeviceRows(Avd avd) =>
    [
        ("型号", ModelName(avd)),
        ("分辨率", avd.Resolution ?? "—"),
        ("初始方向", avd.IsLandscape ? "横屏" : "竖屏"),
    ];

    private (string, string)[] SystemRows(Avd avd)
    {
        if (manager.ImageOf(avd) is not { } image) return [("系统镜像", $"缺失：{avd.Sysdir ?? "未设置"}")];
        return
        [
            ("系统镜像", image.ApiLabel + (image.ExtensionLevel is { } level ? $"（扩展级别 {level}）" : "")),
            ("版本类型", image.KindLabel),
            ("架构", avd.Config.GetValueOrDefault("abi.type") ?? image.Abi),
        ];
    }

    private static (string, string)[] HardwareRows(Avd avd)
    {
        var ram = Sizes.Megabytes(avd.Config.GetValueOrDefault("hw.ramSize"));
        var gpu = avd.Config.GetValueOrDefault("hw.gpu.mode") ?? "auto";
        var cameras = new Dictionary<string, string> { ["virtualscene"] = "虚拟场景", ["emulated"] = "模拟", ["webcam0"] = "电脑摄像头", ["none"] = "无" };
        string Camera(string key) => cameras.GetValueOrDefault(avd.Config.GetValueOrDefault(key) ?? "none") ?? "无";
        return
        [
            ("内存", ram is { } r ? $"{r / 1024:0.##} GB" : "—"),
            ("内部存储 / SD 卡", $"{Sizes.FormatConfigSize(avd.Config.GetValueOrDefault("disk.dataPartition.size")) ?? "—"} / {Sizes.FormatConfigSize(avd.Config.GetValueOrDefault("sdcard.size")) ?? "无"}"),
            ("图形", gpu is "host" or "auto" ? "主机 GPU" : "软件渲染"),
            ("摄像头", $"后置 {Camera("hw.camera.back")} · 前置 {Camera("hw.camera.front")}"),
        ];
    }

    private (string, string)[] StorageRows(Avd avd)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            ("启动方式", avd.IsColdBoot ? "每次冷启动" : "快速启动"),
            ("占用空间", manager.Sizes.TryGetValue(avd.Id, out var size) ? Sizes.Format(size) : "计算中…"),
            ("位置", avd.Folder.Replace(home, "~")),
        ];
    }

    private static UIElement InfoGroup(string title, (string Label, string Value)[] rows)
    {
        var table = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) table.Children.Add(new Border { Height = 1, Margin = new Thickness(12, 0, 0, 0), Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
            var row = new Grid { Padding = new Thickness(12, 7, 12, 7), ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = rows[i].Label, Opacity = 0.65 });
            // A path too long for the column keeps its start and its folder's name.
            var value = new TextBlock { Text = rows[i].Value, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
            ToolTipService.SetToolTip(value, rows[i].Value);
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            table.Children.Add(row);
        }
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.65, Margin = new Thickness(4, 0, 0, 0) },
                new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    Child = table,
                },
            },
        };
    }

    // Dialogs

    /// One dialog at a time, as ContentDialog requires; later ones wait their turn. A dialog asked for while the
    /// window is still opening (checking the forwarder at launch) waits for its content to load: without a XamlRoot
    /// ShowAsync throws, and the app would end.
    public async Task<ContentDialogResult> ShowDialog(ContentDialog dialog)
    {
        await dialogs.WaitAsync();
        try
        {
            await ContentLoaded();
            dialog.XamlRoot = Content.XamlRoot;
            dialog.RequestedTheme = ((FrameworkElement)Content).ActualTheme;
            currentDialog = dialog;
            return await dialog.ShowAsync();
        }
        finally
        {
            currentDialog = null;
            dialogs.Release();
        }
    }

    private Task ContentLoaded()
    {
        if (Content.XamlRoot != null) return Task.CompletedTask;
        var loaded = new TaskCompletionSource();
        var content = (FrameworkElement)Content;
        void Loaded(object sender, RoutedEventArgs e)
        {
            content.Loaded -= Loaded;
            loaded.TrySetResult();
        }
        content.Loaded += Loaded;
        return loaded.Task;
    }

    private Task Alert(string title, string message) =>
        ShowDialog(new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "好" });

    private async Task ConfirmDelete(Avd avd)
    {
        var result = await ShowDialog(new ContentDialog
        {
            Title = $"删除“{avd.Name}”？",
            Content = "它的数据和快照会一起删除，无法撤销。系统镜像会保留。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        });
        if (result == ContentDialogResult.Primary) manager.Delete(avd);
    }

    private async Task ConfirmWipe(Avd avd)
    {
        var result = await ShowDialog(new ContentDialog
        {
            Title = $"擦除“{avd.Name}”的数据？",
            Content = "已安装的应用、账号和快照都会被清除，相当于恢复出厂设置。",
            PrimaryButtonText = "擦除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        });
        if (result == ContentDialogResult.Primary) manager.Wipe(avd);
    }

    private async Task ShowForm(AvdForm form)
    {
        var dialog = new AvdFormDialog(manager, form);
        await ShowDialog(dialog);
        // The images dialog was asked for from the form, which comes back when it closes.
        if (dialog.WantsImages) await ShowImages(dialog.Form);
    }

    private async Task ShowImages(AvdForm? formUnder)
    {
        await ShowDialog(new ImagesDialog(manager));
        if (formUnder == null) return;
        var category = manager.DeviceTypes.FirstOrDefault(t => t.Id == formUnder.DeviceTypeId)?.Category ?? DeviceCategory.Phone;
        formUnder.ImageId ??= manager.DefaultImage(category)?.Id;
        await ShowForm(formUnder);
    }

    // SDK forwarder and command-line tool

    private MenuFlyout AppMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var forwarder = new ToggleMenuFlyoutItem
            {
                Text = "让 Android Studio 等工具使用 Emulator Shell",
                IsChecked = EmulatorForwarder.State == EmulatorForwarder.ForwarderState.Installed,
                IsEnabled = EmulatorForwarder.IsAvailable,
            };
            forwarder.Click += (_, _) => _ = ToggleForwarder();
            menu.Items.Add(forwarder);
            var command = new MenuFlyoutItem { Text = EmulatorForwarder.IsCommandInstalled ? "移除命令行工具 emulator-shell" : "安装命令行工具 emulator-shell" };
            command.Click += (_, _) => _ = ToggleCommand();
            menu.Items.Add(command);
            menu.Items.Add(new MenuFlyoutSeparator());
            var quit = new MenuFlyoutItem { Text = "退出 Emulator Shell", KeyboardAcceleratorTextOverride = "Ctrl+Q" };
            quit.Click += (_, _) => QuitRequested?.Invoke();
            menu.Items.Add(quit);
        };
        return menu;
    }

    private async Task ToggleForwarder()
    {
        if (EmulatorForwarder.State == EmulatorForwarder.ForwarderState.Installed)
        {
            if (await Perform("恢复 SDK 原来的模拟器", EmulatorForwarder.Uninstall)) Settings.Set(ForwarderWantedKey, false);
            return;
        }
        var result = await ShowDialog(new ContentDialog
        {
            Title = "让 Android Studio 等工具使用 Emulator Shell？",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Android Studio、Flutter 和 android 命令行都直接运行 SDK 里的 emulator\\emulator.exe。这会把它换成一个转发程序：" +
                       "启动设备时打开 Emulator Shell，其他用途（列出 AVD、查版本、Gradle 的无窗口测试）仍交给原来的模拟器，它会改名为 emulator.real.exe。\n\n" +
                       "Android Studio 里还要关掉 Settings › Tools › Emulator ›「Launch in the Running Devices tool window」。" +
                       "SDK 更新模拟器后需要重新开启；随时可以在这里关闭，恢复原样。",
            },
            PrimaryButtonText = "开启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (result != ContentDialogResult.Primary) return;
        if (await Perform("开启转发", EmulatorForwarder.Install)) Settings.Set(ForwarderWantedKey, true);
    }

    private async Task ToggleCommand()
    {
        if (EmulatorForwarder.IsCommandInstalled)
        {
            await Perform("移除命令行工具", EmulatorForwarder.UninstallCommand);
        }
        else if (await Perform("安装命令行工具", EmulatorForwarder.InstallCommand))
        {
            await Alert($"已安装 {EmulatorForwarder.CommandPath}", "在终端里运行 emulator-shell <AVD 名称> 打开设备，不带参数打开设备管理器。");
        }
    }

    /// What a device window's menu asked for, once the window can show dialogs.
    public async void Run(ManagerTask task)
    {
        if (task == ManagerTask.Show) return;
        await ContentLoaded();
        _ = task == ManagerTask.ToggleForwarder ? ToggleForwarder() : ToggleCommand();
    }

    /// On opening the manager: an SDK update replaces the forwarder with a new emulator, and a rebuilt or moved
    /// app leaves it pointing at a stale path.
    public async void CheckForwarder()
    {
        switch (EmulatorForwarder.State)
        {
            case EmulatorForwarder.ForwarderState.ReplacedBySdkUpdate when Settings.GetBool(ForwarderWantedKey, false):
                var result = await ShowDialog(new ContentDialog
                {
                    Title = "SDK 更新替换了模拟器转发",
                    Content = "Android Studio 等工具现在会启动 SDK 自带的模拟器。要重新让它们使用 Emulator Shell 吗？",
                    PrimaryButtonText = "重新开启",
                    CloseButtonText = "不用了",
                    DefaultButton = ContentDialogButton.Primary,
                });
                if (result == ContentDialogResult.Primary) await Perform("重新开启转发", EmulatorForwarder.Install);
                else Settings.Set(ForwarderWantedKey, false);
                break;
            case EmulatorForwarder.ForwarderState.Installed when !string.Equals(EmulatorForwarder.InstalledAppPath, EmulatorForwarder.AppPath, StringComparison.OrdinalIgnoreCase):
                await Perform("更新转发程序", EmulatorForwarder.Install);
                break;
        }
    }

    private async Task<bool> Perform(string action, Action body)
    {
        try
        {
            body();
            return true;
        }
        catch (Exception error)
        {
            await Alert($"{action}失败", error.Message);
            return false;
        }
    }
}
