namespace DeltaMusePlayer.Core;

/// <summary>音高命名工具（与音序器无关，纯函数）。</summary>
public static class Music
{
    private static readonly string[] Names =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly string[] JianPu =
        { "1", "#1", "2", "#2", "3", "4", "#4", "5", "#5", "6", "#6", "7" };

    public static int Mod(int a, int b) => ((a % b) + b) % b;

    /// <summary>标准音名，如 C4 / F#5（C4 = 60）。</summary>
    public static string NoteName(int pitch) => $"{Names[Mod(pitch, 12)]}{pitch / 12 - 1}";

    /// <summary>简谱音级（带升降号），不含八度点。</summary>
    public static string DegreeName(int pitch) => JianPu[Mod(pitch, 12)];

    // 八度点用「间距」字符而不是组合字符：界面字体多半没有 U+0307 / U+0323 的字形。
    private const string DotUp = "\u02D9";
    private const string DotDown = ".";

    /// <summary>面向用户的简谱音高：1 / #1 / 1˙ / 1. 等等，基准 60 = 中音 do。</summary>
    public static string SolfegeName(int pitch)
    {
        int octave = (int)Math.Floor((pitch - 60) / 12.0);
        if (octave == 0) return DegreeName(pitch);
        string mark = octave > 0 ? DotUp : DotDown;
        int count = Math.Min(Math.Abs(octave), 2);
        var sb = new System.Text.StringBuilder(DegreeName(pitch));
        for (int i = 0; i < count; i++) sb.Append(mark);
        return sb.ToString();
    }

    /// <summary>简谱音域，例如 5.~2。</summary>
    public static string SolfegeRange(int lo, int hi)
    {
        if (lo > hi) (lo, hi) = (hi, lo);
        return $"{SolfegeName(lo)}~{SolfegeName(hi)}";
    }

    /// <summary>把毫秒格式化成 mm:ss.fff。</summary>
    public static string TimeLabel(double milliseconds)
    {
        if (milliseconds < 0) milliseconds = 0;
        var t = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }
}
