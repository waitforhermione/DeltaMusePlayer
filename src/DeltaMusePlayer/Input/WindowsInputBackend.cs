namespace DeltaMusePlayer.Input;

using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input.Win32;

/// <summary>
/// 真实 Windows 输入后端。
///
/// 职责边界**只有**一件事：收到逻辑动作（按下/抬起某个键或鼠标键）→ 调 Win32 注入。
/// 它不参与 timing、不参与 mapping、不参与 note planning，也不知道「音符」是什么。
///
/// 记账原则（需求 5 / 6）：
///   * 自己维护 <see cref="HeldInputs"/>，只释放**自己确认按下过**的输入。
///     绝不「Stop 时把所有键都发一遍 key-up」—— 用户自己物理按住的键不归本程序管。
///   * 重复按下 / 抬起未按下的输入：记 warning 后当成 no-op，不再往系统里塞一次，
///     避免状态进一步失真。正常的 PlaybackPlan 不该产生这种情况（Trace 后端会当场抓到）。
///
/// 失败原则（需求 9 / 10）：
///   * 每次 SendInput 都检查返回数量，失败就抛 <see cref="WindowsInputException"/>
///     （带 action / input / Win32 错误码）。
///   * <see cref="ReleaseAll"/> 是 best-effort：每个输入都尝试释放，最后汇总失败。
/// </summary>
public sealed class WindowsInputBackend : IInputBackend
{
    private readonly IWin32InputApi _api;
    private readonly object _gate = new();
    private readonly HashSet<InputId> _heldKeys = new();
    private readonly HashSet<InputId> _heldMouse = new();
    private readonly Dictionary<InputId, KeyMapping> _keyCache = new();

    public WindowsInputBackend(IWin32InputApi? api = null)
    {
        _api = api ?? new Win32InputApi();
        if (!_api.IsSupported)
            throw new PlatformNotSupportedException("WindowsInputBackend 只能在 Windows 上使用。");
    }

    public string Name => "Windows SendInput";

    public bool SendsRealInput => true;

    /// <summary>诊断探针：非 null 时记录每条派发的「计划 vs 实际」。默认关闭。</summary>
    public IDispatchTimingSink? TimingSink { get; set; }

    /// <summary>warning 出口（重复按下之类的异常情况）。默认只留在内存里。</summary>
    public Action<string>? Warn { get; set; }

    public IReadOnlyCollection<InputId> HeldInputs
    {
        get { lock (_gate) return _heldKeys.Concat(_heldMouse).ToArray(); }
    }

    // ---------------------------------------------------------------- 键盘

    public void KeyDown(InputId key) => SendKey(key, down: true);

    public void KeyUp(InputId key) => SendKey(key, down: false);

    private void SendKey(InputId key, bool down)
    {
        if (key.Kind != InputKind.Key)
            throw new ArgumentException($"KeyDown/KeyUp 只接受键盘输入，收到 {key.Label}。", nameof(key));

        lock (_gate)
        {
            bool alreadyHeld = _heldKeys.Contains(key);
            if (down && alreadyHeld)
            {
                Warn?.Invoke($"{key.Label} 已经是按下状态，跳过重复按下（不发第二次 key-down）。");
                return;
            }
            if (!down && !alreadyHeld)
            {
                Warn?.Invoke($"{key.Label} 没有处于按下状态，跳过抬起（不发多余的 key-up）。");
                return;
            }
        }

        KeyMapping mapping = ResolveKey(key);

        uint sent = _api.SendKeyboard(mapping.VirtualKey, mapping.ScanCode, keyUp: !down, mapping.ExtendedKey);
        if (sent != 1)
            throw new WindowsInputException(
                down ? "press" : "release", key, _api.LastError,
                $"SendInput 注入键盘事件失败：{key.Label}（VK=0x{mapping.VirtualKey:X2}, SC=0x{mapping.ScanCode:X2}）" +
                $"{(down ? "按下" : "抬起")}，实际发送 {sent} 个事件，Win32 错误码 {_api.LastError}。");

        lock (_gate)
        {
            if (down) _heldKeys.Add(key);
            else _heldKeys.Remove(key);
        }
    }

    /// <summary>
    /// 逻辑键 → VK / 扫描码。扫描码一律问操作系统，不硬编码（尤其是 OEM 标点）。
    /// 认不出或取不到扫描码时抛异常 —— 那说明键位方案里有本后端不支持的键。
    /// </summary>
    private KeyMapping ResolveKey(InputId key)
    {
        lock (_gate)
        {
            if (_keyCache.TryGetValue(key, out var cached)) return cached;
        }

        if (!VirtualKeyMap.TryResolve(key.Name, _api, out var mapping))
            throw new WindowsInputException("resolve", key, _api.LastError,
                $"无法把逻辑键 {key.Name} 解析成 VK / 扫描码：它不在支持的键表里，或 " +
                "MapVirtualKey 没有返回扫描码。请检查键位方案里的键名。");

        lock (_gate)
        {
            _keyCache[key] = mapping;
        }
        return mapping;
    }

    // ---------------------------------------------------------------- 鼠标

    public void MouseDown(InputId button) => SendMouse(button, down: true);

    public void MouseUp(InputId button) => SendMouse(button, down: false);

    private void SendMouse(InputId button, bool down)
    {
        if (!button.IsMouse)
            throw new ArgumentException($"MouseDown/MouseUp 只接受鼠标键，收到 {button.Label}。", nameof(button));

        MouseButtonKind kind = ToMouseKind(button);

        lock (_gate)
        {
            bool alreadyHeld = _heldMouse.Contains(button);
            if (down && alreadyHeld)
            {
                Warn?.Invoke($"{button.Label} 已经是按下状态，跳过重复按下。");
                return;
            }
            if (!down && !alreadyHeld)
            {
                Warn?.Invoke($"{button.Label} 没有处于按下状态，跳过抬起。");
                return;
            }
        }

        uint sent = _api.SendMouse(kind, buttonUp: !down);
        if (sent != 1)
            throw new WindowsInputException(
                down ? "press" : "release", button, _api.LastError,
                $"SendInput 注入鼠标事件失败：{button.Label} {(down ? "按下" : "抬起")}，实际发送 {sent} 个事件，" +
                $"Win32 错误码 {_api.LastError}。");

        lock (_gate)
        {
            if (down) _heldMouse.Add(button);
            else _heldMouse.Remove(button);
        }
    }

    private static MouseButtonKind ToMouseKind(InputId button) => button.Name switch
    {
        "Left" => MouseButtonKind.Left,
        "Middle" => MouseButtonKind.Middle,
        "Right" => MouseButtonKind.Right,
        _ => throw new WindowsInputException("resolve", button, 0,
            $"不支持的鼠标键：{button.Name}（只支持 Left / Middle / Right）。"),
    };

    // ---------------------------------------------------------------- ReleaseAll

    /// <summary>
    /// best-effort 释放：先取一笔快照，然后每个输入都单独尝试一次，
    /// 任何一个失败都不影响后面的。失败的输入会**放回账上**，
    /// 让状态如实反映「系统里可能还按着」，下次调用还能再试。
    ///
    /// 注意这里是「一笔快照 + 逐个尝试一遍」，不是 while 循环重试：
    /// 一直失败时不能死循环。
    /// </summary>
    public void ReleaseAll()
    {
        var failures = new List<WindowsInputException>();

        // 先键盘、后鼠标：音键先松、修饰键（鼠标）后松，与演奏时的收尾顺序一致。
        ReleaseBatch(_heldKeys, failures, isMouse: false);
        ReleaseBatch(_heldMouse, failures, isMouse: true);

        if (failures.Count > 0) throw new ReleaseAllException(failures);
    }

    private void ReleaseBatch(HashSet<InputId> held, List<WindowsInputException> failures, bool isMouse)
    {
        InputId[] batch;
        lock (_gate)
        {
            if (held.Count == 0) return;
            batch = held.ToArray();
            held.Clear();          // 先按「已经抬起来」处理，失败的再补回去
        }

        foreach (var id in batch)
        {
            try
            {
                uint sent;
                if (isMouse)
                {
                    sent = _api.SendMouse(ToMouseKind(id), buttonUp: true);
                }
                else
                {
                    if (!VirtualKeyMap.TryResolve(id.Name, _api, out var mapping))
                        throw new WindowsInputException("release-all", id, _api.LastError,
                            $"{id.Name} 无法解析成扫描码，无法释放。");
                    sent = _api.SendKeyboard(mapping.VirtualKey, mapping.ScanCode, keyUp: true, mapping.ExtendedKey);
                }

                if (sent != 1)
                    throw new WindowsInputException("release-all", id, _api.LastError,
                        $"{id.Label} 释放失败，实际发送 {sent} 个事件。");
            }
            catch (WindowsInputException ex)
            {
                failures.Add(ex);
                lock (_gate) held.Add(id);   // 没抬成功：继续认为按着，状态不丢
            }
        }
    }

    public void Dispose()
    {
        try
        {
            ReleaseAll();
        }
        catch (ReleaseAllException)
        {
            // Dispose 里不再抛：已经尽力抬过一遍了
        }
    }
}
