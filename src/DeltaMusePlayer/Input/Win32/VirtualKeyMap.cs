namespace DeltaMusePlayer.Input.Win32;

using DeltaMusePlayer.Core;

/// <summary>一个逻辑键在 Windows 上的落点。</summary>
public sealed record KeyMapping(
    string LogicalKey,
    ushort VirtualKey,
    ushort ScanCode,
    bool ExtendedKey,
    string Notes)
{
    public override string ToString()
        => $"{LogicalKey,-8} VK=0x{VirtualKey:X2} ({VirtualKey,3})  SC=0x{ScanCode:X2} ({ScanCode,3})  " +
           $"ext={(ExtendedKey ? "yes" : "no ")}  {Notes}";
}

/// <summary>
/// 逻辑键名 → 虚拟键码 → 扫描码。
///
/// 刻度原则：只在**查不到**虚拟键码时报错；扫描码一律问操作系统（MapVirtualKey），
/// 不把扫描码硬编码进表里 —— 硬编码的 OEM 标点扫描码在不同键盘布局上会错。
/// </summary>
public static class VirtualKeyMap
{
    /// <summary>标点键的虚拟键码（US 布局）。</summary>
    private static readonly Dictionary<char, ushort> OemVirtualKeys = new()
    {
        [','] = VK_OEM_COMMA,      // 0xBC —— profile 里的第 8 个音键就是这个
        ['.'] = VK_OEM_PERIOD,     // 0xBE
        [';'] = VK_OEM_1,          // 0xBA
        ['/'] = VK_OEM_2,          // 0xBF
        ['\''] = VK_OEM_3,         // 0xDE
        ['['] = VK_OEM_4,          // 0xDB
        [']'] = VK_OEM_6,          // 0xDD
        ['\\'] = VK_OEM_5,         // 0xDC
        ['-'] = VK_OEM_MINUS,      // 0xBD
        ['='] = VK_OEM_PLUS,       // 0xBB
        ['`'] = VK_OEM_3_BACKTICK, // 0xC0
    };

    public const ushort VK_OEM_COMMA = 0xBC;
    public const ushort VK_OEM_PERIOD = 0xBE;
    public const ushort VK_OEM_1 = 0xBA;
    public const ushort VK_OEM_2 = 0xBF;
    public const ushort VK_OEM_3 = 0xDE;
    public const ushort VK_OEM_4 = 0xDB;
    public const ushort VK_OEM_5 = 0xDC;
    public const ushort VK_OEM_6 = 0xDD;
    public const ushort VK_OEM_MINUS = 0xBD;
    public const ushort VK_OEM_PLUS = 0xBB;
    public const ushort VK_OEM_3_BACKTICK = 0xC0;

    /// <summary>命名键 → 虚拟键码。</summary>
    private static readonly Dictionary<string, ushort> NamedVirtualKeys = new(StringComparer.Ordinal)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Back"] = 0x08, ["Escape"] = 0x1B,
        ["Shift"] = 0xA0, ["Ctrl"] = 0xA2, ["Alt"] = 0xA4,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Home"] = 0x24, ["End"] = 0x23,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74, ["F6"] = 0x75,
        ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63, ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67, ["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
    };

    /// <summary>需要 EXTENDEDKEY 标志的虚拟键码（增强键区）。</summary>
    private static readonly HashSet<ushort> ExtendedVirtualKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x6F,   // PgUp/PgDn/End/Home/方向/Ins/Del/小键盘除号
    };

    /// <summary>
    /// 取逻辑键名的虚拟键码。
    /// 只认：单个字母、单个数字、上表的 OEM 标点、上表的命名键。
    /// </summary>
    public static bool TryGetVirtualKey(string? logicalKey, out ushort virtualKey)
    {
        virtualKey = 0;
        string name = InputId.NormalizeKeyName(logicalKey);
        if (name.Length == 0) return false;

        if (name.Length == 1)
        {
            char c = name[0];
            if (c is >= 'A' and <= 'Z') { virtualKey = c; return true; }        // 字母键的 VK 就是 ASCII 大写
            if (c is >= '0' and <= '9') { virtualKey = c; return true; }
            return OemVirtualKeys.TryGetValue(c, out virtualKey);
        }

        return NamedVirtualKeys.TryGetValue(name, out virtualKey);
    }

    public static bool IsExtendedKey(ushort virtualKey) => ExtendedVirtualKeys.Contains(virtualKey);

    /// <summary>
    /// 解析成完整映射（含扫描码，由 <paramref name="api"/> 提供）。
    /// 逻辑键名认不出时返回 false；扫描码取不到（0）也返回 false —— 那种情况下发出去是无效按键。
    /// </summary>
    public static bool TryResolve(string? logicalKey, IWin32InputApi api, out KeyMapping mapping)
    {
        mapping = new KeyMapping("", 0, 0, false, "");
        if (!TryGetVirtualKey(logicalKey, out ushort vk)) return false;

        ushort scan = api.MapVirtualKeyToScanCode(vk);
        if (scan == 0) return false;

        string name = InputId.NormalizeKeyName(logicalKey);
        mapping = new KeyMapping(name, vk, scan, IsExtendedKey(vk), Describe(vk));
        return true;
    }

    private static string Describe(ushort vk) => vk switch
    {
        VK_OEM_COMMA => "US 布局的逗号键（VK_OEM_COMMA）",
        VK_OEM_PERIOD => "US 布局的句点键",
        >= 0x41 and <= 0x5A => "字母键",
        >= 0x30 and <= 0x39 => "数字键",
        >= 0x70 and <= 0x7B => "功能键",
        _ => "命名键",
    };

    /// <summary>把「键名 → 虚拟键码」的静态表全部导出，用于自检与文档。</summary>
    public static IReadOnlyList<(string Logical, ushort Vk)> AllKnownKeys()
    {
        var list = new List<(string, ushort)>();
        for (char c = 'A'; c <= 'Z'; c++) list.Add((c.ToString(), c));
        for (char c = '0'; c <= '9'; c++) list.Add((c.ToString(), c));
        foreach (var kv in OemVirtualKeys) list.Add((kv.Key.ToString(), kv.Value));
        foreach (var kv in NamedVirtualKeys) list.Add((kv.Key, kv.Value));
        return list;
    }
}
