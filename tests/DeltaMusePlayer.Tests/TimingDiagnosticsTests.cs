using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Input.Win32;
using DeltaMusePlayer.Playback;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 时序诊断必须报告「抖动」，而不是报告「倒计时」。
///
/// 背景：真实播放的逐条日志曾经长这样 ——
///   派发 #1 计划 00:00.450 实际 00:03.481 抖动 +3031.924ms
///   派发 #2 计划 00:00.569 实际 00:03.601 抖动 +3031.920ms
/// 每一条都顶着 +3031ms，那不是抖动，那是 3 秒倒计时加上预滚造成的一个**恒定整体平移**。
/// 它对听感毫无影响（整段音乐只是晚一点开始），但把「均值 / p95 / 最大」全部污染成 3000ms 量级，
/// 指标彻底失效，用户看到会以为时序坏了。
///
/// 修复：先从每条样本的误差里扣掉整体偏移（误差中位数），剩下的残差才是抖动。
/// 这些测试钉住这个口径。
/// </summary>
public sealed class TimingDiagnosticsTests
{
    [Fact]
    public void BaselineOffsetCapturesTheConstantPreRoll()
    {
        var stats = new DispatchTimingStats();
        // 计划 0/100/200ms，实际整体晚了 3000ms（模拟倒计时），另外各自带一点小抖动。
        // 第一条恰好是零额外延迟的那条，所以纯预滚量就是 3000。
        stats.OnDispatch(new DispatchTiming(0, 3000.0, TestKit.Key("Z"), true));
        stats.OnDispatch(new DispatchTiming(100, 3102.0, TestKit.Key("Z"), false));
        stats.OnDispatch(new DispatchTiming(200, 3205.0, TestKit.Key("X"), true));

        var s = stats.Summarize();

        Assert.Equal(3, s.Count);
        Assert.Equal(3000.0, s.BaselineOffsetMs, 3);
    }

    [Fact]
    public void JitterIgnoresThePreRollOffset()
    {
        var stats = new DispatchTimingStats();
        stats.OnDispatch(new DispatchTiming(0, 3000.0, TestKit.Key("Z"), true));
        stats.OnDispatch(new DispatchTiming(100, 3102.0, TestKit.Key("Z"), false));
        stats.OnDispatch(new DispatchTiming(200, 3205.0, TestKit.Key("X"), true));

        var s = stats.Summarize();

        // 残差是 0 / 102 / 105ms，绝不该再出现 3000ms 量级的数字。
        Assert.True(s.MeanAbsoluteMs < 150, $"均值应是在毫秒级残差上算出来的，实际 {s.MeanAbsoluteMs}");
        Assert.True(s.P95Ms < 200, $"p95 应是在毫秒级残差上算出来的，实际 {s.P95Ms}");
        Assert.True(s.MaxAbsoluteMs < 200, $"最大残差应远小于整体偏移，实际 {s.MaxAbsoluteMs}");
    }

    [Fact]
    public void CleanTimelineReportsNearZeroJitterEvenWithLargeOffset()
    {
        var stats = new DispatchTimingStats();
        // 一条完美的时间轴，只是整体晚了 3 秒（倒计时）。
        for (int i = 0; i < 10; i++)
            stats.OnDispatch(new DispatchTiming(i * 50.0, 3000.0 + i * 50.0, TestKit.Key("Z"), i % 2 == 0));

        var s = stats.Summarize();

        Assert.Equal(3000.0, s.BaselineOffsetMs, 3);
        Assert.Equal(0.0, s.MeanAbsoluteMs, 6);
        Assert.Equal(0.0, s.MaxAbsoluteMs, 6);
        // 偏移是全体的下界，所以残差不可能为负。
        Assert.Equal(0.0, s.MinSignedMs, 6);
    }

    [Fact]
    public void LargePreRollDoesNotChangeTheReportedJitter()
    {
        DispatchTimingStats Build(double preRoll)
        {
            var st = new DispatchTimingStats();
            st.OnDispatch(new DispatchTiming(0, preRoll, TestKit.Key("Z"), true));
            st.OnDispatch(new DispatchTiming(100, preRoll + 7, TestKit.Key("Z"), false));
            st.OnDispatch(new DispatchTiming(200, preRoll + 100, TestKit.Key("X"), true));
            return st;
        }

        var small = Build(0).Summarize();
        var large = Build(30_000).Summarize();

        // 预滚从 0 变成 30 秒，抖动读数必须完全一致 —— 这正是修复的核心。
        Assert.Equal(small.BaselineOffsetMs, large.BaselineOffsetMs - 30_000, 6);
        Assert.Equal(small.MeanAbsoluteMs, large.MeanAbsoluteMs, 6);
        Assert.Equal(small.P95Ms, large.P95Ms, 6);
        Assert.Equal(small.MaxAbsoluteMs, large.MaxAbsoluteMs, 6);
    }

    [Fact]
    public void EmptyStatsSummarizeToZero()
    {
        var s = new DispatchTimingStats().Summarize();
        Assert.Equal(0, s.Count);
        Assert.Equal(0.0, s.BaselineOffsetMs);
        Assert.Equal(0.0, s.MaxAbsoluteMs);
    }

    [Fact]
    public void RenderSummaryNamesTheOffsetSoItCannotBeMistakenForJitter()
    {
        var stats = new DispatchTimingStats();
        // 计划 0/100/200/300ms，整体晚 3000ms，另有 0/1/3/0ms 的真实抖动。
        stats.OnDispatch(new DispatchTiming(0, 3000.0, TestKit.Key("Z"), true));
        stats.OnDispatch(new DispatchTiming(100, 3101.0, TestKit.Key("Z"), false));
        stats.OnDispatch(new DispatchTiming(200, 3203.0, TestKit.Key("X"), true));
        stats.OnDispatch(new DispatchTiming(300, 3300.0, TestKit.Key("X"), false));

        string text = stats.RenderSummary();

        Assert.Contains("时间轴抖动", text);
        Assert.Contains("整体偏移", text);
        // 抖动读数是 1.000ms，不是 3000ms 量级 —— 这正是修复要保证的。
        Assert.Contains("均值 1.000", text);
        Assert.Contains("3000.000", text);   // 偏移被单独、显式地报出来
    }

    [Fact]
    public void RenderTableShowsResidualWhenBaselineIsSupplied()
    {
        var stats = new DispatchTimingStats();
        // 整体晚 3000ms；第一条零额外延迟（误差 3000），第二条多拖了 10ms（误差 3010）。
        // 注意误差要按「实际 − 计划」给：第二条是 3010 − 100 = 3000，所以实际写 3110 才是 +10。
        stats.OnDispatch(new DispatchTiming(0, 3000.0, TestKit.Key("Z"), true));
        stats.OnDispatch(new DispatchTiming(100, 3110.0, TestKit.Key("Z"), false));

        var s = stats.Summarize();
        Assert.Equal(3000.0, s.BaselineOffsetMs, 3);

        string text = stats.RenderTable(baselineOffsetMs: s.BaselineOffsetMs);

        // .NET 的三段数值格式里，零值走第三段，所以零残差渲染成不带符号的 "0.000ms"。
        Assert.True(text.Contains("扣偏后 0.000ms"), "表格里应出现首条的零残差，实际：\n" + text);
        Assert.True(text.Contains("+10.000ms"), "表格里应出现第二条约 +10ms 的残差，实际：\n" + text);
        // 若没扣偏，第一行会显示 +3000ms 的原始误差 —— 那正是要避免的噪音。
        Assert.True(!text.Contains("+3000.000ms"), "表格不该再显示被预滚污染的原始误差，实际：\n" + text);
    }

    // ------------------------------------------------------------------ 逐条日志

    [Fact]
    public void SchedulerLogReportsCorrectedJitterNotRawOffset()
    {
        var appLog = new AppLog();
        var schedulerLog = new SchedulerLog(appLog, verboseDispatch: true);
        var plan = TestKit.Plan(TestKit.Sequence(new[] { 60, 62 }, step: 0.3));
        var clock = new FakeClock();
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);

        using var engine = new PlaybackEngine(backend, clock)
        {
            SchedulerEnabled = false,
            Diagnostics = schedulerLog,
        };
        engine.Load(plan, 1.0);
        engine.Play();

        // 先在倒计时之后才开始派发：等价于 3 秒预滚。
        clock.SetTo(3000);
        engine.AdvanceToWallMs(3000);
        // 再往前 400ms，让后续事件也被派发。
        clock.SetTo(3400);
        engine.AdvanceToWallMs(3400);

        string text = appLog.ToText();
        Assert.Contains("起始偏移", text);
        // 逐条日志里的「抖动」是扣偏后的值：如果没扣，这里会出现 3000 左右的数字。
        Assert.DoesNotContain("抖动 +3000", text);
        Assert.DoesNotContain("抖动 +300", text);
    }

    // ------------------------------------------------------------------ 实时派发计数

    [Fact]
    public void DispatchedCountIsLiveDuringPlaybackAndSettlesAtEnd()
    {
        // 74 号音（D5）映射到 X 键 + 鼠标右键修饰，因此是 4 个事件：
        // MOUSE_RIGHT DOWN / X DOWN / X UP / MOUSE_RIGHT UP。
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 5.0) });
        var clock = new FakeClock();
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var schedulerLog = new SchedulerLog(new AppLog());

        using var engine = new PlaybackEngine(backend, clock)
        {
            SchedulerEnabled = false,
            Diagnostics = schedulerLog,
        };
        engine.Load(plan, 1.0);
        engine.Play();

        Assert.Equal(0, engine.DispatchedCount);

        clock.SetTo(500);
        engine.AdvanceToWallMs(500);

        // 前两个事件（修饰键 + 音键按下）已经派发。
        Assert.Equal(2, engine.DispatchedCount);
        // SchedulerLog 的计数在播放中**落后**于引擎（它逐条 +1，而播放中最后一次回调
        // 可能还没发生）。真正要保证的是：停止后它必须等于引擎的最终值，不能停在 0 ——
        // 用户看到「已派发 0 条」正是这么来的。
        Assert.True(schedulerLog.LastDispatchedCount <= engine.DispatchedCount);

        // 音符 5 秒 + 收尾释放间隔，全部事件在 6 秒处都已派发。
        clock.SetTo(6000);
        engine.AdvanceToWallMs(6000);
        Assert.Equal(plan.EventCount, engine.DispatchedCount);
        Assert.Equal(4, engine.DispatchedCount);
        Assert.Equal(engine.DispatchedCount, schedulerLog.LastDispatchedCount);
    }

    [Fact]
    public void DispatchedCountResetsOnEachPlay()
    {
        var clock = new FakeClock();
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);

        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };

        engine.Load(TestKit.Plan(new[] { TestKit.Note(74, 0.0, 5.0) }), 1.0);
        engine.Play();
        clock.SetTo(500);
        engine.AdvanceToWallMs(500);
        Assert.Equal(2, engine.DispatchedCount);

        engine.Load(TestKit.Plan(new[] { TestKit.Note(76, 0.0, 5.0) }), 1.0);
        engine.Play();
        Assert.Equal(0, engine.DispatchedCount);
    }
}
