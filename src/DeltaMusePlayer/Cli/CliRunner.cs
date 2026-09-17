namespace DeltaMusePlayer.Cli;

using System.Globalization;
using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Input.Win32;
using DeltaMusePlayer.Mapping;
using DeltaMusePlayer.Playback;
using DeltaMusePlayer.Profiles;

/// <summary>
/// 命令行前端。预览、统计、导出、「用假时钟走一遍调度」，以及 M2 的真实输入诊断。
///
/// 与 GUI 共用同一条编译链路（<see cref="NoteSession"/>）；
/// Preview 只走 <see cref="TraceInputBackend"/>；真实发送是显式的独立子命令（M2 起）。
/// </summary>
public static class CliRunner
{
    public const string Usage = """
        DeltaMuse Player —— 把 DeltaMuse 导出的单声部 MIDI 转成《三角洲行动》口琴键位

        用法：
          DeltaMusePlayer                                  启动图形界面（默认 Preview）
          DeltaMusePlayer info     <midi> [选项]            只列出声轨与统计，不编译
          DeltaMusePlayer dump     <midi> [选项]            编译播放计划并打印事件 trace
          DeltaMusePlayer validate <midi> [选项]            统计可演奏比例（不修改 MIDI）
          DeltaMusePlayer simulate <midi> [选项]            用假时钟跑一遍调度，校验时序与 release-all
          DeltaMusePlayer profiles                          列出键位方案
          DeltaMusePlayer keymap   [选项]                   逻辑键 → VK → 扫描码 对照表（不发任何输入）
          DeltaMusePlayer dryrun   [选项]                   打印「真实输入将会发出的 Win32 调用」（不发任何输入）
          DeltaMusePlayer send     [选项]                   真实发送！Notepad 验收用的极短序列

        选项：
          --track <n>       主旋律轨号（默认自动挑音符最多的一条）
          --profile <file>  键位方案 JSON（默认 profiles/delta_harmonica.json）
          --speed <x>       播放速度（默认 1.0）
          --limit <n>       只打印前 n 条事件（默认全部）
          --strict          严格模式：有弹不出来的音就拒绝编译
          --no-trim         保留开头静音，不把旋律平移到 0 秒
          --overlap <p>     重叠策略：serialize（默认）| truncate
          --timing <p>      时序档位：default | safe | aggressive
          --countdown <s>   真实发送前的倒计时秒数（默认 3，send 用）
          --with-octave     自检序列里带上半音 / 升八度 / 降八度
          --test-mouse      用鼠标诊断序列（真的会点左/中/右键，谨慎使用）
          --export-json <f> 把事件计划写成 JSON
          --export-csv <f>  把事件计划写成 CSV
          --export-trace <f>把 simulate 的实际派发序列写成 CSV
          --gui             载入之后直接打开图形界面

        退出码：0 成功；1 用法/参数错误；2 文件或键位方案错误；3 编译期拒绝（严格模式等）；
                4 真实输入失败（SendInput 注入错误）。

        `send` 会真的向系统注入键鼠事件。它会先打印倒计时，请把焦点切到 Notepad 之类的
        无害窗口再让它开始。Preview / dryrun / keymap 永远不会发送任何输入。
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stdout.WriteLine(Usage);
            return 1;
        }

        string command = args[0].ToLowerInvariant();
        if (command is "-h" or "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return 0;
        }

        var opts = new Options();
        try
        {
            opts.Parse(args.Skip(1));
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"参数错误：{ex.Message}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return 1;
        }

        if (command == "profiles")
        {
            PrintProfiles(stdout);
            return 0;
        }

        // 不需要 MIDI 文件的命令：键位对照表、真实输入预演、真实发送自检序列。
        if (command is "keymap" or "dryrun" or "send")
        {
            try
            {
                return RunSelfCheckCommand(command, opts, stdout, stderr);
            }
            catch (WindowsInputException ex)
            {
                stderr.WriteLine("真实输入失败：" + ex.Message);
                stderr.WriteLine($"  action={ex.Action} input={ex.Input.Label} win32={ex.Win32Error}");
                return 4;
            }
            catch (ReleaseAllException ex)
            {
                stderr.WriteLine("释放输入失败（已尽力逐个尝试）：" + ex.Message);
                return 4;
            }
            catch (InvalidOperationException ex)
            {
                stderr.WriteLine("自检失败：" + ex.Message);
                return 3;
            }
        }

        if (command is not ("info" or "dump" or "validate" or "simulate"))
        {
            // 先把子命令认下来再碰文件：否则「命令名打错」会被后面的读文件失败盖成「文件问题」。
            stderr.WriteLine($"未知子命令：{command}");
            stderr.WriteLine();
            stderr.WriteLine(Usage);
            return 1;
        }

        if (opts.MidiPath is null)
        {
            stderr.WriteLine("缺少 MIDI 文件路径。");
            return 1;
        }

        var log = new AppLog();
        var session = new NoteSession(log);
        session.Config = opts.BuildConfig();

        try
        {
            session.LoadProfile(opts.ProfilePath);
            session.LoadMidi(opts.MidiPath);
            if (opts.TrackIndex is int t) session.SelectTrack(t);

            if (opts.TrackIndex is null && session.SelectedTrack is null)
            {
                stderr.WriteLine("这份 MIDI 里没有可用作主旋律的音符轨。");
                return 3;
            }

            switch (command)
            {
                case "info":
                    PrintInfo(stdout, session);
                    return 0;
                case "dump":
                    return Dump(stdout, session, opts);
                case "validate":
                    return Validate(stdout, session, opts);
                case "simulate":
                    return Simulate(stdout, session, opts);
                default:
                    return 1;   // 前面已经拦过未知子命令，走不到这里
            }
        }
        catch (FileNotFoundException ex)
        {
            stderr.WriteLine($"找不到文件：{ex.FileName ?? ex.Message}");
            return 2;
        }
        catch (DirectoryNotFoundException ex)
        {
            stderr.WriteLine($"路径不存在：{ex.Message}");
            return 2;
        }
        catch (InvalidDataException ex)
        {
            stderr.WriteLine($"MIDI / 方案文件有问题：{ex.Message}");
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine($"编译失败：{ex.Message}");
            return 3;
        }
    }

    // ------------------------------------------------------------------ 子命令

    private static void PrintProfiles(TextWriter w)
    {
        w.WriteLine("内置键位方案：delta_harmonica");
        string? path = InstrumentProfileLoader.FindDefaultProfilePath();
        if (path is null)
        {
            w.WriteLine("  （磁盘上没有找到 profiles/delta_harmonica.json，程序会使用内置的同一份默认值）");
            return;
        }

        var profile = InstrumentProfileLoader.LoadFile(path);
        var mapper = new NoteMapper(profile);
        w.WriteLine($"  文件：{path}");
        w.WriteLine($"  基准音：{Music.NoteName(profile.BasePitch)}（{profile.BasePitch}）");
        w.WriteLine($"  键位：{string.Join(" ", profile.Keys)}    半音间隔：{string.Join(" ", profile.NaturalIntervals)}");
        w.WriteLine($"  八度档位：{string.Join(" ", profile.EffectiveOctaveOffsets)}");
        w.WriteLine($"  修饰键：{profile.DescribeModifiers()}");
        w.WriteLine($"  可演奏音域：{Music.SolfegeRange(mapper.MinSupportedPitch, mapper.MaxSupportedPitch)}" +
                    $"  共 {mapper.AllPlayablePitches().Count} 个音高");
    }

    private static void PrintInfo(TextWriter w, NoteSession session)
    {
        var song = session.Song!;
        w.WriteLine($"文件：{song.FilePath}");
        w.WriteLine($"格式：MIDI format {song.Format}；每四分音符 {song.TicksPerQuarterNote} tick");
        w.WriteLine($"时长：{Music.TimeLabel(song.DurationSeconds * 1000)}");
        w.WriteLine($"声轨：{song.Tracks.Count} 条");
        foreach (var t in song.Tracks)
            w.WriteLine($"  {(session.SelectedTrack?.Index == t.Index ? "*" : " ")} {t.Describe()}");
        foreach (var warning in song.Warnings) w.WriteLine($"  ! {warning}");
    }

    private static int Dump(TextWriter w, NoteSession session, Options opts)
    {
        var plan = session.Compile(opts.Speed);
        PrintHeader(w, session, plan);

        w.WriteLine("事件 trace（时间 / 输入 / 动作）：");
        int limit = opts.Limit ?? int.MaxValue;
        int shown = 0;
        foreach (var e in plan.Events)
        {
            if (shown++ >= limit) { w.WriteLine($"  … 还有 {plan.EventCount - limit} 条（用 --limit 调大）"); break; }
            w.WriteLine("  " + e.ToTraceLine());
        }
        if (plan.EventCount == 0) w.WriteLine("  （没有任何事件：这条轨上没有可演奏的音）");

        PrintWarnings(w, plan);
        WriteExports(w, plan, null, opts);
        return 0;
    }

    private static int Validate(TextWriter w, NoteSession session, Options opts)
    {
        var plan = session.Compile(opts.Speed);
        var mapper = session.Mapper!;

        var pitches = session.SelectedTrack!.Notes.Select(n => n.Pitch).ToList();
        var distinct = pitches.Distinct().OrderBy(p => p).ToList();
        var unplayable = distinct.Where(p => !mapper.IsPlayable(p)).ToList();

        w.WriteLine($"文件：{session.MidiPath}");
        w.WriteLine($"轨：{session.SelectedTrack.Describe()}");
        w.WriteLine($"音符：{plan.Report.NoteCount}；可演奏 {plan.Report.PlayableCount}；不可演奏 {plan.Report.UnplayableCount}");
        w.WriteLine($"Playable: {plan.Mapping.PlayablePercentLabel}");
        w.WriteLine($"用到的不同音高：{distinct.Count} 个（{Music.SolfegeRange(distinct[0], distinct[^1])}）" +
                    (distinct.Count == 0 ? "" : ""));
        w.WriteLine($"键位方案可演奏音域：{Music.SolfegeRange(mapper.MinSupportedPitch, mapper.MaxSupportedPitch)}");

        if (unplayable.Count > 0)
        {
            w.WriteLine($"方案里没有对应键的音高（{unplayable.Count} 个）：" +
                        string.Join(" ", unplayable.Select(p => $"{Music.NoteName(p)}({Music.SolfegeName(p)})")));
        }

        w.WriteLine("本命令只统计，不修改 MIDI 文件。");
        PrintWarnings(w, plan);
        return 0;
    }

    /// <summary>
    /// 用假时钟把 plan 走一遍：完全确定性，不 Sleep、不发真实输入。
    /// 最后一步一定是 release-all —— 这正是 Stop 路径要验证的行为。
    /// </summary>
    private static int Simulate(TextWriter w, NoteSession session, Options opts)
    {
        var plan = session.Compile(opts.Speed);
        PrintHeader(w, session, plan);

        var clock = new FakeClock();
        var trace = new TraceInputBackend();
        using var engine = new PlaybackEngine(trace, clock, pollMs: 1.0);
        engine.Load(plan, opts.Speed);
        engine.Play();

        // 步长故意取一个非整齐值：如果实现里偷偷按固定帧累加时间，这里立刻会露馅。
        const double step = 3.7;
        double guard = plan.WallTotalMs + 1000;
        for (double t = 0; t <= guard; t += step)
        {
            clock.SetTo(t);
            engine.AdvanceToWallMs(t);
            if (engine.State == PlaybackState.Idle) break;
        }

        engine.Stop();   // 结束时强制 release-all

        w.WriteLine($"simulate：假时钟步长 {step}ms，共 {trace.Entries.Count} 条派发记录");
        foreach (var line in TraceExporter.RenderTrace(trace.Entries).Split(Environment.NewLine))
            if (line.Length > 0) w.WriteLine("  " + line);

        w.WriteLine();
        w.WriteLine("release-all 之后仍按着的输入：" +
                    (trace.HeldInputs.Count == 0 ? "无（正确）" : string.Join(" ", trace.HeldInputs.Select(i => i.Label))));

        PrintWarnings(w, plan);

        if (opts.ExportTracePath is not null)
        {
            File.WriteAllText(opts.ExportTracePath, TraceExporter.TraceToCsv(trace));
            w.WriteLine($"已写出 trace CSV：{opts.ExportTracePath}");
        }

        return trace.HeldInputs.Count == 0 ? 0 : 3;
    }

    // ------------------------------------------------------------------ 输出

    private static void PrintHeader(TextWriter w, NoteSession session, PlaybackPlan plan)
    {
        w.WriteLine($"文件：{session.MidiPath}");
        w.WriteLine($"轨：{session.SelectedTrack!.Describe()}");
        var use = session.SelectedTrack.Notes;
        w.WriteLine($"音符：{plan.Report.NoteCount}（源轨 {use.Count}）");
        w.WriteLine($"映射：可演奏 {plan.Report.PlayableCount}；不可演奏 {plan.Report.UnplayableCount}；" +
                    $"Playable {plan.Mapping.PlayablePercentLabel}");
        var range = plan.PlayablePitchRange;
        w.WriteLine($"音域：{(range is null ? "-" : Music.SolfegeRange(range.Value.Min, range.Value.Max))}");
        w.WriteLine($"事件：{plan.EventCount}");
        w.WriteLine($"总时长：{Music.TimeLabel(plan.WallTotalMs)}（速度 {plan.Speed:0.##}x）");
        w.WriteLine($"方案：{session.Profile!.Name}；{session.Profile.DescribeModifiers()}");
        w.WriteLine($"时序：{session.Config.Describe()}");
        w.WriteLine($"编译统计：{plan.Report.Summary()}");
        w.WriteLine();
    }

    private static void PrintWarnings(TextWriter w, PlaybackPlan plan)
    {
        if (plan.Warnings.Count == 0) return;
        w.WriteLine();
        w.WriteLine("警告：");
        foreach (var warning in plan.Warnings) w.WriteLine($"  ! {warning}");
    }

    private static void WriteExports(TextWriter w, PlaybackPlan plan, TraceInputBackend? trace, Options opts)
    {
        if (opts.ExportJsonPath is not null)
        {
            File.WriteAllText(opts.ExportJsonPath, TraceExporter.PlanToJson(plan));
            w.WriteLine($"已写出事件 JSON：{opts.ExportJsonPath}");
        }
        if (opts.ExportCsvPath is not null)
        {
            File.WriteAllText(opts.ExportCsvPath, TraceExporter.PlanToCsv(plan));
            w.WriteLine($"已写出事件 CSV：{opts.ExportCsvPath}");
        }
        _ = trace;
    }

    // ------------------------------------------------------------------ M2 自检

    /// <summary>
    /// keymap / dryrun / send 三个不需要 MIDI 文件的子命令。
    ///
    /// keymap 与 dryrun **不发送任何输入**；只有 send 会真的注入。
    /// 三者都用 <see cref="RealInputSelfCheck"/> 走与正式播放完全相同的映射与编译链路。
    /// </summary>
    private static int RunSelfCheckCommand(string command, Options opts, TextWriter w, TextWriter err)
    {
        var api = new Win32InputApi();
        if (!api.IsSupported)
        {
            err.WriteLine("真实输入只支持 Windows。");
            return 2;
        }

        var profile = opts.ProfilePath is null
            ? InstrumentProfileLoader.LoadDefault().Profile
            : InstrumentProfileLoader.LoadFile(opts.ProfilePath);

        if (command == "keymap")
        {
            w.WriteLine(RealInputSelfCheck.RenderKeyMap(profile, api));
            return 0;
        }

        PlaybackPlan plan;
        if (opts.TestMouse)
        {
            plan = RealInputSelfCheck.BuildMouseTestPlan(profile, Math.Max(opts.NoteSeconds, 0.25));
            w.WriteLine("!! 鼠标诊断：会真的按下并松开左 / 中 / 右键。请看管好当前的窗口与鼠标指针。");
        }
        else
        {
            int[] pitches = opts.WithOctave
                ? RealInputSelfCheck.ModifierTestPitches
                : RealInputSelfCheck.NotepadRunPitches;
            plan = RealInputSelfCheck.BuildDiagnosticPlan(profile, pitches, opts.NoteSeconds);
        }

        w.WriteLine($"自检序列：{string.Join(" ", plan.Notes.Select(n => Music.NoteName(n.Pitch)))}");
        w.WriteLine($"预期结果：{DescribeExpectedText(plan)}");
        var mouseUsed = RealInputSelfCheck.MouseButtonsUsed(plan);
        if (mouseUsed.Count > 0) w.WriteLine($"用到的鼠标键：{string.Join(" ", mouseUsed)}");
        w.WriteLine();

        if (command == "dryrun")
        {
            w.WriteLine(RealInputSelfCheck.RenderPlannedWin32Calls(plan, profile, api));
            if (opts.ExportCsvPath is not null)
            {
                File.WriteAllText(opts.ExportCsvPath, RealInputSelfCheck.RenderCsv(plan, api));
                w.WriteLine($"已写出 Win32 调用 CSV：{opts.ExportCsvPath}");
            }
            w.WriteLine();
            w.WriteLine("dryrun 没有发送任何输入。确认上面的表无误后，再用 send 做真实发送。");
            return 0;
        }

        // ---- send：真的注入 ----
        w.WriteLine("!! 即将发送真实键鼠输入。请先把焦点切到 Notepad 这类无害窗口。");
        w.WriteLine("!! 任何时刻按 Ctrl+C 都会终止进程；进程退出路径会尽力释放已知按下的输入。");
        w.WriteLine();

        if (opts.CountdownSeconds > 0)
        {
            for (int s = (int)Math.Ceiling(opts.CountdownSeconds); s >= 1; s--)
            {
                w.WriteLine($"Starting in {s}...");
                w.Flush();
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
        w.WriteLine("Sending.");
        w.Flush();

        var stats = new DispatchTimingStats();
        var backend = new WindowsInputBackend(api) { TimingSink = stats, Warn = m => w.WriteLine("  warn: " + m) };
        using var engine = new PlaybackEngine(backend, new StopwatchClock(), pollMs: 1.0);
        engine.Load(plan, opts.Speed);

        try
        {
            engine.Play();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (engine.State == PlaybackState.Playing && sw.ElapsedMilliseconds < plan.WallTotalMs + 5000)
                Thread.Sleep(10);
        }
        finally
        {
            try { engine.Stop(); }
            catch (Exception ex) { w.WriteLine("停止时出错（已尝试 release-all）：" + ex.Message); }
        }

        w.WriteLine();
        w.WriteLine("播放结束。已释放全部输入：" +
                    (backend.HeldInputs.Count == 0 ? "是（held 为空）" : "否！仍有 " + backend.HeldInputs.Count + " 个"));
        w.WriteLine();
        w.WriteLine(stats.RenderSummary());
        if (opts.ExportTracePath is not null)
        {
            File.WriteAllLines(opts.ExportTracePath, stats.Samples.Select(s => s.ToString()));
            w.WriteLine($"已写出时序 CSV：{opts.ExportTracePath}");
        }

        return backend.HeldInputs.Count == 0 ? 0 : 4;
    }

    /// <summary>把计划翻译成「Notepad 里应该看到什么」，方便肉眼核对。</summary>
    private static string DescribeExpectedText(PlaybackPlan plan)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var n in plan.Notes)
        {
            sb.Append(n.Key);
            if (n.Modifiers.Count > 0) sb.Append($"({string.Join("+", n.Modifiers.Select(m => m.Label))})");
            sb.Append(' ');
        }
        return sb.ToString().TrimEnd();
    }
    // ------------------------------------------------------------------ 参数

    public sealed class Options
    {
        public string? MidiPath { get; private set; }
        public string? ProfilePath { get; private set; }
        public int? TrackIndex { get; private set; }
        public double Speed { get; private set; } = 1.0;
        public int? Limit { get; private set; }
        public bool Strict { get; private set; }
        public bool NoTrim { get; private set; }
        public bool Gui { get; private set; }
        public OverlapPolicy Overlap { get; private set; } = OverlapPolicy.Serialize;
        public string Timing { get; private set; } = "default";
        public string? ExportJsonPath { get; private set; }
        public string? ExportCsvPath { get; private set; }
        public string? ExportTracePath { get; private set; }
        public double CountdownSeconds { get; private set; } = 3.0;
        public double NoteSeconds { get; private set; } = 0.15;
        public bool WithOctave { get; private set; }
        public bool TestMouse { get; private set; }

        public void Parse(IEnumerable<string> args)
        {
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                string a = list[i];
                string Need(string name)
                {
                    if (i + 1 >= list.Count) throw new ArgumentException($"{name} 后面缺少取值。");
                    return list[++i];
                }

                switch (a)
                {
                    case "--track": TrackIndex = int.Parse(Need(a), CultureInfo.InvariantCulture); break;
                    case "--profile": ProfilePath = Need(a); break;
                    case "--speed": Speed = double.Parse(Need(a), CultureInfo.InvariantCulture); break;
                    case "--limit": Limit = int.Parse(Need(a), CultureInfo.InvariantCulture); break;
                    case "--strict": Strict = true; break;
                    case "--no-trim": NoTrim = true; break;
                    case "--gui": Gui = true; break;
                    case "--overlap":
                        Overlap = Need(a).ToLowerInvariant() switch
                        {
                            "serialize" => OverlapPolicy.Serialize,
                            "truncate" => OverlapPolicy.Truncate,
                            var other => throw new ArgumentException($"--overlap 只支持 serialize / truncate，收到 {other}。"),
                        };
                        break;
                    case "--timing":
                        Timing = Need(a).ToLowerInvariant();
                        if (Timing is not ("default" or "safe" or "aggressive"))
                            throw new ArgumentException("--timing 只支持 default / safe / aggressive。");
                        break;
                    case "--export-json": ExportJsonPath = Need(a); break;
                    case "--export-csv": ExportCsvPath = Need(a); break;
                    case "--export-trace": ExportTracePath = Need(a); break;
                    case "--countdown": CountdownSeconds = double.Parse(Need(a), CultureInfo.InvariantCulture); break;
                    case "--note-ms":
                        NoteSeconds = double.Parse(Need(a), CultureInfo.InvariantCulture) / 1000.0;
                        break;
                    case "--with-octave": WithOctave = true; break;
                    case "--test-mouse": TestMouse = true; break;
                    default:
                        if (a.StartsWith('-')) throw new ArgumentException($"未知选项：{a}");
                        if (MidiPath is not null) throw new ArgumentException($"给了多个 MIDI 路径：{MidiPath} 与 {a}");
                        MidiPath = a;
                        break;
                }
            }

            if (Speed <= 0) throw new ArgumentException("--speed 必须大于 0。");
            if (Limit is < 0) throw new ArgumentException("--limit 不能是负数。");
            if (CountdownSeconds is < 0 or > 60) throw new ArgumentException("--countdown 必须在 0~60 秒之间。");
            if (NoteSeconds is < 0.02 or > 5) throw new ArgumentException("--note-ms 必须在 20~5000 毫秒之间。");
        }

        public PlaybackConfig BuildConfig()
        {
            var config = Timing switch
            {
                "safe" => PlaybackConfig.Safe,
                "aggressive" => PlaybackConfig.Aggressive,
                _ => PlaybackConfig.Default,
            };
            config.Strict = Strict;
            config.TrimLeadingSilence = !NoTrim;
            config.Overlap = Overlap;
            config.Validate();
            return config;
        }
    }
}
