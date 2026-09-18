namespace DeltaMusePlayer.Core;

using System.Globalization;

/// <summary>
/// 使用者手输时间的解析与格式化。GUI 的「演奏片段」与 CLI 的 <c>--range</c> 共用这一份，
/// 否则两边的接受格式迟早会漂成不一样。
///
/// 接受的形式：
///   * <c>mm:ss.mmm</c> —— 例如 <c>01:23.500</c>
///   * <c>m:ss</c>       —— 例如 <c>1:23</c>
///   * 纯秒数           —— 例如 <c>83</c> 或 <c>83.5</c>
///   * 空串            —— 表示「不设」（起点=曲首，终点=曲尾）
/// </summary>
public static class TimeInput
{
    /// <summary>解析成功返回 true；<paramref name="ms"/> 为 null 表示空输入（不设边界）。</summary>
    public static bool TryParse(string? raw, out double? ms)
    {
        ms = null;
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return true;

        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length != 2) return false;
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes))
                return false;
            if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                return false;
            if (minutes < 0 || seconds < 0) return false;
            ms = (minutes * 60.0 + seconds) * 1000.0;
            return true;
        }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double totalSeconds))
            return false;
        if (totalSeconds < 0) return false;
        ms = totalSeconds * 1000.0;
        return true;
    }

    /// <summary>格式化回 <c>mm:ss.mmm</c>，与 <see cref="Music.TimeLabel"/> 一致。</summary>
    public static string Format(double ms) => Music.TimeLabel(ms);

    /// <summary>
    /// 解析区间写法 <c>起点-终点</c>，两端各自用 <see cref="TryParse"/> 的格式。
    /// 终点可以省略（<c>1:30-</c> 或 <c>1:30</c>）表示到曲尾。
    ///
    /// 分隔符取**第一个能让右边解析成合法时间**的 '-'：这样
    /// <c>0:30-1:00</c> 会按后一个 '-' 切，而不是按 <c>0:30</c> 里的那个。
    /// </summary>
    public static bool TryParseRange(string? raw, out double? startMs, out double? endMs)
    {
        startMs = null;
        endMs = null;
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return true;   // 空 = 整曲

        int split = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '-') continue;
            if (TryParse(s[(i + 1)..], out _)) { split = i; break; }
        }

        if (split < 0)
        {
            // 没有分隔符：整串当作起点，终点留空（到曲尾）。
            if (!TryParse(s, out double? only)) return false;
            startMs = only;
            return true;
        }

        string left = s[..split].Trim();
        string right = s[(split + 1)..].Trim();
        if (!TryParse(left, out double? start)) return false;
        if (!TryParse(right, out double? end)) return false;

        startMs = start;
        endMs = end;
        return true;
    }
}
