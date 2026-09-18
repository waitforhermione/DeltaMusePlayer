namespace DeltaMusePlayer.Playback;

using DeltaMusePlayer.Core;

/// <summary>
/// 时序与按键策略配置。**所有**毫秒数值都在这里，不许散落到调度或计划代码里。
///
/// 口径（重要）：
///   本类里的毫秒**绝大部分**是**播放墙钟毫秒**，即真实物理时间。
///   计划内部的音符时间是**音乐毫秒**（MidiParser 给出的秒 × 1000）。
///   编译计划时按 speed 把物理毫秒折算成音乐毫秒，所以边沿间隔不会被速度压缩。
///
///   唯一的例外是 <see cref="RangeStartMs"/> / <see cref="RangeEndMs"/>：
///   它们是**音乐时间**（源文件的秒数），描述「弹哪一段」，与速度无关。
/// </summary>
public sealed class PlaybackConfig
{
    // ---------------------------------------------------------------- 修饰键时序

    /// <summary>修饰键（鼠标键）比音键早按下的提前量。太短会被目标程序一帧折进去而漏音。</summary>
    public double ModifierLeadMs { get; set; } = 15.0;

    /// <summary>音键抬起之后，修饰键再多按一会儿再抬。</summary>
    public double ModifierReleaseDelayMs { get; set; } = 10.0;

    /// <summary>
    /// 由一个修饰键切到另一个修饰键（例如降八度 → 升八度，或松开半音键）时，
    /// 释放与按下之间保留的最小间隔。修饰键之间必须串行，不能同刻切换。
    /// </summary>
    public double ModifierTransitionGapMs { get; set; } = 5.0;

    // ---------------------------------------------------------------- 音键时序

    /// <summary>相邻两个音之间，前音抬起、后音按下必须留出的间隔。</summary>
    public double ReleaseGapMs { get; set; } = 15.0;

    /// <summary>同一个键连续两次按下之间的最小间隔（必须跨过「抬起」那一帧）。</summary>
    public double SameKeyRetriggerGapMs { get; set; } = 12.0;

    /// <summary>音键最短按住时长。过短的 MIDI 音会被拉到这个长度，绝不产生零时长按键。</summary>
    public double MinimumKeyHoldMs { get; set; } = 30.0;

    // ---------------------------------------------------------------- 其他

    /// <summary>末音结束之后再补一段修饰键释放的余量，收尾事件不会挤在最后一个音上。</summary>
    public double FinishReleaseDelayMs { get; set; } = 20.0;

    /// <summary>重叠音符的处理策略。</summary>
    public OverlapPolicy Overlap { get; set; } = OverlapPolicy.Serialize;

    /// <summary>严格模式：遇到不可演奏的音高直接拒绝播放，而不是跳过。</summary>
    public bool Strict { get; set; }

    /// <summary>是否把前导静音剪掉，让旋律从 0 秒开始。</summary>
    public bool TrimLeadingSilence { get; set; } = true;

    // ---------------------------------------------------------------- 片段范围
    //
    // 这两个是**音乐时间**（源文件的秒数），不是物理毫秒 —— 它们描述「弹曲子的哪一段」，
    // 与速度无关，也和上面那批物理预算不是一类东西，所以单独放一段。
    //
    // 两个都是 null 表示整曲。只设 Start 表示「从这儿到结尾」，只设 End 表示「从头到这儿」。

    /// <summary>片段起点（音乐毫秒，含）。null = 从曲首开始。</summary>
    public double? RangeStartMs { get; set; }

    /// <summary>片段终点（音乐毫秒，含）。null = 到曲尾结束。跨越终点的长音会被截断在终点。</summary>
    public double? RangeEndMs { get; set; }

    /// <summary>是否设置了片段范围。</summary>
    public bool HasRange => RangeStartMs is not null || RangeEndMs is not null;

    /// <summary>真实输入播放前的倒计时秒数（0 = 不倒计时）。</summary>
    public double CountdownSeconds { get; set; } = 3.0;

    // ---------------------------------------------------------------- 预设

    /// <summary>默认档：60fps 目标程序的保守值。</summary>
    public static PlaybackConfig Default => new();

    /// <summary>稳健档：目标程序 30fps、掉帧或机器负载高时用。</summary>
    public static PlaybackConfig Safe => new()
    {
        ModifierLeadMs = 40,
        ModifierReleaseDelayMs = 15,
        ModifierTransitionGapMs = 15,
        ReleaseGapMs = 30,
        SameKeyRetriggerGapMs = 45,
        MinimumKeyHoldMs = 45,
        FinishReleaseDelayMs = 30,
    };

    /// <summary>紧凑档：帧率很高、曲谱很密时用，余量最小。</summary>
    public static PlaybackConfig Aggressive => new()
    {
        ModifierLeadMs = 8,
        ModifierReleaseDelayMs = 5,
        ModifierTransitionGapMs = 3,
        ReleaseGapMs = 8,
        SameKeyRetriggerGapMs = 8,
        MinimumKeyHoldMs = 20,
        FinishReleaseDelayMs = 10,
    };

    public PlaybackConfig Clone() => (PlaybackConfig)MemberwiseClone();

    /// <summary>把物理毫秒折算成音乐毫秒（计划内部统一用音乐时间）。</summary>
    public double ToMusicMs(double physicalMs, double speed)
        => physicalMs * (speed <= 0 ? 1.0 : speed);

    /// <summary>配置自检：不合理的数值当场报出来，而不是变成难查的时序怪象。</summary>
    public void Validate()
    {
        if (ModifierLeadMs < 0) throw new ArgumentOutOfRangeException(nameof(ModifierLeadMs));
        if (ModifierReleaseDelayMs < 0) throw new ArgumentOutOfRangeException(nameof(ModifierReleaseDelayMs));
        if (ModifierTransitionGapMs < 0) throw new ArgumentOutOfRangeException(nameof(ModifierTransitionGapMs));
        if (ReleaseGapMs < 0) throw new ArgumentOutOfRangeException(nameof(ReleaseGapMs));
        if (SameKeyRetriggerGapMs < 0) throw new ArgumentOutOfRangeException(nameof(SameKeyRetriggerGapMs));
        if (MinimumKeyHoldMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumKeyHoldMs), "最短按住必须大于 0：不允许零时长按键。");
        if (FinishReleaseDelayMs < 0) throw new ArgumentOutOfRangeException(nameof(FinishReleaseDelayMs));
        if (RangeStartMs is { } rs && rs < 0)
            throw new ArgumentOutOfRangeException(nameof(RangeStartMs), "片段起点不能是负数。");
        if (RangeEndMs is { } re && re < 0)
            throw new ArgumentOutOfRangeException(nameof(RangeEndMs), "片段终点不能是负数。");
        if (RangeStartMs is { } s && RangeEndMs is { } e && e <= s)
            throw new ArgumentOutOfRangeException(nameof(RangeEndMs),
                $"片段终点必须晚于起点（起点 {s:F0}ms，终点 {e:F0}ms）。");
    }

    public string Describe()
        => $"修饰键提前 {ModifierLeadMs:F0}ms，修饰键延后释放 {ModifierReleaseDelayMs:F0}ms，" +
           $"修饰键切换间隔 {ModifierTransitionGapMs:F0}ms，音间间隔 {ReleaseGapMs:F0}ms，" +
           $"同键重触发 {SameKeyRetriggerGapMs:F0}ms，最短按住 {MinimumKeyHoldMs:F0}ms，" +
           $"重叠策略 {Overlap}，严格模式 {(Strict ? "开" : "关")}" +
           (HasRange
               ? $"，片段 {(RangeStartMs is { } s ? Music.TimeLabel(s) : "00:00.000")} ~ " +
                 $"{(RangeEndMs is { } e ? Music.TimeLabel(e) : "曲尾")}"
               : "");
}

/// <summary>重叠音符的处理策略。</summary>
public enum OverlapPolicy
{
    /// <summary>
    /// 默认。后一个音顺延到前一个音安全释放之后（口琴是单音乐器，任何时候只按一个音键）。
    /// 前音不被缩短，因此不会产生零时长按键。
    /// </summary>
    Serialize = 0,

    /// <summary>前音被截短到后音开始之前，后音时间不动。留作配置项，M1 默认不用。</summary>
    Truncate = 1,
}

/// <summary>播放计划里一个音的最终落点与弹法（供 UI 与 resume 使用）。时间单位是播放位置毫秒。</summary>
public sealed record PlannedNote(
    int Index,
    int Pitch,
    double StartMs,
    double EndMs,
    bool Playable,
    string SkipReason,
    int Lane,
    string Key,
    bool Semitone,
    int OctaveOffset,
    IReadOnlyList<InputId> Modifiers);
