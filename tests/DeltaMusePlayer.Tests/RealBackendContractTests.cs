using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Input.Win32;
using DeltaMusePlayer.Playback;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// Engine + 真实后端的契约测试（需求 42 / 43）：
///
///   PlaybackEngine → WindowsInputBackend → FakeWin32InputApi
///
/// 断言的是：真实后端在**系统层**发出的调用序列，与同一份 PlaybackPlan 在
/// TraceInputBackend 上产生的逻辑事件序列**一一对应**。
/// 没有第二套调度器，只有 backend 不同。
/// </summary>
public sealed class RealBackendContractTests
{
    /// <summary>把真实后端的 API 调用翻译回与 Trace 后端同样的「输入:动作」键。</summary>
    private static List<string> Win32Actions(FakeWin32InputApi api)
    {
        var result = new List<string>();
        foreach (var o in api.Ordered)
        {
            switch (o)
            {
                case FakeWin32InputApi.KeyCall k:
                    result.Add($"{KeyNameOfScan(k.ScanCode)}:{(k.KeyUp ? "UP" : "DOWN")}");
                    break;
                case FakeWin32InputApi.MouseCall m:
                    result.Add($"MOUSE_{m.Button.ToString().ToUpperInvariant()}:{(m.ButtonUp ? "UP" : "DOWN")}");
                    break;
            }
        }
        return result;
    }

    private static string KeyNameOfScan(ushort scan) => scan switch
    {
        0x2C => "Z",
        0x2D => "X",
        0x2E => "C",
        0x2F => "V",
        0x30 => "B",
        0x31 => "N",
        0x32 => "M",
        0x33 => ",",
        _ => $"SC{scan:X2}",
    };

    private static List<string> TraceActions(TraceInputBackend trace)
        => trace.Entries.Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToArray().ToList();

    /// <summary>用假时钟把计划完整跑一遍（确定性，不 Sleep）。</summary>
    private static void RunToEnd(PlaybackEngine engine, FakeClock clock, PlaybackPlan plan, double stepMs = 1.0)
    {
        engine.Play();
        for (double t = 0; t <= plan.WallTotalMs + 50; t += stepMs)
        {
            clock.SetTo(t);
            engine.AdvanceToWallMs(t);
        }
    }

    [Fact]
    public void RealBackendProducesTheSameLogicalSequenceAsTraceBackend()
    {
        // 一份覆盖普通键 / 半音 / 升八度 / 降八度 / 同键重复的短旋律
        var pitches = new[] { 60, 60, 62, 64, 67, 72, 73, 74, 48 };
        var plan = TestKit.Plan(TestKit.Sequence(pitches, step: 0.25, duration: 0.2));

        // --- Trace ---
        var traceClock = new FakeClock();
        var trace = new TraceInputBackend();
        using (var traceEngine = new PlaybackEngine(trace, traceClock) { SchedulerEnabled = false })
        {
            traceEngine.Load(plan, 1.0);
            RunToEnd(traceEngine, traceClock, plan);
        }

        // --- Real（打假 API） ---
        var api = new FakeWin32InputApi();
        var realBackend = new WindowsInputBackend(api);
        var realClock = new FakeClock();
        using (var realEngine = new PlaybackEngine(realBackend, realClock) { SchedulerEnabled = false })
        {
            realEngine.Load(plan, 1.0);
            RunToEnd(realEngine, realClock, plan);
        }

        Assert.Equal(TraceActions(trace), Win32Actions(api));
        Assert.Empty(trace.HeldInputs);
        Assert.Empty(realBackend.HeldInputs);
        Assert.Equal(plan.EventCount, api.TotalCalls);
    }

    [Fact]
    public void SamePlanSameEventCountRegardlessOfBackend()
    {
        var plan = TestKit.Plan(TestKit.Sequence(new[] { 60, 62, 64, 65 }, step: 0.3));

        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);

        RunToEnd(engine, clock, plan);

        Assert.Equal(plan.EventCount, api.TotalCalls);
    }

    [Fact]
    public void PauseReleasesEverythingEvenWithTheRealBackend()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 5.0) });   // RightMouse + X
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);
        engine.Play();

        clock.SetTo(300);
        engine.AdvanceToWallMs(300);
        Assert.Equal(2, backend.HeldInputs.Count);

        engine.Pause();

        Assert.Empty(backend.HeldInputs);
        var tail = api.Ordered.TakeLast(2).ToArray();
        Assert.True(tail.All(o => (o is FakeWin32InputApi.KeyCall k && k.KeyUp) ||
                                  (o is FakeWin32InputApi.MouseCall m && m.ButtonUp)));
    }

    [Fact]
    public void StopReleasesEverythingEvenWithTheRealBackend()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 5.0) });
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);
        engine.Play();

        clock.SetTo(400);
        engine.AdvanceToWallMs(400);
        Assert.Equal(2, backend.HeldInputs.Count);

        engine.Stop();

        Assert.Empty(backend.HeldInputs);
        Assert.Equal(PlaybackState.Idle, engine.State);
    }

    [Fact]
    public void SeekReleasesTheOldModifierBeforeRebuildingTheNewOne()
    {
        var plan = TestKit.Plan(new[]
        {
            TestKit.Note(48, 0.0, 0.5),    // LeftMouse + Z
            TestKit.Note(84, 1.0, 0.5),    // RightMouse + ,
        });
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);
        engine.Play();

        clock.SetTo(200);
        engine.AdvanceToWallMs(200);
        Assert.Contains(InputId.Mouse("Left"), backend.HeldInputs);

        engine.SeekMusicMs(1000);

        // Seek = 先放掉旧输入、再重建目标位置需要的输入：
        // 此刻按着的应该是「下一颗音的 RightMouse + 逗号键」，旧的 LeftMouse 必须已经松开。
        Assert.DoesNotContain(InputId.Mouse("Left"), backend.HeldInputs);
        Assert.Contains(InputId.Mouse("Right"), backend.HeldInputs);
        Assert.Contains(InputId.Key(","), backend.HeldInputs);
    }

    /// <summary>
    /// 需求 43：Z down 成功、RightMouse down 成功、下一个注入失败 ——
    /// 最终必须尝试释放 Z 与 RightMouse，held 状态不能悄悄丢。
    /// </summary>
    [Fact]
    public void InjectionFailureMidPlaybackStillReleasesEverything()
    {
        var plan = TestKit.Plan(new[]
        {
            TestKit.Note(74, 0.0, 0.3),    // RightMouse + X
            TestKit.Note(76, 0.5, 0.3),    // RightMouse + C
        });

        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);
        engine.Play();

        // 前两次注入（RightMouse down、X down）成功
        clock.SetTo(plan.Events[1].TimeMs);
        engine.AdvanceToWallMs(plan.Events[1].TimeMs);
        Assert.Equal(2, backend.HeldInputs.Count);

        // 之后所有注入都失败
        api.FailFromCall = api.TotalCalls + 1;

        // 再往前走：X up 会失败 -> 引擎必须 ReleaseAll，并且把问题抛出来
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            clock.SetTo(plan.Events[2].TimeMs + 1);
            engine.AdvanceToWallMs(plan.Events[2].TimeMs + 1);
        });
        Assert.Contains("派发事件失败", ex.Message);

        // 引擎在异常路径里已经尽力 release 过；这里再显式抬一次，确认账本没有丢
        try { backend.ReleaseAll(); } catch (ReleaseAllException) { /* 仍在失败注入期，预期之内 */ }

        // 关键断言：账本里仍然记得「系统里可能还按着这两个」
        Assert.Contains(InputId.Mouse("Right"), backend.HeldInputs);
        Assert.Contains(InputId.Key("X"), backend.HeldInputs);
    }

    [Fact]
    public void ReleaseAllAfterAFailurePeriodSucceedsOnceInjectionRecovers()
    {
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);

        backend.KeyDown(InputId.Key("Z"));
        backend.MouseDown(InputId.Mouse("Right"));

        api.FailFromCall = api.TotalCalls + 1;
        Assert.Throws<ReleaseAllException>(() => backend.ReleaseAll());
        Assert.Equal(2, backend.HeldInputs.Count);

        api.FailFromCall = 0;                     // 注入恢复
        backend.ReleaseAll();                     // 再抬一次必须成功

        Assert.Empty(backend.HeldInputs);
    }

    /// <summary>
    /// 需求 44：播放中关闭窗口（等价于 ShutdownRequested / Closing 调 EmergencyStop）
    /// 必须触发 stop + release-all，而且不能先释放对象再抬键。
    /// </summary>
    [Fact]
    public void WindowCloseDuringPlaybackStopsAndReleases()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 5.0) });
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);
        var clock = new FakeClock();
        var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);
        engine.Play();

        clock.SetTo(500);
        engine.AdvanceToWallMs(500);
        Assert.Equal(2, backend.HeldInputs.Count);

        // 模拟 App.OnFrameworkInitializationCompleted 里注册的 ShutdownRequested -> EmergencyStop
        engine.Stop();
        backend.Dispose();                        // 关窗时的顺序：先 stop 再 dispose backend

        Assert.Empty(backend.HeldInputs);
        // 账本清空之后再 ReleaseAll 不会再往系统发东西
        int calls = api.TotalCalls;
        backend.ReleaseAll();
        Assert.Equal(calls, api.TotalCalls);
    }

    [Fact]
    public void TimingSinkReceivesOneSamplePerEvent()
    {
        var plan = TestKit.Plan(TestKit.Sequence(new[] { 60, 62, 64 }, step: 0.3));
        var api = new FakeWin32InputApi();
        var stats = new DispatchTimingStats();
        var backend = new WindowsInputBackend(api) { TimingSink = stats };
        var clock = new FakeClock();

        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);

        // 引擎构造时应当已自动接上后端自己的探针
        Assert.Same(stats, engine.TimingSink);

        RunToEnd(engine, clock, plan);

        Assert.Equal(plan.EventCount, stats.Count);
        Assert.All(stats.Samples, s => Assert.True(s.JitterMs >= 0, "假时钟下不该有负抖动"));
    }

    [Fact]
    public void TimingSinkStaysNullWhenNotConfigured()
    {
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);          // 没设 TimingSink
        var clock = new FakeClock();
        using var engine = new PlaybackEngine(backend, clock) { SchedulerEnabled = false };
        Assert.Null(engine.TimingSink);
    }
}
