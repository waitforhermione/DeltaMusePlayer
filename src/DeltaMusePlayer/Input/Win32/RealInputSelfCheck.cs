namespace DeltaMusePlayer.Input.Win32;

using DeltaMusePlayer.Core;
using DeltaMusePlayer.Mapping;
using DeltaMusePlayer.Playback;
using DeltaMusePlayer.Profiles;

/// <summary>
/// M2 的离线自检：**不发送任何真实输入**，只做两件事：
///
///   1. 把键位方案里的每个逻辑键解析成 VK / 扫描码，打印成一张表
///      —— 验证「logical key → virtual key → scan code」这条链路，尤其是 OEM 逗号键。
///   2. 生成一条诊断用 <see cref="PlaybackPlan"/>（Notepad 打 zxcvbnm, 的极短旋律，
///      以及一条覆盖半音 / 升八度 / 降八度的短旋律），并输出「如果真跑，会发出哪些 Win32 调用」。
///
/// 第 2 步是需求 29/30 的离线前置：用户先看这张表，确认无误，再切到 Notepad 跑真实输入。
/// </summary>
public static class RealInputSelfCheck
{
    /// <summary>Notepad 键盘验收用的 8 个音：恰好产出 Z X C V B N M , </summary>
    public static readonly int[] NotepadRunPitches = { 60, 62, 64, 65, 67, 69, 71, 72 };

    /// <summary>覆盖普通键 / 半音 / 升八度 / 降八度的短旋律。</summary>
    public static readonly int[] ModifierTestPitches = { 60, 62, 64, 67, 72, 73, 74, 48 };

    /// <summary>
    /// 鼠标三个键的诊断序列。**不要在普通窗口里跑这个**：
    /// 它会真的点左/中/右键。只在专门的测试窗口（或干脆不用）里跑。
    /// </summary>
    public static readonly int[] MouseTestPitches = { 48, 49, 84, 85, 61, 62 };

    /// <summary>把方案里出现的所有逻辑输入解析成映射；失败的以错误文本返回。</summary>
    public static (IReadOnlyList<KeyMapping> Mappings, IReadOnlyList<string> Errors) ResolveProfileKeys(
        InstrumentProfile profile, IWin32InputApi api)
    {
        var mappings = new List<KeyMapping>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string key in profile.Keys)
        {
            string name = InputId.NormalizeKeyName(key);
            if (!seen.Add(name)) continue;

            if (VirtualKeyMap.TryResolve(name, api, out var mapping)) mappings.Add(mapping);
            else errors.Add($"{name}: 无法解析成 VK / 扫描码");
        }

        foreach (var (role, raw) in profile.Modifiers.Bound())
        {
            if (!InputId.TryParse(raw, out var id)) continue;
            if (id.IsMouse) continue;                       // 鼠标键走 MOUSEINPUT，不需要扫描码
            if (!seen.Add(id.Name)) continue;

            if (VirtualKeyMap.TryResolve(id.Name, api, out var mapping)) mappings.Add(mapping);
            else errors.Add($"{id.Name}（{role}）: 无法解析成 VK / 扫描码");
        }

        return (mappings, errors);
    }

    /// <summary>把键位表渲染成文本。</summary>
    public static string RenderKeyMap(InstrumentProfile profile, IWin32InputApi api)
    {
        var (mappings, errors) = ResolveProfileKeys(profile, api);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"键位方案：{profile.Name}");
        sb.AppendLine("逻辑键 → 虚拟键码 → 扫描码（扫描码由 MapVirtualKeyW 取得，不硬编码）");
        sb.AppendLine();
        foreach (var m in mappings) sb.AppendLine("  " + m);

        sb.AppendLine();
        sb.AppendLine("修饰键（鼠标，不需要扫描码）：");
        foreach (var (role, raw) in profile.Modifiers.Bound())
        {
            if (!InputId.TryParse(raw, out var id)) continue;
            if (!id.IsMouse) continue;
            string label = role switch
            {
                "octave_down" => "降八度",
                "octave_up" => "升八度",
                "semitone" => "升半音",
                _ => role,
            };
            sb.AppendLine($"  {label,-8} {id.Label}");
        }

        if (errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("解析失败：");
            foreach (var e in errors) sb.AppendLine("  ! " + e);
        }

        return sb.ToString();
    }

    /// <summary>用给定的音高序列生成一条诊断计划（走与正式播放完全相同的编译链路）。</summary>
    public static PlaybackPlan BuildDiagnosticPlan(
        InstrumentProfile profile, IEnumerable<int> pitches, double noteSeconds = 0.15, double gapSeconds = 0.05)
    {
        var notes = new List<PlaybackNote>();
        double t = 0;
        foreach (int p in pitches)
        {
            notes.Add(new PlaybackNote(p, t, noteSeconds, 100));
            t += noteSeconds + gapSeconds;
        }

        var config = PlaybackConfig.Default.Clone();
        config.TrimLeadingSilence = false;
        config.Validate();

        var mapper = new NoteMapper(profile);
        var plan = new PlaybackPlanner(mapper, config).Compile(notes, speed: 1.0);

        if (plan.Report.UnplayableCount > 0)
            throw new InvalidOperationException(
                $"自检旋律里有 {plan.Report.UnplayableCount} 个音在当前键位方案下弹不出来：" +
                string.Join(" ", plan.Notes.Where(n => !n.Playable).Select(n => Music.NoteName(n.Pitch))));

        return plan;
    }

    /// <summary>
    /// 鼠标诊断计划：每个音只带一种鼠标修饰键，且音键故意选不会重复的组合，
    /// 好让 trace 里清楚看到 LEFT / MIDDLE / RIGHT 的 down 与 up。
    /// </summary>
    public static PlaybackPlan BuildMouseTestPlan(InstrumentProfile profile, double noteSeconds = 0.25)
    {
        var notes = new List<PlaybackNote>();
        double t = 0;
        foreach (int p in MouseTestPitches)
        {
            notes.Add(new PlaybackNote(p, t, noteSeconds, 100));
            t += noteSeconds + 0.3;
        }

        var config = PlaybackConfig.Default.Clone();
        config.TrimLeadingSilence = false;
        config.Validate();

        var plan = new PlaybackPlanner(new NoteMapper(profile), config).Compile(notes, 1.0);
        if (plan.Report.UnplayableCount > 0)
            throw new InvalidOperationException(
                "鼠标诊断序列里有弹不出来的音：" +
                string.Join(" ", plan.Notes.Where(n => !n.Playable).Select(n => Music.NoteName(n.Pitch))));
        return plan;
    }

    /// <summary>统计一份计划里用到了哪几种鼠标键（用于确认覆盖了 left / middle / right）。</summary>
    public static IReadOnlyList<string> MouseButtonsUsed(PlaybackPlan plan)
        => plan.Events.Where(e => e.Input.IsMouse)
                      .Select(e => e.Input.Label)
                      .Distinct()
                      .OrderBy(x => x, StringComparer.Ordinal)
                      .ToArray();

    /// <summary>把计划渲染成「预计会发出的 Win32 调用」清单。
    /// 用于真实输入之前的离线核对：看的是**将发送什么**，而不是真的发出去。
    /// </summary>
    public static string RenderPlannedWin32Calls(PlaybackPlan plan, InstrumentProfile profile, IWin32InputApi api)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"事件 {plan.EventCount} 条；总时长 {Music.TimeLabel(plan.WallTotalMs)}；速度 {plan.Speed:0.##}x");
        sb.AppendLine("（下面每一行都是 WindowsInputBackend 会真正调用的 Win32 注入）");
        sb.AppendLine();

        foreach (var e in plan.Events)
        {
            string note = e.Pitch >= 0 ? $"  (note {e.NoteIndex} {Music.NoteName(e.Pitch)})" : "";
            if (e.Input.IsMouse)
            {
                MouseButtonKind kind = e.Input.Name switch
                {
                    "Left" => MouseButtonKind.Left,
                    "Middle" => MouseButtonKind.Middle,
                    _ => MouseButtonKind.Right,
                };
                uint flags = Win32InputConstants.MouseFlag(kind, up: !e.IsDown);
                sb.AppendLine($"{Music.TimeLabel(e.TimeMs)}  SendInput MOUSE  dwFlags=0x{flags:X4} " +
                              $"{e.Input.Label} {(e.IsDown ? "DOWN" : "UP")}{note}");
            }
            else
            {
                if (!VirtualKeyMap.TryResolve(e.Input.Name, api, out var m))
                {
                    sb.AppendLine($"{Music.TimeLabel(e.TimeMs)}  !! 无法解析 {e.Input.Name}{note}");
                    continue;
                }

                uint flags = Win32InputConstants.KeyboardFlags(keyUp: !e.IsDown, extendedKey: m.ExtendedKey);
                sb.AppendLine($"{Music.TimeLabel(e.TimeMs)}  SendInput KEYBD   wVk=0x00 wScan=0x{m.ScanCode:X2} " +
                              $"dwFlags=0x{flags:X4}  {e.Input.Label} {(e.IsDown ? "DOWN" : "UP")}{note}");
            }
        }

        return sb.ToString();
    }

    /// <summary>CSV 导出：时间,类型,输入,VK,scan,extended。</summary>
    public static string RenderCsv(PlaybackPlan plan, IWin32InputApi api)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("time_ms,time,type,input,vk,scan,extended");
        foreach (var e in plan.Events)
        {
            string vk = "", scan = "", ext = "";
            if (!e.Input.IsMouse && VirtualKeyMap.TryResolve(e.Input.Name, api, out var m))
            {
                vk = $"0x{m.VirtualKey:X2}";
                scan = $"0x{m.ScanCode:X2}";
                ext = m.ExtendedKey ? "1" : "0";
            }

            sb.Append(e.TimeMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(Music.TimeLabel(e.TimeMs)).Append(',')
              .Append(e.TypeLabel).Append(',')
              .Append(e.Input.Label).Append(',')
              .Append(vk).Append(',').Append(scan).Append(',').Append(ext).AppendLine();
        }
        return sb.ToString();
    }
}
