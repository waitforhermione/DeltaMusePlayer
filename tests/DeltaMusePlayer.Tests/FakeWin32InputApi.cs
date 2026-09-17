namespace DeltaMusePlayer.Tests;

using DeltaMusePlayer.Input;
using DeltaMusePlayer.Input.Win32;

/// <summary>
/// 假的 Win32 输入 API：只记录调用，绝不真的注入。
///
/// 所有 <see cref="WindowsInputBackend"/> 的逻辑（VK/扫描码、标志位、held 记账、
/// 错误处理、ReleaseAll 的 best-effort）都靠它来测。
/// 可以按「第 N 次注入开始失败」制造部分失败场景。
/// </summary>
internal sealed class FakeWin32InputApi : IWin32InputApi
{
    public sealed record KeyCall(ushort VirtualKey, ushort ScanCode, bool KeyUp, bool ExtendedKey)
    {
        public uint Flags => Win32InputConstants.KeyboardFlags(KeyUp, ExtendedKey);

        public override string ToString()
            => $"KEY vk=0x{VirtualKey:X2} sc=0x{ScanCode:X2} flags=0x{Flags:X4} {(KeyUp ? "UP" : "DOWN")}";
    }

    public sealed record MouseCall(MouseButtonKind Button, bool ButtonUp)
    {
        public uint Flags => Win32InputConstants.MouseFlag(Button, ButtonUp);

        public override string ToString()
            => $"MOUSE {Button} flags=0x{Flags:X4} {(ButtonUp ? "UP" : "DOWN")}";
    }

    private readonly object _gate = new();
    private readonly List<object> _ordered = new();

    public List<KeyCall> KeyCalls { get; } = new();
    public List<MouseCall> MouseCalls { get; } = new();

    public bool IsSupported { get; set; } = true;

    /// <summary>扫描码表：默认按 US 布局给 Z X C V B N M , ；可以覆盖。</summary>
    public Dictionary<ushort, ushort> ScanCodes { get; } = new()
    {
        [0x5A] = 0x2C,  // Z
        [0x58] = 0x2D,  // X
        [0x43] = 0x2E,  // C
        [0x56] = 0x2F,  // V
        [0x42] = 0x30,  // B
        [0x4E] = 0x31,  // N
        [0x4D] = 0x32,  // M
        [VirtualKeyMap.VK_OEM_COMMA] = 0x33,   // ,
    };

    /// <summary>从第 N 次注入开始失败（1 起；0 或负数 = 从不失败）。</summary>
    public int FailFromCall { get; set; }

    /// <summary>失败时返回的 Win32 错误码（默认 87 = ERROR_INVALID_PARAMETER）。</summary>
    public int FailureError { get; set; } = 87;

    public int TotalCalls
    {
        get { lock (_gate) return _ordered.Count; }
    }

    /// <summary>按真实调用顺序渲染，用于与 Trace 后端的事件序列逐条比对。</summary>
    public IReadOnlyList<object> Ordered
    {
        get { lock (_gate) return _ordered.ToArray(); }
    }

    public string Trace()
    {
        lock (_gate) return string.Join(" | ", _ordered);
    }

    public ushort MapVirtualKeyToScanCode(ushort virtualKey)
    {
        lock (_gate) return ScanCodes.TryGetValue(virtualKey, out ushort sc) ? sc : (ushort)0;
    }

    public uint SendKeyboard(ushort virtualKey, ushort scanCode, bool keyUp, bool extendedKey)
    {
        lock (_gate)
        {
            var call = new KeyCall(virtualKey, scanCode, keyUp, extendedKey);
            KeyCalls.Add(call);
            _ordered.Add(call);
            if (ShouldFail())
            {
                LastError = FailureError;
                return 0;
            }
            LastError = 0;
            return 1;
        }
    }

    public uint SendMouse(MouseButtonKind button, bool buttonUp)
    {
        lock (_gate)
        {
            var call = new MouseCall(button, buttonUp);
            MouseCalls.Add(call);
            _ordered.Add(call);
            if (ShouldFail())
            {
                LastError = FailureError;
                return 0;
            }
            LastError = 0;
            return 1;
        }
    }

    private bool ShouldFail() => FailFromCall > 0 && _ordered.Count >= FailFromCall;

    public int GetLastError()
    {
        lock (_gate) return LastError;
    }

    public int LastError { get; private set; }

    public void Clear()
    {
        lock (_gate)
        {
            KeyCalls.Clear();
            MouseCalls.Clear();
            _ordered.Clear();
            LastError = 0;
        }
    }
}
