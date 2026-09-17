namespace DeltaMusePlayer.Input;

using DeltaMusePlayer.Core;

/// <summary>Trace 里的一条记录。</summary>
public sealed record InputTraceEntry(
    double WallMs,
    InputActionType Type,
    InputId Input,
    int NoteIndex,
    int Pitch)
{
    public bool IsDown => Type is InputActionType.KeyDown or InputActionType.MouseDown;

    public string ActionLabel => IsDown ? "DOWN" : "UP";

    public override string ToString() => $"{Music.TimeLabel(WallMs)}  {Input.Label} {ActionLabel}";
}

/// <summary>
/// 只记录、不发送任何真实输入的后端。
///
/// 所有调度算法首先必须通过它验收：M1 的全部时序测试都跑在这个后端上，
/// Preview 模式在真机上用的也是它。因此它记录的事件序列就是「真实模式会发出的东西」。
///
/// 同时它自建一份按下状态表，任何「重复按下同一个键」「抬起没按下的键」
/// 都会当场抛 <see cref="InvalidOperationException"/> —— 状态机 bug 会被测试直接抓住。
/// </summary>
public sealed class TraceInputBackend : IInputBackend
{
    private readonly object _gate = new();
    private readonly List<InputTraceEntry> _entries = new();
    private readonly HashSet<InputId> _held = new();

    /// <summary>严格自检：true（默认）时状态机错误立刻抛异常。</summary>
    public bool Strict { get; set; } = true;

    public string Name => "Trace";

    public bool SendsRealInput => false;

    /// <summary>记录下来的全部事件，按发生顺序。</summary>
    public IReadOnlyList<InputTraceEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public IReadOnlyCollection<InputId> HeldInputs
    {
        get { lock (_gate) return _held.ToArray(); }
    }

    /// <summary>记录时用的墙钟毫秒（由调用方给出，通常是 FakeClock 或真实时钟）。</summary>
    public Func<double>? NowMs { get; set; }

    /// <summary>每记录一条就回调一次（测试用它实时检查「同一时刻只按一个音键」之类的约束）。</summary>
    public event Action<InputTraceEntry>? EntryRecorded;

    private void Record(InputActionType type, InputId input, bool down)
    {
        lock (_gate)
        {
            if (Strict)
            {
                bool changed = down ? _held.Add(input) : _held.Remove(input);
                if (!changed)
                    throw new InvalidOperationException(
                        down
                            ? $"Trace 后端收到重复按下：{input.Label} 已经是按下状态。"
                            : $"Trace 后端收到抬起一个没按下的输入：{input.Label}。");
            }
            else
            {
                if (down) _held.Add(input); else _held.Remove(input);
            }

            _entries.Add(new InputTraceEntry(NowMs?.Invoke() ?? 0, type, input, -1, -1));
        }
    }

    /// <summary>带上下文（音符序号/音高）的记录入口，调度器用这个。</summary>
    private void Record(InputEvent e)
    {
        InputTraceEntry? recorded = null;
        lock (_gate)
        {
            if (Strict)
            {
                bool changed = e.IsDown ? _held.Add(e.Input) : _held.Remove(e.Input);
                if (!changed)
                    throw new InvalidOperationException(
                        e.IsDown
                            ? $"Trace 后端收到重复按下：{e.Input.Label} 已经是按下状态（note {e.NoteIndex}）。"
                            : $"Trace 后端收到抬起一个没按下的输入：{e.Input.Label}（note {e.NoteIndex}）。");
            }
            else
            {
                if (e.IsDown) _held.Add(e.Input); else _held.Remove(e.Input);
            }

            recorded = new InputTraceEntry(NowMs?.Invoke() ?? e.TimeMs, e.Type, e.Input, e.NoteIndex, e.Pitch);
            _entries.Add(recorded);
        }

        EntryRecorded?.Invoke(recorded);
    }

    public void KeyDown(InputId key) => Record(new InputEvent(0, InputActionType.KeyDown, key, -1, -1));

    public void KeyUp(InputId key) => Record(new InputEvent(0, InputActionType.KeyUp, key, -1, -1));

    public void MouseDown(InputId button) => Record(new InputEvent(0, InputActionType.MouseDown, button, -1, -1));

    public void MouseUp(InputId button) => Record(new InputEvent(0, InputActionType.MouseUp, button, -1, -1));

    /// <summary>派发一个计划事件（保留音符上下文，便于排查是哪个音出的问题）。</summary>
    public void Dispatch(InputEvent e)
    {
        switch (e.Type)
        {
            case InputActionType.KeyDown: Record(e); break;
            case InputActionType.KeyUp: Record(e); break;
            case InputActionType.MouseDown: Record(e); break;
            case InputActionType.MouseUp: Record(e); break;
            default: throw new ArgumentOutOfRangeException(nameof(e));
        }
    }

    public void ReleaseAll()
    {
        InputId[] snapshot;
        lock (_gate)
        {
            snapshot = _held.ToArray();
            _held.Clear();
        }
        // 按固定顺序抬键，保证 trace 可预测：先鼠标，再按名字排的键。
        foreach (var id in snapshot.Where(x => x.IsMouse).OrderBy(x => x.Name, StringComparer.Ordinal))
            _entries.Add(new InputTraceEntry(NowMs?.Invoke() ?? 0, InputActionType.MouseUp, id, -1, -1));
        foreach (var id in snapshot.Where(x => !x.IsMouse).OrderBy(x => x.Name, StringComparer.Ordinal))
            _entries.Add(new InputTraceEntry(NowMs?.Invoke() ?? 0, InputActionType.KeyUp, id, -1, -1));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _held.Clear();
        }
    }

    public void Dispose() => ReleaseAll();
}
