namespace DeltaMusePlayer.Core;

/// <summary>
/// MIDI 解析后的一个音符：时间轴已经统一成**秒**。
/// 解析层之后的模块（映射、计划、调度）只认 seconds timeline，不再接触 tick / tempo。
/// </summary>
public sealed record PlaybackNote(
    int Pitch,
    double StartSeconds,
    double DurationSeconds,
    int Velocity)
{
    public double EndSeconds => StartSeconds + DurationSeconds;

    /// <summary>这个音在源文件里的（轨道，声道）归属，仅用于界面与日志。</summary>
    public int SourceTrack { get; init; }

    public int SourceChannel { get; init; }

    public override string ToString()
        => $"{Music.NoteName(Pitch)} {StartSeconds:F3}s+{DurationSeconds:F3}s v{Velocity}";
}

/// <summary>一条可被选作主旋律的声轨（= MIDI 里的一个 track chunk）。</summary>
public sealed class MidiTrackInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Instrument { get; init; } = "";
    public bool IsDrum { get; init; }
    public int NoteCount { get; init; }
    public int MinPitch { get; init; }
    public int MaxPitch { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>该轨上的音符，按时间排序。</summary>
    public IReadOnlyList<PlaybackNote> Notes { get; init; } = Array.Empty<PlaybackNote>();

    public string PitchRangeLabel => NoteCount == 0 ? "-" : Music.SolfegeRange(MinPitch, MaxPitch);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Track {Index + 1}" : Name;

    public string Describe()
        => $"[{Index}] {DisplayName} | {Instrument} | {NoteCount} notes | {PitchRangeLabel} | {DurationSeconds:F2}s";
}

/// <summary>一份解析完成的 MIDI：声轨列表 + 全曲时长。</summary>
public sealed class MidiSong
{
    public string FilePath { get; init; } = "";

    /// <summary>MIDI format 0 / 1 / 2，或 -1（未知）。</summary>
    public int Format { get; init; } = -1;

    public int TicksPerQuarterNote { get; init; }

    public double DurationSeconds { get; init; }

    public IReadOnlyList<MidiTrackInfo> Tracks { get; init; } = Array.Empty<MidiTrackInfo>();

    /// <summary>解析期的提示（例如「某轨复音，已按规则处理」）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 自动挑主旋律轨：音符最多且非鼓的轨；全都一样多时取轨道号最小的。
    /// **不做**任何旋律提取 —— 这里只是替用户点一下列表。
    /// </summary>
    public MidiTrackInfo? SuggestMelodyTrack()
    {
        MidiTrackInfo? best = null;
        foreach (var t in Tracks)
        {
            if (t.NoteCount == 0) continue;
            if (t.IsDrum) continue;
            if (best is null) { best = t; continue; }
            if (t.NoteCount > best.NoteCount) best = t;
        }
        return best ?? Tracks.FirstOrDefault(t => t.NoteCount > 0);
    }
}
