namespace DeltaMusePlayer.Midi;

using DeltaMusePlayer.Core;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

/// <summary>
/// MIDI 解析：把文件里的音符换算成秒时间轴的 <see cref="PlaybackNote"/>。
///
/// 这一层是**唯一**接触 tick / tempo / channel 的地方。它之后的所有模块只认秒。
/// 解析实现沿用 ChickenD233/midikey-player（MIT）的 MidiLoader 思路：
/// 先整体读入内存、RIFF(.rmi) 解包、脏数据就近纠正、轨道名 UTF-8/GBK 智能解码、
/// 残缺文件按完整 MTrk 截断修复。见 THIRD_PARTY_NOTICES.md。
///
/// DryWetMidi 的 <c>GetNotes()</c> 已经处理了 note-on / note-off 配对，
/// 其中 velocity = 0 的 note-on 按 note-off 处理，这正是 MIDI 规范要求的行为。
/// </summary>
public sealed class MidiParser
{
    static MidiParser()
    {
        // 国内老 MIDI 的轨道名常用 GBK / GB2312。
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch { /* 平台不支持就算了，退回 UTF-8 */ }
    }

    /// <summary>极短音符的下限：20ms。真正的播放下限由 PlaybackConfig.MinimumKeyHoldMs 决定。</summary>
    public const double MinNoteSeconds = 0.02;

    public MidiSong Parse(string path)
    {
        byte[] data = File.ReadAllBytes(path);

        if (data.Length == 0)
            throw new InvalidDataException("文件是 0 字节：多半是下载失败，或网盘的「占位文件」还没同步完成。");

        string sig = System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(4, data.Length));
        if (sig == "RIFF")
        {
            int p = IndexOf(data, new byte[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d' }, 12);
            if (p < 0) throw new InvalidDataException("是 RIFF(.rmi) 格式，但内部找不到 MIDI 数据，文件可能损坏。");
            data = data[p..];
            sig = "MThd";
        }
        if (sig != "MThd")
            throw new InvalidDataException(
                $"不是标准 MIDI 文件：开头是「{sig}」。文件可能损坏、被改过扩展名，或根本不是 MIDI。");

        MidiFile file = ReadWithRepair(data);

        var tempoMap = file.GetTempoMap();
        var warnings = new List<string>();

        var tracks = new List<MidiTrackInfo>();
        int trackIndex = 0;
        double fileEnd = 0;

        foreach (var chunk in file.GetTrackChunks())
        {
            string trackName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text?.Trim() ?? "";

            var rawNotes = chunk.GetNotes().ToList();
            if (rawNotes.Count > 0)
            {
                var notes = new List<PlaybackNote>(rawNotes.Count);
                foreach (var n in rawNotes)
                {
                    double start = ToSeconds(tempoMap, n.Time);
                    double end = ToSeconds(tempoMap, n.EndTime);
                    double duration = Math.Max(MinNoteSeconds, end - start);

                    notes.Add(new PlaybackNote(n.NoteNumber, start, duration, n.Velocity)
                    {
                        SourceTrack = trackIndex,
                        SourceChannel = (int)n.Channel,
                    });
                    if (start + duration > fileEnd) fileEnd = start + duration;
                }
                notes.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));

                var drumNotes = notes.Count(x => x.SourceChannel == 9);
                bool isDrum = drumNotes * 2 > notes.Count;   // 过半在通道 10 就按鼓组轨看待

                tracks.Add(new MidiTrackInfo
                {
                    Index = trackIndex,
                    Name = trackName,
                    Instrument = DescribeInstrument(chunk, isDrum),
                    IsDrum = isDrum,
                    NoteCount = notes.Count,
                    MinPitch = notes.Min(x => x.Pitch),
                    MaxPitch = notes.Max(x => x.Pitch),
                    DurationSeconds = notes[^1].EndSeconds - notes[0].StartSeconds,
                    Notes = notes,
                });
            }
            else
            {
                // 空轨也列出来：用户需要知道它为什么没音（通常是纯元事件轨）。
                tracks.Add(new MidiTrackInfo
                {
                    Index = trackIndex,
                    Name = trackName,
                    Instrument = DescribeInstrument(chunk, false),
                    IsDrum = false,
                    NoteCount = 0,
                    Notes = Array.Empty<PlaybackNote>(),
                });
            }

            trackIndex++;
        }

        if (tracks.All(t => t.NoteCount == 0))
            warnings.Add("这份 MIDI 里没有任何音符事件。");

        return new MidiSong
        {
            FilePath = path,
            Format = (int)file.OriginalFormat,
            TicksPerQuarterNote = file.TimeDivision is TicksPerQuarterNoteTimeDivision td ? td.TicksPerQuarterNote : 0,
            DurationSeconds = fileEnd,
            Tracks = tracks,
            Warnings = warnings,
        };
    }

    // ------------------------------------------------------------------ 内部

    private static MidiFile ReadWithRepair(byte[] data)
    {
        var settings = new ReadingSettings
        {
            TextEncoding = System.Text.Encoding.UTF8,
            DecodeTextCallback = DecodeTextSmart,
            NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
            // 网上 MIDI 常带脏数据，一律就近纠正而不中断：这些事件对演奏没有影响。
            InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
            InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.SnapToLimits,
            InvalidSystemCommonEventParameterValuePolicy = InvalidSystemCommonEventParameterValuePolicy.SnapToLimits,
        };

        try
        {
            using var stream = new MemoryStream(data);
            return MidiFile.Read(stream, settings);
        }
        catch (Exception ex) when (ex is NotEnoughBytesException || ex is InvalidChunkSizeException)
        {
            // 兜底：裁掉最后一个完整 MTrk 之后的残缺字节，并修正轨道数，再解析一次。
            byte[]? trimmed = TryTrimToCompleteChunks(data);
            if (trimmed is null)
                throw new InvalidDataException("文件不是完整可用的 MIDI（数据损坏或被截断）。", ex);
            try
            {
                using var ms = new MemoryStream(trimmed);
                return MidiFile.Read(ms, settings);
            }
            catch (Exception ex2)
            {
                throw new InvalidDataException("文件不是完整可用的 MIDI（数据损坏或被截断）。", ex2);
            }
        }
    }

    private static double ToSeconds(TempoMap tempoMap, long ticks)
        => TimeConverter.ConvertTo<MetricTimeSpan>(ticks, tempoMap).TotalSeconds;

    private static string DescribeInstrument(TrackChunk chunk, bool isDrum)
    {
        if (isDrum) return "Drums (ch10)";

        int? program = chunk.Events.OfType<ProgramChangeEvent>()
                            .Select(e => (int?)e.ProgramNumber)
                            .FirstOrDefault();
        if (program is null) return "—";
        return $"{GeneralMidi.InstrumentName(program.Value)} (#{program.Value + 1})";
    }

    private static string DecodeTextSmart(byte[] raw, ReadingSettings settings)
    {
        if (raw is null || raw.Length == 0) return "";
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false, true);
            string s = utf8.GetString(raw);
            if (!s.Contains('\uFFFD')) return s;
        }
        catch { }
        try { return System.Text.Encoding.GetEncoding(936).GetString(raw); } catch { }
        return System.Text.Encoding.Latin1.GetString(raw);
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static byte[]? TryTrimToCompleteChunks(byte[] data)
    {
        try
        {
            if (data.Length < 14) return null;
            int headerLen = BE32(data, 4);
            if (headerLen < 6) return null;
            int headerTotal = 8 + headerLen;
            if (headerTotal > data.Length) return null;

            var chunks = new List<byte[]>();
            int i = headerTotal;
            while (i + 8 <= data.Length)
            {
                if (data[i] != (byte)'M' || data[i + 1] != (byte)'T' ||
                    data[i + 2] != (byte)'r' || data[i + 3] != (byte)'k') break;
                long size = BE32(data, i + 4);
                long end = (long)i + 8 + size;
                if (end > data.Length) break;
                chunks.Add(data[i..(int)end]);
                i = (int)end;
            }
            if (chunks.Count == 0) return null;

            byte[] head = data[..headerTotal];
            head[10] = (byte)(chunks.Count >> 8);
            head[11] = (byte)(chunks.Count & 0xFF);

            using var ms = new MemoryStream();
            ms.Write(head, 0, head.Length);
            foreach (var c in chunks) ms.Write(c, 0, c.Length);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private static int BE32(byte[] b, int o)
        => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
