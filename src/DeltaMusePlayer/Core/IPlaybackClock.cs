using System.Diagnostics;

namespace DeltaMusePlayer.Core;

/// <summary>
/// 单调整时钟。播放位置一律由它推导，**禁止**用 `timer_tick += 10ms` 累加。
///
/// 设计口径：
///   position = anchorPosition + (now - anchorTime)
/// UI 定时器只负责刷新界面，不参与任何时序判定。
/// </summary>
public interface IPlaybackClock
{
    /// <summary>单调递增的当前时间（毫秒）。</summary>
    double NowMs { get; }
}

/// <summary>基于 <see cref="Stopwatch"/> 的高精度单调时钟。</summary>
public sealed class StopwatchClock : IPlaybackClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public double NowMs => _sw.Elapsed.TotalMilliseconds;
}

/// <summary>
/// 测试用假时钟：只有显式 <see cref="Advance"/> 才会走时。
/// 时序单测一律用它，禁止 Thread.Sleep 做 timing 断言。
/// </summary>
public sealed class FakeClock : IPlaybackClock
{
    private readonly object _gate = new();
    private double _now;

    public FakeClock(double startMs = 0) => _now = startMs;

    public double NowMs
    {
        get { lock (_gate) return _now; }
    }

    public void Advance(double deltaMs)
    {
        if (deltaMs < 0) throw new ArgumentOutOfRangeException(nameof(deltaMs), "假时钟不能倒着走。");
        lock (_gate) _now += deltaMs;
    }

    /// <summary>直接设置当前时间（只能往前）。</summary>
    public void SetTo(double ms)
    {
        lock (_gate)
        {
            if (ms < _now) throw new ArgumentOutOfRangeException(nameof(ms), "假时钟不能倒着走。");
            _now = ms;
        }
    }
}
