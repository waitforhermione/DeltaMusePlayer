using DeltaMusePlayer.Core;
using DeltaMusePlayer.Input;
using DeltaMusePlayer.Input.Win32;
using Xunit;

namespace DeltaMusePlayer.Tests;

/// <summary>
/// WindowsInputBackend 的单元测试。**不真的调用 SendInput**：
/// 全部注入都打到 <see cref="FakeWin32InputApi"/> 上，断言结构、标志位与记账。
/// </summary>
public sealed class WindowsInputBackendTests
{
    private static (WindowsInputBackend Backend, FakeWin32InputApi Api) Rig(int failFromCall = 0)
    {
        var api = new FakeWin32InputApi { FailFromCall = failFromCall };
        var backend = new WindowsInputBackend(api);
        return (backend, api);
    }

    private static InputId K(string name) => InputId.Key(name);

    private static InputId M(string name) => InputId.Mouse(name);

    // ------------------------------------------------------------------ 扫描码 / VK

    [Theory]
    [InlineData("Z", 0x5A)]
    [InlineData("X", 0x58)]
    [InlineData("C", 0x43)]
    [InlineData("V", 0x56)]
    [InlineData("B", 0x42)]
    [InlineData("N", 0x4E)]
    [InlineData("M", 0x4D)]
    public void LetterKeysMapToTheirAsciiVirtualKey(string logical, ushort expectedVk)
    {
        Assert.True(VirtualKeyMap.TryGetVirtualKey(logical, out ushort vk));
        Assert.Equal(expectedVk, vk);
    }

    [Fact]
    public void CommaKeyUsesVkOemCommaAndTheLayoutsScanCode()
    {
        // 需求 7：`,` 必须明确走 VK_OEM_COMMA，且扫描码问操作系统而不是硬编码。
        Assert.True(VirtualKeyMap.TryGetVirtualKey(",", out ushort vk));
        Assert.Equal(0xBC, vk);
        Assert.Equal(VirtualKeyMap.VK_OEM_COMMA, vk);

        // 扫描码由 api 提供：假 API 用 US 布局的 0x33
        var api = new FakeWin32InputApi();
        Assert.True(VirtualKeyMap.TryResolve(",", api, out var mapping));
        Assert.Equal(0xBC, mapping.VirtualKey);
        Assert.Equal(0x33, mapping.ScanCode);
        Assert.False(mapping.ExtendedKey);
    }

    [Fact]
    public void EveryKeyInTheShippedProfileResolvesToAScanCode()
    {
        var profile = Profiles.InstrumentProfileLoader.CreateDeltaHarmonica();
        var api = new FakeWin32InputApi();
        var (mappings, errors) = RealInputSelfCheck.ResolveProfileKeys(profile, api);

        Assert.Empty(errors);
        Assert.Equal(profile.Keys.Count, mappings.Count);
        Assert.All(mappings, m => Assert.NotEqual(0, m.ScanCode));
    }

    [Fact]
    public void UnknownKeyNameCannotBeResolved()
    {
        var api = new FakeWin32InputApi();
        Assert.False(VirtualKeyMap.TryResolve("NoSuchKey", api, out _));
        Assert.False(VirtualKeyMap.TryGetVirtualKey("NoSuchKey", out _));
    }

    [Fact]
    public void KeyWithoutAScanCodeIsRejectedInsteadOfSilentlyInjectingNothing()
    {
        var api = new FakeWin32InputApi();
        api.ScanCodes.Clear();                       // MapVirtualKey 返回 0

        var backend = new WindowsInputBackend(api);
        var ex = Assert.Throws<WindowsInputException>(() => backend.KeyDown(K("Z")));
        Assert.Contains("无法把逻辑键", ex.Message);
        Assert.Equal(0, api.KeyCalls.Count);         // 没有往系统里塞无效按键
    }

    // ------------------------------------------------------------------ 键盘注入结构

    [Fact]
    public void ZDownAndUpUseScanCodeFlags()
    {
        var (backend, api) = Rig();

        backend.KeyDown(K("Z"));
        backend.KeyUp(K("Z"));

        Assert.Equal(2, api.KeyCalls.Count);
        Assert.Equal(new FakeWin32InputApi.KeyCall(0x5A, 0x2C, KeyUp: false, ExtendedKey: false), api.KeyCalls[0]);
        Assert.Equal(new FakeWin32InputApi.KeyCall(0x5A, 0x2C, KeyUp: true, ExtendedKey: false), api.KeyCalls[1]);

        // 标志位：一律带 KEYEVENTF_SCANCODE；抬起再加 KEYEVENTF_KEYUP
        Assert.Equal(0x0008u, api.KeyCalls[0].Flags);
        Assert.Equal(0x000Au, api.KeyCalls[1].Flags);
        Assert.Empty(backend.HeldInputs);
    }

    [Fact]
    public void CommaDownAndUpUseTheInjectedScanCodeNotAHardCodedOne()
    {
        var (backend, api) = Rig();
        backend.KeyDown(K(","));
        backend.KeyUp(K(","));

        Assert.Equal(2, api.KeyCalls.Count);
        Assert.Equal(0xBC, api.KeyCalls[0].VirtualKey);    // VK_OEM_COMMA
        Assert.Equal(0x33, api.KeyCalls[0].ScanCode);      // 来自 api.MapVirtualKeyToScanCode，不是写死的
        Assert.NotEqual(0xBE, api.KeyCalls[0].ScanCode);   // 0xBE 是句点键的扫描码，写错就会打错键
    }

    [Fact]
    public void ScanCodeComesFromTheApiSoALayoutChangeIsPickedUp()
    {
        // 把逗号键的扫描码换掉（模拟不同键盘布局），后端必须跟着变 —— 证明没有硬编码。
        var api = new FakeWin32InputApi();
        api.ScanCodes[VirtualKeyMap.VK_OEM_COMMA] = 0x7E;
        var backend = new WindowsInputBackend(api);

        backend.KeyDown(K(","));
        Assert.Equal(0x7E, api.KeyCalls[0].ScanCode);
    }

    [Fact]
    public void KeyNameIsNormalizedSoLowercaseDoesNotCreateASecondInput()
    {
        var (backend, api) = Rig();
        backend.KeyDown(K("z"));
        backend.KeyUp(K("Z"));

        // 小写 z 与 Z 归一化成同一个输入：正好一对 down/up，没有多余的第二次按下
        Assert.Equal(2, api.KeyCalls.Count);
        Assert.False(api.KeyCalls[0].KeyUp);
        Assert.True(api.KeyCalls[1].KeyUp);
        Assert.Empty(backend.HeldInputs);
    }

    // ------------------------------------------------------------------ 鼠标注入结构

    [Theory]
    [InlineData("Left", 0x0002u, 0x0004u)]
    [InlineData("Right", 0x0008u, 0x0010u)]
    [InlineData("Middle", 0x0020u, 0x0040u)]
    public void MouseButtonsUseTheCorrectDownAndUpFlags(string name, uint downFlag, uint upFlag)
    {
        var (backend, api) = Rig();

        backend.MouseDown(M(name));
        backend.MouseUp(M(name));

        Assert.Equal(2, api.MouseCalls.Count);
        Assert.Equal(Win32InputConstants.MouseFlag(ToKind(name), false), api.MouseCalls[0].Flags);
        Assert.Equal(Win32InputConstants.MouseFlag(ToKind(name), true), api.MouseCalls[1].Flags);
        Assert.Equal(downFlag, api.MouseCalls[0].Flags);
        Assert.Equal(upFlag, api.MouseCalls[1].Flags);
        Assert.Empty(backend.HeldInputs);
    }

    private static MouseButtonKind ToKind(string name) => name switch
    {
        "Left" => MouseButtonKind.Left,
        "Right" => MouseButtonKind.Right,
        _ => MouseButtonKind.Middle,
    };

    [Fact]
    public void MouseAndKeyboardInputsAreAccountedSeparately()
    {
        var (backend, api) = Rig();

        backend.MouseDown(M("Right"));
        backend.KeyDown(K("Z"));

        Assert.Equal(2, backend.HeldInputs.Count);
        Assert.Contains(InputId.Mouse("Right"), backend.HeldInputs);
        Assert.Contains(InputId.Key("Z"), backend.HeldInputs);

        backend.ReleaseAll();

        Assert.Empty(backend.HeldInputs);
        // 收尾顺序：先键盘后鼠标（与演奏时的收尾一致）
        var tail = api.Ordered.TakeLast(2)
            .Select(o => o is FakeWin32InputApi.KeyCall ? "KEY" : "MOUSE").ToArray();
        Assert.Equal(new[] { "KEY", "MOUSE" }, tail);
    }

    // ------------------------------------------------------------------ 重复按下 / 多余抬起

    [Fact]
    public void DuplicatePressIsNotSentTwiceAndWarns()
    {
        var api = new FakeWin32InputApi();
        var warnings = new List<string>();
        var backend = new WindowsInputBackend(api) { Warn = warnings.Add };

        backend.KeyDown(K("Z"));
        backend.KeyDown(K("Z"));      // 重复

        Assert.Single(api.KeyCalls);                     // 只发了一次
        Assert.Single(warnings);
        Assert.Contains("已经是按下状态", warnings[0]);
        Assert.Contains(InputId.Key("Z"), backend.HeldInputs);
    }

    [Fact]
    public void ReleaseOfSomethingNotHeldIsNotSentAndWarns()
    {
        var api = new FakeWin32InputApi();
        var warnings = new List<string>();
        var backend = new WindowsInputBackend(api) { Warn = warnings.Add };

        backend.KeyUp(K("Z"));        // 从没按下过

        Assert.Empty(api.KeyCalls);
        Assert.Single(warnings);
        Assert.Contains("没有处于按下状态", warnings[0]);
    }

    [Fact]
    public void DuplicateMousePressIsAlsoSuppressed()
    {
        var api = new FakeWin32InputApi();
        var warnings = new List<string>();
        var backend = new WindowsInputBackend(api) { Warn = warnings.Add };

        backend.MouseDown(M("Middle"));
        backend.MouseDown(M("Middle"));

        Assert.Single(api.MouseCalls);
        Assert.Single(warnings);
    }

    // ------------------------------------------------------------------ 失败处理

    [Fact]
    public void FailedInjectionThrowsWithActionInputAndWin32Code()
    {
        var (backend, api) = Rig(failFromCall: 1);

        var ex = Assert.Throws<WindowsInputException>(() => backend.KeyDown(K("Z")));

        Assert.Equal("press", ex.Action);
        Assert.Equal(InputId.Key("Z"), ex.Input);
        Assert.Equal(87, ex.Win32Error);
        Assert.Contains("SendInput", ex.Message);
        Assert.Empty(backend.HeldInputs);      // 按失败就不该记账
    }

    [Fact]
    public void FailedReleaseKeepsTheInputOnTheBooks()
    {
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);

        backend.KeyDown(K("Z"));
        api.FailFromCall = 2;                  // 下一次注入开始失败

        var ex = Assert.Throws<WindowsInputException>(() => backend.KeyUp(K("Z")));
        Assert.Equal("release", ex.Action);
        Assert.Equal(87, ex.Win32Error);

        // 抬失败必须留在账上：否则 ReleaseAll 再也不会去抬它
        Assert.Contains(InputId.Key("Z"), backend.HeldInputs);
    }

    [Fact]
    public void ReleaseAllIsBestEffortAndDoesNotStopAtTheFirstFailure()
    {
        var api = new FakeWin32InputApi();
        var backend = new WindowsInputBackend(api);

        backend.KeyDown(K("Z"));
        backend.MouseDown(M("Right"));

        api.Clear();
        api.FailFromCall = 1;                  // 第一次释放就失败

        var ex = Assert.Throws<ReleaseAllException>(() => backend.ReleaseAll());

        // 两次释放都尝试过了（不是失败一次就收工），两个都失败、都记在异常里
        Assert.Equal(2, api.TotalCalls);
        Assert.Equal(2, ex.Failures.Count);
        Assert.Contains(ex.Failures, f => f.Input == InputId.Key("Z"));
        Assert.Contains(ex.Failures, f => f.Input == InputId.Mouse("Right"));

        // 没抬成功的两个都还在账上，下次还能再试
        Assert.Contains(InputId.Key("Z"), backend.HeldInputs);
        Assert.Contains(InputId.Mouse("Right"), backend.HeldInputs);
    }

    [Fact]
    public void ReleaseAllSucceedsAndEmptiesTheBooks()
    {
        var (backend, api) = Rig();

        backend.MouseDown(M("Right"));
        backend.MouseDown(M("Middle"));
        backend.KeyDown(K("Z"));
        backend.KeyDown(K("X"));

        backend.ReleaseAll();

        Assert.Empty(backend.HeldInputs);
        var tail = api.Ordered.TakeLast(4).ToArray();
        Assert.Equal(4, tail.Length);
        Assert.All(tail, o => Assert.True(
            (o is FakeWin32InputApi.KeyCall k && k.KeyUp) || (o is FakeWin32InputApi.MouseCall m && m.ButtonUp)));
    }

    [Fact]
    public void ReleaseAllOnAnEmptyBackendDoesNothing()
    {
        var (backend, api) = Rig();
        backend.ReleaseAll();
        Assert.Equal(0, api.TotalCalls);
    }

    [Fact]
    public void ReleaseAllTwiceIsHarmless()
    {
        var (backend, api) = Rig();
        backend.KeyDown(K("Z"));
        backend.ReleaseAll();
        int after = api.TotalCalls;
        backend.ReleaseAll();
        Assert.Equal(after, api.TotalCalls);
    }

    [Fact]
    public void BackendRejectsWrongInputKind()
    {
        var (backend, _) = Rig();
        Assert.Throws<ArgumentException>(() => backend.KeyDown(M("Left")));
        Assert.Throws<ArgumentException>(() => backend.MouseDown(K("Z")));
    }

    [Fact]
    public void NonWindowsPlatformIsRejectedAtConstruction()
    {
        var api = new FakeWin32InputApi { IsSupported = false };
        Assert.Throws<PlatformNotSupportedException>(() => new WindowsInputBackend(api));
    }

    [Fact]
    public void BackendDeclaresThatItSendsRealInput()
    {
        var (backend, _) = Rig();
        Assert.True(backend.SendsRealInput);
        Assert.NotEqual("Trace", backend.Name);
    }
}
