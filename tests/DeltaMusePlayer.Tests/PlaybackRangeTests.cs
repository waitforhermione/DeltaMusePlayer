using DeltaMusePlayer.Core;
using DeltaMusePlayer.Playback;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 「只弹某一段」的语义。
///
/// 这里钉住几条容易悄悄出错的约定：
///   * 起点**含**、终点**不含** —— 把曲子切成 [0,X) 与 [X,尾] 两段必须不漏音也不重音。
///   * 跨越终点的长音**截断在终点** —— 口琴是单音乐器，正在吹的音必须在片段边界松开，
///     否则残留按键会漏到片段之外。
///   * 片段内音符的时间**保持源文件时间轴不变**（不平移到 0），界面读数才对得上谱子。
/// </summary>
public sealed class PlaybackRangeTests
{
    /// <summary>计划里出现过的不同音高，按大小排序（用来看「选了哪几个音」）。</summary>
    private static int[] Pitches(PlaybackPlan plan)
        => plan.Events.Where(e => e.Pitch >= 0).Select(e => e.Pitch).Distinct().OrderBy(p => p).ToArray();

    /// <summary>某个音高的全部事件时刻（音乐毫秒→计划墙钟毫秒，1x 速度下相同）。</summary>
    private static double[] TimesOf(PlaybackPlan plan, int pitch)
        => plan.Events.Where(e => e.Pitch == pitch).Select(e => e.TimeMs).OrderBy(t => t).ToArray();

    // 默认 Sequence：从 0 开始、每 500ms 一个音、每个音 400ms 长。
    //   C4(60) 0–400   D4(62) 500–900   E4(64) 1000–1400
    //   F4(65) 1500–1900  G4(67) 2000–2400
    private static readonly int[] FiveNotes = { 60, 62, 64, 65, 67 };

    [Fact]
    public void WholeSongKeepsEveryNote()
    {
        var plan = TestKit.Plan(TestKit.Sequence(FiveNotes));
        Assert.Equal(5, Pitches(plan).Length);
        Assert.Equal(new[] { 60, 62, 64, 65, 67 }, Pitches(plan));
    }

    [Fact]
    public void RangeKeepsOnlyNotesStartingInsideIt()
    {
        // 只留 550–2050：D4(500 起) 落在起点之前 → 丢掉；E4/F4 在内；G4(2000 起) 在内。
        var plan = TestKit.Plan(TestKit.Sequence(FiveNotes), tweak: c =>
        {
            c.RangeStartMs = 550;
            c.RangeEndMs = 2050;
        });

        Assert.Equal(new[] { 64, 65, 67 }, Pitches(plan));
        Assert.DoesNotContain(60, Pitches(plan));
        Assert.DoesNotContain(62, Pitches(plan));
    }

    [Fact]
    public void NoteStartingExactlyAtRangeStartIsIncluded()
    {
        // 起点含：D4 正好 500ms 起音，必须留下。
        var plan = TestKit.Plan(TestKit.Sequence(FiveNotes), tweak: c =>
        {
            c.RangeStartMs = 500;
            c.RangeEndMs = 1000;
        });

        Assert.Contains(62, Pitches(plan));
    }

    [Fact]
    public void NoteStartingExactlyAtRangeEndIsExcluded()
    {
        // 终点不含：E4 正好 1000ms 起音，属于下一段，必须排除。
        var plan = TestKit.Plan(TestKit.Sequence(FiveNotes), tweak: c =>
        {
            c.RangeStartMs = 500;
            c.RangeEndMs = 1000;
        });

        Assert.Contains(62, Pitches(plan));       // 500 起音：在内
        Assert.DoesNotContain(64, Pitches(plan)); // 1000 起音：在外
    }

    [Fact]
    public void NoteCrossingRangeEndIsTruncatedAtTheEnd()
    {
        // G4 是 2000–2400；终点 2100 → 抬起必须落在 2100，不能拖到 2400。
        var plan = TestKit.Plan(TestKit.Sequence(FiveNotes), tweak: c =>
        {
            c.RangeStartMs = 2000;
            c.RangeEndMs = 2100;
        });

        var g4 = TimesOf(plan, 67);
        Assert.Equal(2, g4.Length);                       // 一按一放
        Assert.Equal(2000, g4[0], 3);
        Assert.Equal(2100, g4[1], 3);                     // 截断在终点

        // 而且不能有事件越过终点。
        Assert.All(plan.Events, e => Assert.True(e.TimeMs <= 2100 + 0.001,
            $"事件 {e.Input.Label} 落在终点之后：{e.TimeMs:F3}ms"));
    }

    [Fact]
    public void SplittingSongIntoTwoRangesLosesAndDuplicatesNothing()
    {
        // 切点落在 D4 与 E4 之间（750ms）。
        int[] Cut(double? from, double? to)
            => Pitches(TestKit.Plan(TestKit.Sequence(FiveNotes), tweak: c =>
            {
                c.RangeStartMs = from;
                c.RangeEndMs = to;
            }));

        var left = Cut(null, 750);      // [曲首, 750)
        var right = Cut(750, null);     // [750, 曲尾]

        var all = left.Concat(right).OrderBy(p => p).ToArray();
        var expected = Pitches(TestKit.Plan(TestKit.Sequence(FiveNotes)));

        // 合起来必须正好等于整曲：不漏（left+right 覆盖全部）也不重（不出现两次）。
        Assert.Equal(expected, all.Distinct().OrderBy(p => p).ToArray());
        Assert.Equal(expected.Length, all.Length);
    }

    [Fact]
    public void RangeKeepsAbsoluteMusicTimeline()
    {
        // 片段不把时间平移到 0（TrimLeadingSilence 关掉时）：后面那颗音的绝对时刻不变。
        var plan = TestKit.Plan(new[] { TestKit.Note(67, 2.0, 0.4) }, tweak: c =>
        {
            c.RangeStartMs = 1000;
            c.RangeEndMs = 3000;
        });

        Assert.Equal(2000, TimesOf(plan, 67)[0], 3);
    }

    [Fact]
    public void EmptyRangeIsRejectedWithAReadableMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestKit.Plan(TestKit.Sequence(FiveNotes), tweak: c =>
            {
                c.RangeStartMs = 3000;
                c.RangeEndMs = 4000;   // 这一段里一个音都没有
            }));

        Assert.Contains("没有任何音符", ex.Message);
    }

    [Fact]
    public void InvertedRangeIsRejectedByConfigValidation()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            TestKit.Plan(TestKit.Sequence(FiveNotes), tweak: c =>
            {
                c.RangeStartMs = 2000;
                c.RangeEndMs = 1000;
            }));

        Assert.Contains("必须晚于起点", ex.Message);
    }

    [Fact]
    public void DescribeRangeMatchesWhatCompileActuallyDoes()
    {
        var notes = TestKit.Sequence(FiveNotes);

        var (count, first, last) = PlaybackPlanner.DescribeRange(notes, 550, 2050);
        var plan = TestKit.Plan(notes, tweak: c =>
        {
            c.RangeStartMs = 550;
            c.RangeEndMs = 2050;
        });

        // 预览的音符数必须与实际编译出来的一致，否则界面是在骗人。
        // 550–2050 之内起音的是 E4(1000) / F4(1500) / G4(2000)，共 3 个。
        Assert.Equal(Pitches(plan).Length, count);
        Assert.Equal(3, count);
        Assert.Equal(1000, first, 3);   // E4 起
        // G4 从 2000 开始、自然结束在 2400（越过了终点 2050）。
        // 这里报的是**音乐跨度**，所以是 2400；播放时它会被截断在 2050。
        Assert.Equal(2400, last, 3);
    }

    [Fact]
    public void DescribeRangeReportsNaturalEndWhileCompileTruncatesIt()
    {
        // 同一个片段，两个数字含义不同，必须是「预览报跨度、编译做截断」：
        // 预览若报 2050，使用者会以为这段音乐只到 2050；实际那是截断点，不是音乐结束点。
        var notes = TestKit.Sequence(FiveNotes);

        var (_, _, naturalEnd) = PlaybackPlanner.DescribeRange(notes, 550, 2050);
        var plan = TestKit.Plan(notes, tweak: c =>
        {
            c.RangeStartMs = 550;
            c.RangeEndMs = 2050;
        });

        Assert.Equal(2400, naturalEnd, 3);

        var g4 = TimesOf(plan, 67);
        Assert.Equal(2, g4.Length);
        Assert.Equal(2050, g4[1], 3);                    // 实际抬起被截断在终点
        Assert.NotEqual(naturalEnd, g4[1]);              // 两个数字确实不同，不是巧合相等
    }

    [Fact]
    public void DescribeRangeOnWholeSongCountsEverything()
    {
        var notes = TestKit.Sequence(FiveNotes);
        var (count, first, last) = PlaybackPlanner.DescribeRange(notes, null, null);

        Assert.Equal(5, count);
        Assert.Equal(0, first, 3);
        Assert.Equal(2400, last, 3);
    }
}

/// <summary>
/// 手输时间的解析。GUI 的「演奏片段」和 CLI 的 <c>--range</c> 共用 <see cref="TimeInput"/>。
/// </summary>
public sealed class TimeInputTests
{
    [Theory]
    [InlineData("01:23.500", 83500)]
    [InlineData("1:23", 83000)]
    [InlineData("0:30", 30000)]
    [InlineData("83", 83000)]
    [InlineData("83.5", 83500)]
    [InlineData("0", 0)]
    [InlineData("  1:00  ", 60000)]
    public void ParsesAcceptedForms(string raw, double expectedMs)
    {
        Assert.True(TimeInput.TryParse(raw, out double? ms), $"应当能解析 \"{raw}\"");
        Assert.Equal(expectedMs, ms!.Value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyMeansUnset(string raw)
    {
        Assert.True(TimeInput.TryParse(raw, out double? ms));
        Assert.Null(ms);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1:2:3")]
    [InlineData("-5")]
    [InlineData("1:-2")]
    public void RejectsGarbage(string raw)
        => Assert.False(TimeInput.TryParse(raw, out _), $"不该接受 \"{raw}\"");

    [Fact]
    public void ParsesRangeWithBothEnds()
    {
        Assert.True(TimeInput.TryParseRange("0:30-1:15", out double? s, out double? e));
        Assert.Equal(30000, s!.Value, 3);
        Assert.Equal(75000, e!.Value, 3);
    }

    [Fact]
    public void ParsesRangeWithOpenEnd()
    {
        Assert.True(TimeInput.TryParseRange("1:30-", out double? s, out double? e));
        Assert.Equal(90000, s!.Value, 3);
        Assert.Null(e);
    }

    [Fact]
    public void ParsesRangeWithSingleValueAsStartOnly()
    {
        Assert.True(TimeInput.TryParseRange("45", out double? s, out double? e));
        Assert.Equal(45000, s!.Value, 3);
        Assert.Null(e);
    }

    [Fact]
    public void ParsesSecondsRange()
    {
        Assert.True(TimeInput.TryParseRange("30-75", out double? s, out double? e));
        Assert.Equal(30000, s!.Value, 3);
        Assert.Equal(75000, e!.Value, 3);
    }

    [Fact]
    public void EmptyRangeMeansWholeSong()
    {
        Assert.True(TimeInput.TryParseRange("", out double? s, out double? e));
        Assert.Null(s);
        Assert.Null(e);
    }

    [Fact]
    public void RejectsGarbageRange()
        => Assert.False(TimeInput.TryParseRange("abc-def", out _, out _));
}
