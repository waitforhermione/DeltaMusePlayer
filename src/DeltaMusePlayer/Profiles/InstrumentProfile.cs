using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaMusePlayer.Profiles;

/// <summary>
/// 口琴键位方案。**所有** MIDI 音高 → 键位的规则都从这里派生，
/// 播放器内部不允许出现任何硬编码的 Z/X/C/V… 映射。
///
/// JSON 形态见 profiles/delta_harmonica.json。
/// </summary>
public sealed class InstrumentProfile
{
    /// <summary>方案名（界面/日志用）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>基准音：lane 0 + 不使用半音修饰 + 八度偏移 0 时发出的音高。默认 60 = 中音 C。</summary>
    [JsonPropertyName("base_pitch")]
    public int BasePitch { get; set; } = 60;

    /// <summary>八个自然音键，下标即 lane。顺序必须是音高从低到高。</summary>
    [JsonPropertyName("keys")]
    public List<string> Keys { get; set; } = new();

    /// <summary>每个 lane 相对基准音的半音距离，长度必须与 <see cref="Keys"/> 相同。</summary>
    [JsonPropertyName("natural_intervals")]
    public List<int> NaturalIntervals { get; set; } = new();

    /// <summary>可用的八度修饰档位（相对基准八度）。至少要含 0。</summary>
    [JsonPropertyName("octave_offsets")]
    public List<int> OctaveOffsets { get; set; } = new() { 0 };

    /// <summary>修饰键绑定：八度上/下、半音。</summary>
    [JsonPropertyName("modifiers")]
    public ModifierKeys Modifiers { get; set; } = new();

    /// <summary>键位方案里可用的八度档位（去重、升序）。</summary>
    public IReadOnlyList<int> EffectiveOctaveOffsets =>
        OctaveOffsets.Distinct().OrderBy(x => x).ToArray();

    public string DescribeModifiers() => Modifiers.Describe();
}

/// <summary>
/// 三种修饰动作绑定到哪个物理输入（鼠标左/中/右，或键盘键名）。
/// 空字符串 = 该修饰没有绑定（对应音高就弹不出来）。
/// </summary>
public sealed class ModifierKeys
{
    [JsonPropertyName("octave_down")]
    public string OctaveDown { get; set; } = "";

    [JsonPropertyName("semitone")]
    public string Semitone { get; set; } = "";

    [JsonPropertyName("octave_up")]
    public string OctaveUp { get; set; } = "";

    public IEnumerable<KeyValuePair<string, string>> Bound()
    {
        if (!string.IsNullOrWhiteSpace(OctaveDown)) yield return new("octave_down", OctaveDown.Trim());
        if (!string.IsNullOrWhiteSpace(Semitone)) yield return new("semitone", Semitone.Trim());
        if (!string.IsNullOrWhiteSpace(OctaveUp)) yield return new("octave_up", OctaveUp.Trim());
    }

    public string Describe()
    {
        var parts = new List<string>();
        foreach (var kv in Bound())
        {
            string label = kv.Key switch
            {
                "octave_down" => "降八度",
                "octave_up" => "升八度",
                "semitone" => "升半音",
                _ => kv.Key,
            };
            parts.Add($"{label}={kv.Value}");
        }
        return parts.Count == 0 ? "（无修饰键）" : string.Join(" ", parts);
    }
}

/// <summary>键位方案的加载、校验与内置默认值。</summary>
public static class InstrumentProfileLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // 发布包开了裁剪，System.Text.Json 的「按反射取类型信息」默认被关掉。
        // 这里显式启用：键位方案是用户可编辑的 JSON，源生成器不适合这种「未知形状」的场景。
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>《三角洲行动》口琴默认方案，与 profiles/delta_harmonica.json 内容一致。</summary>
    public static InstrumentProfile CreateDeltaHarmonica() => new()
    {
        Name = "delta_harmonica",
        BasePitch = 60,
        Keys = new List<string> { "Z", "X", "C", "V", "B", "N", "M", "," },
        NaturalIntervals = new List<int> { 0, 2, 4, 5, 7, 9, 11, 12 },
        OctaveOffsets = new List<int> { -1, 0, 1 },
        Modifiers = new ModifierKeys
        {
            OctaveDown = "mouse_left",
            Semitone = "mouse_middle",
            OctaveUp = "mouse_right",
        },
    };

    public static InstrumentProfile FromJson(string json)
    {
        var profile = JsonSerializer.Deserialize<InstrumentProfile>(json, JsonOptions)
                      ?? throw new InvalidDataException("键位方案 JSON 解析结果为空。");
        Validate(profile);
        return profile;
    }

    public static InstrumentProfile LoadFile(string path)
        => FromJson(File.ReadAllText(path));

    /// <summary>把方案序列化成 JSON 文本（导出用）。</summary>
    public static string ToJson(InstrumentProfile profile)
        => JsonSerializer.Serialize(profile, JsonOptions);

    /// <summary>
    /// 找默认方案的路径：先看 exe 同目录的 profiles\，再看工作目录，
    /// 都没有就返回 null（调用方回退到内置默认值）。
    /// </summary>
    public static string? FindDefaultProfilePath(string fileName = "delta_harmonica.json")
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "profiles", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "profiles", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>加载默认方案；文件缺失时回退内置默认值并给出提示。</summary>
    public static (InstrumentProfile Profile, string? Warning) LoadDefault()
    {
        string? path = FindDefaultProfilePath();
        if (path is null)
            return (CreateDeltaHarmonica(), "没有找到 profiles/delta_harmonica.json，已使用内置的三角洲口琴默认键位。");

        try
        {
            return (LoadFile(path), null);
        }
        catch (Exception ex)
        {
            return (CreateDeltaHarmonica(), $"读取键位方案 {path} 失败：{ex.Message}。已使用内置默认键位。");
        }
    }

    /// <summary>
    /// 校验方案。任何一条不成立都抛 <see cref="InvalidDataException"/>：
    /// 宁可启动时响，也不要在演奏到一半时才发现键位是错的。
    /// </summary>
    public static void Validate(InstrumentProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
            throw new InvalidDataException("键位方案缺少 name。");

        if (p.Keys is null || p.Keys.Count == 0)
            throw new InvalidDataException("键位方案缺少 keys。");

        if (p.NaturalIntervals is null || p.NaturalIntervals.Count != p.Keys.Count)
            throw new InvalidDataException(
                $"natural_intervals 的数量（{p.NaturalIntervals?.Count ?? 0}）必须与 keys 的数量（{p.Keys.Count}）一致：每个 lane 一个半音距离。");

        if (p.BasePitch < 0 || p.BasePitch > 127)
            throw new InvalidDataException($"base_pitch 必须在 0..127 之间，当前是 {p.BasePitch}。");

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < p.Keys.Count; i++)
        {
            string k = (p.Keys[i] ?? "").Trim();
            if (k.Length == 0)
                throw new InvalidDataException($"keys[{i}] 是空键名。");
            if (!seenKeys.Add(k))
                throw new InvalidDataException($"keys 里有重复键名：{k}。同一个键不能占两个 lane。");
        }

        if (p.NaturalIntervals[0] != 0)
            throw new InvalidDataException("natural_intervals[0] 必须是 0：lane 0 就是基准音。");

        for (int i = 1; i < p.NaturalIntervals.Count; i++)
        {
            if (p.NaturalIntervals[i] <= p.NaturalIntervals[i - 1])
                throw new InvalidDataException(
                    "natural_intervals 必须严格递增：lane 越大音越高，映射的 canonical 选择依赖这个顺序。");
        }

        if (p.OctaveOffsets is null || p.OctaveOffsets.Count == 0)
            throw new InvalidDataException("octave_offsets 不能为空；至少要包含 0。");
        if (!p.OctaveOffsets.Contains(0))
            throw new InvalidDataException("octave_offsets 必须包含 0（不移八度的基准档）。");

        // 修饰键不能互相抢占同一个物理输入：降八度与升八度若绑同一个键，状态机无法表达。
        var mods = p.Modifiers ?? new ModifierKeys();
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in mods.Bound())
        {
            if (used.TryGetValue(kv.Value, out string? other))
                throw new InvalidDataException(
                    $"修饰键 {kv.Value} 同时被 {other} 与 {kv.Key} 使用；两者会互相冲突。");
            used[kv.Value] = kv.Key;

            if (p.Keys.Any(k => string.Equals(k, kv.Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    $"修饰键 {kv.Value} 与自然音键重名；一个输入不能既是修饰键又是音键。");
        }
    }
}
