namespace DeltaMusePlayer.Views;

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

/// <summary>
/// 本次运行第一次切到 Real Input 时的风险确认。
///
/// 用代码建窗（只有一个静态入口，不需要 XAML 资源），保证不会因为资源缺失而在关键时刻失效。
/// 用户确认后本次会话不再弹；下次启动程序仍默认 Preview。
/// </summary>
internal static class RealInputConsentDialog
{
    public const string Title = "Real Input — 请先确认";

    public const string Body =
        "Real Input sends synthetic keyboard and mouse input to Windows.\n" +
        "\n" +
        "Automation in online games may violate game rules or result in account penalties.\n" +
        "\n" +
        "This program does not bypass anti-cheat or modify the game.\n" +
        "\n" +
        "它只做一件事：把 MIDI 译成普通的 Windows 用户态合成键鼠事件。\n" +
        "不注入进程、不读写游戏内存、不使用驱动、不做反作弊绕过。";

    /// <summary>弹一次确认框；返回 true 表示用户点了「I Understand」。</summary>
    public static async Task<bool> ConfirmAsync(Window owner)
    {
        var window = new Window
        {
            Title = Title,
            Width = 580,
            Height = 320,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        bool accepted = false;

        var heading = new TextBlock
        {
            Text = "Real Input 会向系统发送真实的键盘与鼠标事件",
            FontWeight = FontWeight.Bold,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
        };

        var body = new TextBlock
        {
            Text = Body,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Avalonia.Thickness(0, 0, 0, 16),
        };

        var cancel = new Button { Content = "Cancel", MinWidth = 110 };
        var accept = new Button
        {
            Content = "I Understand",
            MinWidth = 140,
            Background = Brush.Parse("#FFE0E0"),
        };

        cancel.Click += (_, _) => window.Close();
        accept.Click += (_, _) => { accepted = true; window.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);

        var panel = new StackPanel { Margin = new Avalonia.Thickness(20) };
        panel.Children.Add(heading);
        panel.Children.Add(body);
        panel.Children.Add(buttons);
        window.Content = panel;

        await window.ShowDialog(owner);
        return accepted;
    }
}
