namespace DeltaMusePlayer.Playback;

using DeltaMusePlayer.Input.Win32;

/// <summary>
/// 全局紧急停止热键。默认 **F9**。
///
/// 为什么需要它（这不是锦上添花）：
///   真实播放的正常流程是「点播放 → 在倒计时里切到目标窗口」。
///   一旦切走，本程序窗口就不是焦点了，挂在窗口上的 <c>KeyDown</c> 收不到任何按键 ——
///   也就是说窗口级快捷键恰恰在**最需要它的时刻**是聋的。
///   所以紧急停止必须走系统级热键注册（<c>RegisterHotKey</c>），才能在任何前台窗口上生效。
///
/// 它**不是**键盘钩子：没有 <c>WH_KEYBOARD_LL</c>，不注入进程，不装驱动，
/// 不轮询键盘状态。系统只在本程序注册的那个组合键被按下时投递一条 <c>WM_HOTKEY</c>。
///
/// 失败是**可接受的降级**，不是致命错误：组合键可能已被别的程序占用。
/// 这种情况下 <see cref="IsActive"/> 为 false、<see cref="FailureReason"/> 有原因，
/// 调用方应当把界面上的提示改回「窗口内 F9」，而不是假装热键可用。
/// </summary>
public sealed class EmergencyHotkey : IDisposable
{
    /// <summary>WM_HOTKEY 的 id。1 是本程序唯一一个全局热键。</summary>
    public const int HotkeyId = 1;

    /// <summary>默认虚拟键码：F9（0x78）。</summary>
    public const uint DefaultVirtualKey = 0x78;

    private readonly IGlobalHotkeyApi _api;
    private readonly Action _onTriggered;
    private readonly uint _virtualKey;
    private readonly HotkeyModifiers _modifiers;
    private readonly object _gate = new();

    private Thread? _thread;
    private volatile bool _stop;

    public EmergencyHotkey(
        IGlobalHotkeyApi api,
        Action onTriggered,
        uint virtualKey = DefaultVirtualKey,
        HotkeyModifiers modifiers = HotkeyModifiers.NoRepeat)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onTriggered = onTriggered ?? throw new ArgumentNullException(nameof(onTriggered));
        _virtualKey = virtualKey;
        _modifiers = modifiers;
    }

    /// <summary>热键是否真的注册上了。false 表示正在降级模式下运行。</summary>
    public bool IsActive { get; private set; }

    /// <summary>注册失败的原因（成功时为 null）。用于界面提示与日志。</summary>
    public string? FailureReason { get; private set; }

    /// <summary>人可读的组合键名，例如 "F9"。</summary>
    public string Label => new GlobalHotkeyInfo(HotkeyId, _modifiers, _virtualKey).Label;

    /// <summary>
    /// 建立后台线程、注册热键、开始收消息。注册失败不会抛异常，只把
    /// <see cref="IsActive"/> 置 false 并记下 <see cref="FailureReason"/>。
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return;   // 幂等

            if (!_api.IsSupported)
            {
                IsActive = false;
                FailureReason = "当前平台不支持系统级热键注册。";
                return;
            }

            _stop = false;
            var ready = new ManualResetEventSlim(false);
            bool ok = false;
            int error = 0;

            // 注册、消息循环、注销必须在**同一个线程**上：窗口过程由系统在该线程回调。
            _thread = new Thread(() =>
            {
                // 整个线程体包在 try/catch 里：.NET 对**线程入口**的未处理异常一律终止进程，
                // 哪怕是后台线程。紧急停止是保险丝，它出问题绝不能把演奏程序带崩。
                try
                {
                    ok = _api.Register(HotkeyId, _modifiers, _virtualKey);
                    error = _api.LastError;
                    ready.Set();
                    if (!ok) return;

                    while (!_stop)
                        _api.PumpOnce(HotkeyId, _onTriggered, timeoutMs: 200);

                    _api.Unregister(HotkeyId);
                }
                catch
                {
                    ready.Set();   // 别让 Start() 白等到超时
                    // 吞掉：见上面的注释，这里宁可热键失效也不能让进程崩。
                }
            })
            {
                IsBackground = true,
                Name = "DeltaMusePlayer-EmergencyHotkey",
            };
            _thread.Start();

            // 等注册结果，最多 2 秒。等不到就按失败处理，绝不无限期挂着 UI。
            if (!ready.Wait(TimeSpan.FromSeconds(2)))
            {
                IsActive = false;
                FailureReason = "等待全局热键注册超时（2 秒）。";
                return;
            }

            IsActive = ok;
            FailureReason = ok ? null : IGlobalHotkeyApi.Describe(error);
        }
    }

    public void Dispose()
    {
        Thread? t;
        lock (_gate)
        {
            t = _thread;
            _thread = null;
        }
        if (t is null) return;

        _stop = true;
        // 唤醒阻塞中的 PumpOnce，否则要等它超时（最多 200ms）才看得到 _stop。
        try { _api.WakeUp(); } catch { /* 退出路径上尽力而为 */ }

        if (!t.Join(TimeSpan.FromSeconds(2)))
        {
            // 不抛异常：关窗路径上宁可留一个后台线程，也不能把退出卡死。
        }

        IsActive = false;
    }
}
