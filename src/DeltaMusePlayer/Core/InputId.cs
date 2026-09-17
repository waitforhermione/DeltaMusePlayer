namespace DeltaMusePlayer.Core;

/// <summary>输入的物理种类。</summary>
public enum InputKind
{
    Key,
    MouseButton,
}

/// <summary>
/// 一个物理输入（键盘键或鼠标键）的规范标识。
///
/// 规范化的意义：同一个物理输入必须只有一个实例，否则 InputBackend 的
/// 「按下/抬起记账」会因为 'z' 与 'Z' 这类写法不同而漏抬键。
/// </summary>
public readonly struct InputId : IEquatable<InputId>
{
    public InputKind Kind { get; }

    /// <summary>规范名。键盘：单字符键统一大写（"z" → "Z"），命名键保持原样（"PageUp"）。鼠标："Left" / "Right" / "Middle"。</summary>
    public string Name { get; }

    private InputId(InputKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public static InputId Key(string name) => new(InputKind.Key, NormalizeKeyName(name));

    public static InputId Mouse(string name) => new(InputKind.MouseButton, NormalizeMouseName(name));

    public bool IsMouse => Kind == InputKind.MouseButton;

    public static string NormalizeKeyName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) return "";
        if (n.Length == 1) return char.ToUpperInvariant(n[0]).ToString();
        return n;
    }

    /// <summary>鼠标键名归一：mouse_left / MouseLeft / left → "Left"。</summary>
    public static string NormalizeMouseName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) return "";
        n = n.Replace("mouse_", "", StringComparison.OrdinalIgnoreCase)
             .Replace("mouse", "", StringComparison.OrdinalIgnoreCase)
             .Replace("_", "").Replace("-", "").Replace(" ", "");
        return n.ToLowerInvariant() switch
        {
            "left" or "l" or "lbutton" => "Left",
            "right" or "r" or "rbutton" => "Right",
            "middle" or "m" or "mbutton" => "Middle",
            _ => n,
        };
    }

    /// <summary>
    /// 解析方案里写的输入名：支持 "mouse_left" / "MouseLeft" / "Z" / "PageUp" / "Space"。
    /// 空的或认不出的名字返回 false（该修饰没有绑定）。
    /// </summary>
    public static bool TryParse(string? raw, out InputId id)
    {
        id = default;
        string n = (raw ?? "").Trim();
        if (n.Length == 0) return false;

        string lower = n.ToLowerInvariant();
        if (lower.StartsWith("mouse") || lower.StartsWith("m_"))
        {
            string mouseName = NormalizeMouseName(n);
            if (mouseName is "Left" or "Right" or "Middle")
            {
                id = Mouse(mouseName);
                return true;
            }
            return false;
        }

        string keyName = NormalizeKeyName(n);
        if (keyName.Length == 0) return false;
        id = Key(keyName);
        return true;
    }

    public bool Equals(InputId other) => Kind == other.Kind &&
        string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is InputId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(Name));

    public static bool operator ==(InputId a, InputId b) => a.Equals(b);

    public static bool operator !=(InputId a, InputId b) => !a.Equals(b);

    /// <summary>日志/CSV 里的写法：鼠标写成 MOUSE_LEFT，键写成 Z / PageUp。</summary>
    public string Label => Kind == InputKind.MouseButton ? $"MOUSE_{Name.ToUpperInvariant()}" : Name;

    public override string ToString() => Label;
}
