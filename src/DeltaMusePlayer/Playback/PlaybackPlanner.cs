namespace DeltaMusePlayer.Playback;

using DeltaMusePlayer.Core;
using DeltaMusePlayer.Mapping;
using DeltaMusePlayer.Profiles;

/// <summary>
/// 把一条秒时间轴的旋律编译成确定性的 <see cref="PlaybackPlan"/>。
///
/// 编译分四步：
///   1. 映射：音高 → 弹法（不可演奏的音跳过并记 warning；strict 模式直接拒绝）。
///   2. 时间规整：最短按住、音间间隔、同键重触发间隔、重叠处理。全部在**物理时间**上判断。
///   3. 事件生成：修饰键状态机（保持、切换先松后按、正反八度不共存）→ 生成按下/抬起事件。
///   4. 收尾与换算：稳定排序，把音乐时间换算成播放位置毫秒（墙钟）。
///
/// 编译产物是纯数据：不含时钟、不含任何输入发送。同一条旋律永远编译出同一份计划。
/// </summary>
public sealed class PlaybackPlanner
{
    private readonly PlaybackConfig _config;
    private readonly NoteMapper _mapper;

    private readonly bool _hasOctaveDown;
    private readonly bool _hasOctaveUp;
    private readonly bool _hasSemitone;
    private readonly InputId _octaveDown;
    private readonly InputId _octaveUp;
    private readonly InputId _semitone;

    public PlaybackPlanner(NoteMapper mapper, PlaybackConfig config)
    {
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _config = (config ?? PlaybackConfig.Default).Clone();
        _config.Validate();

        var p = mapper.Profile;
        _hasOctaveDown = InputId.TryParse(p.Modifiers.OctaveDown, out _octaveDown);
        _hasOctaveUp = InputId.TryParse(p.Modifiers.OctaveUp, out _octaveUp);
        _hasSemitone = InputId.TryParse(p.Modifiers.Semitone, out _semitone);
    }

    public PlaybackConfig Config => _config;

    public PlaybackPlan Compile(IEnumerable<PlaybackNote> notes, double speed = 1.0)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed), "速度必须大于 0。");
        double speedSafe = speed;

        var list = notes.ToList();
        if (_config.TrimLeadingSilence && list.Count > 0)
            list = TrimLeadingSilence(list);

        var mapping = _mapper.MapAll(list);
        var warnings = new List<string>();

        int overlapCount = CountOverlaps(list);
        var playable = mapping.Notes.Where(n => n.IsPlayable).ToList();
        var unplayable = mapping.Notes.Where(n => !n.IsPlayable).ToList();

        foreach (var group in unplayable.GroupBy(n => n.SkipReason))
        {
            var sample = group.Take(4).Select(n => $"{Music.NoteName(n.Pitch)}@{Music.TimeLabel(n.StartSeconds * 1000)}");
            warnings.Add($"有 {group.Count()} 个音弹不出来（{group.Key}）：{string.Join(" ", sample)}" +
                         (group.Count() > 4 ? " …" : ""));
        }

        if (unplayable.Count > 0 && _config.Strict)
            throw new InvalidOperationException(
                $"严格模式拒绝播放：{unplayable.Count} 个音超出可演奏范围（{unplayable[0].SkipReason}）。" +
                "请调整键位方案，或关掉严格模式让这些音被跳过。");

        if (overlapCount > 0)
            warnings.Add(
                $"检测到 {overlapCount} 处音符重叠（疑似复音）。口琴是单音乐器，" +
                $"已按 {_config.Overlap} 策略排成单声部；本工具不做旋律提取，请确认上游 DeltaMuse 的输出。");

        // ---------------------------------------------------------------- 1) 时间规整（音乐毫秒）
        // 物理毫秒 → 音乐毫秒：乘速度。这样调度时再按速度积分回去，边沿间隔就是恒定的物理时间。
        double leadMusic = ToMusicMs(_config.ModifierLeadMs, speedSafe);
        double releaseDelayMusic = ToMusicMs(_config.ModifierReleaseDelayMs, speedSafe);
        double transitionGapMusic = ToMusicMs(_config.ModifierTransitionGapMs, speedSafe);
        double releaseGapMusic = ToMusicMs(_config.ReleaseGapMs, speedSafe);
        double retriggerGapMusic = ToMusicMs(_config.SameKeyRetriggerGapMs, speedSafe);
        double minHoldMusic = ToMusicMs(_config.MinimumKeyHoldMs, speedSafe);
        double handoffMusic = releaseDelayMusic + transitionGapMusic + leadMusic + ToMusicMs(1.0, speedSafe);

        var slots = new List<NoteSlot>(playable.Count);
        for (int sourceIndex = 0; sourceIndex < playable.Count; sourceIndex++)
        {
            var n = playable[sourceIndex];
            var b = n.Binding!;
            slots.Add(new NoteSlot
            {
                Note = n,
                SourceIndex = sourceIndex,
                Binding = b,
                Modifiers = b.Modifiers(_mapper.Profile).ToArray(),
                Start = n.StartSeconds * 1000.0,
                End = n.EndSeconds * 1000.0,
            });
        }

        // 播放起点归位：计划必须从时间轴 0 开始，但首音带修饰键时它会落在负时间上。
        // 处理办法是给整条时间轴加一个**前置留白**（而不是把首音平移到 0）：
        // 这一小段留白在播放开始时被消耗掉，谱面时间不会被削，音符之间的相对关系也不动。
        // 留白必须在这里加 —— 放到收尾阶段再加，修饰键的释放与音键的先后就乱了。
        // 只有「首音确实要按修饰键」时才需要这段留白；首音没有修饰键的话
        // 它的按下时刻就是 s.Start，直接让它落在 0 上，不要凭空插一段静音。
        double leadInMs = 0;
        if (slots.Count > 0 && slots[0].Modifiers.Count > 0)
        {
            double firstPress = slots[0].Start - leadMusic;
            if (firstPress < 0) leadInMs = -firstPress;
        }
        double cursor = double.NegativeInfinity;      // 前一个音的自然结束
        double prevStart = double.NegativeInfinity;   // 前一个音的起点
        string? cursorKey = null;                     // 前一个音的音键
        IReadOnlyList<InputId>? cursorMods = null;    // 前一个音的修饰键集合
        int oppositeOctaveTransitions = 0;

        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            bool hasPrevious = !double.IsNegativeInfinity(cursor);

            // 这一对音之间至少要让出多少音乐时间。
            double needGap = releaseGapMusic;
            if (cursorMods is not null && ModifierSetChanged(cursorMods, s.Modifiers))
            {
                // 换修饰键要「先松后按」：释放延迟 + 切换间隔 + 新的提前量。
                needGap = Math.Max(needGap, handoffMusic);
            }

            // 降八度 → 升八度（或反过来）：两个键绝不能同时按着。
            // 这时下一颗音的第一件事就是按下新的八度键，所以必须等旧键完全松开 ——
            // 需要的间隔 = 释放延迟 + 归位间隔 + 新的提前量，比普通换键还要长一点。
            int cursorOctave = cursorMods is null ? 0 : OctaveSignOf(cursorMods, _octaveDown, _octaveUp);
            if (cursorMods is not null && cursorOctave * s.OctaveSign < 0)
            {
                needGap = Math.Max(needGap,
                    releaseDelayMusic + 2 * transitionGapMusic + leadMusic + ToMusicMs(1.0, speedSafe));
                oppositeOctaveTransitions++;
            }
            if (string.Equals(cursorKey, s.KeyId.Name, StringComparison.Ordinal))
                needGap = Math.Max(needGap, retriggerGapMusic);

            bool overlapped = hasPrevious && s.Start < cursor;
            // 前音实际持有的结束时刻（最短按住会把它拉长）。
            double prevHeldEnd = hasPrevious ? Math.Max(prevStart + minHoldMusic, cursor) : 0;

            if (_config.Overlap == OverlapPolicy.Truncate)
            {
                // 截短模式：把**前音**截到「后音按下之前」，后音的时间不动。
                // 下限是前音的最短按住，所以绝不会压出零时长按键。
                if (hasPrevious && s.Start < prevHeldEnd + needGap)
                {
                    var prev = slots[i - 1];
                    double allowed = Math.Max(prev.Start + minHoldMusic, s.Start - needGap);
                    if (allowed < prev.End)
                    {
                        prev.End = allowed;
                        s.PushedByOverlap++;
                    }
                }
            }
            else if (hasPrevious && s.Start < cursor + needGap)
            {
                // 默认（串行化）：只顺延「挨得太近」的音，排到前音按满最短时长 + 音间间隔之后。
                s.Start = Math.Max(cursor, prevStart + minHoldMusic) + needGap;
                if (string.Equals(cursorKey, s.KeyId.Name, StringComparison.Ordinal)) s.PushedByRetrigger++;
                else s.PushedByGap++;
                if (overlapped) s.PushedByOverlap++;
            }

            // 最短按住：过短的音拉到下限；本来就够长的音保持原样。
            if (s.End - s.Start < minHoldMusic)
            {
                s.End = s.Start + minHoldMusic;
                s.MinHoldApplied = true;
            }

            cursor = s.End;
            prevStart = s.Start;
            cursorKey = s.KeyId.Name;
            cursorMods = s.Modifiers;
        }

        // ---------------------------------------------------------------- 2) 事件生成（音乐毫秒）
        //
        // 修饰键状态机（这一段最容易出错，所以写得直白）：
        //  * 每个输入同一时刻只有一个状态：held / not held。
        //  * 「换修饰键」= 先松掉本音与下一颗音都不需要的，再按下新需要的；
        //    两者之间至少隔一个切换间隔（时间规整阶段已把两音起点拉开这么多）。
        //  * 八度键（降/升）绝不共存；半音键可以和任一八度键同时按着。
        var events = new List<InputEvent>(slots.Count * 4 + 8);
        bool octaveDownHeld = false, octaveUpHeld = false, semitoneHeld = false;
        int minHoldExtended = 0;
        int lastOctaveIndex = -1;      // 最后用到八度修饰键的那颗音
        int lastSemitoneIndex = -1;    // 最后用到半音修饰键的那颗音
        double lastOctaveEnd = 0;      // 那颗音的结束时刻（音乐毫秒，含 leadIn）
        double lastSemitoneEnd = 0;

        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            bool hasNext = i + 1 < slots.Count;
            double pressT = s.Start - leadMusic;
            int nextOctave = hasNext ? slots[i + 1].OctaveSign : 0;
            bool nextSemitone = hasNext && slots[i + 1].Binding.Semitone;
            double nextPressT = hasNext ? slots[i + 1].Start - leadMusic : double.PositiveInfinity;

            void PressMod(InputId id) => events.Add(new InputEvent(pressT, InputActionType.MouseDown, id, s.SourceIndex, s.Binding.Pitch));
            void ReleaseMod(InputId id, double t) => events.Add(new InputEvent(t, InputActionType.MouseUp, id, s.SourceIndex, s.Binding.Pitch));

            // 修饰键的释放时刻：从「最后用到它的那颗音」的结束算起，
            // 换键时再加一个切换间隔；但绝不晚于下一颗音的修饰键按下时刻。
            double ModRelease(double lastEnd)
            {
                double t = lastEnd + releaseDelayMusic;
                if (hasNext)
                {
                    t = Math.Min(t + transitionGapMusic, nextPressT - ToMusicMs(1.0, speedSafe));
                    t = Math.Max(t, lastEnd + releaseDelayMusic);
                }
                return t;
            }

            // (a) 先松掉本音与下一颗音都不需要的修饰键。
            //     事件按时间排序，所以它们真实落在对应那颗音抬起之后。
            if (semitoneHeld && !s.Binding.Semitone && !nextSemitone)
            {
                ReleaseMod(_semitone, ModRelease(lastSemitoneEnd));
                semitoneHeld = false;
            }
            if (octaveDownHeld && s.OctaveSign != -1 && nextOctave != -1)
            {
                ReleaseMod(_octaveDown, ModRelease(lastOctaveEnd));
                octaveDownHeld = false;
            }
            if (octaveUpHeld && s.OctaveSign != 1 && nextOctave != 1)
            {
                ReleaseMod(_octaveUp, ModRelease(lastOctaveEnd));
                octaveUpHeld = false;
            }

            // (b) 再按下本音需要的修饰键。
            if (s.OctaveSign < 0 && _hasOctaveDown)
            {
                if (!octaveDownHeld) PressMod(_octaveDown);
                octaveDownHeld = true;
                lastOctaveIndex = i;
                lastOctaveEnd = s.End;
            }
            else if (s.OctaveSign > 0 && _hasOctaveUp)
            {
                if (!octaveUpHeld) PressMod(_octaveUp);
                octaveUpHeld = true;
                lastOctaveIndex = i;
                lastOctaveEnd = s.End;
            }

            if (s.Binding.Semitone && _hasSemitone)
            {
                if (!semitoneHeld) PressMod(_semitone);
                semitoneHeld = true;
                lastSemitoneIndex = i;
                lastSemitoneEnd = s.End;
            }

            // (c) 音键：明确的按下 / 抬起，绝不依赖操作系统的自动重复。
            events.Add(new InputEvent(s.Start, InputActionType.KeyDown, s.KeyId, s.SourceIndex, s.Binding.Pitch));
            events.Add(new InputEvent(Math.Max(s.End, s.Start + minHoldMusic), InputActionType.KeyUp, s.KeyId, s.SourceIndex, s.Binding.Pitch));
            if (s.MinHoldApplied) minHoldExtended++;
        }
        // (d) 收尾：把还按着的修饰键放掉。半音键先松、八度键后松。
        //     基准是「最后按下它的那颗音」的结束时间，不能一律用最后一颗音。
        double TailTime(int lastPressIndex)
            => slots[lastPressIndex].End + releaseDelayMusic + transitionGapMusic
               + ToMusicMs(_config.FinishReleaseDelayMs, speedSafe);

        double semitoneTail = semitoneHeld && lastSemitoneIndex >= 0 ? TailTime(lastSemitoneIndex) : double.NegativeInfinity;
        double octaveTail = double.NegativeInfinity;
        if (octaveDownHeld || octaveUpHeld)
        {
            octaveTail = lastOctaveIndex >= 0 ? TailTime(lastOctaveIndex) : 0;
            if (semitoneTail > double.NegativeInfinity)
                octaveTail = Math.Max(octaveTail, semitoneTail + transitionGapMusic);
        }

        if (octaveDownHeld && _hasOctaveDown)
            events.Add(new InputEvent(octaveTail, InputActionType.MouseUp, _octaveDown, slots[lastOctaveIndex].SourceIndex, -1));
        if (octaveUpHeld && _hasOctaveUp)
            events.Add(new InputEvent(octaveTail, InputActionType.MouseUp, _octaveUp, slots[lastOctaveIndex].SourceIndex, -1));
        if (semitoneHeld && _hasSemitone)
            events.Add(new InputEvent(semitoneTail, InputActionType.MouseUp, _semitone, slots[lastSemitoneIndex].SourceIndex, -1));


        return Finalize(events, mapping, slots, speedSafe, warnings, minHoldExtended, oppositeOctaveTransitions, leadInMs);
    }

    // ------------------------------------------------------------------ 收尾

    private PlaybackPlan Finalize(
        List<InputEvent> events,
        MappingResult mapping,
        List<NoteSlot> slots,
        double speed,
        List<string> warnings,
        int minHoldExtended,
        int oppositeOctaveTransitions,
        double leadInMs)
    {

        // 稳定、确定的排序：时间 → 类型优先级 → 输入名 → 音符序号。
        events.Sort((a, b) => a.SortKey.CompareTo(b.SortKey));


        // 到这里为止事件时刻都是**音乐毫秒**（= 物理秒 × 1000）。
        // 计划对外交付的是**播放位置毫秒**（墙钟）：UI 进度条、trace 导出、调度比较用的都是它。
        // 位置 = 音乐时间 / speed。边沿间隔在编译时已经乘过 speed，除回来正好是物理毫秒。
        double musicToWall(double musicMs) => musicMs / speed;

        double leadInWall = musicToWall(leadInMs);
        double leadIn = leadInWall;
        for (int i = 0; i < events.Count; i++)
            events[i] = events[i] with { TimeMs = musicToWall(events[i].TimeMs) + leadInWall };

        double wallTotal = events.Count > 0 ? events[^1].TimeMs : 0;

        // 音符落点用同一套换算，UI 上显示的位置才与事件流一致。
        var planned = new List<PlannedNote>(mapping.Notes.Count);
        int slotIndex = 0;
        for (int i = 0; i < mapping.Notes.Count; i++)
        {
            var m = mapping.Notes[i];
            if (!m.IsPlayable)
            {
                planned.Add(new PlannedNote(i, m.Pitch,
                    musicToWall(m.StartSeconds * 1000), musicToWall(m.EndSeconds * 1000),
                    false, m.SkipReason, -1, "", false, 0, Array.Empty<InputId>()));
                continue;
            }

            var s = slots[slotIndex++];
            planned.Add(new PlannedNote(i, m.Pitch, musicToWall(s.Start + leadInMs), musicToWall(s.End + leadInMs), true, "",
                s.Binding.Lane, s.Binding.Key, s.Binding.Semitone, s.Binding.OctaveOffset,
                s.Modifiers));
        }

        double maxPush = slots.Count == 0 ? 0 : slots.Max(s => s.Start - s.Note.StartSeconds * 1000);

        var report = new PlanCompileReport
        {
            NoteCount = mapping.Notes.Count,
            PlayableCount = mapping.PlayableCount,
            UnplayableCount = mapping.UnplayableCount,
            PushedByOverlap = slots.Sum(s => s.PushedByOverlap),
            PushedByRetrigger = slots.Sum(s => s.PushedByRetrigger),
            PushedByGap = slots.Sum(s => s.PushedByGap),
            MinHoldExtendedCount = minHoldExtended,
            OppositeOctaveTransitions = oppositeOctaveTransitions,
            MaxPushMs = maxPush,
        };

        return new PlaybackPlan
        {
            Events = events,
            Notes = planned,
            Speed = speed,
            LeadInMs = leadIn,
            Mapping = mapping,
            Report = report,
            Warnings = warnings,
            WallTotalMs = wallTotal,
        };
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>物理毫秒 → 音乐毫秒。</summary>
    private static double ToMusicMs(double physicalMs, double speed)
        => physicalMs * (speed <= 0 ? 1.0 : speed);

    private static List<PlaybackNote> TrimLeadingSilence(List<PlaybackNote> notes)
    {
        double first = notes.Min(n => n.StartSeconds);
        if (first <= 0.0005) return notes;
        return notes.Select(n => n with { StartSeconds = Math.Max(0, n.StartSeconds - first) }).ToList();
    }

    private static int CountOverlaps(List<PlaybackNote> notes)
    {
        int count = 0;
        double furthestEnd = double.NegativeInfinity;
        foreach (var n in notes.OrderBy(x => x.StartSeconds))
        {
            if (n.StartSeconds < furthestEnd - 0.0005) count++;
            if (n.EndSeconds > furthestEnd) furthestEnd = n.EndSeconds;
        }
        return count;
    }

    private static bool ModifierSetChanged(IReadOnlyList<InputId> a, IReadOnlyList<InputId> b)
    {
        if (a.Count != b.Count) return true;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return true;
        return false;
    }

    /// <summary>修饰集合里的八度方向：-1 降 / 0 无或只有半音 / +1 升。仅用于统计。</summary>
    private static int OctaveSignOf(IReadOnlyList<InputId> mods, InputId down, InputId up)
    {
        if (up.Name.Length > 0 && mods.Contains(up)) return 1;
        if (down.Name.Length > 0 && mods.Contains(down)) return -1;
        return 0;
    }
}
