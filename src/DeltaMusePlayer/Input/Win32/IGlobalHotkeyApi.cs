namespace DeltaMusePlayer.Input.Win32;

/// <summary>
/// 全局热键的注册器。存在的理由与 <see cref="IWin32InputApi"/> 一样：
/// 让上层逻辑（注册失败怎么办、回调怎么切线程、释放顺序）能**不注册真的系统热键**就被测到。
///
/// 说明（这条很容易被误解，写清楚）：
///   这里用的是 User32 的 <c>RegisterHotKey</c>，那是**系统级热键注册**，
///   不是键盘钩子。不装 <c>WH_KEYBOARD_LL</c>、不注入进程、不装驱动、不轮询键盘状态。
///   系统只在本程序注册的那个组合键被按下时投递一条 <c>WM_HOTKEY</c>，
///   不会把其它按键透露给本程序。
/// </summary>
public interface IGlobalHotkeyApi
{
    /// <summary>当前平台是否支持（非 Windows 为 false）。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 注册一个全局热键。成功返回 true；失败返回 false，原因在 <see cref="LastError"/>。
    /// 同一个组合键被别的程序占用时会失败（<c>ERROR_HOTKEY_ALREADY_REGISTERED</c>）。
    /// </summary>
    bool Register(int id, HotkeyModifiers modifiers, uint virtualKey);

    /// <summary>注销热键。没注册过也应当安全返回。</summary>
    void Unregister(int id);

    /// <summary>
    /// 处理一条待处理的系统消息。返回 true 表示这条是**本热键**触发的、
    /// 且已经调用过 <paramref name="onHotkey"/>。
    ///
    /// 调用方用一个后台线程循环调它，所以这里必须是阻塞等待（有超时）而不是忙等。
    /// </summary>
    bool PumpOnce(int id, Action onHotkey, int timeoutMs);

    /// <summary>
    /// 唤醒阻塞中的 <see cref="PumpOnce"/>，让它立刻返回。
    /// 退出时用它，避免为了等一次超时白白多转一圈。
    /// 未注册时也应当安全返回。
    /// </summary>
    void WakeUp();

    /// <summary>最近一次失败的 Win32 错误码（成功为 0）。</summary>
    int LastError { get; }

    /// <summary>把错误码翻成人能看懂的一句话。</summary>
    static string Describe(int win32Error) => win32Error switch
    {
        0 => "没有错误",
        1409 => "这个组合键已被其它程序占用（ERROR_HOTKEY_ALREADY_REGISTERED）",
        1400 => "窗口句柄无效（ERROR_INVALID_WINDOW_HANDLE）",
        87 => "参数无效（ERROR_INVALID_PARAMETER）",
        _ => $"Win32 错误码 {win32Error}",
    };
}

/// <summary>热键修饰键。数值与 Win32 的 MOD_* 一致，可以直接传下去。</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,

    /// <summary>阻止按键继续传给前台窗口（配合 None 修饰键用）。</summary>
    NoRepeat = 0x4000,
}

/// <summary>一个已注册热键的说明，用于日志与界面。</summary>
public sealed record GlobalHotkeyInfo(int Id, HotkeyModifiers Modifiers, uint VirtualKey)
{
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
            parts.Add(VirtualKeyName(VirtualKey));
            return string.Join("+", parts);
        }
    }

    private static string VirtualKeyName(uint vk) => vk switch
    {
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",           // F1..F24
        _ => $"VK 0x{vk:X2}",
    };
}
