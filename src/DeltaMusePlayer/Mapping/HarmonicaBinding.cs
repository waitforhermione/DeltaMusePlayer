namespace DeltaMusePlayer.Mapping;

using DeltaMusePlayer.Core;
using DeltaMusePlayer.Profiles;

/// <summary>
/// 一个 MIDI 音高在口琴上的具体弹法：
/// 哪个 lane（自然音键）+ 是否需要半音修饰 + 用哪个八度档位。
///
/// 不变式：<see cref="SoundPitch"/> 必须等于原始 MIDI 音高。
/// 本项目**不允许**为了凑键位改音高；弹不出来就是弹不出来。
/// </summary>
public sealed record HarmonicaBinding(
    int Pitch,
    int Lane,
    string Key,
    bool Semitone,
    int OctaveOffset)
{
    /// <summary>这个弹法理论发出的音高。</summary>
    public int SoundPitch(int basePitch, IReadOnlyList<int> naturalIntervals)
        => basePitch + 12 * OctaveOffset + naturalIntervals[Lane] + (Semitone ? 1 : 0);

    /// <summary>需要按住的修饰输入（八度 + 半音），按固定顺序：先八度后半音。</summary>
    public IEnumerable<InputId> Modifiers(InstrumentProfile profile)
    {

        if (OctaveOffset < 0 && InputId.TryParse(profile.Modifiers.OctaveDown, out var down))
            yield return down;
        else if (OctaveOffset > 0 && InputId.TryParse(profile.Modifiers.OctaveUp, out var up))
            yield return up;

        if (Semitone && InputId.TryParse(profile.Modifiers.Semitone, out var semi))
            yield return semi;
    }

    /// <summary>给人看的弹法，例如 `V + RightMouse` 或 `Z`。</summary>
    public string Describe(InstrumentProfile profile)
    {
        var mods = Modifiers(profile).Select(m => m.Label).ToList();
        return mods.Count == 0 ? Key : $"{string.Join(" + ", mods)} + {Key}";
    }

    public override string ToString() => $"{Music.NoteName(Pitch)} → lane {Lane} [{Key}] semi={Semitone} oct={OctaveOffset:+#;-#;0}";
}

/// <summary>一次映射的结果（整条旋律）。</summary>
public sealed class MappingResult
{
    public List<MappedNote> Notes { get; init; } = new();

    /// <summary>可演奏的音数。</summary>
    public int PlayableCount => Notes.Count(n => n.IsPlayable);

    /// <summary>弹不出来的音数。</summary>
    public int UnplayableCount => Notes.Count(n => !n.IsPlayable);

    /// <summary>可演奏比例 0..1。</summary>
    public double PlayableRatio => Notes.Count == 0 ? 1.0 : (double)PlayableCount / Notes.Count;

    public string PlayablePercentLabel => $"{PlayableRatio * 100:F1}%";

    public IEnumerable<MappedNote> Unplayable => Notes.Where(n => !n.IsPlayable);
}

/// <summary>映射后的一个音：原始时间 + 弹法（弹不出来时 Binding 为 null）。</summary>
public sealed record MappedNote(
    PlaybackNote Source,
    HarmonicaBinding? Binding,
    string SkipReason)
{
    public int Pitch => Source.Pitch;

    public double StartSeconds => Source.StartSeconds;

    public double DurationSeconds => Source.DurationSeconds;

    public double EndSeconds => Source.EndSeconds;

    public bool IsPlayable => Binding is not null;

    public override string ToString()
        => IsPlayable
            ? $"{Music.TimeLabel(StartSeconds * 1000)} {Music.NoteName(Pitch)} → {Binding}"
            : $"{Music.TimeLabel(StartSeconds * 1000)} {Music.NoteName(Pitch)} → 不可演奏（{SkipReason}）";
}

/// <summary>映射失败的原因，集中在一处，避免各处的字符串各说各话。</summary>
public static class SkipReasons
{
    public const string OutOfMidiRange = "音高超出 MIDI 0..127";
    public const string OutOfHarmonicaRange = "超出当前键位方案的可演奏音域";
    public const string MissingModifierBinding = "键位方案里对应的修饰键没有绑定";
}
