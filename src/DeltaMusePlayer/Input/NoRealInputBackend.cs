namespace DeltaMusePlayer.Input;

using DeltaMusePlayer.Core;

/// <summary>
/// 占位后端：任何真实输入调用都会抛异常。
///
/// M1 只做 Preview（Trace）。真机输入属于 M2，在它落地之前，
/// 这个后端保证「不可能因为配置错误而发出真实按键」。
/// </summary>
public sealed class NoRealInputBackend : IInputBackend
{
    public string Name => "Disabled (real input arrives in M2)";

    public bool SendsRealInput => false;

    public IReadOnlyCollection<InputId> HeldInputs => Array.Empty<InputId>();

    private static NotSupportedException Blocked() => new(
        "M1 只提供 Preview / Trace 模式，真实键鼠输入要到 M2 才实现。");

    public void KeyDown(InputId key) => throw Blocked();

    public void KeyUp(InputId key) => throw Blocked();

    public void MouseDown(InputId button) => throw Blocked();

    public void MouseUp(InputId button) => throw Blocked();

    public void ReleaseAll() { /* 什么都没按下，不用释放 */ }

    public void Dispose() { }
}
