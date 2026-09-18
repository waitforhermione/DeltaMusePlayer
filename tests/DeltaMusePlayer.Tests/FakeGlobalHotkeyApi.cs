namespace DeltaMusePlayer.Tests;

using DeltaMusePlayer.Input.Win32;

/// <summary>
/// 假的全局热键注册器：只记账，**绝不注册真的系统热键**。
/// 让 <see cref="DeltaMusePlayer.Playback.EmergencyHotkey"/> 的失败降级、
/// 线程编排与释放顺序都能被确定性地测到。
/// </summary>
internal sealed class FakeGlobalHotkeyApi : IGlobalHotkeyApi
{
    private readonly object _gate = new();
    private readonly HashSet<int> _registered = new();

    public bool IsSupported { get; set; } = true;

    /// <summary>true 时 <see cref="Register"/> 一律失败，并返回 <see cref="FailureError"/>。</summary>
    public bool FailRegistration { get; set; }

    /// <summary>注册失败时返回的 Win32 错误码（默认 1409 = 热键已被占用）。</summary>
    public int FailureError { get; set; } = 1409;

    public int LastError { get; private set; }

    /// <summary>Register 被调用的次数（含失败）。</summary>
    public int RegisterCalls { get; private set; }

    /// <summary>成功注册过的 id。</summary>
    public IReadOnlyList<int> RegisteredIds
    {
        get { lock (_gate) return _registered.ToArray(); }
    }

    /// <summary>已注销的 id 序列。</summary>
    public List<int> UnregisterCalls { get; } = new();

    /// <summary>PumpOnce 被调用的次数。</summary>
    public int PumpCalls { get; private set; }

    /// <summary>WakeUp 被调用的次数。</summary>
    public int WakeUpCalls { get; private set; }

    /// <summary>记录下来的修饰键与虚拟键。</summary>
    public (HotkeyModifiers Modifiers, uint VirtualKey)? LastRegistration { get; private set; }

    /// <summary>
    /// 下一次 <see cref="PumpOnce"/> 是否要触发一次「热键被按下」。
    /// 用来在不注册真热键的前提下模拟用户按键。
    /// </summary>
    public bool FireOnNextPump { get; set; }

    /// <summary>PumpOnce 每次被调用时立刻返回（不真的等待）。</summary>
    public bool ReturnImmediately { get; set; } = true;

    /// <summary>
    /// 让消息循环一直转下去，直到 <see cref="ReleasePump"/> 打开。
    /// 用来测「注册成功之后一直在收消息」这条路径。
    /// </summary>
    public bool BlockPumpUntilReleased { get; set; }

    private volatile bool _pumpReleased;

    /// <summary>放行 <see cref="BlockPumpUntilReleased"/> 卡住的循环。</summary>
    public void ReleasePump() => _pumpReleased = true;

    public bool Register(int id, HotkeyModifiers modifiers, uint virtualKey)
    {
        lock (_gate)
        {
            RegisterCalls++;
            LastRegistration = (modifiers, virtualKey);
            if (FailRegistration)
            {
                LastError = FailureError;
                return false;
            }
            _registered.Add(id);
            LastError = 0;
            return true;
        }
    }

    public void Unregister(int id)
    {
        lock (_gate)
        {
            UnregisterCalls.Add(id);
            _registered.Remove(id);
            LastError = 0;
        }
    }

    public bool PumpOnce(int id, Action onHotkey, int timeoutMs)
    {
        lock (_gate)
        {
            PumpCalls++;
            if (FireOnNextPump)
            {
                FireOnNextPump = false;
                try { onHotkey(); }
                catch { /* 与真实实现的契约一致：回调异常绝不允许逃出 PumpOnce */ }
                return true;
            }
        }

        if (BlockPumpUntilReleased)
        {
            // 模拟真实的消息等待：会阻塞，但能被 WakeUp / ReleasePump 放行。
            var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 50);
            while (!_pumpReleased && Environment.TickCount64 < deadline)
                Thread.Sleep(1);
            return false;
        }

        if (!ReturnImmediately) Thread.Sleep(Math.Min(timeoutMs, 5));
        return false;
    }

    public void WakeUp()
    {
        lock (_gate) WakeUpCalls++;
        ReleasePump();
    }
}
