using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Comet.App.Dialogs;

/// <summary>A small modal message box with one or two buttons.</summary>
public static class MessageDialog
{
    public static Task ShowAsync(Window owner, string title, string message) =>
        ShowCoreAsync(owner, title, message, "OK", null);

    /// <summary>Asks a yes/no question. Escape or closing the window answers no.</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText, string cancelText = "Cancel") =>
        ShowCoreAsync(owner, title, message, confirmText, cancelText);

    private static async Task<bool> ShowCoreAsync(Window owner, string title, string message, string confirmText, string? cancelText)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 340,
            MaxWidth = 520,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var confirm = new Button { Content = confirmText, IsDefault = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        confirm.Classes.Add("accent");
        confirm.Click += (_, _) => dialog.Close(true);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancel);
        }
        else
        {
            confirm.IsCancel = true;
        }
        buttons.Children.Add(confirm);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
                buttons,
            },
        };
        dialog.Opened += (_, _) => confirm.Focus();

        return await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true;
    }
}
