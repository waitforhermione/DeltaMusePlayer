using System.Runtime.InteropServices;

namespace DeltaMusePlayer;

using Avalonia;
using DeltaMusePlayer.Cli;

internal static class Program
{
    /// <summary>
    /// 进程入口。
    ///
    /// 两种运行方式：
    ///  * 带参数（除 <c>--midi</c> / <c>--gui</c> 之外的子命令）→ 命令行路径。
    ///    会尽量附着到父进程的控制台，这样从终端运行时能直接看到 trace 输出。
    ///  * 无参数（或只给了 <c>--midi</c>）→ 图形界面（默认 Preview / Trace）。
    /// </summary>
    /// <summary>`--midi &lt;file&gt;` 预载入的文件；null 表示不预载。</summary>
    private static string? _startupMidi;

    [STAThread]
    public static int Main(string[] args)
    {
        // `--midi <file>` 可以把一个文件直接带进图形界面（其余参数照旧走 CLI）。
        // `--diagnose` 打开逐条派发日志，并在界面就绪后自动切 Real Input 走一遍完整播放流程。
        var cliArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--midi", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _startupMidi = args[++i];
                continue;
            }
            if (args[i].Equals("--diagnose", StringComparison.OrdinalIgnoreCase))
            {
                DiagnoseMode = true;
                continue;
            }
            cliArgs.Add(args[i]);
        }

        bool wantsCli = cliArgs.Count > 0;
        if (wantsCli)
        {
            AttachParentConsole();
            int code = CliRunner.Run(cliArgs.ToArray(), Console.Out, Console.Error);
            Console.Out.Flush();
            if (cliArgs.Contains("--gui", StringComparer.OrdinalIgnoreCase)) return LaunchGui();
            return code;
        }

        return LaunchGui();
    }

    private static int LaunchGui()
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("启动界面失败：" + ex);
            return 4;
        }
    }

    /// <summary>界面启动后要预载入的 MIDI（没有就是 null）。</summary>
    /// <summary>界面启动后要预载入的 MIDI（没有就是 null）。</summary>
    internal static string? StartupMidiPath => _startupMidi;

    /// <summary>`--diagnose`：逐条记录派发，并在界面就绪后自动走一遍真实播放流程。</summary>
    internal static bool DiagnoseMode { get; private set; }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    // ---------------------------------------------------------------- 控制台附着

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// WinExe 子系统默认没有控制台，`Console.Out` 会写进虚空。
    /// 从终端运行时附着到父进程的控制台，CLI 输出才看得见；失败就保持原样（GUI 场景）。
    /// </summary>
    private static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(stderr);
            }
        }
        catch { /* 没有父控制台就算了 */ }
    }
}
