using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Input.Win32;
using DeltaMusePlayer.Playback;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 「播放中断」必须能被看见。
///
/// 背景：真实播放时如果后端抛异常，引擎会把自己设成 Idle 并释放输入，
/// UI 以前把这种 Idle 当成「正常播完」，于是屏幕上只剩一句「播放结束（全部输入已释放）」——
/// 用户看到的是假成功，真正原因完全丢失。这些测试钉住修复后的行为。
/// </summary>
public sealed class DispatchFailureVisibilityTests
{
    /// <summary>记录所有诊断回调，便于断言「失败有没有被报出来」。</summary>
    private sealed class RecordingDiagnostics : ISchedulerDiagnostics
    {
        public PlaybackPlanInfo? Started { get; private set; }
        public List<string> Dispatches { get; } = new();
        public List<string> Failures { get; } = new();
        public List<string> Stops { get; } = new();
        public List<string?> Releases { get; } = new();

        public void OnPlaybackStarted(PlaybackPlanInfo info) => Started = info;

        public void OnDispatch(int index, double plannedMs, double actualMs, string inputLabel, bool isDown, int pitch)
            => Dispatches.Add($"#{index} {inputLabel} {(isDown ? "DOWN" : "UP")}");

        public void OnDispatchFailure(int index, double plannedMs, string inputLabel, bool isDown, Exception error)
            => Failures.Add($"#{index} {inputLabel} {(isDown ? "DOWN" : "UP")}: {error.GetType().Name}: {error.Message}");

        public void OnPlaybackStopped(string reason, int dispatchedCount) => Stops.Add($"{reason} | {dispatchedCount}");

        public void OnReleaseAll(Exception? error) => Releases.Add(error?.Message);
    }

    [Fact]
    public void SuccessfulPlaybackLeavesNoFailureMark()
    {
        var plan = TestKit.Plan(TestKit.Sequence(new[] { 60, 62, 64 }, step: 0.3));
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        var diag = new RecordingDiagnostics();

        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false, Diagnostics = diag };
        engine.Load(plan, 1.0);
        engine.Play();
        for (double t = 0; t <= plan.WallTotalMs + 20; t += 1.0)
        {
            clock.SetTo(t);
            engine.AdvanceToWallMs(t);
        }

        Assert.Null(engine.LastDispatchFailure);
        Assert.NotNull(diag.Started);
        Assert.Equal(plan.EventCount, diag.Started!.EventCount);
        Assert.Empty(diag.Failures);
        Assert.Contains(diag.Releases, r => r is null);              // 释放干净
        Assert.Contains(diag.Stops, s => s.StartsWith("natural end"));
    }

    [Fact]
    public void DispatchFailureSetsTheFailureMarkAndIsReported()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.5) });
        var api = new FakeWin32InputApi { FailFromCall = 1 };        // 第一次注入就失败
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        var diag = new RecordingDiagnostics();

        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false, Diagnostics = diag };
        engine.Load(plan, 1.0);
        engine.Play();

        var ex = Assert.Throws<InvalidOperationException>(() => engine.AdvanceToWallMs(10));
        Assert.Contains("派发事件失败", ex.Message);

        // UI 靠这个标记区分「真播完」与「炸了」
        Assert.NotNull(engine.LastDispatchFailure);
        Assert.Contains("Z", engine.LastDispatchFailure);
        Assert.Contains("WindowsInputException", engine.LastDispatchFailure);
        Assert.Contains("87", engine.LastDispatchFailure);           // Win32 错误码在里面

        Assert.Equal(PlaybackState.Idle, engine.State);
        Assert.Single(diag.Failures);
        Assert.Contains(diag.Stops, s => s.StartsWith("dispatch failure"));
    }

    [Fact]
    public void DispatchFailureAlsoReportsAFailedReleaseAll()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.5) });
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        var diag = new RecordingDiagnostics();

        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false, Diagnostics = diag };
        engine.Load(plan, 1.0);
        engine.Play();

        // 先正常按下 Z，然后让所有后续注入（含 ReleaseAll）都失败
        clock.SetTo(0);
        engine.AdvanceToWallMs(0);
        api.FailFromCall = api.TotalCalls + 1;

        Assert.ThrowsAny<Exception>(() =>
        {
            clock.SetTo(600);
            engine.AdvanceToWallMs(600);
        });

        Assert.NotNull(engine.LastDispatchFailure);
        // 释放也失败了，这一点同样要写进失败描述里，不能只字不提
        Assert.Contains("release-all 也失败", engine.LastDispatchFailure);
        Assert.Contains(diag.Releases, r => r is not null);
    }

    [Fact]
    public void StopAndPauseReportTheirOwnReasons()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 5.0) });
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        var diag = new RecordingDiagnostics();

        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false, Diagnostics = diag };
        engine.Load(plan, 1.0);
        engine.Play();
        clock.SetTo(200);
        engine.AdvanceToWallMs(200);

        engine.Pause();

        Assert.Contains(diag.Stops, s => s.StartsWith("paused by user"));

        engine.Stop();

        Assert.Contains(diag.Stops, s => s.StartsWith("stopped by user"));
        Assert.Null(engine.LastDispatchFailure);      // 用户主动停的不算失败
    }

    [Fact]
    public void EmptyPlanIsReportedInsteadOfLookingLikeASuccessfulRun()
    {
        var plan = TestKit.Plan(Array.Empty<PlaybackNote>());
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();

        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);

        // 空计划点播放必须是明确报错，而不是「开始 → 立刻结束」
        var ex = Assert.Throws<InvalidOperationException>(() => engine.Play());
        Assert.Contains("没有任何事件", ex.Message);
    }

    [Fact]
    public void SchedulerLogWritesFailuresAndStartStopIntoTheAppLog()
    {
        var appLog = new AppLog();
        var schedulerLog = new SchedulerLog(appLog);
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.5) });
        var api = new FakeWin32InputApi { FailFromCall = 1 };
        var clock = new FakeClock();

        var backend = new WindowsInputBackend(api);
        using var engine = new PlaybackEngine(backend, clock)
        {
            SchedulerEnabled = false,
            Diagnostics = schedulerLog,
        };
        engine.Load(plan, 1.0);
        engine.Play();
        Assert.Throws<InvalidOperationException>(() => engine.AdvanceToWallMs(10));

        string text = appLog.ToText();
        Assert.Contains("开始播放", text);
        Assert.Contains("派发失败", text);
        Assert.Contains("播放中断", text);
        Assert.NotNull(schedulerLog.LastFailure);
    }
}
