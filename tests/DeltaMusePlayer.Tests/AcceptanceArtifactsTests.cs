using DeltaMusePlayer.Cli;
using DeltaMusePlayer.Core;
using DeltaMusePlayer.Midi;
using Xunit;
using Xunit.Abstractions;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// M1 的验收展示：把「DeltaMuse 风格 MIDI」跑一遍 CLI，把真实输出落到文件里，
/// 供人工核对（报告里的 trace 就是从这份文件里摘出来的）。
/// </summary>
public sealed class AcceptanceArtifactsTests
{
    private readonly ITestOutputHelper _out;

    public AcceptanceArtifactsTests(ITestOutputHelper output) => _out = output;

    /// <summary>往测试输出目录里写一份 DeltaMuse 风格的 sample MIDI。</summary>
    private static string WriteSample()
    {
        // 单声部、C 大调、含重复音 / 休止 / 升降号 / 跨八度
        return MidiFixture.WriteFormat0(new[]
        {
            new MidiFixture.NoteSpec(60, 0, 480),       // C4   0.00~0.50
            new MidiFixture.NoteSpec(60, 480, 480),     // C4   0.50~1.00  重复音
            new MidiFixture.NoteSpec(64, 960, 480),     // E4   1.00~1.50
            new MidiFixture.NoteSpec(67, 1440, 480),    // G4   1.50~2.00
            new MidiFixture.NoteSpec(72, 2400, 480),    // C5   2.50~3.00  中间 0.5s 休止
            new MidiFixture.NoteSpec(73, 2880, 480),    // C#5  3.00~3.50  半音
            new MidiFixture.NoteSpec(74, 3360, 480),    // D5   3.50~4.00  升八度
            new MidiFixture.NoteSpec(48, 3840, 480),    // C3   4.00~4.50  降八度
            new MidiFixture.NoteSpec(77, 4320, 480),    // F5   4.50~5.00  升八度
        });
    }

    [Fact]
    public void WritesThePreviewTraceArtifact()
    {
        string midi = WriteSample();
        string dir = Path.Combine(Path.GetTempPath(), "DeltaMusePlayerTests");
        Directory.CreateDirectory(dir);

        var dump = new StringWriter();
        int dumpCode = CliRunner.Run(new[] { "dump", midi }, dump, new StringWriter());

        var info = new StringWriter();
        int infoCode = CliRunner.Run(new[] { "info", midi }, info, new StringWriter());

        var validate = new StringWriter();
        int validateCode = CliRunner.Run(new[] { "validate", midi }, validate, new StringWriter());

        var simulate = new StringWriter();
        int simulateCode = CliRunner.Run(new[] { "simulate", midi }, simulate, new StringWriter());

        Assert.Equal(0, dumpCode);
        Assert.Equal(0, infoCode);
        Assert.Equal(0, validateCode);
        Assert.Equal(0, simulateCode);

        string artifact = Path.Combine(dir, "acceptance-preview.txt");
        File.WriteAllText(artifact,
            "== DeltaMuse Player · M1 acceptance preview ==\r\n\r\n" +
            "[sample midi] " + midi + "\r\n\r\n" +
            "== info ==\r\n" + info + "\r\n" +
            "== validate ==\r\n" + validate + "\r\n" +
            "== dump (event trace) ==\r\n" + dump + "\r\n" +
            "== simulate (fake clock, deterministic) ==\r\n" + simulate);

        _out.WriteLine("artifact: " + artifact);
        _out.WriteLine(info.ToString());
        _out.WriteLine(validate.ToString());
        _out.WriteLine(dump.ToString());
        _out.WriteLine($"artifact bytes = {new FileInfo(artifact).Length}");
    }
}
