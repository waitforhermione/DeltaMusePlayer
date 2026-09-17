namespace DeltaMusePlayer.Input;

using DeltaMusePlayer.Core;

/// <summary>一次派发的时序记账（仅诊断模式使用）。</summary>
public sealed record DispatchTiming(
    double PlannedMs,
    double ActualMs,
    InputId Input,
    bool IsDown)
{
    /// <summary>实际比计划晚了多少毫秒（可为负）。</summary>
    public double JitterMs => ActualMs - PlannedMs;

    public override string ToString()
        => $"planned {PlannedMs,10:F3}ms  actual {ActualMs,10:F3}ms  jitter {JitterMs,+7:F3}ms  " +
           $"{Input.Label} {(IsDown ? "DOWN" : "UP")}";
}

/// <summary>
/// 派发时序观察者。真实播放（诊断模式）用它记录「计划时刻 vs 实际注入时刻」，
/// 用来判断当前 polling 参数够不够、要不要继续调。
///
/// 默认关闭：它只在被显式挂上时才有开销。
/// </summary>
public interface IDispatchTimingSink
{
    void OnDispatch(DispatchTiming timing);
}

/// <summary>
/// 时序统计：事件数、**时间轴抖动**（均值 / p95 / 最大）与整体起始偏移。
/// 一次播放结束后报告一次，**不**在播放过程中写大量日志。
///
/// 关键：倒计时、计划构建等预滚（pre-roll）会让所有事件整体后移一个恒定量。
/// 那不是抖动，而是起始偏移；它对听感没有影响（整段音乐只是晚了一点开始）。
/// 因此这里先从每条样本的误差里扣掉整体偏移（所有误差的最小值），再统计抖动，
/// 否则 3 秒倒计时会把「均值/p95/最大」全部污染成 3000ms 量级，指标完全失效。
/// </summary>
public sealed class DispatchTimingStats : IDispatchTimingSink
{
    private readonly object _gate = new();
    private readonly List<DispatchTiming> _samples = new();

    public int Count
    {
        get { lock (_gate) return _samples.Count; }
    }

    public void OnDispatch(DispatchTiming timing)
    {
        lock (_gate) _samples.Add(timing);
    }

    public void Clear()
    {
        lock (_gate) _samples.Clear();
    }

    /// <summary>最近一次播放的逐条记录（诊断模式可以导 CSV）。</summary>
    public IReadOnlyList<DispatchTiming> Samples
    {
        get { lock (_gate) return _samples.ToArray(); }
    }

    /// <summary>
    /// 逐条记录的整体偏移（所有误差的最小值，即纯预滚量）。倒计时会集中体现在这里。
    /// 把逐条日志的抖动减去它，剩下的才是真正的调度抖动。
    /// </summary>
    public double BaselineOffsetMs => Summarize().BaselineOffsetMs;

    /// <summary>
    /// 渲染逐条表；<paramref name="baselineOffsetMs"/> 非空时同时打印扣偏后的残差，
    /// 这样逐条日志在扣掉倒计时之后仍然可读。
    /// </summary>
    public string RenderTable(int maxRows = 40, double? baselineOffsetMs = null)
    {
        var samples = Samples;
        var sb = new System.Text.StringBuilder();
        foreach (var s in samples.Take(maxRows))
        {
            sb.Append("  ").Append(s);
            if (baselineOffsetMs is { } b) sb.Append($"  扣偏后 {s.JitterMs - b:+0.000;-0.000;0.000}ms");
            sb.AppendLine();
        }
        if (samples.Count > maxRows) sb.AppendLine($"  …（共 {samples.Count} 条，只打印前 {maxRows} 条）");
        return sb.ToString();
    }

    public TimingSummary Summarize()
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return new TimingSummary(0, 0, 0, 0, 0, 0, 0);

            // 整体偏移 = 所有样本误差里的**最小值**。
            //
            // 为什么是 min 而不是均值/中位数：物理上，预滚（倒计时、构建计划、首次唤醒）
            // 只会让事件比计划**更晚**，不可能更早，所以 min 就是「一次都没有额外延迟」的
            // 那条事件量出来的纯预滚量。扣掉它之后，残差恒为非负，且最小值恒为 0。
            // 用中位数估算在小样本（真实文件往往只有十几条事件）下会被大抖动带偏，
            // 曾经把一条干净的 3000ms 平移算成「均值 49.5ms」。
            var signed = _samples.Select(s => s.JitterMs).ToArray();
            double offset = signed.Min();

            // 抖动 = 扣掉整体偏移之后的残差。这才是调度的真实误差。
            var residual = signed.Select(j => Math.Abs(j - offset)).OrderBy(x => x).ToArray();
            double meanAbs = residual.Average();
            double p95 = Percentile(residual, 0.95);
            double max = residual[^1];

            return new TimingSummary(
                Count: residual.Length,
                MeanAbsoluteMs: meanAbs,
                P95Ms: p95,
                MaxAbsoluteMs: max,
                MinSignedMs: residual[0],
                MaxSignedMs: residual[^1],
                BaselineOffsetMs: offset);
        }
    }

    private static double Percentile(double[] sorted, double q)
    {
        double rank = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        double t = rank - lo;
        return sorted[lo] * (1 - t) + sorted[hi] * t;
    }

    public string RenderSummary()
    {
        var s = Summarize();
        if (s.Count == 0) return "时序诊断：没有采样。";
        return $"时序诊断：事件 {s.Count} 条；" +
               $"时间轴抖动 均值 {s.MeanAbsoluteMs:F3}ms / p95 {s.P95Ms:F3}ms / 最大 {s.MaxAbsoluteMs:F3}ms" +
               $"（已扣除整体偏移 {s.BaselineOffsetMs:F3}ms，该偏移只影响开始时刻，不影响节奏）；" +
               $"扣偏后有符号残差 {s.MinSignedMs:+0.000;-0.000;0.000} ~ {s.MaxSignedMs:+0.000;-0.000;0.000}ms";
    }
}

/// <param name="BaselineOffsetMs">
/// 全体样本误差的最小值，即纯预滚量（倒计时 / 计划构建 / 首次唤醒）。
/// 它不参与抖动统计，单独报出来供人工核对。
/// </param>
public sealed record TimingSummary(
    int Count,
    double MeanAbsoluteMs,
    double P95Ms,
    double MaxAbsoluteMs,
    double MinSignedMs,
    double MaxSignedMs,
    double BaselineOffsetMs);
