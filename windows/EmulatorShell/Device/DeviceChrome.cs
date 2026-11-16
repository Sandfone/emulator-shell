using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using PostureValue = Android.Emulation.Control.Posture.Types.PostureValue;

namespace EmulatorShell;

enum DeviceAction { Close, Minimize, Screenshot, CopyScreenshot, Record, RotateLeft, RotateRight, ToggleFold }

/// The controls around the device, inside its window: Back / Home / Recents in a pill under the device,
/// always shown, and a title bar with the remaining buttons that appears while the pointer is near the
/// device's top; for a foldable also a rail at its right with its postures, always shown. The window keeps
/// room for them all, so its size is the size of everything it shows.
sealed class DeviceChrome
{
    /// A device key went down (`true`) or up; the key is a W3C key name.
    public event Action<string, bool>? Key;
    public event Action<DeviceAction>? Action;
    /// A posture on the rail was chosen.
    public event Action<PostureValue>? Posture;
    /// The title bar showed or hid, or another bar did: the window's click region changes.
    public event Action? ShapeChanged;
    /// What the "More" flyout shows and changes; set once the device's gRPC client exists.
    public DeviceControls? Controls { get; set; }
    /// The window's menu, built each time the title bar's menu button opens it.
    public Func<MenuFlyout>? MenuProvider { get; set; }
    /// The "More" flyout's content for a device without `Controls` (a physical one).
    public Func<UIElement>? PanelProvider { get; set; }

    public const double BarHeight = 44;
    public const double Gap = 12;
    private const double PillWidth = 120;
    /// How far into the device the hover band reaches; with the title bar's room it is 140 DIPs tall.
    private const double BandDepth = 84;
    private const double RevealOffset = 6;

    private readonly DeviceWindow window;
    private readonly Canvas container;
    private readonly Border titleBar, pill, hud;
    private readonly TranslateTransform titleShift = new();
    private readonly TextBlock hudLabel = new() { FontSize = 12, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<ToolbarButton> titleButtons = [];
    /// The device's name and details at the title bar's leading edge.
    private readonly StackPanel titleLabels;
    private readonly List<ToolbarButton> navButtons = [];
    private readonly StackPanel navStack;
    private readonly TextBlock bootLabel = new() { Text = "正在启动…", FontSize = 12, Opacity = 0.65, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToolbarButton moreButton;
    /// At the title bar's leading edge: the window's menu, as a right click opens it.
    private readonly ToolbarButton menuButton;
    /// A foldable's postures, open, closed and half open, the one it is in marked.
    private readonly Border? rail;
    private readonly List<(PostureValue Posture, ToolbarButton Button)> postureButtons = [];
    /// The menu or the "More" panel, whichever is open: the title bar stays while it is.
    private FlyoutBase? flyout;
    private Border? toast;
    private readonly DispatcherQueueTimerHolder toastTimer, hudTimer, showTimer, hideTimer, pointerTimer, recordTimer;
    /// A physical device: no rotate buttons (it turns when it is turned), a record button instead.
    private readonly bool physical;
    private readonly ToolbarButton? recordButton;
    /// While recording: when it started, and the badge that shows for how long while the title bar is hidden.
    private DateTime? recordingSince;
    private readonly Border recordBadge;
    private readonly TextBlock recordLabel = new()
    {
        FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
        Foreground = new SolidColorBrush(Color.FromArgb(255, 0xE5, 0x39, 0x35)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    /// Covers the device with a message, e.g. while a phone is disconnected.
    private readonly Border overlay;
    private readonly TextBlock overlayTitle = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock overlayText = new() { FontSize = 13, Foreground = new SolidColorBrush(Colors.White), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
    private bool titleVisible;
    /// Shown whatever the pointer does, while a drag holds the window against the top of the screen.
    private bool titleHeld;
    /// While the window is being resized the title bar stays away and the size readout takes its place.
    private bool resizing;
    private bool closed;
    /// The box the bars are placed in, and the device in it, in the container's coordinates.
    private Rect box;
    private Rect device;
    /// The title bar's buttons that may be left out on a narrow device, in the order they go.
    private readonly List<List<ToolbarButton>> droppableButtons = [];
    private bool wasActive = true;

    /// `foldable` adds the posture rail; `physical` is a phone mirrored through scrcpy.
    public DeviceChrome(DeviceWindow window, string deviceName, string subtitle, bool foldable, bool physical = false)
    {
        this.window = window;
        this.physical = physical;
        container = window.Root;
        recordTimer = new(window);
        if (physical) bootLabel.Text = "正在连接…";
        toastTimer = new(window);
        hudTimer = new(window);
        showTimer = new(window);
        hideTimer = new(window);
        pointerTimer = new(window);

        // Title bar
        var name = new TextBlock { Text = deviceName, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { Text = subtitle, FontSize = 11, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
        titleLabels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), Children = { name, detail } };
        // Enabled while the device boots too: the manager and quitting do not need it.
        menuButton = new ToolbarButton("", "菜单", null) { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        menuButton.Pressed += pressed => { if (pressed) ToggleMenu(); };

        var screenshot = ActionButton("", "截图", "Ctrl+S", DeviceAction.Screenshot);
        List<ToolbarButton> rotate = [ActionButton("", "向左旋转", "Ctrl+←", DeviceAction.RotateLeft, mirrored: true),
                                      ActionButton("", "向右旋转", "Ctrl+→", DeviceAction.RotateRight)];
        List<ToolbarButton> volume = [KeyButton("", "音量 −", "Ctrl+Alt+↓", "AudioVolumeDown"),
                                      KeyButton("", "音量 +", "Ctrl+Alt+↑", "AudioVolumeUp")];
        var power = KeyButton("", "电源", "Ctrl+L", "Power");
        titleButtons.Add(screenshot);
        if (physical)
        {
            recordButton = ActionButton("\uE7C8", "录屏", "Ctrl+Shift+R", DeviceAction.Record);
            titleButtons.Add(recordButton);
            titleButtons.AddRange(volume);
            titleButtons.Add(power);
            droppableButtons.AddRange([volume, [power], [recordButton], [screenshot]]);
        }
        else
        {
            titleButtons.AddRange(rotate);
            titleButtons.AddRange(volume);
            titleButtons.Add(power);
            droppableButtons.AddRange([volume, [power], rotate, [screenshot]]);
        }
        moreButton = new ToolbarButton("", "更多功能", null);
        moreButton.Pressed += pressed => { if (pressed) ToggleMore(); };
        titleButtons.Add(moreButton);

        // The window's own buttons stay at the trailing edge, where a Windows title bar has them; enabled while
        // the device boots too.
        var minimize = new ToolbarButton("", "最小化", null, glyphSize: 10);
        minimize.Pressed += pressed => { if (!pressed) Action?.Invoke(DeviceAction.Minimize); };
        var close = new ToolbarButton("", "关闭", "Ctrl+W", glyphSize: 10) { IsCloseButton = true };
        close.Pressed += pressed => { if (!pressed) Action?.Invoke(DeviceAction.Close); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in titleButtons) buttons.Children.Add(button);
        var windowButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0),
            Children = { new Border { Width = 1, Height = 18, Margin = new Thickness(0, 0, 6, 0), Background = Stroke }, minimize, close },
        };
        var titleGrid = new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto } } };
        titleGrid.Children.Add(menuButton);
        Grid.SetColumn(titleLabels, 1);
        titleGrid.Children.Add(titleLabels);
        Grid.SetColumn(buttons, 2);
        titleGrid.Children.Add(buttons);
        Grid.SetColumn(windowButtons, 3);
        titleGrid.Children.Add(windowButtons);
        titleBar = Glass(14, titleGrid);
        titleBar.RenderTransform = titleShift;
        titleBar.Opacity = 0;
        titleBar.Visibility = Visibility.Collapsed;

        // Pill
        navButtons.Add(KeyButton("◁", "返回", "Esc · Ctrl+B", "GoBack", 36, symbolFont: true));
        navButtons.Add(KeyButton("○", "主屏幕", "Ctrl+Shift+H", "GoHome", 36, symbolFont: true));
        navButtons.Add(KeyButton("□", "最近任务", "Ctrl+Shift+O", "AppSwitch", 36, symbolFont: true));
        navStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in navButtons) navStack.Children.Add(button);
        pill = Glass(22, new Grid { Children = { navStack, bootLabel } });

        hud = Glass(14, hudLabel);
        hud.Visibility = Visibility.Collapsed;

        if (foldable)
        {
            (PostureValue, string, string)[] postures =
            [
                (PostureValue.PostureOpened, "\uE70A", "展开"), (PostureValue.PostureClosed, "\uE8EA", "合上"),
                (PostureValue.PostureHalfOpened, "\uE736", "半开"),
            ];
            var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (posture, glyph, label) in postures)
            {
                var button = new ToolbarButton(glyph, label, null, 36);
                button.Pressed += pressed => { if (!pressed) Posture?.Invoke(posture); };
                postureButtons.Add((posture, button));
                stack.Children.Add(button);
            }
            rail = Glass(22, stack);
        }

        // Glass, but a click on it stops the recording rather than dragging the window.
        recordBadge = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
            BorderThickness = new Thickness(1),
            Child = recordLabel,
            Visibility = Visibility.Collapsed,
        };
        recordBadge.PointerPressed += (_, e) => e.Handled = true;
        recordBadge.Tapped += (_, _) => Action?.Invoke(DeviceAction.Record);
        ToolTipService.SetToolTip(recordBadge, "停止录屏  Ctrl+Shift+R");
        overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(184, 0, 0, 0)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24),
                Children = { overlayTitle, overlayText },
            },
        };

        container.Children.Add(overlay);
        container.Children.Add(pill);
        if (rail != null) container.Children.Add(rail);
        container.Children.Add(recordBadge);
        container.Children.Add(titleBar);
        container.Children.Add(hud);
        SetReady(false);

        // Over the window's empty room above the device the pointer belongs to whatever is behind it, so its
        // position is polled, as macOS's global event monitor follows it.
        pointerTimer.StartRepeating(TimeSpan.FromMilliseconds(40), PointerMoved);
    }

    private static Brush Stroke => (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"];

    /// A rounded surface over the desktop for the bars. Dragging it, anywhere but on a button, moves the window,
    /// as on macOS; the buttons handle their own presses, so those do not reach it.
    private Border Glass(double radius, UIElement child)
    {
        var glass = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorFlyoutBrush"],
            BorderThickness = new Thickness(1),
            Child = child,
        };
        glass.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(glass).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            window.Drag();
        };
        return glass;
    }

    /// Stops the timers and closes the flyout; the chrome is unusable afterwards.
    public void Close()
    {
        closed = true;
        foreach (var timer in new[] { toastTimer, hudTimer, showTimer, hideTimer, pointerTimer, recordTimer }) timer.Stop();
        flyout?.Hide();
    }

    /// Where the bars are, in the container's coordinates, for the window's click region.
    public IEnumerable<Rect> Shapes
    {
        get
        {
            yield return Bounds(pill);
            if (rail != null) yield return Bounds(rail);
            if (titleBar.Visibility == Visibility.Visible) yield return Bounds(titleBar);
            if (hud.Visibility == Visibility.Visible) yield return Bounds(hud);
            if (recordBadge.Visibility == Visibility.Visible) yield return Bounds(recordBadge);
        }
    }

    private static Rect Bounds(FrameworkElement element) =>
        new(Canvas.GetLeft(element), Canvas.GetTop(element), element.Width, element.Height);

    /// Places the bars in `box` around `device` (both in the container's coordinates): the title bar and the
    /// pill as wide as the device, the rail beside it.
    public void Layout(Rect device, Rect box)
    {
        this.box = box;
        this.device = device;
        var middle = device.X + device.Width / 2;
        FitTitleBar(device.Width);
        Place(titleBar, device.X, box.Y, device.Width, BarHeight);
        titleShift.Y = titleVisible ? 0 : -RevealOffset;
        Place(pill, Math.Round(middle - PillWidth / 2), box.Bottom - BarHeight, PillWidth, BarHeight);
        hudLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var hudWidth = Math.Min(Math.Max(150, Math.Ceiling(hudLabel.DesiredSize.Width) + 28), device.Width);
        Place(hud, Math.Round(middle - hudWidth / 2), box.Y + BarHeight - 32, hudWidth, 32);
        if (rail != null)
        {
            // Buttons of the pill's size, with the pill's inset at the ends.
            var railHeight = postureButtons.Count * 36 + Math.Max(postureButtons.Count - 1, 0) * 2 + 2 * 4;
            Place(rail, device.Right + Gap, Math.Round(device.Y + device.Height / 2 - railHeight / 2.0), BarHeight, railHeight);
        }
        if (toast != null) Place(toast, Math.Round(device.X + device.Width / 2 - toast.Width / 2), device.Y + 60, toast.Width, 36);
        recordLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var badgeWidth = Math.Ceiling(recordLabel.DesiredSize.Width) + 30;
        Place(recordBadge, Math.Round(middle - badgeWidth / 2), box.Y + BarHeight - 36, badgeWidth, 32);
        Place(overlay, device.X, device.Y, device.Width, device.Height);
        overlay.CornerRadius = new CornerRadius(Math.Min(device.Width, device.Height) * 0.09);
    }

    /// Covers the device with `text` (its first line bold); null uncovers it.
    public void ShowOverlay(string? text)
    {
        if (text == null)
        {
            overlay.Visibility = Visibility.Collapsed;
            return;
        }
        var lines = text.Split('\n');
        overlayTitle.Text = lines[0];
        overlayText.Text = string.Join("\n", lines.Skip(1));
        overlayText.Visibility = lines.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        overlay.Visibility = Visibility.Visible;
    }

    /// Marks the record button and shows how long the recording has run; null when it stops.
    public void SetRecording(DateTime? start)
    {
        recordingSince = start;
        recordTimer.Stop();
        recordButton?.Update(start == null ? "\uE7C8" : "\uE71A", start == null ? "录屏" : "停止录屏", "Ctrl+Shift+R");
        if (recordButton != null) recordButton.Tint = start == null ? null : new SolidColorBrush(Color.FromArgb(255, 0xE5, 0x39, 0x35));
        if (start is { } since)
        {
            void Update()
            {
                var seconds = Math.Max(0, (int)(DateTime.Now - since).TotalSeconds);
                recordLabel.Text = $"● {seconds / 60:00}:{seconds % 60:00}";
                Layout(device, box);
            }
            Update();
            recordTimer.StartRepeating(TimeSpan.FromSeconds(1), Update);
        }
        recordBadge.Visibility = titleVisible || start == null ? Visibility.Collapsed : Visibility.Visible;
        ShapeChanged?.Invoke();
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = height;
    }

    /// Until the guest has booted the buttons are disabled and the pill says so.
    public void SetReady(bool ready)
    {
        foreach (var button in titleButtons.Concat(navButtons).Concat(postureButtons.Select(p => p.Button))) button.IsEnabledButton = ready;
        moreButton.IsEnabledButton = ready;
        navStack.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        bootLabel.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
    }

    /// Hides the title bar at once, e.g. when the screen is pressed.
    public void Dismiss()
    {
        showTimer.Stop();
        if (flyout?.IsOpen != true) SetTitleVisible(false);
    }

    /// Shows the title bar while a drag has the window's top against the top of the screen: the window goes no
    /// higher, so the pointer cannot reach the band that shows it, and the bar would otherwise go unnoticed.
    /// Released, it hides unless the pointer is in that band.
    public void HoldTitleBar(bool hold)
    {
        if (hold == titleHeld) return;
        titleHeld = hold;
        showTimer.Stop();
        SetTitleVisible(PointerInZone);
    }

    /// Shows `text` where the title bar goes while the window is resized; null hides it shortly after.
    public void ShowResizeReadout(string? text)
    {
        hudTimer.Stop();
        if (text == null)
        {
            hudTimer.StartOnce(TimeSpan.FromSeconds(0.8), () =>
            {
                hud.Visibility = Visibility.Collapsed;
                resizing = false;
                ShapeChanged?.Invoke();
            });
            return;
        }
        if (!resizing)
        {
            resizing = true;
            flyout?.Hide();
            SetTitleVisible(false, animated: false);
        }
        hudLabel.Text = text;
        hud.Visibility = Visibility.Visible;
        Layout(device, box);
        ShapeChanged?.Invoke();
    }

    /// A short note over the top of the screen; clicking it runs `onClick`.
    public void ShowToast(string text, Action onClick)
    {
        if (toast != null) container.Children.Remove(toast);
        var label = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var note = Glass(18, label);
        note.Width = Math.Ceiling(label.DesiredSize.Width) + 32;
        note.Height = 36;
        note.Tapped += (_, _) =>
        {
            onClick();
            HideToast();
        };
        container.Children.Add(note);
        toast = note;
        Layout(device, box);
        toastTimer.StartOnce(TimeSpan.FromSeconds(3), HideToast);
    }

    private void HideToast()
    {
        if (toast is not { } note) return;
        toast = null;
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromSeconds(0.2) };
        Storyboard.SetTarget(fade, note);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var story = new Storyboard { Children = { fade } };
        story.Completed += (_, _) => container.Children.Remove(note);
        story.Begin();
    }

    // Building

    /// The bar is as wide as the device, so on a small one what does not fit is left out: first the name,
    /// then the volume keys and power (also on the frame's side buttons and in the context menu), then the
    /// rotate buttons, then fold, then screenshot. The menu, the window buttons and "More" always stay.
    private void FitTitleBar(double width)
    {
        var hidden = new List<ToolbarButton>();
        var showsLabels = true;
        double Needed()
        {
            var count = titleButtons.Count - hidden.Count;
            return 6 + 32 + (showsLabels ? 4 + 50 + 8 : 2) + count * 32 + (count - 1) * 2 + 8 + 7 + 2 * 32 + 2 + 6 + 2;
        }
        if (Needed() > width) showsLabels = false;
        foreach (var group in droppableButtons)
        {
            if (Needed() <= width) break;
            hidden.AddRange(group);
        }
        titleLabels.Visibility = showsLabels ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in titleButtons) button.Visibility = hidden.Contains(button) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// Marks the rail's button for the posture the device is in (none for postures it does not offer).
    public void ShowPosture(PostureValue posture)
    {
        foreach (var (value, button) in postureButtons) button.IsOn = value == posture;
    }

    private ToolbarButton KeyButton(string glyph, string label, string shortcut, string key, double size = 32, bool symbolFont = false)
    {
        var button = new ToolbarButton(glyph, label, shortcut, size, symbolFont: symbolFont);
        button.Pressed += pressed => Key?.Invoke(key, pressed);
        return button;
    }

    private ToolbarButton ActionButton(string glyph, string label, string shortcut, DeviceAction action, bool mirrored = false)
    {
        var button = new ToolbarButton(glyph, label, shortcut, mirrored: mirrored);
        button.Pressed += pressed => { if (!pressed) Action?.Invoke(action); };
        return button;
    }

    // Hover

    private bool PointerInZone
    {
        get
        {
            if (titleHeld) return true;
            if (!Native.IsAppActive || resizing || closed || !window.IsShown) return false;
            if (flyout?.IsOpen == true) return true;
            // The title bar's room plus the top of the device, in screen pixels.
            var band = window.ToScreen(new Rect(device.X, box.Y, device.Width, BarHeight + Gap + BandDepth));
            var pointer = Native.CursorPosition;
            return band.Contains(pointer.X, pointer.Y);
        }
    }

    /// Shows the title bar once the pointer has rested in the band for 120 ms; hides it 600 ms after it leaves.
    private void PointerMoved()
    {
        var active = Native.IsAppActive;
        if (active != wasActive)
        {
            wasActive = active;
            pill.Opacity = active ? 1 : 0.6;
            if (!active)
            {
                flyout?.Hide();
                Dismiss();
            }
        }
        if (PointerInZone)
        {
            hideTimer.Stop();
            if (titleVisible || showTimer.IsRunning) return;
            showTimer.StartOnce(TimeSpan.FromMilliseconds(120), () =>
            {
                if (PointerInZone) SetTitleVisible(true);
            });
        }
        else
        {
            showTimer.Stop();
            if (!titleVisible || hideTimer.IsRunning) return;
            hideTimer.StartOnce(TimeSpan.FromMilliseconds(600), () =>
            {
                if (!PointerInZone) SetTitleVisible(false);
            });
        }
    }

    private Storyboard? titleStory;

    private void SetTitleVisible(bool visible, bool animated = true)
    {
        hideTimer.Stop();
        if (visible == titleVisible) return;
        titleVisible = visible;
        recordBadge.Visibility = visible || recordingSince == null ? Visibility.Collapsed : Visibility.Visible;
        titleStory?.Stop();
        // A hidden bar must not take clicks in the empty room it leaves.
        if (visible)
        {
            titleBar.Visibility = Visibility.Visible;
            ShapeChanged?.Invoke();
        }
        var duration = TimeSpan.FromSeconds(animated ? (visible ? 0.16 : 0.12) : 0.001);
        var fade = new DoubleAnimation { To = visible ? 1 : 0, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(fade, titleBar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation { To = visible ? 0 : -RevealOffset, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(slide, titleShift);
        Storyboard.SetTargetProperty(slide, "Y");
        var story = new Storyboard { Children = { fade, slide } };
        story.Completed += (_, _) =>
        {
            if (titleVisible) return;
            titleBar.Visibility = Visibility.Collapsed;
            ShapeChanged?.Invoke();
        };
        titleStory = story;
        story.Begin();
    }

    // More

    public void OpenMoreForTest() => ToggleMore();

    public void OpenMenuForTest() => ToggleMenu();

    private void ToggleMenu()
    {
        if (flyout?.IsOpen == true)
        {
            var wasMenu = flyout is MenuFlyout;
            flyout.Hide();
            if (wasMenu) return;
        }
        if (MenuProvider == null) return;
        var menu = MenuProvider();
        menu.Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft;
        menu.ShouldConstrainToRootBounds = false;
        menu.Closed += (_, _) =>
        {
            menuButton.IsOn = false;
            if (flyout == menu) flyout = null;
            PointerMoved();
        };
        flyout = menu;
        menuButton.IsOn = true;
        menu.ShowAt(menuButton);
    }

    private void ToggleMore()
    {
        if (flyout?.IsOpen == true)
        {
            var wasMore = flyout is Flyout;
            flyout.Hide();
            if (wasMore) return;
        }
        if ((Controls != null ? new MorePanel(Controls) : PanelProvider?.Invoke()) is not { } content) return;
        var more = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            // A popup window of its own, so it may reach past the device window's edges.
            ShouldConstrainToRootBounds = false,
        };
        more.Closed += (_, _) =>
        {
            moreButton.IsOn = false;
            if (flyout == more) flyout = null;
            PointerMoved();
        };
        flyout = more;
        moreButton.IsOn = true;
        more.ShowAt(moreButton);
    }
}

/// A one-shot or repeating timer on the window's thread that can be restarted.
sealed class DispatcherQueueTimerHolder(Window window)
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? timer;

    public bool IsRunning => timer?.IsRunning == true;

    public void StartOnce(TimeSpan delay, Action action) => Start(delay, false, action);

    public void StartRepeating(TimeSpan interval, Action action) => Start(interval, true, action);

    private void Start(TimeSpan interval, bool repeating, Action action)
    {
        Stop();
        var created = window.DispatcherQueue.CreateTimer();
        created.Interval = interval;
        created.IsRepeating = repeating;
        created.Tick += (t, _) =>
        {
            if (!repeating) t.Stop();
            action();
        };
        timer = created;
        created.Start();
    }

    public void Stop() => timer?.Stop();
}

/// An icon button that reports press and release separately, so device keys can be held.
sealed partial class ToolbarButton : Grid
{
    public event Action<bool>? Pressed;

    private readonly FontIcon icon;
    private bool hovered, pressed, enabled = true, isOn;
    private readonly double size;

    public bool IsCloseButton { get; init; }

    public ToolbarButton(string glyph, string label, string? shortcut, double size = 32, bool symbolFont = false, bool mirrored = false, double glyphSize = 15)
    {
        this.size = size;
        Width = Height = size;
        CornerRadius = new CornerRadius(size == 36 ? size / 2 : 8);
        icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = symbolFont ? 17 : glyphSize,
            FontFamily = new FontFamily(symbolFont ? "Segoe UI Symbol" : "Segoe Fluent Icons, Segoe MDL2 Assets"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (mirrored)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
        }
        Children.Add(icon);
        Background = new SolidColorBrush(Colors.Transparent);
        Update(glyph, label, shortcut);

        PointerEntered += (_, _) => { hovered = true; UpdateBackground(); };
        PointerExited += (_, _) => { hovered = false; UpdateBackground(); };
        PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (!enabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            pressed = true;
            CapturePointer(e.Pointer);
            UpdateBackground();
            Pressed?.Invoke(true);
        };
        PointerReleased += (_, e) =>
        {
            e.Handled = true;
            Release();
            ReleasePointerCapture(e.Pointer);
        };
        PointerCaptureLost += (_, _) => Release();
    }

    private void Release()
    {
        if (!pressed) return;
        pressed = false;
        UpdateBackground();
        Pressed?.Invoke(false);
    }

    /// Shows another glyph and name, for a button whose action changes with the device's state.
    public void Update(string glyph, string label, string? shortcut)
    {
        icon.Glyph = glyph;
        ToolTipService.SetToolTip(this, shortcut == null ? label : $"{label}  {shortcut}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, label);
    }

    public bool IsEnabledButton
    {
        get => enabled;
        set
        {
            enabled = value;
            Opacity = value ? 1 : 0.35;
        }
    }

    /// The glyph's colour; null for the text colour.
    public Brush? Tint
    {
        get => tint;
        set
        {
            tint = value;
            UpdateBackground();
        }
    }
    private Brush? tint;

    /// Keeps the pressed look, e.g. while the button's flyout is open.
    public bool IsOn
    {
        get => isOn;
        set
        {
            isOn = value;
            UpdateBackground();
        }
    }

    private void UpdateBackground()
    {
        if (IsCloseButton && (hovered || pressed) && enabled)
        {
            Background = new SolidColorBrush(Color.FromArgb((byte)(pressed ? 0xCC : 0xFF), 0xC4, 0x2B, 0x1C));
            icon.Foreground = new SolidColorBrush(Colors.White);
            return;
        }
        if (tint != null) icon.Foreground = tint; else icon.ClearValue(FontIcon.ForegroundProperty);
        var alpha = pressed || isOn ? 0.15 : hovered && enabled ? 0.07 : 0;
        var text = (Color)Application.Current.Resources["TextFillColorPrimary"];
        Background = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), text.R, text.G, text.B));
    }
}

/// An invisible area at a corner of the device that resizes the window. Like a standard window's corner,
/// it reaches a little outside the device's rectangle and in to just past the body's rounded corner, so it
/// sits on the corner the user sees.
sealed partial class ResizeHandle : Grid
{
    public enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

    /// How far the area reaches outside the device's rectangle, and past the body's corner into it.
    public const double Outside = 8;
    public const double Inside = 8;

    public Corner Which { get; }
    public bool IsLeft => Which is Corner.TopLeft or Corner.BottomLeft;
    public bool IsTop => Which is Corner.TopLeft or Corner.TopRight;
    /// Steps round from the top left, clockwise.
    public int ClockwiseIndex => (int)Which;

    /// A drag began, the box's corner should now be at a screen point (pixels), or it ended.
    public event Action<Corner>? Began;
    public event Action<Point>? Changed;
    public event Action? Ended;

    /// From the pointer to the box's corner when the drag began, so the corner does not jump to the pointer.
    private Point grabOffset;
    private bool dragging;

    public ResizeHandle(Corner corner, Func<PixelRect> box)
    {
        Which = corner;
        // Takes clicks across its whole square, the device's corner included, as the window's region does.
        Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(corner is Corner.TopLeft or Corner.BottomRight
            ? InputSystemCursorShape.SizeNorthwestSoutheast : InputSystemCursorShape.SizeNortheastSouthwest);
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            var frame = box();
            var pointer = Native.CursorPosition;
            grabOffset = new Point((IsLeft ? frame.Left : frame.Right) - pointer.X, (IsTop ? frame.Top : frame.Bottom) - pointer.Y);
            dragging = true;
            CapturePointer(e.Pointer);
            Began?.Invoke(Which);
        };
        PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var pointer = Native.CursorPosition;
            Changed?.Invoke(new Point(pointer.X + grabOffset.X, pointer.Y + grabOffset.Y));
        };
        PointerReleased += (_, e) =>
        {
            ReleasePointerCapture(e.Pointer);
            End();
        };
        PointerCaptureLost += (_, _) => End();
    }

    private void End()
    {
        if (!dragging) return;
        dragging = false;
        Ended?.Invoke();
    }

    /// Puts the area at its corner of `device` (in the container's coordinates), whose body starts `bodyInset`
    /// in from that corner along each axis.
    public void Place(Rect device, double bodyInset)
    {
        var side = Outside + bodyInset + Inside;
        Canvas.SetLeft(this, IsLeft ? device.X - Outside : device.Right - side + Outside);
        Canvas.SetTop(this, IsTop ? device.Y - Outside : device.Bottom - side + Outside);
        Width = Height = side;
    }

    public Rect Bounds => new(Canvas.GetLeft(this), Canvas.GetTop(this), Width, Height);
}
