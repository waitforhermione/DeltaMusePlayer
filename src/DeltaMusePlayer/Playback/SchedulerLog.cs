namespace DeltaMusePlayer.Playback;

using DeltaMusePlayer.Core;

/// <summary>
/// 把调度器的运行期诊断落到 <see cref="AppLog"/>。
///
/// 为什么需要它：真实播放出问题时，屏幕上只留一句最终状态，原因往往已经被丢掉。
/// 这个类保证「计划概况 / 逐条派发 / 失败原因 / 释放结果」都进 play.log，
/// 出问题时有据可查（尤其是「看着像播完了、其实什么都没弹」这类）。
/// </summary>
public sealed class SchedulerLog : ISchedulerDiagnostics
{
    private readonly AppLog _log;
    private readonly bool _verboseDispatch;

    /// <param name="log">目标日志。</param>
    /// <param name="verboseDispatch">
    /// true 时逐条记录每个派发（只在诊断/排查时打开，正常播放不开，避免刷日志）。
    /// </param>
    public SchedulerLog(AppLog log, bool verboseDispatch = false)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _verboseDispatch = verboseDispatch;
    }

    /// <summary>最近一次失败的可读描述（没有失败就是 null）。</summary>
    public string? LastFailure { get; private set; }

    /// <summary>本次播放已经派发了多少条。</summary>
    public int LastDispatchedCount { get; private set; }

    /// <summary>
    /// 本次播放的起始偏移：已派发样本误差的**最小值**。
    ///
    /// 倒计时、计划构建等预滚只会让事件更晚、不会更早，所以最小值就是「零额外延迟」那条
    /// 事件量出来的纯预滚量。逐条日志既报原始误差（含预滚），也报扣掉它之后的残差 ——
    /// 后者才是调度抖动，否则每条都显示 +3031ms 之类的大数，日志等于白写。
    /// 用逐条滚动最小值（而不是首条）是为了不依赖事件顺序，且残差恒非负。
    /// </summary>
    private double _baselineOffsetMs = double.PositiveInfinity;

    public void OnPlaybackStarted(PlaybackPlanInfo info)
    {
        LastFailure = null;
        LastDispatchedCount = 0;
        _baselineOffsetMs = double.PositiveInfinity;
        _log.Info(
            $"开始播放：事件 {info.EventCount}（总时长 {Music.TimeLabel(info.TotalMs)}，速度 {info.Speed:0.##}x）；" +
            $"音符 {info.NoteCount}（可演奏 {info.PlayableCount} / 不可演奏 {info.UnplayableCount}）；" +
            $"首事件 {info.FirstEvent}；末事件 {info.LastEvent}");

        if (info.EventCount == 0)
            _log.Warn("播放计划里没有任何事件 —— 这条轨上一个可演奏的音都没有。");
    }

    public void OnDispatch(int index, double plannedMs, double actualMs, string inputLabel, bool isDown, int pitch)
    {
        double raw = actualMs - plannedMs;
        if (raw < _baselineOffsetMs) _baselineOffsetMs = raw;

        LastDispatchedCount = index;

        if (!_verboseDispatch) return;
        string note = pitch >= 0 ? Music.NoteName(pitch) : "-";
        string offsetNote = index == 1
            ? $"（起始偏移 {_baselineOffsetMs:+0.000;-0.000;0.000}ms，由倒计时/预滚造成，已从后续「抖动」中扣除）"
            : "";
        _log.Info(
            $"派发 #{index} 计划 {Music.TimeLabel(plannedMs)} 实际 {Music.TimeLabel(actualMs)} " +
            $"抖动 {raw - _baselineOffsetMs:+0.000;-0.000;0.000}ms" +
            $"（原始误差 {raw:+0.000;-0.000;0.000}ms）{inputLabel} {(isDown ? "DOWN" : "UP")} ({note}){offsetNote}");
    }

    public void OnDispatchFailure(int index, double plannedMs, string inputLabel, bool isDown, Exception error)
    {
        LastFailure = $"#{index} 计划 {Music.TimeLabel(plannedMs)} {inputLabel} {(isDown ? "DOWN" : "UP")}：{error}";
        _log.Error($"派发失败（事件 #{index}，计划 {Music.TimeLabel(plannedMs)}，{inputLabel} " +
                   $"{(isDown ? "DOWN" : "UP")}）：{error.Message}", error);
    }

    public void OnPlaybackStopped(string reason, int dispatchedCount)
    {
        LastDispatchedCount = dispatchedCount;
        if (LastFailure is null)
            _log.Info($"播放结束（{reason}）：共派发 {dispatchedCount} 条。");
        else
            _log.Error($"播放中断（{reason}）：只派发了 {dispatchedCount} 条。最后失败：{LastFailure}");
    }

    public void OnReleaseAll(Exception? error)
    {
        if (error is null) _log.Info("release-all：全部输入已释放（held 为空）。");
        else _log.Error($"release-all 部分失败：{error.Message}", error);
    }
}
