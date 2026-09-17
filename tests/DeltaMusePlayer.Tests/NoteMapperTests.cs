using DeltaMusePlayer.Core;
using DeltaMusePlayer.Mapping;
using DeltaMusePlayer.Profiles;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 映射规则的穷举测试。每个断言都拿「弹法理论发出的音高」与目标音高对比，
/// 而不是只对比字符串 —— 键位方案改动时这里会立刻炸出来。
/// </summary>
public sealed class NoteMapperTests
{
    private static readonly NoteMapper Mapper = TestKit.Mapper;

    /// <summary>把弹法换算回实际会发出的音高。这是整套映射的地基。</summary>
    private static int SoundPitch(HarmonicaBinding b)
        => b.SoundPitch(TestKit.Profile.BasePitch, TestKit.Profile.NaturalIntervals);

    [Theory]
    [InlineData(60, "Z", 0, false)]     // C4  基准音
    [InlineData(62, "X", 0, false)]     // D4
    [InlineData(64, "C", 0, false)]     // E4
    [InlineData(65, "V", 0, false)]     // F4
    [InlineData(67, "B", 0, false)]     // G4
    [InlineData(69, "N", 0, false)]     // A4
    [InlineData(71, "M", 0, false)]     // B4
    [InlineData(72, ",", 0, false)]     // C5  第八个键就是高八度 do
    public void NaturalsUseTheEightKeysWithoutModifiers(int pitch, string key, int octaveOffset, bool semitone)
    {
        var m = Mapper.Map(pitch, out string reason);

        Assert.NotNull(m);
        Assert.Equal("", reason);
        Assert.Equal(key, m!.Key);
        Assert.Equal(octaveOffset, m.OctaveOffset);
        Assert.Equal(semitone, m.Semitone);
        Assert.Equal(pitch, SoundPitch(m));
    }

    [Theory]
    [InlineData(61, "Z", 0)]    // C#4 → Z + 半音（白键优先，不用 X 降半音）
    [InlineData(63, "X", 0)]    // D#4
    [InlineData(66, "V", 0)]    // F#4
    [InlineData(68, "B", 0)]    // G#4
    [InlineData(70, "N", 0)]    // A#4
    [InlineData(73, ",")]       // C#5 → 用基准八度的第八个键 + 半音（|offset| 更小）
    public void BlackKeysPreferTheKeyBelowPlusSemitone(int pitch, string key, int octaveOffset = 0)
    {
        var m = Mapper.Map(pitch, out _);

        Assert.NotNull(m);
        Assert.True(m!.Semitone, $"{Music.NoteName(pitch)} 应该用半音修饰");
        Assert.Equal(key, m.Key);
        Assert.Equal(octaveOffset, m.OctaveOffset);
        Assert.Equal(pitch, SoundPitch(m));
    }

    [Theory]
    [InlineData(48, "Z", -1, false)]   // C3  = 基准音降八度
    [InlineData(49, "Z", -1, true)]    // C#3
    [InlineData(36, "Z", -2, false)]   // C2  两层降八度之外 → 方案只有 -1..+1，弹不出来
    public void OctaveOffsetIsAppliedWithinTheConfiguredRange(int pitch, string key, int octaveOffset, bool semitone)
    {
        var m = Mapper.Map(pitch, out string reason);

        if (octaveOffset is < -1 or > 1)
        {
            Assert.Null(m);
            Assert.Contains("音域", reason);
            return;
        }

        Assert.NotNull(m);
        Assert.Equal(key, m!.Key);
        Assert.Equal(octaveOffset, m.OctaveOffset);
        Assert.Equal(semitone, m.Semitone);
        Assert.Equal(pitch, SoundPitch(m));
    }

    [Fact]
    public void DuplicateRepresentationPicksTheSmallestAbsoluteOctaveOffset()
    {
        // 84 = C6：可以用 +2 八度的 Z，也可以用 +1 八度的 ,（第八键本身就是高八度 do）。
        // 方案只允许 -1..+1，所以唯一合法解是 +1 八度 + lane 7。
        var m = Mapper.Map(84, out _);
        Assert.NotNull(m);
        Assert.Equal(",", m!.Key);
        Assert.Equal(1, m.OctaveOffset);
        Assert.False(m.Semitone);
    }

    [Fact]
    public void TopOfTheBaseOctaveOwnsTheEighthKey()
    {
        // 72 既能用「基准八度 + lane 7」，也能用「+1 八度 + lane 0」。
        // canonical 规则第一条是 |octave_offset| 最小 → 必须选基准八度。
        var m = Mapper.Map(72, out _);
        Assert.NotNull(m);
        Assert.Equal(",", m!.Key);
        Assert.Equal(0, m.OctaveOffset);
        Assert.Equal(72, SoundPitch(m));
    }

    [Fact]
    public void ExhaustivePlayablePitchTableIsConsistentAndMinimal()
    {
        var profile = TestKit.Profile;
        var all = Mapper.AllPlayablePitches();

        Assert.NotEmpty(all);

        foreach (int pitch in all)
        {
            var m = Mapper.Map(pitch, out string reason);
            Assert.NotNull(m);
            Assert.Equal("", reason);
            Assert.Equal(pitch, SoundPitch(m!));
            Assert.InRange(m!.Lane, 0, profile.Keys.Count - 1);
            Assert.Equal(InputId.NormalizeKeyName(profile.Keys[m.Lane]), m.Key);
            Assert.Contains(m.OctaveOffset, profile.EffectiveOctaveOffsets);

            // canonical 规则 1：不存在 |octave| 更小的另一种弹法
            foreach (int oct in profile.EffectiveOctaveOffsets)
            {
                if (Math.Abs(oct) >= Math.Abs(m.OctaveOffset)) continue;
                for (int lane = 0; lane < profile.Keys.Count; lane++)
                    for (int semi = 0; semi <= 1; semi++)
                    {
                        int sound = profile.BasePitch + 12 * oct + profile.NaturalIntervals[lane] + semi;
                        Assert.True(sound != pitch,
                            $"{Music.NoteName(pitch)} 还存在 |offset| 更小的弹法：oct={oct} lane={lane} semi={semi}");
                    }
            }

            // canonical 规则 2：同八度内不应存在「不用半音」的另一种弹法
            if (m.Semitone)
            {
                for (int lane = 0; lane < profile.Keys.Count; lane++)
                {
                    int sound = profile.BasePitch + 12 * m.OctaveOffset + profile.NaturalIntervals[lane];
                    if (sound == pitch)
                    {
                        // 只有 lane 7 + 半音 与「+1 八度 lane 0」重合这一种情形被规则 1 排除，
                        // 在同一个八度里必须不存在 lane 更小的无半音解。
                        Assert.True(lane > m.Lane,
                            $"{Music.NoteName(pitch)} 在同八度里还能用 lane {lane} 不按半音弹出");
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryMidiPitchOutsideTheRangeIsReportedRatherThanGuessed()
    {
        var playable = Mapper.AllPlayablePitches().ToHashSet();

        for (int pitch = 0; pitch <= 127; pitch++)
        {
            var m = Mapper.Map(pitch, out string reason);
            if (playable.Contains(pitch))
            {
                Assert.NotNull(m);
                Assert.Equal(pitch, SoundPitch(m!));
            }
            else
            {
                Assert.Null(m);
                Assert.False(string.IsNullOrWhiteSpace(reason), "弹不出来的音必须给出原因");
            }
        }
    }

    [Theory]
    [InlineData(35)]   // B1：低于方案最低音
    [InlineData(90)]   // F#6：高于方案最高音
    [InlineData(127)]
    public void OutOfRangeNotesAreSkippedWithAReason(int pitch)
    {
        var m = Mapper.Map(pitch, out string reason);
        Assert.Null(m);
        Assert.Contains("音域", reason);
    }

    [Fact]
    public void MapperCoverageMatchesTheDocumentedHarmonicaRange()
    {
        // 基准 60 + 八度 -1..+1 + 半音 → 48..85 之间的自然音与升半音。
        Assert.Equal(48, Mapper.MinSupportedPitch);
        Assert.Equal(85, Mapper.MaxSupportedPitch);

        var all = Mapper.AllPlayablePitches();
        Assert.Contains(48, all);
        Assert.Contains(85, all);
        Assert.All(all, p => Assert.InRange(p, 48, 85));
    }

    [Fact]
    public void ModifiersListIsOrderedOctaveThenSemitone()
    {
        var sharp = Mapper.Map(61, out _)!;                    // Z + 半音
        Assert.Equal(new[] { TestKit.Middle }, sharp.Modifiers(TestKit.Profile));

        var up = Mapper.Map(84, out _)!;                       // , + 升八度
        Assert.Equal(new[] { TestKit.Right }, up.Modifiers(TestKit.Profile));

        var lowSharp = Mapper.Map(49, out _)!;                 // Z + 降八度 + 半音
        Assert.Equal(new[] { TestKit.Left, TestKit.Middle }, lowSharp.Modifiers(TestKit.Profile));
    }

    [Fact]
    public void DescribeShowsTheHumanReadableBinding()
    {
        Assert.Equal("Z", Mapper.Map(60, out _)!.Describe(TestKit.Profile));
        Assert.Equal("MOUSE_MIDDLE + Z", Mapper.Map(61, out _)!.Describe(TestKit.Profile));
        Assert.Equal("MOUSE_RIGHT + ,", Mapper.Map(84, out _)!.Describe(TestKit.Profile));
        Assert.Equal("MOUSE_LEFT + Z", Mapper.Map(48, out _)!.Describe(TestKit.Profile));
    }

    [Fact]
    public void ProfileWithoutSemitoneModifierCannotPlayBlackKeys()
    {
        var profile = InstrumentProfileLoader.CreateDeltaHarmonica();
        profile.Modifiers.Semitone = "";      // 没有半音键
        var mapper = new NoteMapper(profile);

        Assert.True(mapper.IsPlayable(60));
        Assert.False(mapper.IsPlayable(61));

        var m = mapper.Map(61, out string reason);
        Assert.Null(m);
        Assert.Contains("修饰键", reason);
    }

    [Fact]
    public void ProfileWithoutOctaveModifiersOnlyPlaysTheBaseOctave()
    {
        var profile = InstrumentProfileLoader.CreateDeltaHarmonica();
        profile.Modifiers.OctaveDown = "";
        profile.Modifiers.OctaveUp = "";
        var mapper = new NoteMapper(profile);

        Assert.True(mapper.IsPlayable(72));
        Assert.False(mapper.IsPlayable(84));
        Assert.False(mapper.IsPlayable(48));
    }
}
