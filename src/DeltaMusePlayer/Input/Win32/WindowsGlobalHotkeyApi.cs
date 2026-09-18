namespace DeltaMusePlayer.Input.Win32;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// <see cref="IGlobalHotkeyApi"/> 的真实实现：User32 的 <c>RegisterHotKey</c> +
/// 一个**属于自己的消息窗口**，跑在**自己的后台线程**上。
///
/// 为什么不用 Avalonia 主窗口的句柄：
///   Avalonia 的窗口过程由它自己掌控，把 <c>WM_HOTKEY</c> 塞进去需要一个
///   未公开的消息钩子入口；而且主窗口关掉之后热键就没了。
///   自己建一个 <c>HWND_MESSAGE</c> 消息窗口最干净：只收自己那条热键，
///   与 UI 框架零耦合，也不依赖任何窗口的可见性或焦点。
///
/// 它**不是**键盘钩子：系统只在本程序注册的那个组合键被按下时投递一条
/// <c>WM_HOTKEY</c>，其它按键一概不经过本程序。不注入、不装驱动、不轮询键盘。
///
/// 线程模型：
///   注册、消息循环、注销全部发生在同一个后台线程上（由 <see cref="EmergencyHotkey"/> 建立），
///   线程自己建窗口、自己跑消息循环、自己退出。
///   <see cref="PumpOnce"/> 只是让同一个循环多转一圈，因此它只能在那个线程上被调用。
///
/// 这里用经典 <c>DllImport</c> 而不是 <c>LibraryImport</c>：
/// 后者要求整个程序集打开 <c>AllowUnsafeBlocks</c>，且无法为
/// <c>WNDCLASSEXW</c> 这类自定义结构生成封送代码。本程序不做 AOT，DllImport 完全够用。
/// </summary>
public sealed class WindowsGlobalHotkeyApi : IGlobalHotkeyApi, IDisposable
{
    // ---------------------------------------------------------------- Win32 常量

    private const uint WmHotkey = 0x0312;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;

    private const uint PmRemove = 0x0001;
    private const uint PmNoRemove = 0x0000;

    private const int WaitTimeout = 0x00000102;   // WAIT_TIMEOUT

    private static readonly nint HwndMessage = new(-3);   // HWND_MESSAGE

    // ---------------------------------------------------------------- 状态

    private readonly object _gate = new();
    private nint _hwnd;
    private ushort _classAtom;
    private nint _module;
    private GCHandle _selfHandle;
    private WndProcDelegate? _wndProc;      // 必须存活，否则 GC 回收后窗口过程变野指针
    private int _hotkeyId;                  // 0 = 未注册；只在该线程上读写
    private uint _registeredModifiers;
    private uint _registeredKey;

    public bool IsSupported => OperatingSystem.IsWindows();

    public int LastError { get; private set; }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>
    /// 在**当前线程**上建好消息窗口并注册热键。必须在消息循环所在的线程上调用。
    /// 失败时返回 false，原因在 <see cref="LastError"/>。
    /// </summary>
    public bool Register(int id, HotkeyModifiers modifiers, uint virtualKey)
    {
        if (!IsSupported) { LastError = 0; return false; }
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), "热键 id 必须为正数。");

        lock (_gate)
        {
            if (_hwnd != nint.Zero) { LastError = 0; return true; }   // 已就绪，幂等

            _module = GetModuleHandleW(null);
            _wndProc = WindowProc;
            _selfHandle = GCHandle.Alloc(this);

            var wc = new WndClassExW
            {
                cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = _module,
                hIcon = nint.Zero,
                hCursor = nint.Zero,
                hbrBackground = nint.Zero,
                lpszMenuName = null,
                lpszClassName = "DeltaMusePlayerHotkeyWindow",
                hIconSm = nint.Zero,
            };

            _classAtom = RegisterClassExW(ref wc);
            if (_classAtom == 0)
            {
                // 类名重复（同一进程第二次注册）时也走这里；直接建窗口再试。
                LastError = Marshal.GetLastWin32Error();
            }

            _hwnd = CreateWindowExW(0, "DeltaMusePlayerHotkeyWindow", "DeltaMusePlayerHotkey",
                                    0, 0, 0, 0, 0, HwndMessage, nint.Zero, _module, nint.Zero);
            if (_hwnd == nint.Zero)
            {
                LastError = Marshal.GetLastWin32Error();
                CleanupNative();
                return false;
            }

            if (!RegisterHotKey(_hwnd, id, (uint)modifiers, virtualKey))
            {
                LastError = Marshal.GetLastWin32Error();
                CleanupNative();
                return false;
            }

            _hotkeyId = id;
            _registeredModifiers = (uint)modifiers;
            _registeredKey = virtualKey;
            LastError = 0;
            return true;
        }
    }

    /// <summary>注销热键并销毁消息窗口。必须与 <see cref="Register"/> 在同一个线程上。</summary>
    public void Unregister(int id)
    {
        lock (_gate)
        {
            if (_hwnd == nint.Zero) return;
            if (_hotkeyId != 0) UnregisterHotKey(_hwnd, _hotkeyId);
            _hotkeyId = 0;
            CleanupNative();
        }
    }

    /// <summary>本次注册是否已经成功。</summary>
    public bool IsRegistered
    {
        get { lock (_gate) return _hotkeyId != 0; }
    }

    /// <summary>已注册的组合键（未注册时为 null）。</summary>
    public GlobalHotkeyInfo? Registered
    {
        get
        {
            lock (_gate)
            {
                return _hotkeyId == 0
                    ? null
                    : new GlobalHotkeyInfo(_hotkeyId, (HotkeyModifiers)_registeredModifiers, _registeredKey);
            }
        }
    }

    private void CleanupNative()
    {
        if (_hwnd != nint.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = nint.Zero;
        }
        if (_classAtom != 0)
        {
            UnregisterClassW("DeltaMusePlayerHotkeyWindow", _module);
            _classAtom = 0;
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _wndProc = null;
    }

    // ---------------------------------------------------------------- 消息循环

    /// <summary>
    /// 阻塞等待一条消息（最多 <paramref name="timeoutMs"/> 毫秒）。
    /// 只有被 <c>WM_HOTKEY</c>（且 id 匹配）唤醒时才返回 true 并调用回调。
    ///
    /// 这个方法**只能在创建窗口的那个线程上**调用 —— 窗口过程由系统在该线程上回调，
    /// 换线程调用会拿不到窗口消息。
    ///
    /// 契约：**无论回调做什么，异常都不得逃出本方法**。
    /// 逃出去就会变成线程入口的未处理异常，而 .NET 对线程入口的未处理异常一律终止整个进程 ——
    /// 紧急停止的保险丝把整个演奏程序带崩，是最不能接受的失败方式。
    /// </summary>
    public bool PumpOnce(int id, Action onHotkey, int timeoutMs)
    {
        if (!IsSupported || timeoutMs < 0) return false;

        uint r = MsgWaitForMultipleObjectsEx(0, nint.Zero, (uint)timeoutMs, 0x1CFF, 0x0004);
        if (r == WaitTimeout) return false;

        bool fired = false;
        var msg = default(Msg);
        while (PeekMessageW(ref msg, nint.Zero, 0, 0, PmRemove))
        {
            if (msg.message == WmHotkey && (int)msg.wParam == id)
            {
                fired = true;
                try { onHotkey(); }
                catch { /* 见上面的契约：绝不让回调的异常变成进程级崩溃 */ }
            }
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return fired;
    }

    /// <summary>让阻塞中的 <see cref="PumpOnce"/> 立刻返回（用于退出时唤醒循环）。</summary>
    public void WakeUp()
    {
        lock (_gate)
        {
            if (_hwnd != nint.Zero) PostMessageW(_hwnd, WmClose, nint.Zero, nint.Zero);
        }
    }

    /// <summary>
    /// 注销**当前已注册**的热键并销毁消息窗口。必须在创建它们的线程上调用。
    /// （<see cref="Unregister"/> 收 id 是为了配合接口；这里由自己记住的 id 兜底，
    /// 免得调用方必须把 id 也传一遍。）
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_hwnd == nint.Zero) return;
            if (_hotkeyId != 0) UnregisterHotKey(_hwnd, _hotkeyId);
            _hotkeyId = 0;
            CleanupNative();
        }
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmHotkey) return nint.Zero;
        if (msg == WmClose) { DestroyWindow(hwnd); return nint.Zero; }
        if (msg == WmDestroy) return nint.Zero;
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true,
               CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassExW lpwcx);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true,
               CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
               CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(ref Msg lpMsg, nint hWnd, uint wMsgFilterMin,
                                            uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "MsgWaitForMultipleObjectsEx")]
    private static extern uint MsgWaitForMultipleObjectsEx(
        uint nCount, nint pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);
}
