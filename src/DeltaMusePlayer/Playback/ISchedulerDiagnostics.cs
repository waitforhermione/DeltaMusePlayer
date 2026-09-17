namespace DeltaMusePlayer.Playback;

/// <summary>
/// 调度器的运行期诊断钩子。
///
/// 之前出过一个很难查的问题：真实播放时后端抛异常，<c>TryDispatchLocked</c> 把状态设成 Idle，
/// UI 就报了「播放结束（全部输入已释放）」—— 真正的原因被完全吞掉，用户只能看到一个假的成功收尾。
/// 现在引擎会把「计划统计 / 每条派发 / 失败原因 / 收尾释放结果」通过这个接口吐出来，
/// GUI 与 CLI 都能把它们落到日志、显示在状态栏里。
///
/// 默认没有实现者时开销为零；只有在需要排查时才挂一个。
/// </summary>
public interface ISchedulerDiagnostics
{
    /// <summary>开始播放时的概况。</summary>
    void OnPlaybackStarted(PlaybackPlanInfo info);

    /// <summary>每条事件派发之前（plannedMs 是计划的墙钟时刻，actualMs 是当前时钟读数）。</summary>
    void OnDispatch(int index, double plannedMs, double actualMs, string inputLabel, bool isDown, int pitch);

    /// <summary>派发失败。异常在这里被记录，之后引擎会做 ReleaseAll 并停止。</summary>
    void OnDispatchFailure(int index, double plannedMs, string inputLabel, bool isDown, Exception error);

    /// <summary>播放收尾：正常结束、用户停止、或异常停止。</summary>
    void OnPlaybackStopped(string reason, int dispatchedCount);

    /// <summary>释放全部输入的结果（释放干净了就是 null）。</summary>
    void OnReleaseAll(Exception? error);
}

/// <summary>播放开始时汇报的计划概况。</summary>
public sealed record PlaybackPlanInfo(
    int EventCount,
    int NoteCount,
    int PlayableCount,
    int UnplayableCount,
    double TotalMs,
    double Speed,
    string FirstEvent,
    string LastEvent);
