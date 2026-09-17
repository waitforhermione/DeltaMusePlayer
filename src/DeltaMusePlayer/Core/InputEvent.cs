namespace DeltaMusePlayer.Core;

/// <summary>模拟输入的种类。</summary>
public enum InputActionType
{
    // 顺序即同一时刻的派发优先级：先抬修饰 → 再按下修饰/音键 → 最后抬音键。
    KeyUp = 0,
    MouseUp = 1,
    MouseDown = 2,
    KeyDown = 3,
}

/// <summary>
/// 编译好的播放计划里的一个输入事件。
/// 时间是**播放墙钟毫秒**（速度已经折算进编译结果），排序后由 scheduler 顺序推进。
/// </summary>
public sealed record InputEvent(
    double TimeMs,
    InputActionType Type,
    InputId Input,
    int NoteIndex,
    int Pitch)
{
    public bool IsDown => Type is InputActionType.KeyDown or InputActionType.MouseDown;

    public bool IsMouseEvent => Type is InputActionType.MouseDown or InputActionType.MouseUp;

    /// <summary>事件的稳定排序键：时间 → 类型优先级 → 输入名 → 音符序号。</summary>
    public (double TimeMs, int Type, string Input, int NoteIndex) SortKey
        => (TimeMs, (int)Type, Input.Label, NoteIndex);

    public string TypeLabel => Type switch
    {
        InputActionType.KeyDown => "key_down",
        InputActionType.KeyUp => "key_up",
        InputActionType.MouseDown => "mouse_down",
        InputActionType.MouseUp => "mouse_up",
        _ => Type.ToString(),
    };

    public string ActionLabel => Type switch
    {
        InputActionType.KeyDown or InputActionType.MouseDown => "DOWN",
        _ => "UP",
    };

    /// <summary>trace 行，例如 `00:00.015  Z DOWN  (note 0 C4)`。</summary>
    public string ToTraceLine()
    {
        string note = Pitch >= 0 ? $"  (note {NoteIndex} {Music.NoteName(Pitch)})" : "";
        return $"{Music.TimeLabel(TimeMs)}  {Input.Label} {ActionLabel}{note}";
    }
}
