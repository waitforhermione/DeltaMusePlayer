using DeltaMusePlayer.Core;
using DeltaMusePlayer.Profiles;
using Xunit;

namespace DeltaMusePlayer.Tests;

public sealed class InstrumentProfileTests
{
    [Fact]
    public void BuiltInDeltaHarmonicaMatchesJsonShape()
    {
        var p = InstrumentProfileLoader.CreateDeltaHarmonica();

        Assert.Equal("delta_harmonica", p.Name);
        Assert.Equal(60, p.BasePitch);
        Assert.Equal(new[] { "Z", "X", "C", "V", "B", "N", "M", "," }, p.Keys);
        Assert.Equal(new[] { 0, 2, 4, 5, 7, 9, 11, 12 }, p.NaturalIntervals);
        Assert.Equal(new[] { -1, 0, 1 }, p.EffectiveOctaveOffsets);
        Assert.Equal("mouse_left", p.Modifiers.OctaveDown);
        Assert.Equal("mouse_middle", p.Modifiers.Semitone);
        Assert.Equal("mouse_right", p.Modifiers.OctaveUp);

        InstrumentProfileLoader.Validate(p);   // 不应抛异常
    }

    [Fact]
    public void ShippedProfileFileLoadsAndEqualsBuiltInDefaults()
    {
        // 仓库里的 profiles/delta_harmonica.json 必须与内置默认值一致，
        // 否则「磁盘上的方案」和「EXE 里的兜底方案」会给出不同的演奏结果。
        string? path = InstrumentProfileLoader.FindDefaultProfilePath();
        if (path is null) return;   // 测试进程的工作目录里找不到就跳过（CI 之外不失败）

        var fromFile = InstrumentProfileLoader.LoadFile(path);
        var builtIn = InstrumentProfileLoader.CreateDeltaHarmonica();

        Assert.Equal(builtIn.Name, fromFile.Name);
        Assert.Equal(builtIn.BasePitch, fromFile.BasePitch);
        Assert.Equal(builtIn.Keys, fromFile.Keys);
        Assert.Equal(builtIn.NaturalIntervals, fromFile.NaturalIntervals);
        Assert.Equal(builtIn.EffectiveOctaveOffsets, fromFile.EffectiveOctaveOffsets);
        Assert.Equal(builtIn.Modifiers.OctaveDown, fromFile.Modifiers.OctaveDown);
        Assert.Equal(builtIn.Modifiers.Semitone, fromFile.Modifiers.Semitone);
        Assert.Equal(builtIn.Modifiers.OctaveUp, fromFile.Modifiers.OctaveUp);
    }

    [Fact]
    public void JsonRoundTripKeepsEverything()
    {
        var p = InstrumentProfileLoader.CreateDeltaHarmonica();
        string json = InstrumentProfileLoader.ToJson(p);
        var back = InstrumentProfileLoader.FromJson(json);

        Assert.Equal(p.Keys, back.Keys);
        Assert.Equal(p.NaturalIntervals, back.NaturalIntervals);
        Assert.Equal(p.EffectiveOctaveOffsets, back.EffectiveOctaveOffsets);
        Assert.Equal(p.BasePitch, back.BasePitch);
    }

    [Theory]
    [InlineData("{}", "name")]
    [InlineData("{\"name\":\"x\"}", "keys")]
    [InlineData("{\"name\":\"x\",\"keys\":[\"Z\"],\"natural_intervals\":[0,1]}", "数量")]
    [InlineData("{\"name\":\"x\",\"keys\":[\"Z\",\"Z\"],\"natural_intervals\":[0,1]}", "重复键名")]
    [InlineData("{\"name\":\"x\",\"keys\":[\"Z\",\"X\"],\"natural_intervals\":[2,4]}", "natural_intervals[0]")]
    [InlineData("{\"name\":\"x\",\"keys\":[\"Z\",\"X\"],\"natural_intervals\":[0,0]}", "严格递增")]
    public void InvalidProfilesAreRejectedAtLoadTime(string json, string expectedFragment)
    {
        var ex = Assert.Throws<InvalidDataException>(() => InstrumentProfileLoader.FromJson(json));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void OppositeOctaveModifiersCannotShareOneInput()
    {
        var p = InstrumentProfileLoader.CreateDeltaHarmonica();
        p.Modifiers.OctaveUp = "mouse_left";   // 与 octave_down 撞车

        var ex = Assert.Throws<InvalidDataException>(() => InstrumentProfileLoader.Validate(p));
        Assert.Contains("冲突", ex.Message);
    }

    [Fact]
    public void OctaveOffsetsMustIncludeBaseOctave()
    {
        var p = InstrumentProfileLoader.CreateDeltaHarmonica();
        p.OctaveOffsets = new List<int> { -1, 1 };

        var ex = Assert.Throws<InvalidDataException>(() => InstrumentProfileLoader.Validate(p));
        Assert.Contains("必须包含 0", ex.Message);
    }

    [Fact]
    public void ModifierCannotReuseANaturalKey()
    {
        var p = InstrumentProfileLoader.CreateDeltaHarmonica();
        p.Modifiers.Semitone = "Z";

        var ex = Assert.Throws<InvalidDataException>(() => InstrumentProfileLoader.Validate(p));
        Assert.Contains("重名", ex.Message);
    }

    [Fact]
    public void InputIdParsesTheNamesUsedInProfiles()
    {
        Assert.True(InputId.TryParse("mouse_left", out var left));
        Assert.Equal(InputKind.MouseButton, left.Kind);
        Assert.Equal("Left", left.Name);

        Assert.True(InputId.TryParse("MouseRight", out var right));
        Assert.Equal("Right", right.Name);

        Assert.True(InputId.TryParse("z", out var z));
        Assert.Equal(InputKind.Key, z.Kind);
        Assert.Equal("Z", z.Name);

        Assert.False(InputId.TryParse("", out _));
        Assert.False(InputId.TryParse("   ", out _));
    }
}
