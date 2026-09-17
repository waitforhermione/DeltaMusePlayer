using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Playback;
using Xunit;

namespace DeltaMusePlayer.Tests;

public sealed class TraceInputBackendTests
{
    [Fact]
    public void TraceBackendRecordsActionsWithoutSendingAnything()
    {
        var trace = new TraceInputBackend { NowMs = () => 123.5 };

        trace.MouseDown(InputId.Mouse("Right"));
        trace.KeyDown(InputId.Key("Z"));
        trace.KeyUp(InputId.Key("Z"));
        trace.MouseUp(InputId.Mouse("Right"));

        Assert.False(trace.SendsRealInput);
        Assert.Equal("Trace", trace.Name);
        Assert.Equal(4, trace.Entries.Count);
        Assert.Equal(
            new[] { "MOUSE_RIGHT:DOWN", "Z:DOWN", "Z:UP", "MOUSE_RIGHT:UP" },
            trace.Entries.Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToArray());
        Assert.Equal(123.5, trace.Entries[0].WallMs, 3);
        Assert.Empty(trace.HeldInputs);
    }

    [Fact]
    public void RepeatedPressOfTheSameKeyIsCaughtImmediately()
    {
        var trace = new TraceInputBackend();
        trace.KeyDown(InputId.Key("Z"));

        var ex = Assert.Throws<InvalidOperationException>(() => trace.KeyDown(InputId.Key("Z")));
        Assert.Contains("重复按下", ex.Message);
    }

    [Fact]
    public void ReleaseOfANonHeldInputIsCaughtImmediately()
    {
        var trace = new TraceInputBackend();

        var ex = Assert.Throws<InvalidOperationException>(() => trace.KeyUp(InputId.Key("Z")));
        Assert.Contains("没按下", ex.Message);
    }

    [Fact]
    public void KeyNamesAreCaseInsensitiveSoCaseNeverCausesAStuckKey()
    {
        var trace = new TraceInputBackend();
        trace.KeyDown(InputId.Key("z"));
        trace.KeyUp(InputId.Key("Z"));      // 归一化之后是同一个输入

        Assert.Empty(trace.HeldInputs);
    }

    [Fact]
    public void ReleaseAllLiftsEveryHeldInputExactlyOnce()
    {
        var trace = new TraceInputBackend();
        trace.MouseDown(InputId.Mouse("Right"));
        trace.KeyDown(InputId.Key("Z"));
        trace.KeyDown(InputId.Key("X"));

        trace.ReleaseAll();

        Assert.Empty(trace.HeldInputs);
        var tail = trace.Entries.TakeLast(3).Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToArray();
        Assert.Equal(new[] { "MOUSE_RIGHT:UP", "X:UP", "Z:UP" }, tail);

        // 幂等：再抬一次不会产生新事件
        int before = trace.Entries.Count;
        trace.ReleaseAll();
        Assert.Equal(before, trace.Entries.Count);
    }

    [Fact]
    public void ReleaseAllFollowsTheExceptionPathToo()
    {
        var trace = new TraceInputBackend();
        var clock = new FakeClock();
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 5.0) });   // RightMouse + X
        var engine = new PlaybackEngine(trace, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);
        engine.Play();

        clock.SetTo(100);
        engine.AdvanceToWallMs(100);
        Assert.Equal(new[] { "MOUSE_RIGHT", "X" }, trace.HeldInputs.Select(h => h.Label).OrderBy(n => n).ToArray());

        try
        {
            throw new InvalidOperationException("模拟演奏中出现的异常");
        }
        catch
        {
            engine.Stop();   // 等价于 finally { ReleaseAllInputs(); }
        }

        Assert.Empty(trace.HeldInputs);
    }
}
