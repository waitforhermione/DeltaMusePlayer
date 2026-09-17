using DeltaMusePlayer.Core;
using DeltaMusePlayer.Playback;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// 播放计划编译器的测试。全部是纯逻辑：给一串秒时间轴的音，断言事件序列与时刻。
/// 默认配置见 <see cref="TestKit.Config"/>（不剪前导静音，间隔取整数毫秒便于断言）。
/// </summary>
public sealed class PlaybackPlannerTests
{
    [Fact]
    public void SingleNoteProducesExactlyTwoEvents()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.5, 0.4) });

        Assert.Equal(new[] { "Z" }, plan.Notes.Select(n => n.Key).Distinct());
        Assert.Equal(0, plan.Report.UnplayableCount);
        Assert.Equal(1, plan.Report.PlayableCount);

        // 一颗音、没有任何修饰键 → 就是一对 key_down / key_up
        Assert.Equal(new[] { "Z:DOWN", "Z:UP" }, TestKit.Actions(plan));
        Assert.Equal(500.0, plan.Events[0].TimeMs, 3);
        Assert.Equal(900.0, plan.Events[1].TimeMs, 3);
    }

    [Fact]
    public void ModifierIsPressedBeforeTheKeyAndReleasedAfterIt()
    {
        // F5 = 77 → V + RightMouse（lane 3 落在升八度档）。
        var plan = TestKit.Plan(new[] { TestKit.Note(77, 0.5, 0.4) });

        Assert.Equal(new[] { "MOUSE_RIGHT:DOWN", "V:DOWN", "V:UP", "MOUSE_RIGHT:UP" }, TestKit.Actions(plan));

        double t(int i) => plan.Events[i].TimeMs;
        Assert.Equal(15.0, t(1) - t(0), 3);   // 修饰键比音键早 15ms
        Assert.Equal(400.0, t(2) - t(1), 3);  // 按住整个时值
        Assert.Equal(485.0, t(0), 3);         // 修饰键的绝对时刻
        Assert.Equal(500.0, t(1), 3);         // 音键按谱面时刻按下
        Assert.Equal(900.0, t(2), 3);         // 按满时值后抬起
        Assert.Equal(35.0, t(3) - t(2), 3);   // 音键抬起 → 释延迟 10ms + 切换间隔 5ms + 收尾余量 20ms
    }

    [Fact]
    public void EventOrderIsDeterministicAcrossRepeatedCompiles()
    {
        var notes = TestKit.Sequence(new[] { 60, 62, 64, 65, 67, 69, 71, 72 });
        var a = TestKit.ActionsWithTime(TestKit.Plan(notes));
        var b = TestKit.ActionsWithTime(TestKit.Plan(notes));
        Assert.Equal(a, b);
    }

    [Fact]
    public void SameModifierIsNotReleasedBetweenTwoNotesThatBothNeedIt()
    {
        // D5 与 E5（74 / 76）都在升八度档（lane 1 / lane 2）、都用 RightMouse。
        // 中间不许出现 RightMouse 的 up/down —— 这正是「修饰键状态机」要避免的抖动。
        var plan = TestKit.Plan(new[] { TestKit.Note(74, 0.0, 0.3), TestKit.Note(76, 0.5, 0.3) });

        // 修饰键在两颗音之间一直按着：整份计划里 RightMouse 只按下一次、只抬起一次，
        // 而且抬起的时刻在最后一颗音结束之后。
        Assert.Equal(
            new[] { "MOUSE_RIGHT:DOWN", "X:DOWN", "X:UP", "C:DOWN", "C:UP", "MOUSE_RIGHT:UP" },
            TestKit.Actions(plan));

        Assert.Single(plan.Events.Where(e => e.Input == TestKit.Right && e.Type == InputActionType.MouseDown));
        var rightUp = plan.Events.Single(e => e.Input == TestKit.Right && e.Type == InputActionType.MouseUp);
        var cUp = plan.Events.Single(e => e.Input == TestKit.Key("C") && e.Type == InputActionType.KeyUp);
        Assert.True(rightUp.TimeMs > cUp.TimeMs);
    }

    [Fact]
    public void SemitoneModifierIsRetainedAcrossConsecutiveSharpNotes()
    {
        // C#4 与 D#4 都需要半音键。
        var plan = TestKit.Plan(new[] { TestKit.Note(61, 0.0, 0.3), TestKit.Note(63, 0.5, 0.3) });

        Assert.Equal(
            new[] { "MOUSE_MIDDLE:DOWN", "Z:DOWN", "Z:UP", "X:DOWN", "X:UP", "MOUSE_MIDDLE:UP" },
            TestKit.Actions(plan));

        // 半音键全程只按一次
        Assert.Single(plan.Events.Where(e => e.Input == TestKit.Middle && e.Type == InputActionType.MouseDown));
        Assert.Single(plan.Events.Where(e => e.Input == TestKit.Middle && e.Type == InputActionType.MouseUp));
    }

    [Fact]
    public void OppositeOctaveModifiersNeverOverlapAndReconcileInOrder()
    {
        // 48（LeftMouse + Z）→ 84（RightMouse + ,）：必须 left up 在 right down 之前。
        // 注意 72 是基准八度的逗号键、不需要修饰键 —— 要跨八度得用 48 与 84。
        var plan = TestKit.Plan(new[] { TestKit.Note(48, 0.0, 0.3), TestKit.Note(84, 0.5, 0.3) });

        Assert.Equal(
            new[]
            {
                "MOUSE_LEFT:DOWN", "Z:DOWN", "Z:UP",
                "MOUSE_LEFT:UP", "MOUSE_RIGHT:DOWN", ",:DOWN", ",:UP", "MOUSE_RIGHT:UP",
            },
            TestKit.Actions(plan));

        AssertNeverBothOctaveModifiersHeld(plan);

        // 降八度必须先松开，再按升八度
        var leftUp = plan.Events.Single(e => e.Input == TestKit.Left && e.Type == InputActionType.MouseUp);
        var rightDown = plan.Events.Single(e => e.Input == TestKit.Right && e.Type == InputActionType.MouseDown);
        Assert.True(rightDown.TimeMs > leftUp.TimeMs,
            $"升八度必须在降八度松开之后按下：left up @ {leftUp.TimeMs}, right down @ {rightDown.TimeMs}");
    }

    [Fact]
    public void OppositeOctaveTransitionIsSerializedNotSimultaneous()
    {
        // 两个音挨得很近，时间规整阶段必须为「先松后按」让出足够的时间。
        var plan = TestKit.Plan(new[] { TestKit.Note(48, 0.0, 0.3), TestKit.Note(84, 0.3, 0.3) });

        var leftUp = plan.Events.First(e => e.Input == TestKit.Left && e.Type == InputActionType.MouseUp);
        var rightDown = plan.Events.First(e => e.Input == TestKit.Right && e.Type == InputActionType.MouseDown);

        Assert.True(rightDown.TimeMs > leftUp.TimeMs,
            $"升八度必须在降八度松开之后按下：left up @ {leftUp.TimeMs}, right down @ {rightDown.TimeMs}");
        Assert.True(rightDown.TimeMs - leftUp.TimeMs >= 0, "两个相反的八度修饰键之间不能有时间倒挂");
        AssertNeverBothOctaveModifiersHeld(plan);
    }

    [Fact]
    public void SemitoneAndOctaveModifiersCanBeHeldTogether()
    {
        // C#3 = 49 → LeftMouse + MiddleMouse + Z
        var plan = TestKit.Plan(new[] { TestKit.Note(49, 0.5, 0.3) });

        Assert.Equal(
            new[] { "MOUSE_LEFT:DOWN", "MOUSE_MIDDLE:DOWN", "Z:DOWN", "Z:UP", "MOUSE_MIDDLE:UP", "MOUSE_LEFT:UP" },
            TestKit.Actions(plan));
    }

    [Fact]
    public void SameKeyRepeatGetsAnExplicitReleaseAndGap()
    {
        // 需求 43：两个首尾相接的 Z 不能粘成一个长 Z。
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.1), TestKit.Note(60, 0.1, 0.1) });

        Assert.Equal(new[] { "Z:DOWN", "Z:UP", "Z:DOWN", "Z:UP" }, TestKit.Actions(plan));

        double gap = plan.Events[2].TimeMs - plan.Events[1].TimeMs;
        Assert.True(gap >= 12.0 - 1e-9, $"同键重触发间隔应 ≥ 12ms，实际 {gap}ms");
        Assert.Equal(1, plan.Report.PushedByRetrigger);
    }

    [Fact]
    public void SameKeyRepeatAlsoHonoursTheMinimumHold()
    {
        // 3ms 的重复音：最短按住会把前音拉到 30ms，后音跟着串行化。
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.003), TestKit.Note(60, 0.01, 0.003) });

        Assert.Equal(new[] { "Z:DOWN", "Z:UP", "Z:DOWN", "Z:UP" }, TestKit.Actions(plan));
        double hold = plan.Events[1].TimeMs - plan.Events[0].TimeMs;
        double gap = plan.Events[2].TimeMs - plan.Events[1].TimeMs;
        Assert.Equal(30.0, hold, 3);
        Assert.True(gap >= 12.0 - 1e-9);
    }

    [Fact]
    public void ShortNoteIsLengthenedToTheMinimumHold()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.5, 0.002) });   // 2ms 的音
        double hold = plan.Events[1].TimeMs - plan.Events[0].TimeMs;

        Assert.Equal(30.0, hold, 3);                       // 抬到最短按住
        Assert.Equal(1, plan.Report.MinHoldExtendedCount);

        // 本来就够长的音不该被改动
        var longPlan = TestKit.Plan(new[] { TestKit.Note(60, 0.5, 0.4) });
        Assert.Equal(400.0, longPlan.Events[1].TimeMs - longPlan.Events[0].TimeMs, 3);
        Assert.Equal(0, longPlan.Report.MinHoldExtendedCount);
    }

    [Fact]
    public void ZeroDurationNoteStillProducesARealKeyPress()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.5, 0.0) });
        Assert.Equal(2, plan.Events.Count);
        Assert.True(plan.Events[1].TimeMs > plan.Events[0].TimeMs, "绝不允许零时长按键");
    }

    [Fact]
    public void OverlappingNotesAreSerializedInsteadOfStacked()
    {
        // 第二个音在第一个音还没结束时就开始了：默认策略是把它顺延到前音安全释放之后。
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.5), TestKit.Note(62, 0.2, 0.2) });

        Assert.Equal(1, plan.Report.PushedByOverlap);
        Assert.Contains(plan.Warnings, w => w.Contains("重叠"));

        var zUp = plan.Events.First(e => e.Input == TestKit.Key("Z") && e.Type == InputActionType.KeyUp);
        var xDown = plan.Events.First(e => e.Input == TestKit.Key("X") && e.Type == InputActionType.KeyDown);
        Assert.True(xDown.TimeMs >= zUp.TimeMs + 15.0 - 1e-9,
            $"后音必须在音间间隔之后才按下：Z up @ {zUp.TimeMs}, X down @ {xDown.TimeMs}");
        AssertNoTwoKeysHeldAtOnce(plan);
    }

    [Fact]
    public void TruncatePolicyShortensThePreviousNoteInstead()
    {
        // 默认（Serialize）下前音不动；截短模式下同一个输入会得到更早的前音抬键。
        var notes = new[] { TestKit.Note(60, 0.0, 0.5), TestKit.Note(62, 0.2, 0.2) };

        double ZUpTime(PlaybackConfig config)
        {
            var plan = new PlaybackPlanner(TestKit.Mapper, config).Compile(notes);
            return plan.Events.First(e => e.Input == TestKit.Key("Z") && e.Type == InputActionType.KeyUp).TimeMs;
        }

        double serialize = ZUpTime(TestKit.Config());
        double truncate = ZUpTime(TestKit.Config(c => c.Overlap = OverlapPolicy.Truncate));

        Assert.InRange(serialize, 490.0, 520.0);   // 谱面时刻 + 可能的前置留白
        Assert.True(truncate <= serialize, $"截短模式下前音不该比串行化更晚释放：{truncate} vs {serialize}");

        // 真正重叠、且截到下限仍然早于原结束时间的场景：前音必须被缩短
        var late = new[] { TestKit.Note(60, 0.0, 2.0), TestKit.Note(62, 0.5, 0.2) };
        double LateZUp(OverlapPolicy policy)
        {
            var plan = new PlaybackPlanner(TestKit.Mapper, TestKit.Config(c => c.Overlap = policy)).Compile(late);
            return plan.Events.First(e => e.Input == TestKit.Key("Z") && e.Type == InputActionType.KeyUp).TimeMs;
        }

        Assert.InRange(LateZUp(OverlapPolicy.Serialize), 1990.0, 2020.0);
        Assert.True(LateZUp(OverlapPolicy.Truncate) < LateZUp(OverlapPolicy.Serialize),
            "截短模式下被后音压住的前音应该提前释放");

        var truncated = new PlaybackPlanner(TestKit.Mapper, TestKit.Config(c => c.Overlap = OverlapPolicy.Truncate))
            .Compile(notes);
        AssertNoTwoKeysHeldAtOnce(truncated);   // 无论如何都不允许两个音键同时按着
    }

    [Fact]
    public void UnplayablePitchesAreSkippedWithAWarningAndDoNotBreakThePlan()
    {
        var plan = TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.2), TestKit.Note(20, 0.3, 0.2), TestKit.Note(62, 0.6, 0.2) });

        Assert.Equal(2, plan.Report.PlayableCount);
        Assert.Equal(1, plan.Report.UnplayableCount);
        Assert.Contains(plan.Warnings, w => w.Contains("弹不出来"));
        Assert.Equal(new[] { "Z:DOWN", "Z:UP", "X:DOWN", "X:UP" }, TestKit.Actions(plan));

        var skipped = plan.Notes.Single(n => !n.Playable);
        Assert.Equal(20, skipped.Pitch);
        Assert.False(string.IsNullOrWhiteSpace(skipped.SkipReason));
    }

    [Fact]
    public void StrictModeRefusesToCompileWhenAnyNoteIsUnplayable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestKit.Plan(new[] { TestKit.Note(60, 0.0, 0.2), TestKit.Note(20, 0.3, 0.2) },
                tweak: c => c.Strict = true));

        Assert.Contains("严格模式", ex.Message);
    }

    [Fact]
    public void LeadingSilenceIsTrimmedOnlyWhenConfigured()
    {
        var notes = new[] { TestKit.Note(60, 1.2, 0.3), TestKit.Note(62, 1.6, 0.3) };

        // 默认不剪：第一个事件就在 1200ms（首音无修饰键）
        var kept = TestKit.Plan(notes);
        Assert.Equal(1200.0, kept.Events[0].TimeMs, 3);

        // 打开剪裁之后，第一个音落到 0 秒附近（首音没有修饰键，所以第一条事件就是它）
        var trimmed = TestKit.Plan(notes, tweak: c => c.TrimLeadingSilence = true);
        Assert.InRange(trimmed.Events[0].TimeMs, 0.0, 20.0);
    }

    [Fact]
    public void SpeedScalesMusicalIntervals()
    {
        var notes = new[] { TestKit.Note(60, 0.0, 0.5), TestKit.Note(62, 0.7, 0.5) };

        var normal = TestKit.Plan(notes, speed: 1.0);
        var fast = TestKit.Plan(notes, speed: 2.0);

        double NoteStart(PlaybackPlan p, string key)
            => p.Events.First(e => e.Input == TestKit.Key(key) && e.Type == InputActionType.KeyDown).TimeMs;

        // 用「音与音之间的间隔」做对比：绝对时刻里含有一段不随速度缩放的前置留白。
        double GapNormal = NoteStart(normal, "X") - NoteStart(normal, "Z");
        double GapFast = NoteStart(fast, "X") - NoteStart(fast, "Z");
        Assert.Equal(GapNormal / 2.0, GapFast, 3);
    }

    [Fact]
    public void PhysicalEdgeGapsAreNotCompressedBySpeed()
    {
        // 两个紧邻的音：X 的按下时刻由**物理**间隔决定，与播放速度无关。
        // 计划里存的是音乐毫秒，墙钟 = 音乐 / speed，所以墙钟差就是物理间隔。
        var notes = new[] { TestKit.Note(60, 0.0, 0.1), TestKit.Note(62, 0.1, 0.1) };

        static double WallGap(PlaybackPlan p)
        {
            var zUp = p.Events.First(e => e.Input == TestKit.Key("Z") && e.Type == InputActionType.KeyUp);
            var xDown = p.Events.First(e => e.Input == TestKit.Key("X") && e.Type == InputActionType.KeyDown);
            return xDown.TimeMs - zUp.TimeMs;
        }

        var normal = TestKit.Plan(notes, speed: 1.0);
        var fast = TestKit.Plan(notes, speed: 2.0);
        var slow = TestKit.Plan(notes, speed: 0.5);

        Assert.Equal(15.0, WallGap(normal), 3);                       // 音间间隔 15ms
        Assert.Equal(WallGap(normal), WallGap(fast), 3);
        Assert.Equal(WallGap(normal), WallGap(slow), 3);

        // 最短按住也是物理时间：抬键时刻 − 按下时刻 恒为 30ms（这里音本身只有 100ms，
        // 所以用一颗更短的音来验证）
        static double HoldWall(PlaybackPlan p)
        {
            var down = p.Events.First(e => e.Input == TestKit.Key("Z") && e.Type == InputActionType.KeyDown);
            var up = p.Events.First(e => e.Input == TestKit.Key("Z") && e.Type == InputActionType.KeyUp);
            return up.TimeMs - down.TimeMs;
        }

        var shortNotes = new[] { TestKit.Note(60, 0.0, 0.01) };
        Assert.Equal(30.0, HoldWall(TestKit.Plan(shortNotes, speed: 1.0)), 3);
        Assert.Equal(30.0, HoldWall(TestKit.Plan(shortNotes, speed: 2.0)), 3);
        Assert.Equal(30.0, HoldWall(TestKit.Plan(shortNotes, speed: 0.5)), 3);
    }

    [Fact]
    public void PlanAlwaysStartsAtZeroWallClock()
    {
        // 不剪前导静音时，音乐时间 0 就是曲子开头：F5 的修饰键提前量会落在 -15ms，
        // 计划必须原样保留这段提前量（而不是把首音平移到 0，那会把前导静音吃掉）。
        var kept = TestKit.Plan(new[] { TestKit.Note(77, 0.5, 0.3) });
        Assert.True(kept.Events[0].TimeMs >= 0, "事件时刻不允许是负数");
        Assert.Equal(485.0, kept.Events[0].TimeMs, 3);
        Assert.Equal("MOUSE_RIGHT:DOWN", TestKit.Actions(kept)[0]);

        // 从 0 秒就开始的音：提前量没有地方放，整张表右移到 0。
        var plan = TestKit.Plan(new[] { TestKit.Note(77, 0.0, 0.3) });
        Assert.Equal(0.0, plan.Events[0].TimeMs, 3);
        Assert.Equal("MOUSE_RIGHT:DOWN", TestKit.Actions(plan)[0]);
        Assert.Equal(15.0, plan.Events[1].TimeMs - plan.Events[0].TimeMs, 3);
    }

    [Fact]
    public void NaturalKeyInsideTheBaseOctaveNeedsNoModifierEvenIfAnOctaveFormExists()
    {
        // 67 = G4 就是基准八度的 B 键本身。canonical 规则第一条是「|八度偏移| 最小」，
        // 所以不按任何修饰键 —— 而不是「按右键 + F」。
        var plan = TestKit.Plan(new[] { TestKit.Note(67, 0.0, 0.3) });
        Assert.Equal(new[] { "B:DOWN", "B:UP" }, TestKit.Actions(plan));
    }

    [Fact]
    public void EmptyInputProducesAnEmptyPlanWithoutThrowing()
    {
        var plan = TestKit.Plan(Array.Empty<PlaybackNote>());

        Assert.Empty(plan.Events);
        Assert.Empty(plan.Notes);
        Assert.Equal(0, plan.Report.NoteCount);
        Assert.Equal(1.0, plan.Mapping.PlayableRatio, 6);
    }

    [Fact]
    public void ReportedPlanSummaryMatchesTheActualEventStream()
    {
        var plan = TestKit.Plan(TestKit.Sequence(new[] { 60, 62, 64 }));

        Assert.Equal(3, plan.Report.NoteCount);
        Assert.Equal(3, plan.Report.PlayableCount);
        Assert.Equal(6, plan.EventCount);
        Assert.Equal(plan.Events.Count, plan.EventCount);
    }

    [Fact]
    public void PolyphonyWarningIsEmittedForOverlappingInput()
    {
        var plan = TestKit.Plan(new[]
        {
            TestKit.Note(60, 0.0, 0.5),
            TestKit.Note(64, 0.1, 0.5),
            TestKit.Note(67, 0.2, 0.5),
        });

        Assert.Contains(plan.Warnings, w => w.Contains("复音"));
        AssertNoTwoKeysHeldAtOnce(plan);
    }

    // ------------------------------------------------------------------ 断言助手

    private static void AssertNeverBothOctaveModifiersHeld(PlaybackPlan plan)
    {
        bool left = false, right = false;
        foreach (var e in plan.Events)
        {
            if (e.Input == TestKit.Left) left = e.IsDown;
            if (e.Input == TestKit.Right) right = e.IsDown;
            Assert.False(left && right,
                $"在 {e.TimeMs:F3}ms 处 LeftMouse 与 RightMouse 同时处于按下状态");
        }
    }

    /// <summary>口琴是单音乐器：任何时刻最多一个音键按着。</summary>
    private static void AssertNoTwoKeysHeldAtOnce(PlaybackPlan plan)
    {
        var held = new HashSet<string>();
        foreach (var e in plan.Events)
        {
            if (e.Input.IsMouse) continue;
            if (e.IsDown) held.Add(e.Input.Label); else held.Remove(e.Input.Label);
            Assert.True(held.Count <= 1, $"在 {e.TimeMs:F3}ms 处同时按着多个音键：{string.Join("+", held)}");
        }
        Assert.Empty(held);
    }
}
