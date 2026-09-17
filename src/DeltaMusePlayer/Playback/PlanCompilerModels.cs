namespace DeltaMusePlayer.Playback;

using DeltaMusePlayer.Core;
using DeltaMusePlayer.Mapping;
using DeltaMusePlayer.Profiles;

/// <summary>
/// 一颗音的最终落点（计划编译的中间产物）。时间统一是**音乐毫秒**。
/// </summary>
internal sealed class NoteSlot
{
    public required MappedNote Note { get; init; }

    /// <summary>这个音在「可演奏音」序列里的序号，事件与 resume 都用它做索引。</summary>
    public required int SourceIndex { get; init; }

    public required HarmonicaBinding Binding { get; init; }
    public required IReadOnlyList<InputId> Modifiers { get; init; }

    public double Start;
    public double End;

    /// <summary>前音为了给它让位而被推后的次数（诊断用）。</summary>
    public int PushedByOverlap;

    /// <summary>它为了避开同键重触发而被推后的次数（诊断用）。</summary>
    public int PushedByRetrigger;

    /// <summary>因为前音边界而顺延的次数（诊断用）。</summary>
    public int PushedByGap;


    /// <summary>时值被最短按住拉长过（诊断用）。</summary>
    public bool MinHoldApplied;

    public InputId KeyId => InputId.Key(Binding.Key);

    public int OctaveSign => Math.Sign(Binding.OctaveOffset);
}

/// <summary>编译结果 + 统计。</summary>
public sealed class PlanCompileReport
{
    public int NoteCount { get; init; }
    public int PlayableCount { get; init; }
    public int UnplayableCount { get; init; }
    public int PushedByOverlap { get; init; }
    public int PushedByRetrigger { get; init; }
    public int PushedByGap { get; init; }
    public int MinHoldExtendedCount { get; init; }
    public int OppositeOctaveTransitions { get; init; }
    public double MaxPushMs { get; init; }

    public string Summary()
        => $"音符 {NoteCount}（可演奏 {PlayableCount} / 不可演奏 {UnplayableCount}）；" +
           $"重叠顺延 {PushedByOverlap}，同键重触发顺延 {PushedByRetrigger}，音间间隔顺延 {PushedByGap}，" +
           $"最短按住补长 {MinHoldExtendedCount}，八度反向切换 {OppositeOctaveTransitions}，" +
           $"最大顺延 {MaxPushMs:F1}ms";
}
