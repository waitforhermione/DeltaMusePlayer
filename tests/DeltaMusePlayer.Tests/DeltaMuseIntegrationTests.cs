using System.Globalization;
using System.Text.RegularExpressions;
using DeltaMusePlayer.Cli;
using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Midi;
using DeltaMusePlayer.Playback;
using DeltaMusePlayer.Profiles;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// M1 的验收测试：MIDI 文件 → 映射 → 播放计划 → 用 TraceInputBackend 跑一遍。
/// 同时覆盖 CLI 的实际输出格式。
/// </summary>
public sealed class DeltaMuseIntegrationTests
{
    /// <summary>
    /// 一份「DeltaMuse 风格」的单声部旋律：C 大调、有重复音、有休止、有升降号、跨两个八度。
    /// 对应 DeltaMuse 输出的典型形态（主旋律提取后、已做过移调适配的单声部 MIDI）。
    /// </summary>
    private static string WriteDeltaMuseStyleMelody()
        => MidiFixture.WriteFormat0(new List<MidiFixture.NoteSpec>
        {
            new MidiFixture.NoteSpec(60, 0, 480),       // C4  0.00~0.50
            new MidiFixture.NoteSpec(60, 480, 480),     // C4  0.50~1.00  重复音
            new MidiFixture.NoteSpec(62, 960, 480),     // D4  1.00~1.50
            new MidiFixture.NoteSpec(64, 1440, 480),    // E4  1.50~2.00
            new MidiFixture.NoteSpec(65, 2400, 480),    // F4  2.50~3.00  中间有 0.5s 休止
            new MidiFixture.NoteSpec(67, 2880, 480),    // G4  3.00~3.50
            new MidiFixture.NoteSpec(69, 3360, 480),    // A4  3.50~4.00
            new MidiFixture.NoteSpec(71, 3840, 480),    // B4  4.00~4.50
            new MidiFixture.NoteSpec(72, 4320, 960),    // C5  4.50~5.50
            new MidiFixture.NoteSpec(73, 5280, 480),    // C#5 5.50~6.00
            new MidiFixture.NoteSpec(74, 5760, 480),    // D5  6.00~6.50
            new MidiFixture.NoteSpec(76, 6240, 480),    // E5  6.50~7.00
            new MidiFixture.NoteSpec(79, 6720, 960),    // G5  7.00~8.00
        });

    [Fact]
    public void FullPipelineCompilesADeltaMuseStyleMelody()
    {
        string path = WriteDeltaMuseStyleMelody();
        var session = new NoteSession();
        session.LoadProfile(null);
        session.LoadMidi(path);

        Assert.NotNull(session.SelectedTrack);
        Assert.Equal(13, session.SelectedTrack!.NoteCount);

        var plan = session.Compile();

        Assert.Equal(13, plan.Report.NoteCount);
        Assert.Equal(13, plan.Report.PlayableCount);
        Assert.Equal(0, plan.Report.UnplayableCount);
        Assert.Equal("100.0%", plan.Mapping.PlayablePercentLabel);
        Assert.NotEmpty(plan.Events);

        // 音域：C4..G5
        var range = plan.PlayablePitchRange;
        Assert.NotNull(range);
        Assert.Equal(60, range!.Value.Min);
        Assert.Equal(79, range.Value.Max);

        // 事件里绝不允许出现两个音键同时按下
        var held = new HashSet<string>();
        foreach (var e in plan.Events)
        {
            if (e.Input.IsMouse) continue;
            if (e.IsDown) held.Add(e.Input.Label); else held.Remove(e.Input.Label);
            Assert.True(held.Count <= 1, $"{e.TimeMs}ms 处同时按着 {string.Join("+", held)}");
        }
        Assert.Empty(held);
    }

    [Fact]
    public void SameMelodyRunsEndToEndThroughTheTraceBackend()
    {
        string path = WriteDeltaMuseStyleMelody();
        var session = new NoteSession();
        session.LoadProfile(null);
        session.LoadMidi(path);
        var plan = session.Compile();

        var clock = new FakeClock();
        var trace = new TraceInputBackend();
        using var engine = new PlaybackEngine(trace, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);
        engine.Play();

        for (double t = 0; t <= plan.WallTotalMs + 50; t += 1.0)
        {
            clock.SetTo(t);
            engine.AdvanceToWallMs(t);
        }

        // 计划里的每一条事件都必须真的被派发过，且顺序一致
        Assert.Equal(plan.EventCount, trace.Entries.Count);
        Assert.Equal(
            plan.Events.Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToArray(),
            trace.Entries.Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToArray());

        Assert.Empty(trace.HeldInputs);
    }

    [Fact]
    public void RepeatedNoteInTheMiddleBecomesTwoSeparatePresses()
    {
        string path = WriteDeltaMuseStyleMelody();
        var session = new NoteSession();
        session.LoadProfile(null);
        session.LoadMidi(path);
        var plan = session.Compile();

        // 前两个音都是 C4 = Z，首尾相接 → Z down/up/down/up
        var zEvents = plan.Events
            .Where(e => e.Input == InputId.Key("Z"))
            .Select(e => $"{e.TimeMs:F3}:{e.ActionLabel}")
            .Take(4)
            .ToArray();

        Assert.Equal(4, zEvents.Length);
        Assert.EndsWith(":DOWN", zEvents[0]);
        Assert.EndsWith(":UP", zEvents[1]);
        Assert.EndsWith(":DOWN", zEvents[2]);
        Assert.EndsWith(":UP", zEvents[3]);
    }

    [Fact]
    public void RestInTheMiddleProducesAGapWithoutEvents()
    {
        string path = WriteDeltaMuseStyleMelody();
        var session = new NoteSession();
        session.LoadProfile(null);
        session.LoadMidi(path);
        var plan = session.Compile();

        // E4 结束于 2.00s，F4 从 2.50s 开始：这段休止里一条事件都不该有。
        // （只有超过释放延迟的间隔，才可能实现「这段窗口内没有任何事件」。）
        var inRest = plan.Events.Where(e => e.TimeMs > 2020 && e.TimeMs < 2480).ToList();
        Assert.Empty(inRest);
    }

    [Fact]
    public void UnplayableNotesAreCountedAndSkippedNotTransposed()
    {
        string path = MidiFixture.WriteFormat0(new List<MidiFixture.NoteSpec>
        {
            new MidiFixture.NoteSpec(60, 0, 480),
            new MidiFixture.NoteSpec(28, 480, 480),    // 倍低音，方案弹不出来
            new MidiFixture.NoteSpec(100, 960, 480),   // 极高音，方案弹不出来
            new MidiFixture.NoteSpec(62, 1440, 480),
        });

        var session = new NoteSession();
        session.LoadProfile(null);
        session.LoadMidi(path);
        var plan = session.Compile();

        Assert.Equal(4, plan.Report.NoteCount);
        Assert.Equal(2, plan.Report.PlayableCount);
        Assert.Equal(2, plan.Report.UnplayableCount);
        Assert.Equal("50.0%", plan.Mapping.PlayablePercentLabel);

        // 被跳过的音不会以任何形式出现在事件里
        Assert.DoesNotContain(plan.Events, e => e.Pitch == 28 || e.Pitch == 100);
        Assert.Equal(new[] { "Z:DOWN", "Z:UP", "X:DOWN", "X:UP" },
            plan.Events.Select(e => $"{e.Input.Label}:{e.ActionLabel}").ToArray());
    }

    [Fact]
    public void StrictModeRefusesToPlayAMelodyWithUnplayableNotes()
    {
        string path = MidiFixture.WriteFormat0(new List<MidiFixture.NoteSpec>
        {
            new MidiFixture.NoteSpec(60, 0, 480),
            new MidiFixture.NoteSpec(28, 480, 480),
        });

        var session = new NoteSession { Config = new PlaybackConfig { Strict = true } };
        session.LoadProfile(null);
        session.LoadMidi(path);

        var ex = Assert.Throws<InvalidOperationException>(() => session.Compile());
        Assert.Contains("严格模式", ex.Message);
    }

    [Fact]
    public void PreviewTraceMatchesTheDocumentedShape()
    {
        // 需求 54 的验收形态：00:00.000 MOUSE_RIGHT DOWN / 00:00.015 Z DOWN / …
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 0.5) });   // D5 → RightMouse + X

        var lines = plan.Events.Select(e => e.ToTraceLine()).ToArray();

        Assert.Equal(4, lines.Length);
        Assert.StartsWith("00:00.000", lines[0]);
        Assert.Contains("MOUSE_RIGHT DOWN", lines[0]);
        Assert.StartsWith("00:00.015", lines[1]);
        Assert.Contains("X DOWN", lines[1]);
        Assert.StartsWith(Music.TimeLabel(plan.Events[2].TimeMs), lines[2]);
        Assert.Contains("X UP", lines[2]);
        Assert.StartsWith(Music.TimeLabel(plan.Events[3].TimeMs), lines[3]);
        Assert.Contains("MOUSE_RIGHT UP", lines[3]);

        // 音键按住整个时值；修饰键在音键抬起之后才松
        Assert.Equal(500.0, plan.Events[2].TimeMs - plan.Events[1].TimeMs, 3);
        Assert.True(plan.Events[3].TimeMs > plan.Events[2].TimeMs);
    }

    [Fact]
    public void TraceExporterWritesCsvAndJson()
    {
        string path = WriteDeltaMuseStyleMelody();
        var session = new NoteSession();
        session.LoadProfile(null);
        session.LoadMidi(path);
        var plan = session.Compile();

        string csv = TraceExporter.PlanToCsv(plan);
        Assert.StartsWith("time_ms,time,type,input,note_index,pitch", csv);
        Assert.Equal(plan.EventCount + 1, csv.TrimEnd().Split('\n').Length);

        string json = TraceExporter.PlanToJson(plan);
        Assert.Contains("\"playable\": 13", json);
        Assert.Contains("\"events\"", json);
        Assert.Contains("\"mouse_down\"", json);
        Assert.Contains("\"key_down\"", json);
    }

    [Fact]
    public void PlanIsSuitableForEverySpeedPreset()
    {
        string path = WriteDeltaMuseStyleMelody();

        foreach (double speed in new[] { 0.5, 0.75, 1.0, 1.25, 1.5 })
        {
            var session = new NoteSession();
            session.LoadProfile(null);
            session.LoadMidi(path);
            var plan = session.Compile(speed);

            Assert.Equal(speed, plan.Speed, 6);
            Assert.NotEmpty(plan.Events);
            Assert.True(plan.WallTotalMs > 0);
            // 计划从 0 开始（首音可能需要一小段前置留白来放修饰键提前量）
            Assert.InRange(plan.Events[0].TimeMs, 0.0, 30.0);

            // 全部事件按时间单调不减
            for (int i = 1; i < plan.Events.Count; i++)
                Assert.True(plan.Events[i].TimeMs >= plan.Events[i - 1].TimeMs - 1e-9,
                    $"速度 {speed} 下事件时刻出现回退：{plan.Events[i - 1].TimeMs} → {plan.Events[i].TimeMs}");
        }
    }

    // ------------------------------------------------------------------ CLI

    [Fact]
    public void CliDumpPrintsNoteCountsMappingAndEvents()
    {
        string path = WriteDeltaMuseStyleMelody();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int code = CliRunner.Run(new[] { "dump", path, "--limit", "6" }, stdout, stderr);
        string output = stdout.ToString();

        Assert.Equal(0, code);
        Assert.Equal("", stderr.ToString());
        Assert.Contains("音符：13", output);
        Assert.Contains("可演奏 13", output);
        Assert.Contains("不可演奏 0", output);
        Assert.Contains("事件：", output);
        Assert.Contains("总时长：", output);
        // CLI 的 dump 默认只打印前 --limit 条，所以只断言「确实载入并编译成功」的关键字段。
        // 具体到某个音该按哪个键，由映射与计划层的单测覆盖，不靠这里的字符串。
        Assert.Contains("事件：", output);
        Assert.Contains("Z DOWN", output);
        Assert.Contains("00:00.0", output);
    }

    [Fact]
    public void CliRangePlaysOnlyTheSelectedSegment()
    {
        string path = WriteDeltaMuseStyleMelody();

        string Dump(params string[] extra)
        {
            var stdout = new StringWriter();
            var args = new List<string> { "dump", path };
            args.AddRange(extra);
            int code = CliRunner.Run(args.ToArray(), stdout, new StringWriter());
            Assert.Equal(0, code);
            return stdout.ToString();
        }

        string full = Dump();
        string ranged = Dump("--range", "1-3");

        // 片段必须真的更短：事件数与总时长都要下降。
        Assert.Contains("片段 00:01.000 ~ 00:03.000", ranged);
        Assert.DoesNotContain("片段", full);

        int Events(string s)
        {
            var m = Regex.Match(s, @"事件：(\d+)");
            Assert.True(m.Success, "输出里应当有「事件：N」");
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        Assert.True(Events(ranged) < Events(full),
            $"选中一段之后事件数应当减少（整曲 {Events(full)}，片段 {Events(ranged)}）");
    }

    [Fact]
    public void CliRangeWithNoNotesInItFailsClearly()
    {
        string path = WriteDeltaMuseStyleMelody();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // 选一段完全没有音符的区间：必须明确失败，而不是安静地给出一份空计划。
        int code = CliRunner.Run(new[] { "dump", path, "--range", "600-700" }, stdout, stderr);

        Assert.Equal(3, code);                       // 3 = 编译期拒绝
        Assert.Contains("没有任何音符", stderr.ToString());
    }

    [Fact]
    public void CliRejectsGarbageRange()
    {
        string path = WriteDeltaMuseStyleMelody();
        var stderr = new StringWriter();

        int code = CliRunner.Run(new[] { "dump", path, "--range", "abc-def" }, new StringWriter(), stderr);

        Assert.Equal(1, code);                       // 1 = 参数错误
        Assert.Contains("--range", stderr.ToString());
    }

    [Fact]
    public void CliRejectsInvertedRangeAsAParameterError()
    {
        // 终点早于起点是**参数**问题，必须干净地报「参数错误」并返回 1。
        // 曾经它会一路抛到 Main 外面变成未处理异常：屏幕上是一坨堆栈，退出码是 -1。
        string path = WriteDeltaMuseStyleMelody();
        var stderr = new StringWriter();

        int code = CliRunner.Run(new[] { "dump", path, "--range", "5-2" }, new StringWriter(), stderr);

        Assert.Equal(1, code);
        Assert.Contains("参数错误", stderr.ToString());
        Assert.Contains("必须晚于起点", stderr.ToString());
    }

    [Fact]
    public void CliValidateReportsPlayablePercentWithoutChangingTheFile()    {
        string path = WriteDeltaMuseStyleMelody();
        byte[] before = File.ReadAllBytes(path);

        var stdout = new StringWriter();
        int code = CliRunner.Run(new[] { "validate", path }, stdout, new StringWriter());
        string output = stdout.ToString();

        Assert.Equal(0, code);
        Assert.Contains("Playable: 100.0%", output);
        Assert.Contains("不修改 MIDI", output);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CliSimulateRunsTheSchedulerAndEndsWithEverythingReleased()
    {
        string path = WriteDeltaMuseStyleMelody();
        var stdout = new StringWriter();

        int code = CliRunner.Run(new[] { "simulate", path }, stdout, new StringWriter());
        string output = stdout.ToString();

        Assert.Equal(0, code);
        Assert.Contains("release-all 之后仍按着的输入：无（正确）", output);
    }

    [Fact]
    public void CliInfoListsTracks()
    {
        string path = MidiFixture.WriteFormat1(new (string, List<MidiFixture.NoteSpec>)[]
        {
            ("Piano", new List<MidiFixture.NoteSpec> { new MidiFixture.NoteSpec(60, 0, 240) }),
            ("Melody", new List<MidiFixture.NoteSpec> { new MidiFixture.NoteSpec(72, 0, 240), new MidiFixture.NoteSpec(74, 480, 240) }),
        });

        var stdout = new StringWriter();
        int code = CliRunner.Run(new[] { "info", path }, stdout, new StringWriter());
        string output = stdout.ToString();

        Assert.Equal(0, code);
        Assert.Contains("Piano", output);
        Assert.Contains("Melody", output);
        Assert.Contains("声轨：2 条", output);
    }

    [Fact]
    public void CliRejectsUnknownOptionsAndMissingFilesWithDistinctExitCodes()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // 参数错误 → 1
        Assert.Equal(1, CliRunner.Run(new[] { "dump" }, stdout, stderr));                                    // 缺路径
        Assert.Equal(1, CliRunner.Run(new[] { "dump", "a.mid", "--nope" }, stdout, stderr));                 // 未知选项

        // 未知子命令 → 1（必须在碰文件之前就拒绝，否则会被后面的「找不到文件」盖成 2）
        Assert.Equal(1, CliRunner.Run(new[] { "wat" }, stdout, stderr));
        Assert.Equal(1, CliRunner.Run(new[] { "wat", "a.mid" }, stdout, stderr));

        // 文件问题 → 2
        Assert.Equal(2, CliRunner.Run(new[] { "dump", @"C:\definitely\missing\file.mid" }, stdout, stderr));
        Assert.Equal(2, CliRunner.Run(new[] { "info", @"C:\definitely\missing\dir\file.mid" }, stdout, stderr));
    }

    [Fact]
    public void CliProfilesListsTheHarmonicaRange()
    {
        var stdout = new StringWriter();
        int code = CliRunner.Run(new[] { "profiles" }, stdout, new StringWriter());
        string output = stdout.ToString();

        Assert.Equal(0, code);
        Assert.Contains("delta_harmonica", output);
        Assert.Contains("Z X C V B N M ,", output);
        Assert.Contains("降八度=mouse_left", output);
        Assert.Contains("升半音=mouse_middle", output);
        Assert.Contains("升八度=mouse_right", output);
    }

    [Fact]
    public void EveryCliCommandKeepsTheInputFileUntouched()
    {
        string path = WriteDeltaMuseStyleMelody();
        byte[] before = File.ReadAllBytes(path);

        foreach (string command in new[] { "info", "dump", "validate", "simulate" })
            CliRunner.Run(new[] { command, path }, new StringWriter(), new StringWriter());

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void LoadingAProfileFromDiskGivesTheSamePlanAsTheBuiltInDefault()
    {
        string midi = WriteDeltaMuseStyleMelody();
        string? profilePath = InstrumentProfileLoader.FindDefaultProfilePath();
        if (profilePath is null) return;

        var fromFile = new NoteSession();
        fromFile.LoadProfile(profilePath);
        fromFile.LoadMidi(midi);
        var planA = fromFile.Compile();

        var builtIn = new NoteSession();
        builtIn.LoadProfile(null);
        builtIn.LoadMidi(midi);
        var planB = builtIn.Compile();

        Assert.Equal(
            planB.Events.Select(e => $"{e.TimeMs:F3}:{e.Input.Label}:{e.ActionLabel}"),
            planA.Events.Select(e => $"{e.TimeMs:F3}:{e.Input.Label}:{e.ActionLabel}"));
    }
}
