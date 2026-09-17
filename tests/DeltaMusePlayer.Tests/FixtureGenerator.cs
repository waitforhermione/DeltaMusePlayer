using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 生成 tests/fixtures/deltamuse-style-melody.mid。
///
/// 这份 fixture 是**本项目自己写的原创短旋律**（不是任何现有作品），
/// 用来模拟 DeltaMuse 导出物的形态：
///   * Format 1，多轨；
///   * 一条 Melody 轨：单声部、C 大调、含重复音 / 休止 / 升号 / 降号 / 跨三个八度；
///   * 一条 Piano 轨：伴奏（验证「自动挑音符最多的非鼓轨」与选轨）；
///   * 一条 Meta 轨：只有轨道名与速度事件，没有音符；
///   * 中途有一次速度变化（验证 tempo 处理）。
///
/// 只作为只读 fixture 使用；重新生成会改变字节（时间戳无关，但 DryWetMidi 写出的
/// 顺序是确定的），所以提交之后不要再重新生成。
/// </summary>
internal static class FixtureGenerator
{
    // ---- 主旋律：原创短句，C 大调，单声部 ----
    // (pitch, startTick, lengthTick)  480 tick = 四分音符，120bpm 下 0.5s
    private static readonly (int Pitch, long Start, long Len)[] Melody =
    {
        (60, 0, 480), (60, 480, 240), (62, 720, 240),          // 重复音 C4 C4 D4
        (64, 960, 480), (65, 1440, 480), (67, 1920, 480),      // E4 F4 G4
        (69, 2400, 240), (71, 2640, 240), (72, 2880, 960),     // A4 B4 C5
        // 休止 960 tick
        (73, 4800, 240), (74, 5040, 240), (76, 5280, 480),     // C#5 D5 E5（半音）
        (77, 5760, 240), (79, 6000, 240), (84, 6240, 960),     // F5 G5 C6（升八度）
        // 休止 480 tick
        (48, 7680, 480), (50, 8160, 480), (52, 8640, 480),     // C3 D3 E3（降八度）
        (55, 9120, 480), (57, 9600, 480), (60, 10080, 960),    // G3 A3 C4
        (64, 11040, 480), (67, 11520, 480), (72, 12000, 1440), // E4 G4 C5 收尾
    };

    // ---- 伴奏：只为了让「多轨」这件事真实一点，不会被自动选中 ----
    private static readonly (int Pitch, long Start, long Len)[] Accompaniment =
    {
        (48, 0, 1920), (53, 1920, 1920), (55, 3840, 1920), (48, 5760, 1920),
        (50, 7680, 1920), (55, 9600, 1920), (48, 11520, 1920),
    };

    public const int MelodyNoteCount = 24;
    public const long LastTick = 13440;          // 12000 + 1440

    public static MidiFile Build()
    {
        var file = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(480),
        };

        file.Chunks.Add(BuildMetaTrack());
        file.Chunks.Add(BuildTrack("Melody", Melody, 0));
        file.Chunks.Add(BuildTrack("Piano", Accompaniment, 0));
        return file;
    }

    private static TrackChunk BuildTrack(string name, (int Pitch, long Start, long Len)[] notes, int channel)
    {
        var chunk = new TrackChunk(new SequenceTrackNameEvent(name));
        var events = new List<(long Ticks, int Sort, MidiEvent Event)>();

        foreach (var (pitch, start, len) in notes)
        {
            events.Add((start, 1, new NoteOnEvent((SevenBitNumber)pitch, (SevenBitNumber)90)
            {
                Channel = (FourBitNumber)channel,
            }));
            events.Add((start + len, 0, new NoteOffEvent((SevenBitNumber)pitch, (SevenBitNumber)0)
            {
                Channel = (FourBitNumber)channel,
            }));
        }

        long last = 0;
        foreach (var (ticks, _, ev) in events.OrderBy(x => x.Ticks).ThenBy(x => x.Sort))
        {
            ev.DeltaTime = (int)(ticks - last);
            chunk.Events.Add(ev);
            last = ticks;
        }

        return chunk;
    }

    private static TrackChunk BuildMetaTrack()
    {
        var chunk = new TrackChunk(new SequenceTrackNameEvent("DeltaMuse Meta"));

        // 初始速度 120bpm，第 4800 tick（第 3 小节）起改为 100bpm
        var events = new List<(long Ticks, MidiEvent Event)>
        {
            (0, new SetTempoEvent(500_000)),        // 120 bpm
            (4800, new SetTempoEvent(600_000)),     // 100 bpm
        };

        long last = 0;
        foreach (var (ticks, ev) in events)
        {
            ev.DeltaTime = (int)(ticks - last);
            chunk.Events.Add(ev);
            last = ticks;
        }
        return chunk;
    }
}
