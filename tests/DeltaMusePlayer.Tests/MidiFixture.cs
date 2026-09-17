using DeltaMusePlayer.Core;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 构造测试用 MIDI 文件，全部现场生成（仓库里不放二进制 fixture），
/// 这样每个用例需要的结构（变速、重叠、复音、多轨、Format 0/1）都是自解释的。
///
/// 为什么有几份是手写字节的：DryWetMidi 的
///   * <c>MidiFile(IEnumerable&lt;MidiChunk&gt;)</c> 在只给一个 chunk 时会再补一条空轨；
///   * <c>MidiFile.Write</c> 在 Format 为 MultiTrack 时也会补一条空的速度轨；
///   * <c>MidiFile.OriginalFormat</c> 是只读的，改不动。
/// 于是「Format 0 单轨」这类断言没法靠它产出，只能自己拼字节。
/// </summary>
internal static class MidiFixture
{
    public sealed record NoteSpec(int Pitch, long StartTicks, long LengthTicks, int Velocity = 90, int Channel = 0);

    private sealed record RawEvent(long Ticks, int Sort, byte[] Bytes);

    // ------------------------------------------------------------------ Format 0（手写字节）

    /// <summary>单轨 MIDI format 0。</summary>
    public static string WriteFormat0(IEnumerable<NoteSpec> notes, string? trackName = "Melody",
                                      int ticksPerQuarter = 480)
    {
        var events = new List<RawEvent>();
        if (trackName is not null)
            events.Add(new RawEvent(0, -1, TrackNameBytes(trackName)));

        AddNoteEvents(events, notes);
        return WriteSingleTrackFile(events, ticksPerQuarter);
    }

    /// <summary>直接写 note-on / note-off（力度 0 的 note-off 也走这里）。</summary>
    public static string WriteRawNoteEvents(IEnumerable<(long Ticks, int Pitch, bool On, int Velocity)> events,
                                            int ticksPerQuarter = 480)
    {
        var list = new List<RawEvent>();
        foreach (var e in events)
        {
            byte status = (byte)((e.On ? 0x90 : 0x80) | 0x00);
            list.Add(new RawEvent(e.Ticks, e.On ? 1 : 0,
                new[] { status, (byte)e.Pitch, (byte)e.Velocity }));
        }
        return WriteSingleTrackFile(list, ticksPerQuarter);
    }



    private static string WriteSingleTrackFile(IReadOnlyList<RawEvent> events, int ticksPerQuarter)
    {
        var body = new List<byte>();
        long last = 0;
        foreach (var e in events.OrderBy(x => x.Ticks).ThenBy(x => x.Sort))
        {
            WriteVariableLength(body, e.Ticks - last);
            body.AddRange(e.Bytes);
            last = e.Ticks;
        }

        WriteVariableLength(body, 0);
        body.Add(0xFF);
        body.Add(0x2F);
        body.Add(0x00);

        var file = new List<byte>();
        Ascii(file, "MThd");
        BE32(file, 6);
        BE16(file, 0);                   // format 0：单轨
        BE16(file, 1);                   // ntrks
        BE16(file, ticksPerQuarter);
        Ascii(file, "MTrk");
        BE32(file, body.Count);
        file.AddRange(body);

        string path = TempPath();
        File.WriteAllBytes(path, file.ToArray());
        return path;
    }

    // ------------------------------------------------------------------ Format 1（多轨，交给 DryWetMidi）

    /// <summary>多轨 MIDI format 1，每条轨一个名字。</summary>
    public static string WriteFormat1(IEnumerable<(string Name, List<NoteSpec> Notes)> tracks,
                                      int ticksPerQuarter = 480)
    {
        var chunks = tracks.Select(t => BuildTrack(t.Notes, t.Name)).ToArray();
        var file = new MidiFile { TimeDivision = new TicksPerQuarterNoteTimeDivision((short)ticksPerQuarter) };
        foreach (var c in chunks) file.Chunks.Add(c);
        return SaveTemp(file);
    }

    /// <summary>
    /// 带变速的单轨文件：在指定 tick 处插入速度事件。
    /// 没有初始速度事件时，DryWetMidi 的 TempoMap 按 120bpm 处理开头。
    /// </summary>
    public static string WriteWithTempoChanges(IEnumerable<NoteSpec> notes,
                                               IEnumerable<(long Ticks, double Bpm)> tempoChanges,
                                               int ticksPerQuarter = 480)
    {
        // 不用 ManageTimedEvents()：它 SaveChanges 时会重建 chunk，音符会被丢掉。
        var events = new List<RawEvent>();
        foreach (var (ticks, bpm) in tempoChanges)
        {
            long microsPerQuarter = (long)(60_000_000 / bpm);
            events.Add(new RawEvent(ticks, -2, new[]
            {
                (byte)0xFF, (byte)0x51, (byte)0x03,
                (byte)((microsPerQuarter >> 16) & 0xFF),
                (byte)((microsPerQuarter >> 8) & 0xFF),
                (byte)(microsPerQuarter & 0xFF),
            }));
        }

        AddNoteEvents(events, notes);
        return WriteSingleTrackFile(events, ticksPerQuarter);
    }

    // ------------------------------------------------------------------ 内部

    private static void AddNoteEvents(List<RawEvent> target, IEnumerable<NoteSpec> notes)
    {
        foreach (var n in notes)
        {
            byte status = (byte)(0x90 | (n.Channel & 0x0F));
            target.Add(new RawEvent(n.StartTicks, 1, new[] { status, (byte)n.Pitch, (byte)n.Velocity }));
            target.Add(new RawEvent(n.StartTicks + n.LengthTicks, 0, new[] { status, (byte)n.Pitch, (byte)0 }));
        }
    }

    private static byte[] TrackNameBytes(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        var result = new List<byte> { 0xFF, 0x03 };
        WriteVariableLength(result, bytes.Length);
        result.AddRange(bytes);
        return result.ToArray();
    }

    private static TrackChunk BuildTrack(IEnumerable<NoteSpec> notes, string? name)
    {
        // 不用 chunk.ManageNotes()：那个管理器会把音符写进一个**新的** TrackChunk，
        // 原来那个装着轨道名的 chunk 会留在文件里变成一条空轨。
        var chunk = new TrackChunk();
        var events = new List<(long Ticks, int Sort, MidiEvent Event)>();

        if (name is not null)
            events.Add((0, -1, new SequenceTrackNameEvent(name)));

        foreach (var n in notes)
        {
            events.Add((n.StartTicks, 1, new NoteOnEvent((SevenBitNumber)n.Pitch, (SevenBitNumber)n.Velocity)
            {
                Channel = (FourBitNumber)n.Channel,
            }));
            events.Add((n.StartTicks + n.LengthTicks, 0, new NoteOffEvent((SevenBitNumber)n.Pitch, (SevenBitNumber)0)
            {
                Channel = (FourBitNumber)n.Channel,
            }));
        }

        // 同一 tick 上必须先放 note-off 再放 note-on，否则首尾相接的重复音会被并成一个长音。
        long last = 0;
        foreach (var (ticks, _, ev) in events.OrderBy(x => x.Ticks).ThenBy(x => x.Sort))
        {
            ev.DeltaTime = (int)(ticks - last);
            chunk.Events.Add(ev);
            last = ticks;
        }

        return chunk;
    }

    private static string SaveTemp(MidiFile file)
    {
        string path = TempPath();
        file.Write(path, overwriteFile: true);
        return path;
    }

    public static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DeltaMusePlayerTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"fixture_{Guid.NewGuid():N}.mid");
    }

    // ------------------------------------------------------------------ 字节工具

    private static void WriteVariableLength(List<byte> target, long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "MIDI 的 delta time 不能是负数。");
        var buffer = new List<byte> { (byte)(value & 0x7F) };
        value >>= 7;
        while (value > 0)
        {
            buffer.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buffer.Reverse();
        target.AddRange(buffer);
    }

    private static void BE32(List<byte> target, long value)
    {
        target.Add((byte)((value >> 24) & 0xFF));
        target.Add((byte)((value >> 16) & 0xFF));
        target.Add((byte)((value >> 8) & 0xFF));
        target.Add((byte)(value & 0xFF));
    }

    private static void BE16(List<byte> target, int value)
    {
        target.Add((byte)((value >> 8) & 0xFF));
        target.Add((byte)(value & 0xFF));
    }

    private static void Ascii(List<byte> target, string text)
    {
        foreach (char c in text) target.Add((byte)c);
    }
}
