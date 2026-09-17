using DeltaMusePlayer.Core;
using DeltaMusePlayer.Mapping;
using DeltaMusePlayer.Playback;
using DeltaMusePlayer.Profiles;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 测试脚手架。所有时序测试只允许用 <see cref="FakeClock"/> 推进时间，
/// 一律禁止 Thread.Sleep —— 这样时序断言是确定性的，也不依赖机器负载。
/// </summary>
internal static class TestKit
{
    public static InstrumentProfile Profile => InstrumentProfileLoader.CreateDeltaHarmonica();

    public static NoteMapper Mapper => new(Profile);

    public static NoteMapper MapperOf(InstrumentProfile profile) => new(profile);

    /// <summary>默认时序档，但把间隔压到便于断言的整数毫秒。</summary>
    public static PlaybackConfig Config(Action<PlaybackConfig>? tweak = null)
    {
        var c = new PlaybackConfig
        {
            ModifierLeadMs = 15,
            ModifierReleaseDelayMs = 10,
            ModifierTransitionGapMs = 5,
            ReleaseGapMs = 15,
            SameKeyRetriggerGapMs = 12,
            MinimumKeyHoldMs = 30,
            FinishReleaseDelayMs = 20,
            // 默认关掉剪裁：音符时间就是真值，事件时刻可以直接硬编码断言，
            // 不会因为「回归平移」整体偏掉。要测剪裁的用例自己打开。
            TrimLeadingSilence = false,
        };
        tweak?.Invoke(c);
        c.Validate();
        return c;
    }

    public static PlaybackPlan Plan(IEnumerable<PlaybackNote> notes, double speed = 1.0, Action<PlaybackConfig>? tweak = null)
        => new PlaybackPlanner(Mapper, Config(tweak)).Compile(notes, speed);

    public static PlaybackPlan Plan(int[] pitches, params double[] starts)
    {
        if (starts.Length != pitches.Length)
            throw new ArgumentException("每个音高都要有一个起始秒。");
        var notes = pitches.Select((p, i) => new PlaybackNote(p, starts[i], 0.25, 90)).ToList();
        return Plan(notes);
    }

    public static PlaybackNote Note(int pitch, double start, double duration, int velocity = 90)
        => new(pitch, start, duration, velocity);

    /// <summary>一串音，每个固定时值，按固定间隔排开。</summary>
    public static List<PlaybackNote> Sequence(IEnumerable<int> pitches, double start = 0, double step = 0.5, double duration = 0.4)
    {
        var list = new List<PlaybackNote>();
        double t = start;
        foreach (int p in pitches)
        {
            list.Add(new PlaybackNote(p, t, duration, 90));
            t += step;
        }
        return list;
    }

    /// <summary>把计划渲染成 `输入 动作` 的紧凑序列，便于断言顺序。</summary>
    public static List<string> Actions(PlaybackPlan plan)
        => plan.Events.Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToList();

    public static List<string> ActionsWithTime(PlaybackPlan plan)
        => plan.Events.Select(e => $"{e.TimeMs:F3} {e.Input.Label}:{e.ActionLabel}").ToList();

    public static InputId Key(string name) => InputId.Key(name);

    public static InputId Mouse(string name) => InputId.Mouse(name);

    public static readonly InputId Left = InputId.Mouse("Left");
    public static readonly InputId Right = InputId.Mouse("Right");
    public static readonly InputId Middle = InputId.Mouse("Middle");
}
