using System.Runtime.InteropServices;

namespace DeltaMusePlayer.Input.Win32;

/// <summary>
/// 真实实现：只调用普通 Windows 用户态 API。
///
/// 用到的 Win32 调用一共两个：
///   * <c>SendInput</c>      —— 注入键盘 / 鼠标事件
///   * <c>MapVirtualKeyW</c> —— 虚拟键码 → 扫描码（MAPVK_VK_TO_VSC）
///
/// 错误码用 <c>Marshal.GetLastWin32Error()</c> 在 SendInput 返回之后**立刻**取，
/// 不用 P/Invoke 的 GetLastError：托管代码在两行之间可能已经覆盖了线程错误码。
///
/// 不涉及：进程句柄、内存读写、远程线程、驱动、hook、反作弊交互。
/// </summary>
public sealed class Win32InputApi : IWin32InputApi
{
    public bool IsSupported => OperatingSystem.IsWindows();

    // ---------------------------------------------------------------- P/Invoke

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    // ---------------------------------------------------------------- 实现

    public ushort MapVirtualKeyToScanCode(ushort virtualKey)
    {
        if (!IsSupported) return 0;
        return (ushort)MapVirtualKeyW(virtualKey, MAPVK_VK_TO_VSC);
    }

    public uint SendKeyboard(ushort virtualKey, ushort scanCode, bool keyUp, bool extendedKey)
    {
        if (!IsSupported) return 0;

        // 扫描码由调用方预先算好；wVk 恒为 0，完全走扫描码路径。
        _ = virtualKey;

        uint flags = KEYEVENTF_SCANCODE;
        if (keyUp) flags |= KEYEVENTF_KEYUP;
        if (extendedKey) flags |= KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        LastError = sent == 1 ? 0 : Marshal.GetLastWin32Error();
        return sent;
    }

    public uint SendMouse(MouseButtonKind button, bool buttonUp)
    {
        if (!IsSupported) return 0;

        uint flags = button switch
        {
            MouseButtonKind.Left => buttonUp ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN,
            MouseButtonKind.Right => buttonUp ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN,
            _ => buttonUp ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN,
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        LastError = sent == 1 ? 0 : Marshal.GetLastWin32Error();
        return sent;
    }

    /// <summary>上一次注入失败时的 Win32 错误码（成功时为 0）。</summary>
    public int LastError { get; private set; }

    public int GetLastError() => LastError;
}
