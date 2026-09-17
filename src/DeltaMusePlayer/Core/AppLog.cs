namespace DeltaMusePlayer.Core;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>一条日志。</summary>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message, string? Exception = null)
{
    public string ToLine()
    {
        string level = Level switch
        {
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO",
        };
        string line = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Message}";
        return Exception is null ? line : $"{line}{Environment.NewLine}{Exception}";
    }
}

/// <summary>
/// 进程内日志。内容只与运行状态有关：载入的文件、选中的轨道、音符统计、
/// 播放开始/暂停/停止、输入后端、异常、release_all 结果。
/// **不记录**任何与本程序无关的信息。
/// </summary>
public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>同时写到磁盘的路径；为 null 时只留在内存里。</summary>
    public string? FilePath { get; set; }

    public void Info(string message) => Add(LogLevel.Info, message, null);

    public void Warn(string message) => Add(LogLevel.Warning, message, null);

    public void Error(string message, Exception? ex = null) => Add(LogLevel.Error, message, ex);

    private void Add(LogLevel level, string message, Exception? ex)
    {
        var entry = new LogEntry(DateTime.Now, level, message, ex?.ToString());
        lock (_gate) _entries.Add(entry);

        if (FilePath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // 用带 BOM 的 UTF-8 追加：日志里有中文，不带 BOM 时记事本 / PowerShell
                // 会按 ANSI 解，读出来全是乱码，用户想贴给人都贴不了。
                var utf8Bom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                if (!File.Exists(FilePath) || new FileInfo(FilePath).Length == 0)
                    File.WriteAllText(FilePath, entry.ToLine() + Environment.NewLine, utf8Bom);
                else
                    File.AppendAllText(FilePath, entry.ToLine() + Environment.NewLine, utf8Bom);
            }
            catch { /* 日志写不进去不能影响演奏 */ }
        }

        EntryAdded?.Invoke(entry);
    }

    /// <summary>默认日志文件：%LOCALAPPDATA%\DeltaMusePlayer\play.log。</summary>
    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeltaMusePlayer", "play.log");

    public static AppLog CreateWithFile(string? path = null)
        => new() { FilePath = path ?? DefaultLogPath };

    public string ToText() => string.Join(Environment.NewLine, Entries.Select(e => e.ToLine()));
}
