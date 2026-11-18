using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmulatorShell;

/// Asks for text to put on a device's clipboard. Enter makes a new line in the text; Ctrl+Enter or 发送 sends.
static class SendTextDialog
{
    /// The text sent last, offered again (selected, so typing replaces it).
    private static string lastText = "";

    /// The dialog and the box its text is in; `Text` reads what was typed once it closed with 发送.
    public static (ContentDialog Dialog, Func<string?> Text) Create(string deviceName)
    {
        var box = new TextBox
        {
            Text = lastText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, Width = 360,
            PlaceholderText = "输入要发送的文字",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            Title = $"发送文字到“{deviceName}”的剪贴板",
            Content = new StackPanel
            {
                Spacing = 10,
                Children = { new TextBlock { Text = "发送后在设备上长按输入框粘贴。", Opacity = 0.65, TextWrapping = TextWrapping.Wrap }, box },
            },
            PrimaryButtonText = "发送",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = lastText.Length > 0,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Length > 0;
        var sent = false;
        box.KeyboardAccelerators.Add(SendAccelerator(() =>
        {
            if (box.Text.Length == 0) return;
            sent = true;
            dialog.Hide();
        }));
        dialog.PrimaryButtonClick += (_, _) => sent = true;
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        return (dialog, () =>
        {
            if (!sent || box.Text.Length == 0) return null;
            // A TextBox keeps "\r" for its new lines; Android's are "\n".
            lastText = box.Text;
            return box.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        });
    }

    /// Shows the dialog in a window of its own (a device window) and gives what was typed, or null.
    public static async Task<string?> Show(XamlRoot root, ElementTheme theme, string deviceName)
    {
        var (dialog, text) = Create(deviceName);
        dialog.XamlRoot = root;
        dialog.RequestedTheme = theme;
        await dialog.ShowAsync();
        return text();
    }

    private static KeyboardAccelerator SendAccelerator(Action send)
    {
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.Enter, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, e) =>
        {
            e.Handled = true;
            send();
        };
        return accelerator;
    }
}
