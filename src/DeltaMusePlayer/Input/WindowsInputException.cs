namespace DeltaMusePlayer.Input;

using DeltaMusePlayer.Core;

/// <summary>
/// 真实输入注入失败。
///
/// 带上「哪个动作、哪个输入、Win32 错误码」，不吞错误、不只留一句「失败了」。
/// </summary>
public sealed class WindowsInputException : Exception
{
    public WindowsInputException(string action, InputId input, int win32Error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Action = action;
        Input = input;
        Win32Error = win32Error;
    }

    /// <summary>press / release / release-all 之一。</summary>
    public string Action { get; }

    public InputId Input { get; }

    /// <summary>Win32 错误码（0 = 不可用）。</summary>
    public int Win32Error { get; }

    public override string ToString()
        => $"WindowsInputException: action={Action} input={Input.Label} win32={Win32Error} ({Message})" +
           (InnerException is null ? "" : Environment.NewLine + InnerException);
}

/// <summary>
/// 一次 ReleaseAll 里出现的多个失败。ReleaseAll 是 best-effort：
/// 每个输入都会尝试释放，最后把失败汇总成一个异常抛出。
/// </summary>
public sealed class ReleaseAllException : Exception
{
    public ReleaseAllException(IReadOnlyList<WindowsInputException> failures)
        : base($"释放输入时出现 {failures.Count} 个失败：" +
               string.Join("；", failures.Select(f => $"{f.Input.Label}({f.Win32Error})")))
    {
        Failures = failures;
    }

    public IReadOnlyList<WindowsInputException> Failures { get; }
}
