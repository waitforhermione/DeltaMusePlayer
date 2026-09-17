namespace DeltaMusePlayer.Input;

using DeltaMusePlayer.Core;

/// <summary>
/// 输入后端：调度器唯一允许触碰的出口。
/// MIDI 解析器、映射器、计划编译器都不许直接调用 Windows SendInput。
/// </summary>
public interface IInputBackend : IDisposable
{
    /// <summary>后端名（日志与界面显示）。</summary>
    string Name { get; }

    /// <summary>true = 会真的发出合成输入；false = 只记录（Preview / Trace）。</summary>
    bool SendsRealInput { get; }

    void KeyDown(InputId key);

    void KeyUp(InputId key);

    void MouseDown(InputId button);

    void MouseUp(InputId button);

    /// <summary>
    /// 释放后端认为当前处于按下状态的所有输入。幂等，任何异常路径的最后一道保险。
    /// 只抬本程序按下的键，不碰用户物理按住的键。
    /// </summary>
    void ReleaseAll();

    /// <summary>当前处于按下状态的输入（诊断 / 测试用）。</summary>
    IReadOnlyCollection<InputId> HeldInputs { get; }
}
