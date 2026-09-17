namespace DeltaMusePlayer.Mapping;

using DeltaMusePlayer.Core;
using DeltaMusePlayer.Profiles;

/// <summary>
/// MIDI 音高 → <see cref="HarmonicaBinding"/>。
///
/// 键位规则**全部**从 <see cref="InstrumentProfile"/> 派生，这里没有任何硬编码的 Z/X/C/V。
///
/// 一个音高可能有多种合法弹法（例如 72 既能用 lane 7 = 高音 do，也能用 +1 八度 + lane 0）。
/// canonical 选择顺序（与需求第 5 节一致）：
///   1. |octave_offset| 最小   —— 优先不动八度，少按一个修饰键；
///   2. 不用 semitone 优先     —— 白键优先，半音键只用来补黑键；
///   3. lane 较小优先          —— 靠左的键更稳；
///   4. lane 相同再比 key 名（字符串序）—— 彻底确定，避免同分不稳定。
///
/// 弹不出来的音**不猜、不改音高**：默认跳过并给 warning，strict 模式直接拒绝播放。
/// </summary>
public sealed class NoteMapper
{
    private readonly InstrumentProfile _profile;
    private readonly int[] _octaves;
    private readonly InputId?[] _octaveMods;
    private readonly InputId? _semitoneMod;
    private readonly Dictionary<int, HarmonicaBinding> _cache = new();

    public NoteMapper(InstrumentProfile profile)
    {
        InstrumentProfileLoader.Validate(profile);
        _profile = profile;
        _octaves = profile.EffectiveOctaveOffsets.ToArray();

        _octaveMods = new InputId?[3];
        _octaveMods[0] = InputId.TryParse(profile.Modifiers.OctaveDown, out var d) ? d : null;
        _octaveMods[1] = null;
        _octaveMods[2] = InputId.TryParse(profile.Modifiers.OctaveUp, out var u) ? u : null;
        _semitoneMod = InputId.TryParse(profile.Modifiers.Semitone, out var s) ? s : null;
    }

    public InstrumentProfile Profile => _profile;

    /// <summary>方案覆盖到的最低音（最负的八度档 + lane 0，不带半音）。</summary>
    public int MinSupportedPitch => _profile.BasePitch + 12 * _octaves[0] + _profile.NaturalIntervals[0];

    /// <summary>方案覆盖到的最高音（最高的八度档 + 最高 lane；半音还能再高 1，见 <see cref="IsPlayable"/>）。</summary>
    public int MaxSupportedPitch
        => _profile.BasePitch + 12 * _octaves[^1] + _profile.NaturalIntervals[^1] + 1;

    /// <summary>这个音高能不能弹。</summary>
    public bool IsPlayable(int pitch) => TryMap(pitch, out _);

    /// <summary>把音高映射成弹法；弹不出来返回 false。</summary>
    public bool TryMap(int pitch, out HarmonicaBinding? binding)
    {
        binding = null;
        if (pitch < 0 || pitch > 127) return false;

        if (_cache.TryGetValue(pitch, out var cached))
        {
            binding = cached;
            return true;
        }

        HarmonicaBinding? best = null;

        foreach (int oct in _octaves)
        {
            int slot = Math.Sign(oct);
            // 需要八度修饰、但方案里没绑这个修饰键 → 这一档根本发不出来
            if (slot != 0 && _octaveMods[slot + 1] is null) continue;

            for (int lane = 0; lane < _profile.Keys.Count; lane++)
            {
                for (int semi = 0; semi <= 1; semi++)
                {
                    if (semi == 1 && _semitoneMod is null) continue;

                    int sound = _profile.BasePitch + 12 * oct + _profile.NaturalIntervals[lane] + semi;
                    if (sound != pitch) continue;

                    var candidate = new HarmonicaBinding(
                        pitch, lane, InputId.NormalizeKeyName(_profile.Keys[lane]), semi == 1, oct);

                    if (best is null || IsBetter(candidate, best)) best = candidate;
                }
            }
        }

        if (best is null) return false;

        _cache[pitch] = best;
        binding = best;
        return true;
    }

    private static bool IsBetter(HarmonicaBinding c, HarmonicaBinding b)
    {
        int ca = Math.Abs(c.OctaveOffset), ba = Math.Abs(b.OctaveOffset);
        if (ca != ba) return ca < ba;

        if (c.Semitone != b.Semitone) return !c.Semitone;

        if (c.Lane != b.Lane) return c.Lane < b.Lane;

        return string.CompareOrdinal(c.Key, b.Key) < 0;
    }

    /// <summary>映射一个音高，附失败原因。</summary>
    public HarmonicaBinding? Map(int pitch, out string skipReason)
    {
        skipReason = "";
        if (pitch < 0 || pitch > 127)
        {
            skipReason = SkipReasons.OutOfMidiRange;
            return null;
        }
        if (TryMap(pitch, out var b)) return b;

        // 区分「音域外」与「方案缺修饰键」：两者对用户的处理方式不同。
        if (pitch < MinSupportedPitch || pitch > MaxSupportedPitch)
        {
            skipReason = $"{SkipReasons.OutOfHarmonicaRange}（{Music.SolfegeRange(MinSupportedPitch, MaxSupportedPitch)}）";
            return null;
        }
        skipReason = SkipReasons.MissingModifierBinding;
        return null;
    }

    /// <summary>映射整条旋律。保持输入顺序。</summary>
    public MappingResult MapAll(IEnumerable<PlaybackNote> notes)
    {
        var result = new MappingResult();
        foreach (var n in notes)
        {
            var b = Map(n.Pitch, out string reason);
            result.Notes.Add(new MappedNote(n, b, reason));
        }
        return result;
    }

    /// <summary>
    /// 整个方案的全部可演奏音高（升序），用于 exhaustive 测试与「Validate for harmonica」统计。
    /// </summary>
    public IReadOnlyList<int> AllPlayablePitches()
    {
        var set = new SortedSet<int>();
        foreach (int oct in _octaves)
        {
            int slot = Math.Sign(oct);
            if (slot != 0 && _octaveMods[slot + 1] is null) continue;
            foreach (int interval in _profile.NaturalIntervals)
            {
                int p = _profile.BasePitch + 12 * oct + interval;
                if (p is >= 0 and <= 127) set.Add(p);
                if (_semitoneMod is not null && p + 1 is >= 0 and <= 127) set.Add(p + 1);
            }
        }
        return set.ToArray();
    }
}
