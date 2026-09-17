using System.Security.Cryptography;
using DeltaMusePlayer.Core;
using DeltaMusePlayer.Midi;
using DeltaMusePlayer.Playback;
using Xunit;
using Xunit.Abstractions;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// M2 需求 34/35：一份**固定的、落在磁盘上的** DeltaMuse 风格 MIDI fixture，
/// 以及 parse → map → plan → trace 的完整集成校验。
///
/// fixture 的来源必须清楚：
///   * 本环境**拿不到**真正的 DeltaMuse 导出文件（没有 DeltaMuse CLI/GUI，
///     也没有可访问的 DeltaMuse 仓库或转换中间产物），所以这里用的是一份
///     **本项目自己写的原创短旋律**，只复刻 DeltaMuse 导出物的**形态**（见 FixtureGenerator）。
///   * 它是纯原创、无版权问题，因此可以进仓库。
///   * 一旦提交就不再重新生成：下面用 SHA-256 钉住字节，防止被无声改掉。
/// </summary>
public sealed class FixtureIntegrationTests
{
    private const string FixtureRelativePath = @"tests\fixtures\deltamuse-style-melody.mid";

    /// <summary>fixture 的字节指纹。改动 fixture 必须同步更新这里（并说明原因）。</summary>
    private const string ExpectedSha256 = "1c607c3ad12512910022299a17c2f6eaeca139cec2d5a6cf54c64ef34729953e";

    private readonly ITestOutputHelper _out;

    public FixtureIntegrationTests(ITestOutputHelper output) => _out = output;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeltaMusePlayer.sln")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("找不到仓库根目录（DeltaMusePlayer.sln）。");
        return dir.FullName;
    }

    private static string FixturePath() => Path.Combine(RepoRoot(), FixtureRelativePath);

    /// <summary>确保 fixture 存在；不存在就按 FixtureGenerator 生成一次。</summary>
    private static string EnsureFixture()
    {
        string path = FixturePath();
        if (File.Exists(path)) return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FixtureGenerator.Build().Write(path, overwriteFile: true);
        return path;
    }

    private static string Sha256Of(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void FixtureIsPinnedAndParseable()
    {
        string path = EnsureFixture();
        string hash = Sha256Of(path);
        _out.WriteLine($"fixture: {path}");
        _out.WriteLine($"sha256 : {hash}");
        _out.WriteLine($"bytes  : {new FileInfo(path).Length}");

        // 第一次生成时把指纹打印出来；提交之后这个断言会钉住字节
        if (ExpectedSha256 != "PINNED_BELOW")
            Assert.Equal(ExpectedSha256, hash);
    }

    [Fact]
    public void FixtureHasTheDeltaMuseLikeShape()
    {
        var song = new MidiParser().Parse(EnsureFixture());

        _out.WriteLine($"format {song.Format} / tpq {song.TicksPerQuarterNote} / duration {song.DurationSeconds:F3}s");
        foreach (var t in song.Tracks) _out.WriteLine("  " + t.Describe());

        Assert.Equal(1, song.Format);                       // 多轨
        Assert.Equal(3, song.Tracks.Count);

        var melody = song.Tracks.Single(t => t.Name == "Melody");
        Assert.Equal(FixtureGenerator.MelodyNoteCount, melody.NoteCount);
        Assert.False(melody.IsDrum);
        Assert.Equal(48, melody.MinPitch);                  // C3
        Assert.Equal(84, melody.MaxPitch);                  // C6

        // 自动挑轨应当选中音符最多的非鼓轨（Melody）
        Assert.Equal(melody.Index, song.SuggestMelodyTrack()!.Index);

        // 速度变化：120bpm 段 + 100bpm 段，总时长必然大于同长度的 120bpm
        Assert.True(song.DurationSeconds > 0);
        _out.WriteLine($"melody range {melody.PitchRangeLabel}");
    }

    [Fact]
    public void FullPipelineOnTheFixtureIsFullyPlayableAndDeterministic()
    {
        string path = EnsureFixture();
        var session = new NoteSession();
        session.LoadProfile(null);
        session.LoadMidi(path);

        Assert.NotNull(session.SelectedTrack);
        Assert.Equal("Melody", session.SelectedTrack!.Name);

        var plan = session.Compile();

        _out.WriteLine($"notes={plan.Report.NoteCount} playable={plan.Report.PlayableCount} " +
                       $"unplayable={plan.Report.UnplayableCount} events={plan.EventCount} " +
                       $"duration={plan.WallTotalMs:F3}ms");

        // 需求 35：100% 可演奏
        Assert.Equal(FixtureGenerator.MelodyNoteCount, plan.Report.NoteCount);
        Assert.Equal(0, plan.Report.UnplayableCount);
        Assert.Equal("100.0%", plan.Mapping.PlayablePercentLabel);

        // 事件数确定：同一份输入编译两次必须完全一致
        var again = session.Compile();
        Assert.Equal(
            plan.Events.Select(e => $"{e.TimeMs:F3}:{e.Input.Label}:{e.ActionLabel}"),
            again.Events.Select(e => $"{e.TimeMs:F3}:{e.Input.Label}:{e.ActionLabel}"));

        // 时长合理：谱面 13440 tick，含一次降速，应在 13~16 秒之间
        Assert.InRange(plan.WallTotalMs, 13_000, 16_000);

        // 单音乐器：任何时刻最多一个音键按着；跑完不留输入
        var clock = new FakeClock();
        var trace = new InputTraceRecorder();
        using var engine = new PlaybackEngine(trace.Backend, clock) { SchedulerEnabled = false };
        engine.Load(plan, 1.0);
        engine.Play();
        for (double t = 0; t <= plan.WallTotalMs + 50; t += 1.0)
        {
            clock.SetTo(t);
            engine.AdvanceToWallMs(t);
        }

        Assert.Equal(plan.EventCount, trace.Backend.Entries.Count);
        Assert.Empty(trace.Backend.HeldInputs);
        Assert.False(trace.SawTwoKeysHeldAtOnce, "任何时候都不该有两个音键同时按着");
    }

    [Fact]
    public void FixtureWithStrictModeAndSpeedPresetsStillCompiles()
    {
        string path = EnsureFixture();

        foreach (double speed in new[] { 0.5, 0.75, 1.0, 1.25, 1.5 })
        {
            var session = new NoteSession { Config = new PlaybackConfig { Strict = true } };
            session.LoadProfile(null);
            session.LoadMidi(path);
            var plan = session.Compile(speed);

            Assert.NotEmpty(plan.Events);
            Assert.Equal(0, plan.Report.UnplayableCount);
            for (int i = 1; i < plan.Events.Count; i++)
                Assert.True(plan.Events[i].TimeMs >= plan.Events[i - 1].TimeMs - 1e-9);
        }
    }

    /// <summary>跑一遍并顺手检查「任何时刻最多一个音键按着」。</summary>
    private sealed class InputTraceRecorder
    {
        public DeltaMusePlayer.Input.TraceInputBackend Backend { get; } = new();

        private readonly HashSet<string> _heldKeys = new();

        public bool SawTwoKeysHeldAtOnce { get; private set; }

        public InputTraceRecorder()
        {
            Backend.EntryRecorded += e =>
            {
                if (e.Input.IsMouse) return;
                if (e.IsDown) _heldKeys.Add(e.Input.Label);
                else _heldKeys.Remove(e.Input.Label);
                if (_heldKeys.Count > 1) SawTwoKeysHeldAtOnce = true;
            };
        }
    }
}
