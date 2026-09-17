namespace DeltaMusePlayer.Playback;

using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;

/// <summary>播放状态。</summary>
public enum PlaybackState
{
    Idle,
    Playing,
    Paused,
}

/// <summary>
/// 调度器：按 <see cref="PlaybackPlan"/> 的时间轴把事件推给 <see cref="IInputBackend"/>。
///
/// 硬性约定：
///  * 时钟只有 <see cref="IPlaybackClock"/> 一个来源。位置 = 锚点位置 + (now - 锚点时刻)。
///    不存在 `timer_tick += 10ms` 这样的累加。
///  * 调度只按 index 前进，不每帧扫描整份计划。
///  * Pause / Stop / 异常：一律 <see cref="IInputBackend.ReleaseAll"/>，绝不留按住的键。
///  * 事件的**音**逻辑：resume 时从「不早于当前位置的第一颗音」的起点重建修饰键与音键，
///    因此不会出现半截音（例如键已按下但修饰键没按上）。
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly IInputBackend _backend;
    private readonly IPlaybackClock _clock;
    private readonly double _tickMs;
    private readonly double _spinMs;

    private PlaybackPlan? _plan;
    private int _index;

    private double _anchorWallMs;
    private double _anchorMusicMs;

    private PlaybackState _state = PlaybackState.Idle;
    private Thread? _worker;
    private volatile bool _workerStop;
    private readonly AutoResetEvent _wake = new(false);

    public PlaybackEngine(IInputBackend backend, IPlaybackClock clock, double pollMs = 2.0, double spinMs = 2.0)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (pollMs <= 0) throw new ArgumentOutOfRangeException(nameof(pollMs));
        if (spinMs < 0) throw new ArgumentOutOfRangeException(nameof(spinMs));
        _tickMs = pollMs;
        _spinMs = spinMs;
        if (backend is TraceInputBackend trace) trace.NowMs = () => _clock.NowMs;

        // 诊断探针顺手接上真实后端自己的探针：调用方不需要记住两处都设。
        if (backend is WindowsInputBackend windows && windows.TimingSink is not null)
            TimingSink = windows.TimingSink;
    }

    private int _dispatchedTotal;
    private volatile string? _lastDispatchFailure;

    /// <summary>
    /// 最近一次派发失败的可读描述；成功播放收尾时为 null。
    /// UI 用它区分「真的播完了」与「派发炸了被当成播完」——这两种情况以前长得一模一样。
    /// </summary>
    public string? LastDispatchFailure => _lastDispatchFailure;

    /// <summary>
    /// 本次播放已派发的事件数（**实时**），播放开始时归零。
    /// 以前 UI 读的是 <see cref="SchedulerLog.LastDispatchedCount"/>，那个值只在停止时更新，
    /// 所以播放过程中永远显示 0 —— 用户看到「已派发 0 条」会以为根本没弹。
    /// </summary>
    public int DispatchedCount
    {
        get { lock (_gate) return _dispatchedTotal; }
    }

    /// <summary>
    /// 运行期诊断钩子（可选）。挂上之后会收到计划概况、逐条派发、失败原因与释放结果。
    /// 默认 null，开销为零。
    /// </summary>
    public ISchedulerDiagnostics? Diagnostics { get; set; }

    /// <summary>当前速度。播放中改变会在当前位置重新锚定，位置不跳。</summary>
    public double Speed { get; private set; } = 1.0;

    public PlaybackState State
    {
        get { lock (_gate) return _state; }
    }

    public PlaybackPlan? Plan
    {
        get { lock (_gate) return _plan; }
    }

    /// <summary>当前位置（音乐毫秒）。</summary>
    public double PositionMusicMs
    {
        get
        {
            lock (_gate)
            {
                if (_state != PlaybackState.Playing) return _anchorMusicMs;
                double elapsed = _clock.NowMs - _anchorWallMs;
                return _anchorMusicMs + elapsed * Speed;
            }
        }
    }

    /// <summary>当前位置（墙钟毫秒，界面进度条用）。</summary>
    public double PositionWallMs
    {
        get
        {
            lock (_gate)
            {
                var plan = _plan;
                if (plan is null) return 0;
                return Math.Max(0, PositionMusicMs);
            }
        }
    }

    public double TotalWallMs
    {
        get { lock (_gate) return _plan?.WallTotalMs ?? 0; }
    }

    /// <summary>
    /// 打开后台调度线程。默认为 true。
    ///
    /// 纯逻辑时序测试会把它关掉，只调用 <see cref="AdvanceToWallMs"/> 手动推进 ——
    /// 这样断言是确定性的，也不会有「测试线程和调度线程同时推时间」的竞态。
    /// </summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>事件派发回调（UI 显示当前音，测试记录顺序）。</summary>
    public event Action<InputEvent>? EventDispatched;

    /// <summary>
    /// 派发时序探针（诊断模式）。非 null 时每条派发都会收到一份
    /// 「计划时刻 / 实际注入时刻」，用来观察 jitter。
    /// 它只读单调时钟、只做记录，**不参与任何调度判定**。
    /// </summary>
    public IDispatchTimingSink? TimingSink { get; set; }

    public event Action<PlaybackState>? StateChanged;

    // ---------------------------------------------------------------- 装载

    /// <summary>装载一份新计划。会先停止并释放全部输入。</summary>
    public void Load(PlaybackPlan plan, double speed = 1.0)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));

        Stop();
        lock (_gate)
        {
            _plan = plan;
            _index = 0;
            Speed = speed;
            _anchorMusicMs = 0;
            _anchorWallMs = 0;
            _state = PlaybackState.Idle;
        }
        RaiseState();
    }

    // ---------------------------------------------------------------- 播放控制

    /// <summary>从头（或从当前 Idle 位置）开始播放。已经播完时自动从头再来。</summary>
    public void Play()
    {
        PlaybackPlanInfo? info = null;
        lock (_gate)
        {
            if (_plan is null) throw new InvalidOperationException("还没有装载播放计划。");
            if (_plan.Events.Count == 0)
                throw new InvalidOperationException("播放计划里没有任何事件。");

            if (_index >= _plan.Events.Count)
            {
                // 上一遍已经播完：从零重来。锚点必须一起重置，
                // 否则位置会按「时钟已经走了多久」算出负数，事件全部被跳过。
                _index = 0;
                _anchorMusicMs = 0;
            }
            _anchorWallMs = _clock.NowMs;
            _state = PlaybackState.Playing;
            _dispatchedTotal = 0;
            _lastDispatchFailure = null;
            info = BuildPlanInfo(_plan, _index);
            StartWorkerLocked();
        }
        RaiseState();

        if (info is not null)
        {
            try { Diagnostics?.OnPlaybackStarted(info); } catch { /* 诊断自身不能影响播放 */ }
        }
    }

    private PlaybackPlanInfo BuildPlanInfo(PlaybackPlan plan, int startIndex)
    {
        var events = plan.Events;
        string first = events.Count > startIndex
            ? $"{Music.TimeLabel(events[startIndex].TimeMs)} {events[startIndex].Input.Label} {events[startIndex].ActionLabel}"
            : "(none)";
        string last = events.Count > 0
            ? $"{Music.TimeLabel(events[^1].TimeMs)} {events[^1].Input.Label} {events[^1].ActionLabel}"
            : "(none)";

        return new PlaybackPlanInfo(
            events.Count,
            plan.Report.NoteCount,
            plan.Report.PlayableCount,
            plan.Report.UnplayableCount,
            plan.WallTotalMs,
            Speed,
            first,
            last);
    }

    /// <summary>暂停：立即释放全部输入，记录当前位置。</summary>
    public void Pause()
    {
        StopWorker();
        lock (_gate)
        {
            if (_state != PlaybackState.Playing) return;
            _anchorMusicMs = CurrentPositionLocked();
            _state = PlaybackState.Paused;
        }

        Exception? releaseError = null;
        try { _backend.ReleaseAll(); }
        catch (Exception ex) { releaseError = ex; }
        Diagnostics?.OnReleaseAll(releaseError);
        Diagnostics?.OnPlaybackStopped(
            releaseError is null ? "paused by user" : $"paused by user, release-all failed: {releaseError.Message}",
            _dispatchedTotal);
        RaiseState();
    }

    /// <summary>继续：从当前位置之后的第一颗音重建修饰键与音键，然后继续走时钟。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_plan is null) throw new InvalidOperationException("还没有装载播放计划。");
            if (_state != PlaybackState.Paused) return;
            RebuildAtLocked(_anchorMusicMs);
            _anchorWallMs = _clock.NowMs;
            _state = PlaybackState.Playing;
            StartWorkerLocked();
        }
        RaiseState();
    }

    /// <summary>停止：停调度、位置归零、释放全部输入、清空状态。任何异常路径下都会先 ReleaseAll。</summary>
    public void Stop()
    {
        StopWorker();
        Exception? releaseError = null;
        try
        {
            _backend.ReleaseAll();
        }
        catch (Exception ex)
        {
            releaseError = ex;
        }
        finally
        {
            lock (_gate)
            {
                _index = 0;
                _anchorMusicMs = 0;
                _anchorWallMs = 0;
                _state = PlaybackState.Idle;
            }
        }

        Diagnostics?.OnReleaseAll(releaseError);
        Diagnostics?.OnPlaybackStopped(
            releaseError is null ? "stopped by user" : $"stopped by user, release-all failed: {releaseError.Message}",
            _dispatchedTotal);
        RaiseState();
    }

    /// <summary>跳转（只允许在停止或暂停状态下调用）。位置会被对齐到不早于目标位置的第一颗音。</summary>
    public void SeekMusicMs(double musicMs)
    {
        StopWorker();

        // 顺序很重要：**先**把旧输入全部放掉，**再**重建新位置需要的修饰键。
        // 反过来的话，ReleaseAll 会把刚刚重建好的修饰键又抬掉，留下一个没有修饰键的音。
        _backend.ReleaseAll();

        lock (_gate)
        {
            if (_plan is null) return;
            if (_state == PlaybackState.Playing) _state = PlaybackState.Paused;
            RebuildAtLocked(Math.Max(0, musicMs));
            _anchorWallMs = _clock.NowMs;
        }
        RaiseState();
    }

    /// <summary>播放中改速度：在当前位置重新锚定，位置不跳。</summary>
    public void SetSpeed(double speed)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        lock (_gate)
        {
            if (_state == PlaybackState.Playing) _anchorMusicMs = CurrentPositionLocked();
            Speed = speed;
            _anchorWallMs = _clock.NowMs;
        }
    }

    // ---------------------------------------------------------------- 推进（可被测试直接调用）

    /// <summary>
    /// 把调度推进到指定的**墙钟**毫秒（相对播放开始）。返回本次派发的事件数。
    /// 真实运行时由后台线程调用；测试里用假时钟直接调用，因此不需要任何 Sleep 就能断言时序。
    /// </summary>
    public int AdvanceToWallMs(double wallMs)
    {
        int dispatched = 0;
        bool finishedNow = false;
        lock (_gate)
        {
            if (_plan is null || _state != PlaybackState.Playing) return 0;

            // 位置只由「锚点 + 已经过去的墙钟时间」推导，**不累加**。
            // 这里绝不能顺手改锚点：一改，同一个 wallMs 再算一次就会得出不同的位置，
            // 调用方按「绝对时刻」逐点推进时（测试就是这种用法）事件会提前漏出去。
            double musicNow = _anchorMusicMs + (wallMs - _anchorWallMs) * Speed;
            var events = _plan.Events;
            var sink = TimingSink;

            while (_index < events.Count && events[_index].TimeMs <= musicNow)
            {
                var e = events[_index];
                // 诊断用：记录「注入之前」的单调时钟时刻。只读时钟，不参与任何判定。
                double actualMs = _clock.NowMs;
                int index = _index;

                Diagnostics?.OnDispatch(index, e.TimeMs, actualMs, e.Input.Label, e.IsDown, e.Pitch);

                // 先记账再派发：万一派发抛异常，_index 已经越过这一条，
                // 收尾诊断里报出的「已派发数量」才和真实进度一致。
                TryDispatchLocked(e, actualMs);

                _index++;
                dispatched++;
                _dispatchedTotal++;
                sink?.OnDispatch(new DispatchTiming(e.TimeMs, actualMs, e.Input, e.IsDown));
            }

            // 播完最后一条事件就自然结束（位置落在末尾），不必等用户点停止。
            if (events.Count > 0 && _index >= events.Count && musicNow >= events[^1].TimeMs)
            {
                _anchorMusicMs = events[^1].TimeMs;
                _state = PlaybackState.Idle;
                _workerStop = true;
                finishedNow = true;
            }
        }

        if (finishedNow)
        {
            Exception? releaseError = null;
            try { _backend.ReleaseAll(); }
            catch (Exception ex) { releaseError = ex; }
            Diagnostics?.OnReleaseAll(releaseError);
            Diagnostics?.OnPlaybackStopped(
                releaseError is null ? "natural end" : $"natural end, release-all failed: {releaseError.Message}",
                _dispatchedTotal);
            RaiseState();
        }
        return dispatched;
    }

    private void TryDispatchLocked(InputEvent e, double actualMs)
    {
        try
        {
            switch (e.Type)
            {
                case InputActionType.KeyDown: _backend.KeyDown(e.Input); break;
                case InputActionType.KeyUp: _backend.KeyUp(e.Input); break;
                case InputActionType.MouseDown: _backend.MouseDown(e.Input); break;
                case InputActionType.MouseUp: _backend.MouseUp(e.Input); break;
            }
        }
        catch (Exception ex)
        {
            // 后端拒绝（例如重复按下）：先保证不留残留输入，再把问题抛给上层。
            //
            // 注意这里以前是「静默收尾」：状态被设成 Idle，UI 就报「播放结束」，
            // 真正的原因完全看不到。现在把它记进 _lastDispatchFailure、抛给诊断层、
            // 并把 ReleaseAll 的结果也报出来 —— 出事时一定要留下证据。
            _workerStop = true;
            _state = PlaybackState.Idle;

            Exception? releaseError = null;
            try { _backend.ReleaseAll(); }
            catch (Exception releaseEx) { releaseError = releaseEx; }

            string description =
                $"{e.TypeLabel} {e.Input.Label} @ {Music.TimeLabel(e.TimeMs)}（note {e.NoteIndex}）" +
                $" 失败：{ex.GetType().Name}: {ex.Message}" +
                (releaseError is null ? "" : $"；随后的 release-all 也失败：{releaseError.Message}");
            _lastDispatchFailure = description;

            Diagnostics?.OnReleaseAll(releaseError);
            Diagnostics?.OnDispatchFailure(_index, e.TimeMs, e.Input.Label, e.IsDown, ex);
            Diagnostics?.OnPlaybackStopped("dispatch failure: " + description, _dispatchedTotal);

            throw new InvalidOperationException($"派发事件失败：{description}", ex);
        }

        EventDispatched?.Invoke(e);
    }

    // ---------------------------------------------------------------- resume / seek 的落点重建

    /// <summary>
    /// 把游标放到「不早于 musicMs 的下一颗可演奏音」的起点，
    /// 并把该音需要的修饰键与音键按正确顺序直接按下（不依赖计划里更早的事件）。
    /// 找不到可演奏音时游标落到计划末尾。
    /// </summary>
    private void RebuildAtLocked(double musicMs)
    {
        var plan = _plan;
        if (plan is null) return;

        // 落点如果落在某一颗音中间，就顺延到它结束之后：绝不从半截音开始。
        double resumeAt = musicMs;
        foreach (var n in plan.Notes)
        {
            if (!n.Playable) continue;
            if (n.StartMs < musicMs - 1e-9 && musicMs < n.EndMs - 1e-9)
            {
                resumeAt = n.EndMs;
                break;
            }
        }
        _anchorMusicMs = resumeAt;

        var events = plan.Events;
        while (_index < events.Count && events[_index].TimeMs < resumeAt - 1e-9) _index++;

        var target = plan.Notes.FirstOrDefault(n => n.Playable && n.StartMs >= resumeAt - 1e-9);
        if (target is null) return;   // 后面没有音了，收尾事件由 Advance 派发

        // 从这一颗音的起点重建输入状态：先修饰键，再音键。
        // 同时把它在计划里的对应事件跳过，否则下一轮推进会把同一对输入再发一遍。
        foreach (var m in target.Modifiers) _backend.MouseDown(m);
        _backend.KeyDown(InputId.Key(target.Key));

        var key = InputId.Key(target.Key);
        while (_index < events.Count && events[_index].TimeMs <= target.StartMs + 1e-9)
        {
            var e = events[_index];
            bool isThisNoteMod = e.NoteIndex == target.Index && e.IsMouseEvent && e.IsDown;
            bool isThisNoteKeyDown = e.Type == InputActionType.KeyDown && e.Input == key && e.NoteIndex == target.Index;
            if (!isThisNoteMod && !isThisNoteKeyDown) break;
            _index++;
        }
    }

    // ---------------------------------------------------------------- 后台线程

    private void StartWorkerLocked()
    {
        if (!SchedulerEnabled) return;
        if (_worker is { IsAlive: true }) return;
        _workerStop = false;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "DeltaMusePlayer.Scheduler",
            Priority = ThreadPriority.AboveNormal,
        };
        _worker.Start();
    }

    private void StopWorker()
    {
        Thread? w;
        lock (_gate)
        {
            _workerStop = true;
            w = _worker;
            _worker = null;
        }
        _wake.Set();
        if (w is { IsAlive: true }) w.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// 后台调度线程。
    ///
    /// 等待策略分两段，目的是**同时**保住精度与 CPU：
    ///   * 距下一个 deadline 还远：整段睡过去（`_wake.WaitOne`），CPU 几乎为零；
    ///   * 进入精等窗口：改用 `Thread.Yield`，把抖动压到亚毫秒级。
    ///
    /// 关键细节：Windows 的默认时钟粒度约 15.6ms，`Thread.Sleep(1)` / `WaitOne(1)`
    /// 往往会真的睡 16ms。「还剩多少才该睡」必须把这段**实测的 overshoot** 算进去，
    /// 否则最后一次粗等会整整睡过头一个粒度。
    ///
    /// 这里**不是**累加时间：每次醒来都重新读单调时钟（<see cref="IPlaybackClock"/>），
    /// 位置仍由「锚点 + 已过去的时间」推导。
    /// </summary>
    private void WorkerLoop()
    {
        double sleepOvershootMs = 0;

        while (!_workerStop)
        {
            double now;
            try
            {
                now = _clock.NowMs;
                AdvanceToWallMs(now);
            }
            catch
            {
                // 异常已经在 Advance 里做过 ReleaseAll；这里只负责退出循环，
                // 异常通过 EventDispatched / StateChanged 的订阅方与日志呈现。
                _workerStop = true;
                break;
            }

            if (_workerStop) break;

            double nextDeadline = NextDeadlineWallMs();
            if (double.IsPositiveInfinity(nextDeadline))
            {
                _wake.WaitOne(20);
                continue;
            }

            double before = _clock.NowMs;
            double remaining = nextDeadline - _tickMs - before;

            if (remaining > _spinMs + sleepOvershootMs)
            {
                int wait = (int)Math.Clamp(remaining - _spinMs - sleepOvershootMs, 1, 50);
                _wake.WaitOne(wait);

                double overshoot = _clock.NowMs - before - wait;
                sleepOvershootMs = Math.Max(overshoot, sleepOvershootMs * 0.9);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    /// <summary>下一条待发事件的墙钟时刻；已经发完或没在播放时返回 +∞。</summary>
    private double NextDeadlineWallMs()
    {
        lock (_gate)
        {
            if (_plan is null || _state != PlaybackState.Playing) return double.PositiveInfinity;
            if (_index >= _plan.Events.Count) return double.PositiveInfinity;

            // 事件时刻是音乐域，换回墙钟才好和 _clock.NowMs 比。
            return _anchorWallMs + (_plan.Events[_index].TimeMs - _anchorMusicMs) / Speed;
        }
    }

    private double CurrentPositionLocked()
    {
        if (_state != PlaybackState.Playing) return _anchorMusicMs;
        return _anchorMusicMs + (_clock.NowMs - _anchorWallMs) * Speed;
    }

    private void RaiseState() => StateChanged?.Invoke(State);

    public void Dispose()
    {
        StopWorker();
        try { _backend.ReleaseAll(); } catch { }
        _wake.Dispose();
        _backend.Dispose();
    }
}
