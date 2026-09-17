using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Playback;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 调度器测试：FakeClock + TraceInputBackend，手动推进时间。
/// 全程不使用 Thread.Sleep，也不启动后台线程。
/// </summary>
public sealed class PlaybackEngineTests
{
    private static (PlaybackEngine Engine, TraceInputBackend Trace, FakeClock Clock) Rig(PlaybackPlan plan, double speed = 1.0)
    {
        var clock = new FakeClock();
        var trace = new TraceInputBackend();
        var engine = new PlaybackEngine(trace, clock, pollMs: 1.0) { SchedulerEnabled = false };
        engine.Load(plan, speed);
        return (engine, trace, clock);
    }

    private static void AdvanceTo(FakeClock clock, PlaybackEngine engine, double wallMs)
    {
        clock.SetTo(wallMs);
        engine.AdvanceToWallMs(wallMs);
    }

    private static List<string> Actions(TraceInputBackend t)
        => t.Entries.Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToList();

    /// <summary>把时钟推到「计划里第 n 条事件」的时刻。</summary>
    private static void AdvanceToEvent(FakeClock clock, PlaybackEngine engine, PlaybackPlan plan, int eventIndex)
        => AdvanceTo(clock, engine, plan.Events[eventIndex].TimeMs);

    [Fact]
    public void SingleNoteIsDispatchedAtTheRightWallClockTimes()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.5, 0.4) });   // C4 → Z（无修饰键）
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        Assert.Equal(2, plan.EventCount);
        AdvanceTo(clock, engine, plan.Events[0].TimeMs - 1);
        Assert.Empty(trace.Entries);

        AdvanceToEvent(clock, engine, plan, 0);
        Assert.Equal(new[] { "Z:DOWN" }, Actions(trace));

        AdvanceTo(clock, engine, plan.Events[1].TimeMs - 1);
        Assert.Equal(new[] { "Z:DOWN" }, Actions(trace));

        AdvanceToEvent(clock, engine, plan, 1);
        Assert.Equal(new[] { "Z:DOWN", "Z:UP" }, Actions(trace));
    }

    [Fact]
    public void ModifierIsPressedBeforeTheKeyAtTheRightWallClockTimes()
    {
        // F5 = 77 → V + RightMouse
        var plan = TestKit.Plan(new[] { TestKit.Note(77, 0.5, 0.4) });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        Assert.Equal(new[] { "MOUSE_RIGHT:DOWN", "V:DOWN", "V:UP", "MOUSE_RIGHT:UP" },
            plan.Events.Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToArray());

        AdvanceToEvent(clock, engine, plan, 0);
        Assert.Equal(new[] { "MOUSE_RIGHT:DOWN" }, Actions(trace));

        AdvanceToEvent(clock, engine, plan, 1);
        Assert.Equal(new[] { "MOUSE_RIGHT:DOWN", "V:DOWN" }, Actions(trace));

        AdvanceToEvent(clock, engine, plan, 2);
        Assert.Equal(new[] { "MOUSE_RIGHT:DOWN", "V:DOWN", "V:UP" }, Actions(trace));

        AdvanceToEvent(clock, engine, plan, 3);
        Assert.Equal(new[] { "MOUSE_RIGHT:DOWN", "V:DOWN", "V:UP", "MOUSE_RIGHT:UP" }, Actions(trace));
    }

    [Fact]
    public void NothingIsDispatchedBeforeTheFirstEventTime()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 1.0, 0.2) });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, plan.Events[0].TimeMs - 1);
        Assert.Empty(trace.Entries);
        Assert.Equal(PlaybackState.Playing, engine.State);
    }

    [Fact]
    public void RestBetweenNotesProducesNoEvents()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.2), TestKit.Note(62, 1.0, 0.2) });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        // 第一颗音的全部事件放完
        AdvanceTo(clock, engine, plan.Events[1].TimeMs);
        Assert.Equal(new[] { "Z:DOWN", "Z:UP" }, Actions(trace));

        // 休止中间：一条都不许有
        AdvanceTo(clock, engine, plan.Events[2].TimeMs - 1);
        Assert.Equal(new[] { "Z:DOWN", "Z:UP" }, Actions(trace));

        // 第二颗音开始
        AdvanceTo(clock, engine, plan.Events[2].TimeMs);
        Assert.Contains("X:DOWN", Actions(trace));
    }

    [Fact]
    public void RepeatedKeyProducesDistinctPressReleasePressRelease()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.1), TestKit.Note(60, 0.1, 0.1) });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, plan.WallTotalMs + 50);

        Assert.Equal(new[] { "Z:DOWN", "Z:UP", "Z:DOWN", "Z:UP" }, Actions(trace));
    }

    [Fact]
    public void PauseReleasesEverythingImmediatelyAndKeepsThePosition()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 1.0) });   // D5 → RightMouse + X
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, 300);
        Assert.Equal(new[] { "MOUSE_RIGHT:DOWN", "X:DOWN" }, Actions(trace));
        Assert.Equal(2, trace.HeldInputs.Count);

        engine.Pause();

        // Pause 之后不能留下任何按住的输入
        Assert.Empty(trace.HeldInputs);
        Assert.Contains("X:UP", Actions(trace));
        Assert.Contains("MOUSE_RIGHT:UP", Actions(trace));
        Assert.Equal(PlaybackState.Paused, engine.State);
        Assert.Equal(300.0, engine.PositionMusicMs, 3);
    }

    [Fact]
    public void ResumeRebuildsModifiersAndKeyFromTheNextNoteBoundary()
    {
        var plan = TestKit.Plan(new[]
        {
            TestKit.Note(74, 0.0, 1.0),    // RightMouse + X
            TestKit.Note(76, 2.0, 0.5),    // RightMouse + C
        });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, 300);
        engine.Pause();
        int entriesAtPause = trace.Entries.Count;

        engine.Resume();

        // 继续时不会停在半截音里：位置被对齐到下一颗音的边界，重建出它需要的输入。
        var after = Actions(trace).Skip(entriesAtPause).ToArray();
        Assert.Contains("MOUSE_RIGHT:DOWN", after);
        Assert.Contains("C:DOWN", after);
        Assert.Equal(2, trace.HeldInputs.Count);   // 修饰键 + 音键
    }

    [Fact]
    public void ResumeAfterThePausePointDoesNotReplayThePartiallyPlayedNote()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 1.0), TestKit.Note(62, 2.0, 0.5) });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, 500);
        engine.Pause();
        int before = trace.Entries.Count;

        AdvanceTo(clock, engine, 900);
        // 暂停期间时钟走了，但不该派发任何东西
        Assert.Equal(before, trace.Entries.Count);

        engine.Resume();
        // 恢复后第一件事是把下一颗音需要的输入建起来（X 没有修饰键）。
        var after = Actions(trace).Skip(before).ToArray();
        Assert.Contains("X:DOWN", after);
        Assert.DoesNotContain("Z:DOWN", after);
    }

    [Fact]
    public void StopReleasesEverythingAndResetsThePosition()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 5.0) });   // RightMouse + X，很长
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, 500);
        Assert.Equal(2, trace.HeldInputs.Count);

        engine.Stop();

        Assert.Empty(trace.HeldInputs);
        Assert.Equal(PlaybackState.Idle, engine.State);
        Assert.Equal(0.0, engine.PositionMusicMs, 3);

        // trace 的最后两条必须是抬键，且先鼠标后键（固定的收尾顺序）
        var tail = Actions(trace).TakeLast(2).ToArray();
        Assert.Equal("MOUSE_RIGHT:UP", tail[0]);
        Assert.Equal("X:UP", tail[1]);
    }

    [Fact]
    public void StopOnANeverStartedEngineIsHarmless()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.5) });
        var (engine, trace, _) = Rig(plan);

        engine.Stop();
        engine.Stop();

        Assert.Empty(trace.Entries);
        Assert.Equal(PlaybackState.Idle, engine.State);
    }

    [Fact]
    public void EngineFinishesOnItsOwnAndReleasesInputs()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.2) });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, plan.WallTotalMs + 100);

        Assert.Equal(PlaybackState.Idle, engine.State);
        Assert.Empty(trace.HeldInputs);
        Assert.Equal(new[] { "Z:DOWN", "Z:UP" }, Actions(trace));
    }

    [Fact]
    public void PlayAfterNaturalFinishRestartsFromTheBeginning()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.2) });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();
        AdvanceTo(clock, engine, plan.WallTotalMs + 100);
        Assert.Equal(PlaybackState.Idle, engine.State);

        engine.Play();
        AdvanceTo(clock, engine, clock.NowMs + plan.WallTotalMs + 100);

        Assert.Equal(new[] { "Z:DOWN", "Z:UP", "Z:DOWN", "Z:UP" }, Actions(trace));
    }

    [Fact]
    public void SeekMovesToTheNextNoteBoundaryAndReleasesInputs()
    {
        var plan = TestKit.Plan(new[]
        {
            TestKit.Note(60, 0.0, 0.5),
            TestKit.Note(62, 1.0, 0.5),
            TestKit.Note(64, 2.0, 0.5),
        });
        var (engine, trace, clock) = Rig(plan);
        engine.Play();
        AdvanceTo(clock, engine, 200);

        engine.SeekMusicMs(1000);

        // Seek = 先放掉旧输入、再重建目标位置那颗音需要的输入。
        // 下一颗音是 X（无修饰键），所以此刻按着的正好只有 X。
        Assert.Equal(new[] { "X" }, trace.HeldInputs.Select(h => h.Label).ToArray());

        var xDown = plan.Events.First(e => e.Input == InputId.Key("X") && e.Type == InputActionType.KeyDown);
        AdvanceTo(clock, engine, xDown.TimeMs);
        Assert.Contains("X:DOWN", Actions(trace));
        Assert.DoesNotContain("C:DOWN", Actions(trace));
    }

    [Fact]
    public void SpeedChangeKeepsThePositionAndScalesTheRemainingTimeline()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 2.0) });
        var (engine, _, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, 500);
        double position = engine.PositionMusicMs;
        Assert.Equal(500.0, position, 3);

        engine.SetSpeed(2.0);
        Assert.Equal(position, engine.PositionMusicMs, 3);

        // 2x 之后同样的墙钟时间覆盖两倍音乐时间
        AdvanceTo(clock, engine, 1000);
        Assert.Equal(1500.0, engine.PositionMusicMs, 3);
    }

    [Fact]
    public void BackendFailureTriggersReleaseAllAndSurfacesTheProblem()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.5) });
        var trace = new TraceInputBackend { Strict = true };
        var clock = new FakeClock();
        var engine = new PlaybackEngine(trace, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);

        // 人为制造「重复按下」：后端会抛，引擎必须 release-all 并把问题带出来。
        trace.KeyDown(InputId.Key("Z"));

        engine.Play();
        var ex = Assert.Throws<InvalidOperationException>(() => { clock.SetTo(10); engine.AdvanceToWallMs(10); });

        Assert.Contains("派发事件失败", ex.Message);
        Assert.Equal(PlaybackState.Idle, engine.State);
    }

    [Fact]
    public void AdvanceDoesNothingWhenNotPlaying()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.5) });
        var (engine, trace, clock) = Rig(plan);

        clock.SetTo(10_000);
        Assert.Empty(trace.Entries);
        Assert.Equal(0, engine.AdvanceToWallMs(10_000));
    }

    [Fact]
    public void PlanSummaryCountsEveryDispatchedEvent()
    {
        var plan = TestKit.Plan(TestKit.Sequence(new[] { 60, 62, 64, 65 }));
        var (engine, trace, clock) = Rig(plan);
        engine.Play();

        AdvanceTo(clock, engine, plan.WallTotalMs + 10);

        Assert.Equal(plan.EventCount, trace.Entries.Count);
    }
}
