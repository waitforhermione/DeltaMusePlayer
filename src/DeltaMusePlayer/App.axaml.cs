using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DeltaMusePlayer;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Views.MainWindow();
            desktop.MainWindow = window;

            // `--midi <file>`：界面起来之后把它载进来（等同于用户点「浏览…」）
            if (Program.StartupMidiPath is { Length: > 0 } midi && File.Exists(midi))
            {
                try { window.LoadMidiFromArguments(midi); }
                catch { /* 载入失败会写在窗口状态里，不阻塞启动 */ }
            }

            // `--diagnose`：等界面完全就绪后自动走一遍完整播放流程（含倒计时），便于排查
            if (Program.DiagnoseMode)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(1500);
                    await window.RunDiagnoseAutoplayAsync();
                }, Avalonia.Threading.DispatcherPriority.Background);
            }

            // 关闭窗口时尽最大努力释放所有本程序按下的输入（进程退出前的最后一道保险）。
            desktop.ShutdownRequested += (_, _) =>
            {
                window.EmergencyStop();
                window.DisposeEmergencyHotkey();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
