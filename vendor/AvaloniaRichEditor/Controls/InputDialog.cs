using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AvaloniaRichEditor.Controls;

internal static class InputDialog
{
    public static async Task<(string Text, string Url)?> ShowAsync(Window owner, string title, string initialText, string initial)
    {
        var box = new TextBox { Text = initial, PlaceholderText = "https://...", Width = 320 };
        var textBox = new TextBox { Text = initialText, Width = 320 };
        (string Text, string Url)? result = null;

        var ok = new Button { Content = RichEditorLocalization.GetString("OK"), IsDefault = true };
        var cancel = new Button { Content = RichEditorLocalization.GetString("Cancel"), IsCancel = true };

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            result = null;
            dialog.Close();
        }, RoutingStrategies.Tunnel);

        ok.Click += (_, _) => { result = (textBox.Text ?? "", box.Text ?? ""); dialog.Close(); };
        cancel.Click += (_, _) => { result = null; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = RichEditorLocalization.GetString("LinkText") });
        panel.Children.Add(textBox);
        panel.Children.Add(new TextBlock { Text = RichEditorLocalization.GetString("LinkUrl") });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);
        return result;
    }
}
