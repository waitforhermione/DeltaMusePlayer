using DeltaMusePlayer.Core;
using DeltaMusePlayer.Midi;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Xunit;

namespace DeltaMusePlayer.Tests;

public sealed class MidiParserTests
{
    private static readonly MidiParser Parser = new();

    [Fact]
    public void Format0SingleTrackIsParsedIntoSeconds()
    {
        // 480 tick/四分音符、120bpm → 每 tick 1.0416667ms；960 tick = 1.0 秒
        string path = MidiFixture.WriteFormat0(new List<MidiFixture.NoteSpec>
        {
            new MidiFixture.NoteSpec(60, 0, 480),      // 0.0s ~ 0.5s
            new MidiFixture.NoteSpec(62, 960, 480),    // 1.0s ~ 1.5s
        });

        var song = Parser.Parse(path);

        Assert.Equal(1, song.Tracks.Count);
        Assert.Equal(0, song.Format);
        Assert.Equal(480, song.TicksPerQuarterNote);
        Assert.Equal(2, song.Tracks[0].NoteCount);
        Assert.Equal("Melody", song.Tracks[0].Name);

        var notes = song.Tracks[0].Notes;
        Assert.Equal(0.0, notes[0].StartSeconds, 4);
        Assert.Equal(0.5, notes[0].DurationSeconds, 4);
        Assert.Equal(1.0, notes[1].StartSeconds, 4);
        Assert.Equal(62, notes[1].Pitch);
        Assert.Equal(1.5, song.DurationSeconds, 4);
    }

    [Fact]
    public void TempoChangesAreConvertedToRealSeconds()
    {
        // tick 0~480 是 120bpm（0.5s），tick 480 起改成 60bpm（每四分音符 1.0s）。
        // 于是 tick 960 落在 0.5 + 1.0 = 1.5 秒上。
        string path = MidiFixture.WriteWithTempoChanges(
            new List<MidiFixture.NoteSpec>
            {
                new MidiFixture.NoteSpec(60, 0, 240),     // 0.0 ~ 0.25s
                new MidiFixture.NoteSpec(64, 960, 480),   // 1.5s ~ 2.0s
            },
            new[] { (0L, 120.0), (480L, 60.0) });

        var song = Parser.Parse(path);
        var notes = song.Tracks[0].Notes;

        Assert.Equal(0.0, notes[0].StartSeconds, 4);
        Assert.Equal(0.25, notes[0].DurationSeconds, 4);
        // 第二颗音从 tick 960 起（= 0.5s + 1.0s = 1.5s），时值 480 tick = 1.0s
        Assert.Equal(1.5, notes[1].StartSeconds, 4);
        Assert.Equal(2.5, notes[1].EndSeconds, 4);
    }

    [Fact]
    public void VelocityZeroNoteOnCountsAsNoteOff()
    {
        string path = MidiFixture.WriteRawNoteEvents(new[]
        {
            (0L, 60, true, 90),
            (480L, 60, true, 0),     // 力度 0 的 note-on = MIDI 规范里的 note-off
        });

        var song = Parser.Parse(path);

        Assert.Equal(1, song.Tracks[0].NoteCount);
        Assert.Equal(0.5, song.Tracks[0].Notes[0].DurationSeconds, 4);
    }

    [Fact]
    public void AdjacentRepeatedNotesKeepTheirOwnBoundaries()
    {
        string path = MidiFixture.WriteRawNoteEvents(new[]
        {
            (0L, 60, true, 90),
            (240L, 60, false, 0),
            (240L, 60, true, 90),
            (480L, 60, false, 0),
        });

        var song = Parser.Parse(path);
        var notes = song.Tracks[0].Notes;

        Assert.Equal(0.0, notes[0].StartSeconds, 4);
        Assert.Equal(0.25, notes[0].DurationSeconds, 4);
        Assert.Equal(0.25, notes[1].StartSeconds, 4);
        Assert.Equal(0.25, notes[1].DurationSeconds, 4);
        Assert.Equal(0.5, notes[1].EndSeconds, 4);
    }

    [Fact]
    public void Format1MultipleTracksAreListedAndTheBusiestIsSuggested()
    {
        string path = MidiFixture.WriteFormat1(new[]
        {
            ("Piano", new List<MidiFixture.NoteSpec>
            {
                new MidiFixture.NoteSpec(60, 0, 240),
                new MidiFixture.NoteSpec(62, 480, 240),
                new MidiFixture.NoteSpec(64, 960, 240),
            }),
            ("Melody", new List<MidiFixture.NoteSpec>
            {
                new MidiFixture.NoteSpec(72, 0, 240),
                new MidiFixture.NoteSpec(74, 480, 240),
                new MidiFixture.NoteSpec(76, 960, 240),
                new MidiFixture.NoteSpec(77, 1440, 240),
            }),
            ("Empty", new List<MidiFixture.NoteSpec>()),
        });

        var song = Parser.Parse(path);

        Assert.Equal(1, song.Format);
        Assert.Equal(3, song.Tracks.Count);
        Assert.Equal("Piano", song.Tracks[0].Name);
        Assert.Equal(3, song.Tracks[0].NoteCount);
        Assert.Equal(4, song.Tracks[1].NoteCount);
        Assert.Equal(0, song.Tracks[2].NoteCount);

        var suggested = song.SuggestMelodyTrack();
        Assert.NotNull(suggested);
        Assert.Equal(1, suggested!.Index);
    }

    [Fact]
    public void DrumTrackIsDetectedAndNotSuggestedAsMelody()
    {
        string path = MidiFixture.WriteFormat1(new[]
        {
            ("Drums", new List<MidiFixture.NoteSpec>
            {
                new MidiFixture.NoteSpec(36, 0, 120, Channel: 9),
                new MidiFixture.NoteSpec(38, 240, 120, Channel: 9),
                new MidiFixture.NoteSpec(36, 480, 120, Channel: 9),
                new MidiFixture.NoteSpec(38, 720, 120, Channel: 9),
                new MidiFixture.NoteSpec(42, 960, 120, Channel: 9),
            }),
            ("Melody", new List<MidiFixture.NoteSpec> { new MidiFixture.NoteSpec(72, 0, 240) }),
        });

        var song = Parser.Parse(path);

        Assert.True(song.Tracks[0].IsDrum);
        Assert.False(song.Tracks[1].IsDrum);
        Assert.Equal(1, song.SuggestMelodyTrack()!.Index);
    }

    [Fact]
    public void NonMidiFileIsRejectedWithAClearMessage()
    {
        string path = MidiFixture.TempPath();
        File.WriteAllText(path, "this is definitely not a midi file");

        var ex = Assert.Throws<InvalidDataException>(() => Parser.Parse(path));
        Assert.Contains("不是标准 MIDI 文件", ex.Message);
    }

    [Fact]
    public void EmptyFileIsRejectedWithAClearMessage()
    {
        string path = MidiFixture.TempPath();
        File.WriteAllBytes(path, Array.Empty<byte>());

        var ex = Assert.Throws<InvalidDataException>(() => Parser.Parse(path));
        Assert.Contains("0 字节", ex.Message);
    }

    [Fact]
    public void NoteShorterThanTheFloorIsLiftedToTwentyMilliseconds()
    {
        // 1 tick ≈ 1.04ms：解析层要把它抬到 20ms 的下限，绝不给播放层零时长音符。
        string path = MidiFixture.WriteFormat0(new[] { new MidiFixture.NoteSpec(60, 0, 1) });

        var song = Parser.Parse(path);

        Assert.Equal(MidiParser.MinNoteSeconds, song.Tracks[0].Notes[0].DurationSeconds, 6);
    }
}
