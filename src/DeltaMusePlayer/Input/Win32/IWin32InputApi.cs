namespace DeltaMusePlayer.Input.Win32;

/// <summary>
/// Win32 输入 API 的最小抽象。
///
/// 为什么要这一层：<see cref="WindowsInputBackend"/> 的全部逻辑（held 记账、错误处理、
/// ReleaseAll 的 best-effort）都必须能在**不真的发按键**的前提下被测到。
/// 生产用 <see cref="Win32InputApi"/>，测试用 FakeWin32InputApi。
///
/// 这一层只暴露 M2 需要的那几个调用，不是 user32 的通用包装。
/// </summary>
public interface IWin32InputApi
{
    /// <summary>当前平台是否支持这套调用。</summary>
    bool IsSupported { get; }

    /// <summary>虚拟键码 → 扫描码（<c>MapVirtualKeyW(vk, MAPVK_VK_TO_VSC)</c>）。查不到返回 0。</summary>
    ushort MapVirtualKeyToScanCode(ushort virtualKey);

    /// <summary>
    /// 发送一个键盘事件（<c>SendInput</c> + <c>KEYBDINPUT</c>）。
    /// 返回实际注入的事件数（成功为 1）。失败返回 0，错误码由 <see cref="GetLastError"/> 取。
    /// </summary>
    uint SendKeyboard(ushort virtualKey, ushort scanCode, bool keyUp, bool extendedKey);

    /// <summary>
    /// 发送一个鼠标按键事件（<c>SendInput</c> + <c>MOUSEINPUT</c>）。
    /// 返回实际注入的事件数（成功为 1）。
    /// </summary>
    uint SendMouse(MouseButtonKind button, bool buttonUp);

    /// <summary>上一个 Win32 错误码（成功注入之后应被清成 0）。</summary>
    int GetLastError();

    /// <summary>上一次注入失败时的 Win32 错误码（成功时为 0）。</summary>
    int LastError { get; }
}

/// <summary>后端要发的鼠标键。</summary>
public enum MouseButtonKind
{
    Left,
    Middle,
    Right,
}

/// <summary>Win32 输入相关常量。集中放一处，避免散落魔数。</summary>
public static class Win32InputConstants
{
    // SendInput 的输入类型
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;

    // 键盘事件标志
    public const uint KeyEventExtendedKey = 0x0001;
    public const uint KeyEventKeyUp = 0x0002;
    public const uint KeyEventScanCode = 0x0008;

    // 鼠标事件标志
    public const uint MouseEventLeftDown = 0x0002;
    public const uint MouseEventLeftUp = 0x0004;
    public const uint MouseEventRightDown = 0x0008;
    public const uint MouseEventRightUp = 0x0010;
    public const uint MouseEventMiddleDown = 0x0020;
    public const uint MouseEventMiddleUp = 0x0040;

    /// <summary>MapVirtualKey 的 MAPVK_VK_TO_VSC。</summary>
    public const uint MapVkToVsc = 0;

    public static uint MouseFlag(MouseButtonKind button, bool up) => button switch
    {
        MouseButtonKind.Left => up ? MouseEventLeftUp : MouseEventLeftDown,
        MouseButtonKind.Right => up ? MouseEventRightUp : MouseEventRightDown,
        _ => up ? MouseEventMiddleUp : MouseEventMiddleDown,
    };

    public static uint KeyboardFlags(bool keyUp, bool extendedKey)
    {
        // 一律带 KEYEVENTF_SCANCODE、wVk = 0：很多目标程序（含用 DirectInput 的）
        // 只认扫描码，不认虚拟键码。这与上游 midikey-player 的做法一致。
        uint flags = KeyEventScanCode;
        if (keyUp) flags |= KeyEventKeyUp;
        if (extendedKey) flags |= KeyEventExtendedKey;
        return flags;
    }
}
