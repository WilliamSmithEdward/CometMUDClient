using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Comet.Core.Configuration;

namespace Comet.App.Dialogs;

public sealed class AboutDialog : Window
{
    public AboutDialog()
    {
        Title = "About Comet";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "dev";
        AppPaths paths = AppPaths.Default();

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 88, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = "Comet MUD Client", Classes = { "h1" } },
                new TextBlock { Text = $"Version {version}" },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "A cross-platform MUD client with TinTin-style aliases, actions, highlights and scripting.",
                },
                new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "hint" },
                    Text = $"Settings and scripts: {paths.ConfigDirectory}\nLogs: {paths.LogsDirectory}\n{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}",
                },
                close,
            },
        };
        Opened += (_, _) => close.Focus();
    }
}
