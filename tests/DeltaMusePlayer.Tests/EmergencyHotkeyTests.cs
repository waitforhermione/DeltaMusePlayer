using DeltaMusePlayer.Input.Win32;
using DeltaMusePlayer.Playback;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 全局紧急停止热键。
///
/// 为什么必须做成全局的：真实播放的正常流程是「点播放 → 在倒计时里切到目标窗口」。
/// 一旦切走，本程序窗口就不是焦点了，挂在窗口上的 KeyDown 收不到任何按键 ——
/// 窗口级快捷键恰恰在**最需要它的时刻**是聋的。这些测试钉住「用系统级热键注册」这条路径，
/// 以及注册失败时的降级行为（不能假装热键可用）。
///
/// 测试全程用 <see cref="FakeGlobalHotkeyApi"/>，**绝不注册真的系统热键**。
/// </summary>
public sealed class EmergencyHotkeyTests
{
    [Fact]
    public void DefaultHotkeyIsF9()
    {
        var info = new GlobalHotkeyInfo(1, HotkeyModifiers.NoRepeat, EmergencyHotkey.DefaultVirtualKey);
        Assert.Equal("F9", info.Label);
        Assert.Equal(0x78u, EmergencyHotkey.DefaultVirtualKey);   // VK_F9
    }

    [Fact]
    public void RegistersF9AndReportsActive()
    {
        var api = new FakeGlobalHotkeyApi();
        using var hotkey = new EmergencyHotkey(api, () => { });

        hotkey.Start();

        Assert.True(hotkey.IsActive);
        Assert.Null(hotkey.FailureReason);
        Assert.Equal(new[] { EmergencyHotkey.HotkeyId }, api.RegisteredIds);
        Assert.NotNull(api.LastRegistration);
        Assert.Equal(EmergencyHotkey.DefaultVirtualKey, api.LastRegistration!.Value.VirtualKey);
        Assert.Equal("F9", hotkey.Label);
    }

    [Fact]
    public void PressingTheHotkeyInvokesTheCallback()
    {
        var api = new FakeGlobalHotkeyApi { BlockPumpUntilReleased = true };
        using var fired = new ManualResetEventSlim(false);
        using var hotkey = new EmergencyHotkey(api, () => fired.Set());

        hotkey.Start();
        Assert.True(hotkey.IsActive);

        api.FireOnNextPump = true;
        // 回调发生在热键自己的线程上，所以这里必须等信号，不能靠 Sleep 猜。
        Assert.True(fired.Wait(TimeSpan.FromSeconds(3)), "按下热键后回调没有被调用");
    }

    [Fact]
    public void CallbackCanFireMoreThanOnce()
    {
        // 紧急停止必须能反复用：按一次之后热键就失效，比没有热键更危险。
        var api = new FakeGlobalHotkeyApi { BlockPumpUntilReleased = true };
        int count = 0;
        using var second = new ManualResetEventSlim(false);
        using var hotkey = new EmergencyHotkey(api, () =>
        {
            if (Interlocked.Increment(ref count) >= 2) second.Set();
        });

        hotkey.Start();
        api.FireOnNextPump = true;
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref count) >= 1, TimeSpan.FromSeconds(3)),
            "第一次按下没有触发");

        api.FireOnNextPump = true;
        Assert.True(second.Wait(TimeSpan.FromSeconds(3)), "第二次按下没有触发 —— 热键被用一次就失效了");
    }

    [Fact]
    public void OccupiedHotkeyDegradesInsteadOfThrowing()
    {
        // 组合键可能已被别的程序占用。这必须是**可接受的降级**，不能让程序起不来。
        var api = new FakeGlobalHotkeyApi { FailRegistration = true, FailureError = 1409 };

        using var hotkey = new EmergencyHotkey(api, () => { });
        hotkey.Start();

        Assert.False(hotkey.IsActive);
        Assert.NotNull(hotkey.FailureReason);
        Assert.Contains("占用", hotkey.FailureReason!);   // 错误码 1409 被翻成人话
        Assert.Empty(api.RegisteredIds);
    }

    [Fact]
    public void UnsupportedPlatformIsReportedWithoutRegistering()
    {
        var api = new FakeGlobalHotkeyApi { IsSupported = false };
        using var hotkey = new EmergencyHotkey(api, () => { });

        hotkey.Start();

        Assert.False(hotkey.IsActive);
        Assert.Contains("不支持", hotkey.FailureReason!);
        Assert.Equal(0, api.RegisterCalls);   // 不支持就不该去调注册
    }

    [Fact]
    public void DisposeUnregistersTheSystemHotkey()
    {
        // 关窗后必须把系统组合键还回去，否则 F9 被本进程一直占着。
        var api = new FakeGlobalHotkeyApi();
        var hotkey = new EmergencyHotkey(api, () => { });
        hotkey.Start();
        Assert.True(hotkey.IsActive);

        hotkey.Dispose();

        Assert.Contains(EmergencyHotkey.HotkeyId, api.UnregisterCalls);
        Assert.False(hotkey.IsActive);
    }

    [Fact]
    public void DisposeWakesUpThePumpSoExitIsNotBlocked()
    {
        // 消息循环阻塞在等待里，退出时必须被唤醒，否则关窗要多等一个超时。
        var api = new FakeGlobalHotkeyApi { BlockPumpUntilReleased = true };
        var hotkey = new EmergencyHotkey(api, () => { });
        hotkey.Start();
        Assert.True(hotkey.IsActive);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        hotkey.Dispose();
        sw.Stop();

        Assert.True(api.WakeUpCalls >= 1, "退出路径没有唤醒消息循环");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"释放用了 {sw.ElapsedMilliseconds}ms，说明没有被唤醒");
    }

    [Fact]
    public void StartIsIdempotent()
    {
        var api = new FakeGlobalHotkeyApi();
        using var hotkey = new EmergencyHotkey(api, () => { });

        hotkey.Start();
        hotkey.Start();
        hotkey.Start();

        Assert.Equal(1, api.RegisterCalls);   // 重复 Start 不能重复注册
    }

    [Fact]
    public void DisposeBeforeStartIsSafe()
    {
        var api = new FakeGlobalHotkeyApi();
        var hotkey = new EmergencyHotkey(api, () => { });
        hotkey.Dispose();                     // 没 Start 就 Dispose 不应该抛
        Assert.False(hotkey.IsActive);
        Assert.Equal(0, api.RegisterCalls);
    }

    [Fact]
    public void FailingCallbackDoesNotKillTheHotkey()
    {
        // 回调里出问题（例如关窗竞态）不能让消息循环死掉，否则热键再也收不到。
        var api = new FakeGlobalHotkeyApi { BlockPumpUntilReleased = true };
        int calls = 0;
        using var second = new ManualResetEventSlim(false);
        using var hotkey = new EmergencyHotkey(api, () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("模拟回调出错");
            second.Set();
        });

        hotkey.Start();

        api.FireOnNextPump = true;            // 第一次抛异常
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 1, TimeSpan.FromSeconds(3)));

        api.FireOnNextPump = true;            // 第二次必须仍然能收到
        Assert.True(second.Wait(TimeSpan.FromSeconds(3)), "回调抛异常之后热键就收不到消息了");
    }

    [Fact]
    public void DefaultModifiersSuppressKeyRepeat()
    {
        // 按住不放不该连续触发一串紧急停止。
        var api = new FakeGlobalHotkeyApi();
        using var hotkey = new EmergencyHotkey(api, () => { });
        hotkey.Start();

        Assert.NotNull(api.LastRegistration);
        Assert.True(api.LastRegistration!.Value.Modifiers.HasFlag(HotkeyModifiers.NoRepeat));
    }
}
