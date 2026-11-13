using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmulatorShell;

/// The new / edit dialog: name, device type and system image, with the rest under "高级设置".
sealed partial class AvdFormDialog : ContentDialog
{
    public AvdForm Form { get; }
    /// The user asked for the system images dialog; it opens once this one has closed, and this one comes back after.
    public bool WantsImages { get; private set; }

    private readonly DeviceManager manager;
    private readonly DevicePreview preview = new() { Width = 120, Height = 250, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox name = new();
    private readonly TextBlock nameHint = new() { FontSize = 12 };
    private readonly ComboBox deviceTypes = new() { MinWidth = 300 };
    private readonly TextBlock typeHint = new() { FontSize = 12, Opacity = 0.65 };
    private readonly ComboBox images = new() { MinWidth = 300 };
    private readonly StackPanel imageInfo = new() { Spacing = 6 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private bool updating;

    private DeviceType? DeviceType => manager.DeviceTypes.FirstOrDefault(t => t.Id == Form.DeviceTypeId);

    private DeviceCategory Category
    {
        get
        {
            if (DeviceType is { } type) return type.Category;
            if (Form.EditingId is { } id && manager.Avds.FirstOrDefault(a => a.Id == id) is { } avd) return manager.CategoryOf(avd);
            return DeviceCategory.Phone;
        }
    }

    private List<SystemImage> CompatibleImages => manager.Images.Where(i => i.Family == Category.Family() && i.Status != ImageStatus.Available).ToList();
    private SystemImage? SelectedImage => manager.Images.FirstOrDefault(i => i.Id == Form.ImageId);

    public AvdFormDialog(DeviceManager manager, AvdForm form)
    {
        this.manager = manager;
        Form = form with { };
        Title = form.IsEdit ? "编辑模拟器" : "新建模拟器";
        PrimaryButtonText = form.IsEdit ? "保存" : "创建";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;

        name.Text = Form.Name;
        name.TextChanged += (_, _) =>
        {
            if (updating || name.Text == Form.Name) return;
            Form.Name = name.Text;
            Form.NameTouched = true;
            Validate();
        };
        deviceTypes.SelectionChanged += (_, _) =>
        {
            if (updating || deviceTypes.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            ChangeDeviceType(id);
        };
        images.SelectionChanged += (_, _) =>
        {
            if (updating || images.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            Form.ImageId = id;
            Validate();
        };

        var fields = new StackPanel { Spacing = 14, Width = 460 };
        fields.Children.Add(Field("名称", new StackPanel { Spacing = 4, Children = { name, nameHint } }));
        var typeBox = new StackPanel { Spacing = 4 };
        if (form.IsEdit) typeBox.Children.Add(new TextBlock { Text = DeviceType?.Name ?? form.DeviceTypeId });
        else typeBox.Children.Add(deviceTypes);
        typeBox.Children.Add(typeHint);
        fields.Children.Add(Field("设备类型", typeBox));
        var downloadOthers = new HyperlinkButton { Content = "下载其他系统镜像…", Padding = new Thickness(0), FontSize = 12 };
        downloadOthers.Click += (_, _) => AskForImages();
        fields.Children.Add(Field("系统镜像", new StackPanel { Spacing = 6, Children = { images, imageInfo, downloadOthers } }));
        fields.Children.Add(new Expander
        {
            Header = "高级设置",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = Advanced(),
        });
        fields.Children.Add(status);

        var layout = new Grid { ColumnSpacing = 22 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(preview);
        Grid.SetColumn(fields, 1);
        layout.Children.Add(fields);
        Content = new ScrollViewer { Content = layout, MaxHeight = 620 };

        PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            status.Text = Form.IsEdit ? "正在保存…" : "正在创建…";
            status.ClearValue(TextBlock.ForegroundProperty);
            IsPrimaryButtonEnabled = false;
            var error = await manager.Submit(Form);
            if (error != null)
            {
                status.Text = error;
                status.Foreground = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
                args.Cancel = true;
            }
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        };
        manager.Changed += Refill;
        Closed += (_, _) => manager.Changed -= Refill;
        Refill();
    }

    private static UIElement Field(string label, UIElement content)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 0) });
        Grid.SetColumn((FrameworkElement)content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private UIElement Advanced()
    {
        TextBox Number(string value, Action<string> set, bool enabled = true)
        {
            var box = new TextBox { Text = value, Width = 90, IsEnabled = enabled };
            box.TextChanged += (_, _) => set(box.Text);
            return box;
        }
        UIElement WithUnit(UIElement box, string unit) =>
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { box, new TextBlock { Text = unit, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center } } };
        ComboBox Choice((string Title, string Value)[] options, string current, Action<string> set)
        {
            var box = new ComboBox { MinWidth = 200 };
            foreach (var (title, value) in options)
            {
                box.Items.Add(new ComboBoxItem { Content = title, Tag = value });
                if (value == current) box.SelectedIndex = box.Items.Count - 1;
            }
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is ComboBoxItem { Tag: string value }) set(value);
            };
            return box;
        }
        var boot = new RadioButtons { Items = { "快速启动（保存快照）", "每次冷启动" }, SelectedIndex = Form.ColdBoot ? 1 : 0 };
        boot.SelectionChanged += (_, _) => Form.ColdBoot = boot.SelectedIndex == 1;
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Field("内存", WithUnit(Number(Form.RamMB, v => Form.RamMB = v), "MB")),
                Field("内部存储", WithUnit(Number(Form.DataGB, v => Form.DataGB = v), "GB")),
                Field("SD 卡", WithUnit(Number(Form.SdMB, v => Form.SdMB = v, enabled: !Form.IsEdit), "MB")),
                Field("图形", Choice([("主机 GPU（推荐）", "host"), ("软件渲染", "software")], Form.HostGpu ? "host" : "software", v => Form.HostGpu = v == "host")),
                Field("启动方式", boot),
                Field("后置摄像头", Choice([("虚拟场景", "virtualscene"), ("模拟", "emulated"), ("电脑摄像头", "webcam0"), ("无", "none")], Form.CameraBack, v => Form.CameraBack = v)),
                Field("前置摄像头", Choice([("模拟", "emulated"), ("电脑摄像头", "webcam0"), ("无", "none")], Form.CameraFront, v => Form.CameraFront = v)),
                Field("初始方向", Choice([("竖屏", "portrait"), ("横屏", "landscape")], Form.Landscape ? "landscape" : "portrait", v => Form.Landscape = v == "landscape")),
            },
        };
    }

    /// Fills the device type and image lists again, e.g. once avdmanager has listed the types or a download ended.
    private void Refill()
    {
        updating = true;
        deviceTypes.Items.Clear();
        if (manager.DeviceTypes.Count == 0)
        {
            deviceTypes.Items.Add(new ComboBoxItem { Content = "正在读取设备类型…", IsEnabled = false });
            deviceTypes.SelectedIndex = 0;
        }
        foreach (var category in CategoryInfo.All)
        {
            var types = manager.DeviceTypes.Where(t => t.Category == category).ToList();
            if (types.Count == 0) continue;
            deviceTypes.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock { Text = category.Title(), FontWeight = FontWeights.SemiBold, FontSize = 12 },
                IsEnabled = false,
            });
            foreach (var type in types)
            {
                var item = new ComboBoxItem { Content = type.Name, Tag = type.Id, Padding = new Thickness(20, 6, 12, 6) };
                deviceTypes.Items.Add(item);
                if (type.Id == Form.DeviceTypeId) deviceTypes.SelectedItem = item;
            }
        }
        FillImages();
        updating = false;
        Validate();
    }

    private void FillImages()
    {
        var wasUpdating = updating;
        updating = true;
        images.Items.Clear();
        var compatible = CompatibleImages;
        if (compatible.Count == 0)
        {
            images.Items.Add(new ComboBoxItem { Content = $"没有已安装的{Category.Title()}镜像", IsEnabled = false });
            images.SelectedIndex = 0;
        }
        foreach (var image in compatible)
        {
            var item = new ComboBoxItem
            {
                Content = $"{image.ApiLabel} · {image.KindLabel}" + (image.Status == ImageStatus.Broken ? "（安装不完整）" : ""),
                Tag = image.Id,
            };
            images.Items.Add(item);
            if (image.Id == Form.ImageId) images.SelectedItem = item;
        }
        updating = wasUpdating;
    }

    private void ChangeDeviceType(string id)
    {
        Form.DeviceTypeId = id;
        if (manager.DeviceTypes.FirstOrDefault(t => t.Id == id) is not { } type) return;
        if (!Form.NameTouched)
        {
            Form.Name = manager.UniqueName(type.Name);
            updating = true;
            name.Text = Form.Name;
            updating = false;
        }
        Form.Landscape = type.Category.IsLandscape();
        if (SelectedImage?.Family != type.Category.Family()) Form.ImageId = manager.DefaultImage(type.Category)?.Id;
        FillImages();
        Validate();
    }

    private void Validate()
    {
        var nameError = manager.NameError(Form);
        nameHint.Text = nameError ?? (Form.IsEdit ? "" : $"AVD 名称：{Avd.IdFor(Form.Name)}");
        if (nameError != null) nameHint.Foreground = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
        else nameHint.ClearValue(TextBlock.ForegroundProperty);
        nameHint.Opacity = nameError != null ? 1 : 0.65;
        nameHint.Visibility = nameHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var parts = new List<string> { Category.Title() };
        if (DeviceType?.SkinFolder is { } folder && SkinGeometry.Read(folder) is { } skin)
            parts.Add($"{(int)skin.Display.Width} × {(int)skin.Display.Height}");
        else if (!Form.IsEdit)
            parts.Add("没有外观图，按屏幕比例显示");
        typeHint.Text = string.Join(" · ", parts);
        preview.Show(DeviceType, Category.IsLandscape());

        imageInfo.Children.Clear();
        var problem = manager.ImageProblem(Form, Category);
        if (problem != null)
        {
            imageInfo.Children.Add(ProblemBox(problem));
        }
        else if (SelectedImage is { } image)
        {
            var details = new[] { image.ExtensionLevel is { } level ? $"扩展级别 {level}" : null,
                                  manager.ImageSizes.TryGetValue(image.Id, out var size) ? "已安装 " + Sizes.Format(size) : null };
            imageInfo.Children.Add(new TextBlock { Text = string.Join(" · ", details.OfType<string>()), FontSize = 12, Opacity = 0.65 });
        }
        IsPrimaryButtonEnabled = nameError == null && problem == null && !manager.Busy.Contains("form");
    }

    private UIElement ProblemBox(string problem)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(8), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(31, 0xF7, 0x93, 0x0E)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF7, 0x93, 0x0E)) });
        var text = new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Button? action = null;
        if (SelectedImage is { } image && image.Family == Category.Family() && image.Status == ImageStatus.Broken && !manager.Downloads.ContainsKey(image.Id))
        {
            action = new Button { Content = "重新下载" };
            action.Click += (_, _) => manager.Download(image);
        }
        else if (SelectedImage == null || SelectedImage.Family != Category.Family())
        {
            action = new Button { Content = "下载…" };
            action.Click += (_, _) => AskForImages();
        }
        if (action != null)
        {
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }
        return grid;
    }

    private void AskForImages()
    {
        WantsImages = true;
        Hide();
    }
}
