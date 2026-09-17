namespace DeltaMusePlayer.Playback;

using DeltaMusePlayer.Core;
using DeltaMusePlayer.Mapping;
using DeltaMusePlayer.Profiles;

/// <summary>
/// 整首曲子预编译成的事件计划。
///
/// 关键约定：
///  * <see cref="Events"/> 的时间是**播放位置毫秒**（墙钟）：计划一开始就走，边沿间隔
///    （修饰键提前量、同键重触发间隔等）已经是物理毫秒，音符时刻则是谱面时间 ÷ 速度。
///  * 事件已经排好序，调度器只按 index 前进，**不**每帧扫描整份计划。
/// </summary>
public sealed class PlaybackPlan
{
    public required IReadOnlyList<InputEvent> Events { get; init; }

    /// <summary>整份计划（含所有音符，含不可演奏的），供 UI 列表与统计使用。</summary>
    public required IReadOnlyList<PlannedNote> Notes { get; init; }

    /// <summary>编译时使用的速度。</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>
    /// 首个事件之前留出的位置（毫秒，≥ 0）。从 0 秒就开始、又带修饰键提前量的曲子，
    /// 这张表整体右移过 <see cref="LeadInMs"/>，所以谱面时间 0 对应位置 <see cref="LeadInMs"/>。
    /// </summary>
    public double LeadInMs { get; init; }

    public required MappingResult Mapping { get; init; }

    public required PlanCompileReport Report { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int EventCount => Events.Count;

    /// <summary>整首曲子的播放总时长（毫秒）。</summary>
    public double WallTotalMs { get; init; }

    /// <summary>可演奏音高范围（没得弹时返回 null）。</summary>
    public (int Min, int Max)? PlayablePitchRange
    {
        get
        {
            var pitches = Notes.Where(n => n.Playable).Select(n => n.Pitch).ToArray();
            return pitches.Length == 0 ? null : (pitches.Min(), pitches.Max());
        }
    }

    /// <summary>某个播放位置（毫秒）在弹的、可演奏的音。</summary>
    public PlannedNote? NoteAtWallMs(double wallMs)
    {
        // Notes 按开始时间排序，倒着找第一个已开始的即可。
        for (int i = Notes.Count - 1; i >= 0; i--)
        {
            var n = Notes[i];
            if (!n.Playable) continue;
            if (n.StartMs <= wallMs && wallMs < n.EndMs) return n;
            if (n.StartMs <= wallMs) return null;
        }
        return null;
    }

    /// <summary>下一个还没开始的音（UI 用）。</summary>
    public PlannedNote? NextNoteAfterWallMs(double wallMs)
    {
        foreach (var n in Notes)
            if (n.Playable && n.StartMs > wallMs) return n;
        return null;
    }

    public string Summary()
        => $"事件 {EventCount}；{Report.Summary()}；总时长 {Music.TimeLabel(WallTotalMs)}（速度 {Speed:0.##}x）";
}
