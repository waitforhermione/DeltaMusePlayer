namespace DeltaMusePlayer.Playback;

using System.Globalization;
using System.Text;
using System.Text.Json;
using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;

/// <summary>
/// 把 <see cref="PlaybackPlan"/> 或一段 <see cref="TraceInputBackend"/> 记录导出成
/// CSV / JSON，用于事后逐条核对 MIDI → 输入事件的编译结果。
/// </summary>
public static class TraceExporter
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 发布包开了裁剪：不显式给 TypeInfoResolver 的话，导出 JSON 会直接抛异常。
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>导出计划事件：`time_ms,type,key,note_index,pitch`。</summary>
    public static string PlanToCsv(PlaybackPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("time_ms,time,type,input,note_index,pitch");
        foreach (var e in plan.Events)
        {
            sb.Append(e.TimeMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
              .Append(Music.TimeLabel(e.TimeMs)).Append(',')
              .Append(e.TypeLabel).Append(',')
              .Append(e.Input.Label).Append(',')
              .Append(e.NoteIndex.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append((e.Pitch >= 0 ? Music.NoteName(e.Pitch) : "")).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>导出计划事件为 JSON（含统计与 warning）。</summary>
    public static string PlanToJson(PlaybackPlan plan)
    {
        var payload = new
        {
            speed = plan.Speed,
            total_ms = plan.WallTotalMs,
            total = Music.TimeLabel(plan.WallTotalMs),
            report = new
            {
                notes = plan.Report.NoteCount,
                playable = plan.Report.PlayableCount,
                unplayable = plan.Report.UnplayableCount,
                events = plan.EventCount,
                pushed_by_overlap = plan.Report.PushedByOverlap,
                pushed_by_retrigger = plan.Report.PushedByRetrigger,
                pushed_by_gap = plan.Report.PushedByGap,
                min_hold_extended = plan.Report.MinHoldExtendedCount,
                opposite_octave_transitions = plan.Report.OppositeOctaveTransitions,
                max_push_ms = Math.Round(plan.Report.MaxPushMs, 3),
            },
            warnings = plan.Warnings,
            events = plan.Events.Select(e => new
            {
                time_ms = Math.Round(e.TimeMs, 3),
                time = Music.TimeLabel(e.TimeMs),
                type = e.TypeLabel,
                input = e.Input.Label,
                note_index = e.NoteIndex,
                pitch = e.Pitch >= 0 ? Music.NoteName(e.Pitch) : null,
            }),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>导出 trace 后端记录的实际派发序列。</summary>
    public static string TraceToCsv(TraceInputBackend backend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("time_ms,time,type,input,pitch");
        foreach (var e in backend.Entries)
        {
            sb.Append(e.WallMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
              .Append(Music.TimeLabel(e.WallMs)).Append(',')
              .Append(e.Type switch
              {
                  InputActionType.KeyDown => "key_down",
                  InputActionType.KeyUp => "key_up",
                  InputActionType.MouseDown => "mouse_down",
                  _ => "mouse_up",
              }).Append(',')
              .Append(e.Input.Label).Append(',')
              .Append(e.Pitch >= 0 ? Music.NoteName(e.Pitch) : "").AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>把 trace 渲染成对齐的文本（CLI 与 GUI 的 Preview 都用它）。</summary>
    public static string RenderTrace(IEnumerable<InputTraceEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            string note = e.Pitch >= 0 ? $"   (note {e.NoteIndex} {Music.NoteName(e.Pitch)})" : "";
            sb.AppendLine($"{Music.TimeLabel(e.WallMs)}  {e.Input.Label,-12} {e.ActionLabel}{note}");
        }
        return sb.ToString();
    }

    /// <summary>把整个计划渲染成文本，用于 CLI 预览。</summary>
    public static string RenderPlan(PlaybackPlan plan)
        => RenderTrace(plan.Events.Select(e => new InputTraceEntry(e.TimeMs, e.Type, e.Input, e.NoteIndex, e.Pitch)));
}
