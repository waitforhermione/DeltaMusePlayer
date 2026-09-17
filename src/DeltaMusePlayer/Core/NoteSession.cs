namespace DeltaMusePlayer.Core;

using DeltaMusePlayer.Mapping;
using DeltaMusePlayer.Midi;
using DeltaMusePlayer.Playback;
using DeltaMusePlayer.Profiles;

/// <summary>
/// 一次「载入 MIDI → 选轨 → 映射 → 编译计划」的会话。
///
/// 它的存在是为了让 GUI 与 CLI 走**同一条**编译链路：
/// MidiParser → NoteMapper → PlaybackPlanner → PlaybackPlan。
/// 任何一侧都不得自己另写一套映射或调度。
/// </summary>
public sealed class NoteSession
{
    private readonly MidiParser _parser = new();
    private readonly AppLog _log;

    public NoteSession(AppLog? log = null) => _log = log ?? new AppLog();

    public AppLog Log => _log;

    public MidiSong? Song { get; private set; }

    public MidiTrackInfo? SelectedTrack { get; private set; }

    public InstrumentProfile? Profile { get; private set; }

    public NoteMapper? Mapper { get; private set; }

    public PlaybackConfig Config { get; set; } = PlaybackConfig.Default;

    public MappingResult? Mapping { get; private set; }

    public PlaybackPlan? Plan { get; private set; }

    public string? MidiPath { get; private set; }

    public string? ProfilePath { get; private set; }

    /// <summary>载入 MIDI 文件并自动挑一条主旋律轨。</summary>
    public MidiSong LoadMidi(string path)
    {
        var song = _parser.Parse(path);
        Song = song;
        MidiPath = path;

        _log.Info($"已载入 MIDI：{path}（format {song.Format}，{song.Tracks.Count} 轨，" +
                  $"{song.Tracks.Sum(t => t.NoteCount)} 个音符，时长 {Music.TimeLabel(song.DurationSeconds * 1000)}）");
        foreach (var w in song.Warnings) _log.Warn(w);

        var suggestion = song.SuggestMelodyTrack();
        if (suggestion is not null) SelectTrack(suggestion.Index);
        else _log.Warn("这份 MIDI 里没有可用作主旋律的音符轨。");

        return song;
    }

    /// <summary>选择主旋律轨。会清掉之前编译好的计划。</summary>
    public void SelectTrack(int trackIndex)
    {
        if (Song is null) throw new InvalidOperationException("还没有载入 MIDI。");
        var track = Song.Tracks.FirstOrDefault(t => t.Index == trackIndex)
                    ?? throw new ArgumentOutOfRangeException(nameof(trackIndex),
                        $"没有第 {trackIndex} 轨。这份文件有 {Song.Tracks.Count} 轨。");

        SelectedTrack = track;
        Plan = null;
        Mapping = null;
        _log.Info($"选中主旋律轨：{track.Describe()}");
    }

    /// <summary>装载键位方案（不传就用内置的三角洲口琴键位）。</summary>
    public void LoadProfile(string? path)
    {
        if (path is null)
        {
            var (profile, warning) = InstrumentProfileLoader.LoadDefault();
            Profile = profile;
            ProfilePath = InstrumentProfileLoader.FindDefaultProfilePath();
            if (warning is not null) _log.Warn(warning);
        }
        else
        {
            Profile = InstrumentProfileLoader.LoadFile(path);
            ProfilePath = path;
        }

        InstrumentProfileLoader.Validate(Profile);
        Mapper = new NoteMapper(Profile);
        Plan = null;
        Mapping = null;
        _log.Info($"键位方案：{Profile.Name}（基准音 {Music.NoteName(Profile.BasePitch)}，" +
                  $"键 {string.Join(" ", Profile.Keys)}，{Profile.DescribeModifiers()}）");
    }

    /// <summary>编译播放计划。需要先载入 MIDI 并选中轨道。</summary>
    public PlaybackPlan Compile(double speed = 1.0)
    {
        if (Profile is null || Mapper is null) LoadProfile(null);
        if (SelectedTrack is null) throw new InvalidOperationException("还没有选中主旋律轨。");

        var planner = new PlaybackPlanner(Mapper!, Config);
        var plan = planner.Compile(SelectedTrack.Notes, speed);

        Mapping = plan.Mapping;
        Plan = plan;

        _log.Info($"已编译播放计划：{plan.Summary()}");

        return plan;
    }
}
