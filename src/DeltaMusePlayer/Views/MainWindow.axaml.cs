using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Input.Win32;
using DeltaMusePlayer.Playback;
using DeltaMusePlayer.Profiles;

namespace DeltaMusePlayer.Views;

/// <summary>
/// 界面：Preview / Real Input 两种模式。
///
/// 硬要求：
///   * 每次启动都默认 <b>Preview</b>，不记住上次选择（有意为之的安全设计）。
///   * 本次运行第一次切到 Real Input 时弹一次风险确认，确认后本次会话不再弹。
///   * Real Input 播放在倒计时结束后才真正开始，position=0 从那一刻算起。
///   * 播放中显示醒目但不闪烁的 REAL INPUT ACTIVE 状态。
///   * F9 / 紧急停止按钮 / 关窗：一律 stop + release-all。
///
/// 两种模式共用同一份 <see cref="PlaybackPlan"/> 与同一个 <see cref="PlaybackEngine"/>，
/// 差别只有注入时用的 <see cref="IInputBackend"/>。
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly NoteSession _session = new(AppLog.CreateWithFile());
    private readonly DispatcherTimer _uiTimer;

    private TraceInputBackend? _trace;
    private WindowsInputBackend? _windows;
    private PlaybackEngine? _engine;
    private int _traceCursor;

    /// <summary>trace 视图的起始偏移基准（首条样本的误差），用来把逐条误差还原成真正的抖动。</summary>
    private double _traceBaselineMs;
    private bool _traceBaselineTaken;
    private readonly StringBuilder _traceText = new();

    private readonly DispatchTimingStats _timingStats = new();
    private SchedulerLog? _schedulerLog;
    private EmergencyHotkey? _emergencyHotkey;
    private bool _realInputConsentGiven;
    private bool _isRealInputActive;

    /// <summary>倒计时会话令牌：每次点播放自增；换计划 / 停止会作废当前会话。</summary>
    private int _countdownToken;

    /// <summary>明确的「倒计时取消」标记，只由 Stop / EmergencyStop / 换计划设置。</summary>
    private bool _countdownCancelled;

    private readonly double[] _speeds = { 0.5, 0.75, 1.0, 1.25, 1.5 };
    private readonly double[] _countdowns = { 0, 1, 2, 3, 5, 10 };

    public MainWindow()
    {
        InitializeComponent();

        DataContext = this;
        InitializeSelections();

        ProfilePath = InstrumentProfileLoader.FindDefaultProfilePath() ?? "(内置默认键位)";
        _session.LoadProfile(null);
        ApplyTimingPreset();
        ValidateSummary = "";

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += (_, _) => TickUi();
        _uiTimer.Start();

        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            EmergencyStop();
            DisposeEmergencyHotkey();   // 关窗才归还这个系统组合键
        };
        StartEmergencyHotkey();

        StatusText = "就绪。默认 Preview / Trace 模式：不会发送任何真实输入。";
        WarningText = "";
        UpdateModeBanner();
    }

    // ---------------------------------------------------------------- 绑定属性

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _midiPath = "";
    public string MidiPath { get => _midiPath; set { _midiPath = value; Raise(); } }

    private string _profilePath = "";
    public string ProfilePath { get => _profilePath; set { _profilePath = value; Raise(); } }

    private string _statusText = "";
    public string StatusText { get => _statusText; set { _statusText = value; Raise(); } }

    private string _clockText = "00:00.000 / 00:00.000";
    public string ClockText { get => _clockText; set { _clockText = value; Raise(); } }

    private string _currentNoteText = "—";
    public string CurrentNoteText { get => _currentNoteText; set { _currentNoteText = value; Raise(); } }

    private string _currentBindingText = "—";
    public string CurrentBindingText { get => _currentBindingText; set { _currentBindingText = value; Raise(); } }

    private string _heldInputsText = "—";
    public string HeldInputsText { get => _heldInputsText; set { _heldInputsText = value; Raise(); } }

    private string _nextNoteText = "";
    public string NextNoteText { get => _nextNoteText; set { _nextNoteText = value; Raise(); } }

    private string _traceView = "";
    public string TraceText { get => _traceView; set { _traceView = value; Raise(); } }

    private string _warningText = "";
    public string WarningText { get => _warningText; set { _warningText = value; Raise(); } }

    private string _songSummary = "";
    public string SongSummary { get => _songSummary; set { _songSummary = value; Raise(); } }

    private string _planSummary = "";
    public string PlanSummary { get => _planSummary; set { _planSummary = value; Raise(); } }

    private string _validateSummary = "";
    public string ValidateSummary { get => _validateSummary; set { _validateSummary = value; Raise(); } }

    // 刻意避开与 XAML 生成字段同名的属性（控件叫 TimingText / UnplayableSummary / PrivilegeHint）
    private string _timingStatsText = "";
    public string TimingStatsText { get => _timingStatsText; set { _timingStatsText = value; Raise(); } }

    private string _unplayableSummaryText = "";
    public string UnplayableSummaryText { get => _unplayableSummaryText; set { _unplayableSummaryText = value; Raise(); } }

    private string _privilegeHintText = "";
    public string PrivilegeHintText { get => _privilegeHintText; set { _privilegeHintText = value; Raise(); } }

    private string _backendText = "";
    public string BackendText { get => _backendText; set { _backendText = value; Raise(); } }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set { _progressValue = value; Raise(); } }

    /// <summary>下拉框初值必须在控件树建好之后设，否则 SelectionChanged 会打到未初始化的字段上。</summary>
    private void InitializeSelections()
    {
        SpeedBox.SelectedIndex = 2;        // 1.0x
        CountdownBox.SelectedIndex = 3;    // 3 秒（需求默认值）
        TimingBox.SelectedIndex = 0;       // 默认档
        PreviewMode.IsChecked = true;      // 每次启动都默认 Preview
        RealInputMode.IsChecked = false;
    }

    // ---------------------------------------------------------------- 模式

    public bool IsRealInputMode => RealInputMode.IsChecked == true;

    /// <summary>防止「程序把单选框改回去」再次触发处理逻辑。</summary>
    private bool _suppressModeChanged;

    private async void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressModeChanged) return;

        if (!IsRealInputMode)
        {
            StopPlayback();
            UpdateModeBanner();
            StatusText = "Preview / Trace 模式：不会发送任何真实输入。";
            return;
        }

        // 本次运行第一次切到 Real Input：弹一次风险确认
        if (!_realInputConsentGiven)
        {
            bool ok;
            try
            {
                ok = await RealInputConsentDialog.ConfirmAsync(this);
            }
            catch (Exception ex)
            {
                _session.Log.Error("Real Input 确认框失败，已保持 Preview", ex);
                ok = false;
            }

            if (!ok)
            {
                _suppressModeChanged = true;
                PreviewMode.IsChecked = true;
                _suppressModeChanged = false;
                UpdateModeBanner();
                StatusText = "已取消：仍停留在 Preview / Trace 模式，不会发送真实输入。";
                return;
            }

            _realInputConsentGiven = true;
            _session.Log.Info("用户已确认 Real Input 风险提示（本次会话不再重复提示）。");
        }

        StopPlayback();
        RebuildPlan();          // 换后端：Real Input 用 WindowsInputBackend
        UpdateModeBanner();

        // 权限提示（需求 19）：只提示，不自动提权
        PrivilegeHintText =
            "如果目标程序以更高权限运行，Windows UIPI 可能拦下合成输入。\n" +
            "此时请关闭 DeltaMuse Player，手动「以管理员身份运行」——程序不会自动提权。";

        StatusText = "Real Input 模式：播放时会向系统发送真实键鼠事件（按播放后有倒计时）。";
    }

    private void UpdateModeBanner()
    {
        bool real = IsRealInputMode;
        ModeBanner.Background = Avalonia.Media.Brush.Parse(real ? "#FFE0E0" : "#FFF4E5");
        ModeBanner.BorderBrush = Avalonia.Media.Brush.Parse(real ? "#C00000" : "#F0C36D");

        ModeBannerTitle.Text = real
            ? "Real Input 模式已选中：播放时会发送真实键鼠事件。"
            : "Preview / Trace 模式：不会发送任何真实键鼠输入。";
        ModeBannerDetail.Text = real
            ? "Automation in online games may violate game rules or result in account penalties. " +
              "按播放后有倒计时，请在它结束前切到目标窗口；按 F9 可立即停止并释放全部输入。"
            : "Preview 显示的 trace 就是 Real Input 会发出的那串输入；两者走同一份 PlaybackPlan 与同一个调度器，只有输入后端不同。";

        BackendText = real
            ? "输入后端：WindowsInputBackend（SendInput / 扫描码）"
            : "输入后端：TraceInputBackend（只记录，不发送）";
    }

    /// <summary>
    /// 启动时预载入一个 MIDI 文件（供 `DeltaMusePlayer.exe --midi &lt;file&gt;` 使用）。
    /// 只是替用户点一下「浏览」，不改变任何模式/安全默认值。
    /// </summary>
    public void LoadMidiFromArguments(string path)
    {
        MidiPath = path;
        LoadMidi(path);
    }

    // ---------------------------------------------------------------- 文件选择

    private async void OnBrowseMidi(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 DeltaMuse 导出的 MIDI",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("MIDI") { Patterns = new[] { "*.mid", "*.midi", "*.kar", "*.rmi" } },
            },
        });

        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        MidiPath = path;
        LoadMidi(path);
    }

    private async void OnBrowseProfile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择键位方案 JSON",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
        });

        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        ProfilePath = path;
        try
        {
            _session.LoadProfile(path);
            RebuildPlan();
            StatusText = $"已切换键位方案：{_session.Profile!.Name}";
        }
        catch (Exception ex)
        {
            StatusText = "键位方案载入失败：" + ex.Message;
        }
    }

    private void LoadMidi(string path)
    {
        StopPlayback();
        try
        {
            var song = _session.LoadMidi(path);
            // 换文件就重置选段：上一个文件的 mm:ss 片段套到新文件上毫无意义。
            ResetRangeToWholeSong();
            TrackList.ItemsSource = song.Tracks.Select(t => t.Describe()).ToList();
            TrackList.SelectedIndex = song.SuggestMelodyTrack()?.Index ?? -1;
            SongSummary =
                $"格式 {song.Format}；{song.Tracks.Count} 轨；" +
                $"音符 {song.Tracks.Sum(t => t.NoteCount)}；时长 {Music.TimeLabel(song.DurationSeconds * 1000)}";
            RebuildPlan();
        }
        catch (Exception ex)
        {
            StatusText = "载入失败：" + ex.Message;
            _session.Log.Error("载入 MIDI 失败", ex);
        }
    }

    private void OnTrackSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (TrackList.SelectedIndex < 0 || _session.Song is null) return;
        StopPlayback();
        _session.SelectTrack(TrackList.SelectedIndex);
        RebuildPlan();
    }

    // ---------------------------------------------------------------- 时序档位 / 速度

    private void OnTimingChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyTimingPreset();
        RebuildPlan();
    }

    private void ApplyTimingPreset()
    {
        var preset = TimingBox.SelectedIndex switch
        {
            1 => PlaybackConfig.Safe,
            2 => PlaybackConfig.Aggressive,
            _ => PlaybackConfig.Default,
        };
        preset.CountdownSeconds = CurrentCountdown;
        // 片段范围是使用者的选择，不是档位的一部分：切档位不能把手选的片段重置掉。
        var previous = _session.Config;
        preset.RangeStartMs = previous?.RangeStartMs;
        preset.RangeEndMs = previous?.RangeEndMs;
        _session.Config = preset;
        TimingDetail.Text =
            $"修饰键提前 {preset.ModifierLeadMs:F0}ms / 最短按住 {preset.MinimumKeyHoldMs:F0}ms / " +
            $"同键重触发 {preset.SameKeyRetriggerGapMs:F0}ms（物理毫秒，不随速度缩放）";
    }

    private double CurrentCountdown => _countdowns[Math.Max(0, CountdownBox.SelectedIndex)];

    private void OnCountdownChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_session.Config is null) return;
        _session.Config.CountdownSeconds = CurrentCountdown;
    }

    private double CurrentSpeed => _speeds[Math.Max(0, SpeedBox.SelectedIndex)];

    private void OnSpeedChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MidiPath.Length == 0) return;
        RebuildPlan();
    }

    // ---------------------------------------------------------------- 演奏片段

    private string _rangeStartText = "";
    private string _rangeEndText = "";
    private string _rangeSummaryText = "";

    public string RangeStartText { get => _rangeStartText; set { _rangeStartText = value; Raise(); } }

    public string RangeEndText { get => _rangeEndText; set { _rangeEndText = value; Raise(); } }

    /// <summary>片段的一句话预览；范围非法时显示原因，而不是留一个静默的失败。</summary>
    public string RangeSummaryText { get => _rangeSummaryText; set { _rangeSummaryText = value; Raise(); } }

    /// <summary>载入文件后把选段重置为整曲，避免上一个文件的片段范围串到新文件上。</summary>
    private void ResetRangeToWholeSong()
    {
        // 先把配置清干净再动文本框：如果两个框本来就是空的，TextChanged 不会触发，
        // 光清文本框会把上一份文件的 RangeStartMs/RangeEndMs 留在 Config 里。
        _session.Config.RangeStartMs = null;
        _session.Config.RangeEndMs = null;
        RangeStartText = "";
        RangeEndText = "";
        RangeSummaryText = "整曲。要只弹一段就填起止时间。";
    }

    private void OnRangeChanged(object? sender, TextChangedEventArgs e)
    {
        if (_session.SelectedTrack is null)
        {
            RangeSummaryText = "";
            return;
        }

        string startRaw = (RangeStartBox.Text ?? "").Trim();
        string endRaw = (RangeEndBox.Text ?? "").Trim();

        // 范围不合法时**提前返回**，让上一份可用计划留在引擎里：
        // 使用者在输入框里一边打字一边触发重建，不能让半截输入把计划清空。
        if (!TryParseUserTime(startRaw, out double? start))
        {
            RangeSummaryText = $"! 起点看不懂：\"{startRaw}\"。可以写 mm:ss.mmm（如 01:23.500）、1:23 或直接写秒数。";
            return;
        }
        if (!TryParseUserTime(endRaw, out double? end))
        {
            RangeSummaryText = $"! 终点看不懂：\"{endRaw}\"。可以写 mm:ss.mmm、1:23 或直接写秒数；留空表示到曲尾。";
            return;
        }
        if (start is { } s && end is { } en && en <= s)
        {
            RangeSummaryText = $"! 终点（{Music.TimeLabel(en)}）必须晚于起点（{Music.TimeLabel(s)}）。";
            return;
        }

        var (count, first, last) = PlaybackPlanner.DescribeRange(_session.SelectedTrack.Notes, start, end);
        if (count == 0)
        {
            RangeSummaryText = "! 这一段里没有任何音符，播放会直接报错。换个大一点的范围。";
            return;
        }

        _session.Config.RangeStartMs = start;
        _session.Config.RangeEndMs = end;
        RangeSummaryText =
            $"这一段 {count} 个音；实际弹到 {Music.TimeLabel(first)} ~ {Music.TimeLabel(last)}" +
            $"（{(end is null ? "到曲尾" : "终点 " + Music.TimeLabel(end.Value))}）。" +
            "时间显示仍按源文件时间轴，不会平移。";

        RebuildPlan();
    }

    /// <summary>
    /// 解析使用者手输的时间。格式由 <see cref="TimeInput"/> 统一定义，
    /// 与 CLI 的 <c>--range</c> 是同一套，不在这里另写一份。
    /// </summary>
    private static bool TryParseUserTime(string raw, out double? ms) => TimeInput.TryParse(raw, out ms);

    /// <summary>把毫秒写成 mm:ss.mmm，用于回填输入框。</summary>
    private static string FormatUserTime(double ms) => TimeInput.Format(ms);

    private void OnRangeFull(object? sender, RoutedEventArgs e)
    {
        RangeStartBox.Text = "";
        RangeEndBox.Text = "";
    }

    private void OnRangeFirst30(object? sender, RoutedEventArgs e)
    {
        if (_session.Song is null) return;
        RangeStartBox.Text = "";
        RangeEndBox.Text = FormatUserTime(30_000);
    }

    private void OnRangeLast30(object? sender, RoutedEventArgs e)
    {
        if (_session.Song is null) return;
        double total = _session.Song.DurationSeconds * 1000.0;
        RangeStartBox.Text = FormatUserTime(Math.Max(0, total - 30_000));
        RangeEndBox.Text = "";
    }

    private void OnRangePlus30(object? sender, RoutedEventArgs e)
    {
        if (_session.Song is null) return;
        double total = _session.Song.DurationSeconds * 1000.0;
        double baseMs = 0;
        if (TryParseUserTime((RangeEndBox.Text ?? "").Trim(), out double? cur) && cur is { } c) baseMs = c;
        else baseMs = total;
        RangeEndBox.Text = FormatUserTime(Math.Min(total + 30_000, baseMs + 30_000));
    }

    private void OnRangeFromPosition(object? sender, RoutedEventArgs e)
    {
        if (_session.Song is null) return;
        double pos = _engine?.PositionWallMs ?? 0;
        RangeStartBox.Text = FormatUserTime(pos);
    }

    // ---------------------------------------------------------------- 编译

    private void RebuildPlan()
    {
        if (_session.SelectedTrack is null) return;
        StopPlayback();

        try
        {
            _session.Config.CountdownSeconds = CurrentCountdown;
            var plan = _session.Compile(CurrentSpeed);

            _traceText.Clear();
            _traceCursor = 0;
            _traceBaselineTaken = false;
            _traceBaselineMs = 0;
            TraceText = "";
            PlanSummary = $"音符 {plan.Report.NoteCount}（可演奏 {plan.Report.PlayableCount} / " +
                          $"不可演奏 {plan.Report.UnplayableCount}）\n" +
                          $"事件 {plan.EventCount}\n" +
                          $"总时长 {Music.TimeLabel(plan.WallTotalMs)}\n" +
                          $"Playable {plan.Mapping.PlayablePercentLabel}\n" +
                          $"{plan.Report.Summary()}";
            WarningText = plan.Warnings.Count == 0 ? "" : string.Join("\n", plan.Warnings.Select(w => "! " + w));
            ValidateSummary = BuildValidateSummary();
            BuildUnplayableList(plan);
            StatusText = $"已编译播放计划：{plan.EventCount} 个事件，总时长 {Music.TimeLabel(plan.WallTotalMs)}。";

            BuildEngine(plan);
        }
        catch (Exception ex)
        {
            PlanSummary = "";
            StatusText = "编译失败：" + ex.Message;
            _session.Log.Error("编译播放计划失败", ex);
        }
    }

    /// <summary>按当前模式建对应后端 + 引擎。两种模式只有后端不同。</summary>
    private void BuildEngine(PlaybackPlan plan)
    {
        DisposeEngine();
        _timingStats.Clear();

        if (IsRealInputMode)
        {
            _windows = new WindowsInputBackend
            {
                TimingSink = _timingStats,
                Warn = m =>
                {
                    _session.Log.Warn(m);
                    Dispatcher.UIThread.Post(() => WarningText = "! " + m);
                },
            };
            _engine = new PlaybackEngine(_windows, new StopwatchClock(), pollMs: 1.0);
            PrivilegeHintText =
                "如果目标程序以更高权限运行，Windows UIPI 可能拦下合成输入。\n" +
                "此时请关闭 DeltaMuse Player，手动「以管理员身份运行」——程序不会自动提权。";
        }
        else
        {
            _trace = new TraceInputBackend();
            _engine = new PlaybackEngine(_trace, new StopwatchClock(), pollMs: 2.0);
            PrivilegeHintText = "";
        }

        // 真实播放挂上诊断：计划概况 / 失败原因 / 释放结果都会进 play.log。
        // 逐条派发默认只记前若干条，避免正常播放把日志刷爆。
        _schedulerLog = new SchedulerLog(_session.Log, verboseDispatch: DiagnoseMode);
        _engine.Diagnostics = _schedulerLog;

        _engine.Load(plan, CurrentSpeed);
    }

    private void DisposeEngine()
    {
        try { _engine?.Dispose(); } catch { /* 关闭时的清理失败不再往上抛 */ }
        _engine = null;
        _windows = null;
        _trace = null;
    }

    private string BuildValidateSummary()
    {
        if (_session.SelectedTrack is null || _session.Mapper is null) return "";
        var mapper = _session.Mapper;
        var distinct = _session.SelectedTrack.Notes.Select(n => n.Pitch).Distinct().OrderBy(p => p).ToList();
        if (distinct.Count == 0) return "这条轨上没有音符。";

        var bad = distinct.Where(p => !mapper.IsPlayable(p)).ToList();
        var sb = new StringBuilder();
        sb.Append($"方案 {mapper.Profile.Name}：可演奏音域 ")
          .Append(Music.SolfegeRange(mapper.MinSupportedPitch, mapper.MaxSupportedPitch))
          .Append("；这条轨用了 ").Append(distinct.Count).Append(" 个不同音高");
        sb.Append(bad.Count == 0
            ? "，全部在方案内。"
            : $"；其中 {bad.Count} 个弹不出来：" + string.Join(" ", bad.Select(p => Music.NoteName(p))));
        sb.Append("\n只统计，不修改 MIDI。");
        return sb.ToString();
    }

    /// <summary>不可演奏音清单：时间 / 音名 / 简谱 / 原因。只读，不做编辑。</summary>
    private void BuildUnplayableList(PlaybackPlan plan)
    {
        var bad = plan.Notes.Where(n => !n.Playable).ToList();
        UnplayableList.ItemsSource = bad
            .Select(n => $"{Music.TimeLabel(n.StartMs)}  {Music.NoteName(n.Pitch),-4} {Music.SolfegeName(n.Pitch),-4}  {n.SkipReason}")
            .ToList();
        UnplayableSummaryText = bad.Count == 0
            ? "没有弹不出来的音。"
            : $"共 {bad.Count} 个音弹不出来（已跳过，不改变音高）。";
    }

    // ---------------------------------------------------------------- 播放控制

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        // 点击播放这条路径逐步记日志：一旦出现「点了没反应 / 立刻结束」，
        // 日志里能直接看出卡在哪一步，不必再靠猜。
        _session.Config.CountdownSeconds = CurrentCountdown;
        _session.Log.Info(
            $"点击播放：模式={(IsRealInputMode ? "Real Input" : "Preview")}，" +
            $"计划事件 {_engine?.Plan?.EventCount.ToString() ?? "无计划"} 条，" +
            $"速度 {CurrentSpeed:0.##}x，倒计时 {CurrentCountdown:0}s，" +
            $"引擎状态 {_engine?.State.ToString() ?? "无引擎"}。");

        if (_engine?.Plan is null)
        {
            StatusText = "还没有可播放的计划，先载入 MIDI。";
            return;
        }

        if (!IsRealInputMode)
        {
            // Preview：不需要倒计时
            try
            {
                _engine.SeekMusicMs(0);
                _engine.Play();
                PauseButton.IsEnabled = true;
                StopButton.IsEnabled = true;
                StatusText = "Preview 播放中（不发送任何真实输入）。";
            }
            catch (Exception ex)
            {
                StatusText = "播放失败：" + ex.Message;
                EmergencyStop();
            }
            return;
        }

        await RunCountdownAndPlayAsync();
    }

    private async Task RunCountdownAndPlayAsync()
    {
        var engine = _engine!;
        var plan = engine.Plan!;

        engine.SeekMusicMs(0);
        _session.Log.Info($"倒计时前就位：Seek(0) 完成，引擎状态 {engine.State}，" +
                          $"倒计时 {CurrentCountdown:0}s，首事件 {plan.Events.FirstOrDefault()?.ToTraceLine() ?? "(无)"}。");

        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _traceText.Clear();
        _traceCursor = 0;
        _traceBaselineTaken = false;
        _traceBaselineMs = 0;
        TraceText = "";
        _timingStats.Clear();

        // 倒计时是否被打断，必须由**明确的取消动作**决定（换计划 / 停止 / 紧急停止 / 关窗），
        // 绝不能用 StopButton.IsEnabled 当信号 —— 那个按钮的可用性会被别的路径改掉
        // （RebuildPlan 里的 StopPlayback 就会把它置 false），
        // 结果就是「点了播放、倒计时刚开始就被判为已取消」，什么都播不了。
        int token = ++_countdownToken;
        _countdownCancelled = false;

        int seconds = (int)Math.Round(CurrentCountdown);
        for (int s = seconds; s >= 1; s--)
        {
            CountdownText.Text = $"Starting in {s}...";
            StatusText = $"Real Input：{s} 秒后开始，请切到目标窗口。";
            await Task.Delay(1000);

            if (_countdownCancelled || token != _countdownToken || !ReferenceEquals(_engine, engine))
            {
                CountdownText.Text = "";
                _session.Log.Warn(
                    $"倒计时被中止（还剩 {s} 秒）：取消标记={_countdownCancelled}，" +
                    $"令牌={token}/{_countdownToken}，引擎是否还是同一个={ReferenceEquals(_engine, engine)}。");
                return;
            }
        }

        CountdownText.Text = "";
        try
        {
            engine.Play();
            _session.Log.Info($"倒计时结束，已调用 Play()：引擎状态 {engine.State}。" +
                              (engine.LastDispatchFailure is null ? "" : $" 派发失败：{engine.LastDispatchFailure}"));

            // Play 之后必须真的处于 Playing；否则说明计划是空的或已经跑完，
            // 直接说出来，不要等 TickUi 报一句含糊的「播放结束」。
            if (engine.State != PlaybackState.Playing)
            {
                RealInputBanner.IsVisible = false;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                StatusText = $"没有开始播放：计划事件 {plan.EventCount} 条，引擎状态 {engine.State}。";
                _session.Log.Warn($"Play 之后引擎状态是 {engine.State}，计划事件 {plan.EventCount} 条。");
                return;
            }

            _isRealInputActive = true;
            RealInputBanner.IsVisible = true;
            PauseButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            StatusText = "REAL INPUT ACTIVE — 正在发送真实键鼠事件（F9 紧急停止）。";
            _session.Log.Info($"Real Input 播放开始：{plan.EventCount} 个事件，速度 {CurrentSpeed:0.##}x。");
        }
        catch (Exception ex)
        {
            StatusText = "Real Input 播放失败：" + ex.Message;
            _session.Log.Error("Real Input 播放失败", ex);
            EmergencyStop();
        }
    }

    private void OnPause(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (_engine.State == PlaybackState.Playing)
        {
            _engine.Pause();
            _isRealInputActive = false;
            RealInputBanner.IsVisible = false;
            StatusText = $"已暂停在 {Music.TimeLabel(_engine.PositionWallMs)}（已释放全部输入）。";
        }
        else if (_engine.State == PlaybackState.Paused)
        {
            _engine.Resume();
            _isRealInputActive = IsRealInputMode;
            RealInputBanner.IsVisible = IsRealInputMode;
            StatusText = IsRealInputMode
                ? "REAL INPUT ACTIVE — 已从当前位置继续（修饰键已按需重建）。"
                : "已继续。";
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        StatusText = "已停止：位置归零，全部输入已释放。";
    }

    private void StopPlayback()
    {
        try
        {
            _engine?.Stop();
        }
        catch (Exception ex)
        {
            _session.Log.Error("停止时出错（已尝试 release-all）", ex);
        }
        _isRealInputActive = false;
        _countdownCancelled = true;      // 作废正在跑的倒计时
        _countdownToken++;
        CountdownText.Text = "";
        RealInputBanner.IsVisible = false;
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
    }

    /// <summary>紧急停止：任何情况下都必须把已知按下的输入放开。</summary>
    public void EmergencyStop()
    {
        try
        {
            _engine?.Stop();
        }
        catch (Exception ex)
        {
            _session.Log.Error("紧急停止时出错", ex);
        }

        try { _windows?.ReleaseAll(); } catch (Exception ex) { _session.Log.Error("紧急停止释放后端失败", ex); }
        try { _trace?.ReleaseAll(); } catch { /* Trace 没有真实输入 */ }

        // 注意：这里**不**释放全局热键。
        // 热键要活到窗口关闭为止 —— 否则按一次 F9 之后它就没了，第二次按下去毫无反应，
        // 而「紧急停止只能用一次」比没有紧急停止更危险。
        // 真正的释放放在 DisposeEmergencyHotkey()，由 Closing / 关窗路径调用。

        _isRealInputActive = false;
        _countdownCancelled = true;
        _countdownToken++;
        CountdownText.Text = "";
        RealInputBanner.IsVisible = false;
        StatusText = "紧急停止：已释放全部输入。";
        PauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
    }

    /// <summary>
    /// 建立系统级紧急停止热键（默认 F9）。
    ///
    /// 有了它，切到目标窗口之后 F9 仍然有效 —— 这一点很关键：窗口级的 KeyDown 只在
    /// 本窗口是焦点时才有反应，而真实用法正是「倒计时里切走」，那时窗口级快捷键是聋的。
    ///
    /// 注册可能失败（组合键被别的程序占了），这时只降级、不报错：
    /// 状态栏与提示文字会改成「仅窗口内有效」，让使用者知道真实情况。
    /// </summary>
    private void StartEmergencyHotkey()
    {
        try
        {
            _emergencyHotkey = new EmergencyHotkey(new WindowsGlobalHotkeyApi(), OnHotkeyTriggered);
            _emergencyHotkey.Start();

            if (_emergencyHotkey.IsActive)
            {
                _session.Log.Info($"全局紧急停止热键已注册：{_emergencyHotkey.Label}（系统级热键，非键盘钩子）");
                EmergencyButton.Content = $"紧急停止（{_emergencyHotkey.Label}，全局有效）";
                EmergencyHintText = $"按 {_emergencyHotkey.Label} 立即停止并释放全部输入 —— " +
                                    "切到目标窗口后依然有效（系统级热键注册，不是键盘钩子）。";
            }
            else
            {
                _session.Log.Warn(
                    $"全局紧急停止热键注册失败：{_emergencyHotkey.FailureReason}。" +
                    "已降级为「仅本窗口为焦点时有效」。");
                EmergencyButton.Content = "紧急停止（F9，仅本窗口）";
                EmergencyHintText =
                    $"全局热键注册失败（{_emergencyHotkey.FailureReason}），F9 现在只在" +
                    "本窗口是焦点时有效。切到目标窗口后请用鼠标点本窗口的「紧急停止」按钮，或先切回来按 F9。";
            }
        }
        catch (Exception ex)
        {
            // 热键只是保险，起不来也不能让程序启动不了。
            _session.Log.Error("建立全局紧急停止热键时出错（已继续启动）", ex);
            EmergencyHintText = "全局热键初始化失败，F9 仅在本窗口为焦点时有效。";
        }
    }

    /// <summary>
    /// 热键回调。它在**热键自己的后台线程**上被调用，而 <see cref="EmergencyStop"/>
    /// 会碰 Avalonia 控件，所以必须切回 UI 线程。
    /// </summary>
    private void OnHotkeyTriggered()
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) EmergencyStop();
            else Dispatcher.UIThread.Post(EmergencyStop, DispatcherPriority.Send);
        }
        catch { /* 关窗竞态：窗口已经没了，没什么可停的 */ }
    }

    private string _emergencyHintText = "按 F9 立即停止并释放全部输入。";

    /// <summary>紧急停止的提示文字；会按全局热键是否注册成功改成实话。</summary>
    public string EmergencyHintText { get => _emergencyHintText; set { _emergencyHintText = value; Raise(); } }

    /// <summary>
    /// 释放全局热键占用的系统组合键。只在关窗时调用一次 ——
    /// 想停播放请用 <see cref="EmergencyStop"/>，它会保留热键以便反复使用。
    /// </summary>
    public void DisposeEmergencyHotkey()
    {
        try
        {
            _emergencyHotkey?.Dispose();
            _emergencyHotkey = null;
        }
        catch (Exception ex)
        {
            _session.Log.Error("释放全局热键失败", ex);
        }
    }

    private void OnEmergencyStop(object? sender, RoutedEventArgs e) => EmergencyStop();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // F9 优先于普通 UI 操作：先处理并吞掉事件。
        if (e.Key == Key.F9)
        {
            EmergencyStop();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- UI 心跳

    private void TickUi()
    {
        var engine = _engine;
        var plan = engine?.Plan;
        if (engine is null || plan is null) return;

        double progressWall = engine.PositionWallMs;
        ClockText = $"{Music.TimeLabel(progressWall)} / {Music.TimeLabel(plan.WallTotalMs)}";
        ProgressValue = plan.WallTotalMs <= 0 ? 0 : Math.Clamp(progressWall / plan.WallTotalMs, 0, 1);

        var current = plan.NoteAtWallMs(progressWall);
        if (current is not null)
        {
            CurrentNoteText = $"{Music.NoteName(current.Pitch)} ({Music.SolfegeName(current.Pitch)})";
            var mods = current.Modifiers.Select(m => m.Label).ToList();
            CurrentBindingText = mods.Count == 0 ? current.Key : $"{string.Join(" + ", mods)} + {current.Key}";
        }
        else if (engine.State == PlaybackState.Playing)
        {
            CurrentNoteText = "—";
            CurrentBindingText = "—";
        }

        var held = CurrentHeldInputs();
        HeldInputsText = held.Count == 0 ? "无" : string.Join(" ", held.Select(h => h.Label));

        var next = plan.NextNoteAfterWallMs(progressWall);
        NextNoteText = next is null
            ? "后面没有音了"
            : $"下一个：{Music.TimeLabel(next.StartMs)} {Music.NoteName(next.Pitch)} → {next.Key}" +
              (next.Modifiers.Count == 0 ? "" : $"（{string.Join(" + ", next.Modifiers.Select(m => m.Label))}）");

        TimingStatsText = _timingStats.Count == 0 ? "" : _timingStats.RenderSummary();

        // 只把新增的记录追加到 trace 视图
        if (_windows is not null)
        {
            int n = _timingStats.Count;
            if (_traceCursor < n)
            {
                var fresh = _timingStats.Samples.Skip(_traceCursor).ToList();
                // 整体起始偏移（倒计时/预滚）只会让事件更晚，所以取至今为止误差的**最小值**。
                // trace 里逐条扣掉它，否则每行都顶着「+3031ms」，看起来像时序坏了，
                // 其实只是晚开始了一点。
                if (fresh.Count > 0)
                {
                    double minRaw = fresh.Min(s => s.JitterMs);
                    _traceBaselineMs = _traceBaselineTaken
                        ? Math.Min(_traceBaselineMs, minRaw)
                        : minRaw;
                    _traceBaselineTaken = true;
                }

                foreach (var s in fresh)
                {
                    _traceText.AppendLine(
                        $"{s}  扣偏后 {s.JitterMs - _traceBaselineMs:+0.000;-0.000;0.000}ms");
                }

                _traceCursor = n;
                TraceText = _traceText.ToString();
            }
        }
        else if (_trace is not null && _traceCursor < _trace.Entries.Count)
        {
            for (int i = _traceCursor; i < _trace.Entries.Count; i++)
                _traceText.AppendLine(_trace.Entries[i].ToString());
            _traceCursor = _trace.Entries.Count;
            TraceText = _traceText.ToString();
        }

        if (engine.State == PlaybackState.Idle && StopButton.IsEnabled)
        {
            PauseButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            _isRealInputActive = false;
            RealInputBanner.IsVisible = false;

            // 关键区分：引擎的 Idle 有两种来源 —— 真的播完了，或者派发炸了被当成播完。
            // 以前两者都显示「播放结束」，用户完全看不出真实原因。
            string? failure = engine.LastDispatchFailure ?? _schedulerLog?.LastFailure;
            if (failure is not null)
            {
                StatusText = "播放中断（不是正常放完）：" + failure;
                WarningText = "! 播放中断：" + failure +
                              $"\n（已派发 {LiveDispatchedCount()} / {plan.EventCount} 条；详见 play.log）";
                _session.Log.Error("播放中断：" + failure);
            }
            else
            {
                StatusText = $"播放结束：已派发 {LiveDispatchedCount()} 条，全部输入已释放。";
                if (plan.EventCount == 0)
                    StatusText = "播放结束：这份计划里没有任何事件（这条轨没有可演奏的音）。";
            }

            if (_timingStats.Count > 0) _session.Log.Info(_timingStats.RenderSummary());
        }
    }

    private IReadOnlyCollection<InputId> CurrentHeldInputs()
    {
        if (_windows is not null) return _windows.HeldInputs;
        if (_trace is not null) return _trace.HeldInputs;
        return Array.Empty<InputId>();
    }

    /// <summary>
    /// 已派发事件数。播放中必须问引擎（<see cref="PlaybackEngine.DispatchedCount"/> 实时更新）；
    /// <see cref="SchedulerLog.LastDispatchedCount"/> 只在停止时写，放播放中读到的是上一轮的旧值。
    /// 引擎不在时退回日志值。
    /// </summary>
    private int LiveDispatchedCount()
        => _engine?.DispatchedCount ?? _schedulerLog?.LastDispatchedCount ?? 0;

    // ---------------------------------------------------------------- 诊断自动播放（--diagnose）

    private static bool DiagnoseMode => Program.DiagnoseMode;

    /// <summary>
    /// `--diagnose` 用：不经过鼠标点击，直接按「切到 Real Input → 倒计时 → 播放」的顺序走一遍。
    /// 目的是在没有可靠 UI 自动化的环境里也能确定性地复现整条真实播放路径。
    /// </summary>
    public async Task RunDiagnoseAutoplayAsync()
    {
        _session.Log.Info("== 诊断自动播放开始 ==");

        try
        {
            if (!IsRealInputMode)
            {
                _suppressModeChanged = true;
                RealInputMode.IsChecked = true;
                _suppressModeChanged = false;
                _realInputConsentGiven = true;      // 诊断路径不弹确认框
                StopPlayback();
                RebuildPlan();
                UpdateModeBanner();
                _session.Log.Info($"诊断：已切到 Real Input，计划事件 {_engine?.Plan?.EventCount.ToString() ?? "无"} 条。");
            }

            PauseButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            await RunCountdownAndPlayAsync();

            // 观察 8 秒，把状态变化记下来
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000)
            {
                await Task.Delay(200);
                _session.Log.Info(
                    $"诊断采样 t={sw.ElapsedMilliseconds}ms 状态={_engine?.State} " +
                    $"位置={_engine?.PositionWallMs:F0}ms " +
                    $"后端按住={_windows?.HeldInputs.Count.ToString() ?? "-"} " +
                    $"派发={LiveDispatchedCount()} " +
                    $"失败={_engine?.LastDispatchFailure ?? "无"}");
            }
        }
        catch (Exception ex)
        {
            _session.Log.Error("诊断自动播放异常", ex);
        }

        _session.Log.Info("== 诊断自动播放结束 ==");
    }

    // ---------------------------------------------------------------- 导出

    private async void OnExportTrace(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 trace CSV",
            SuggestedFileName = "delta_trace.csv",
            DefaultExtension = "csv",
        });
        string? path = file?.TryGetLocalPath();
        if (path is null) return;

        if (_windows is not null)
            await File.WriteAllLinesAsync(path, _timingStats.Samples.Select(s => s.ToString()));
        else if (_trace is not null)
            await File.WriteAllTextAsync(path, TraceExporter.TraceToCsv(_trace));

        StatusText = "已导出 trace：" + path;
    }

    private async void OnExportPlanJson(object? sender, RoutedEventArgs e)
    {
        if (_engine?.Plan is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出播放计划 JSON",
            SuggestedFileName = "delta_plan.json",
            DefaultExtension = "json",
        });
        string? path = file?.TryGetLocalPath();
        if (path is null) return;
        await File.WriteAllTextAsync(path, TraceExporter.PlanToJson(_engine.Plan));
        StatusText = "已导出播放计划：" + path;
    }
}
