# Third-Party Notices / 第三方声明

本文件列出 DeltaMuse Player 复用、改编或依赖的第三方作品，以及各自的许可证。

---

## 1. ChickenD233/midikey-player（MIT）—— 直接复用与改编

- **Project**: MidiKeyPlayer（MIDI 按键播放器）/ 上游仓库名 harmonica-auto-player
- **Source URL**: <https://github.com/ChickenD233/midikey-player>
- **License**: MIT License，Copyright (c) 2026 ChickenD233
- **License file in upstream**: `LICENSE`（仓库根目录）

### 复用方式

M1 / M2 阶段**改编**了下列源文件（不是原样拷贝：命名空间、类型名、中文注释与整体架构都按本项目重写，
保留的是算法与处理策略）：

| 上游文件 | 本项目文件 | 复用了什么 |
|---|---|---|
| `MidiKeyPlayer/Midi/MidiLoader.cs` | `src/DeltaMusePlayer/Midi/MidiParser.cs` | MIDI 载入策略：整体读入内存、RIFF(.rmi) 解包、脏数据 `Invalid*Policy.SnapToLimits`、轨道名 UTF-8→GBK(936)→Latin1 智能解码、残缺文件按完整 `MTrk` 截断修复（`TryTrimToCompleteChunks`）、`TimeConverter.ConvertTo<MetricTimeSpan>` 的 tick→秒换算 |
| `MidiKeyPlayer/Midi/MidiModels.cs` | `src/DeltaMusePlayer/Core/PlaybackNote.cs`、`Core/Music.cs` | `RawNote` → `PlaybackNote` 的秒时间轴模型；音名 / 简谱（八度点用 U+02D9 而非组合字符，避免字体缺字形）的换算思路 |
| `MidiKeyPlayer/Engine/InputTiming.cs` | `src/DeltaMusePlayer/Playback/PlaybackConfig.cs` | 「输入时序预算是**物理毫秒**、与播放速度无关」这一核心口径，以及修饰键提前量 / 同键重触发间隔 / 最短按住 / 音间释放间隔这四类预算的概念与量级；`PlaybackConfig.Safe` 的数值取自 `InputTiming.Safe` 的量级 |
| `MidiKeyPlayer/Input/InputSender.cs` | `src/DeltaMusePlayer/Input/Win32/Win32InputApi.cs` | **M2**：`SendInput` 的注入形态 —— 键盘一律走扫描码模式（`wVk = 0`、`KEYEVENTF_SCANCODE`，抬起加 `KEYEVENTF_KEYUP`，增强键加 `KEYEVENTF_EXTENDEDKEY`），`wScan` 由 `MapVirtualKeyW(vk, MAPVK_VK_TO_VSC)` 取得；鼠标用 `INPUT_MOUSE` + `MOUSEEVENTF_{LEFT,MIDDLE,RIGHT}{DOWN,UP}`；以及「只抬本程序按过的键」的记账思路（本项目把它重写成 `WindowsInputBackend` 的 held 账本 + best-effort `ReleaseAll`） |

### 与上游的差异（本项目自行设计，未抄）

* `IWin32InputApi` 抽象层 + `FakeWin32InputApi`：让真实后端的逻辑（标志位、held 记账、
  部分失败下的 best-effort 释放）能在**不真的发按键**的前提下被单测覆盖。
* `WindowsInputException`（带 action / input / Win32 错误码）与 `ReleaseAllException`（聚合部分失败）。
* 「重复按下当成 no-op + warning」而不是无声重发。
* 时序诊断 `DispatchTiming` / `DispatchTimingStats`：逐条记录 planned vs actual，
  并从误差里扣掉「整体起始偏移（所有误差的最小值，即倒计时等预滚量）」后再统计抖动
  （均值 / p95 / 最大值），避免预滚污染指标。
* 预编译 `PlaybackPlan` + 单调时钟索引推进 + FakeClock 全量时序测试。
* `profiles/*.json` 驱动全部键位规则；canonical 选法与 strict 模式。

### 仅借鉴架构与行为，未复制源码的部分

- **全局热键**：上游用低层键盘钩子 `WH_KEYBOARD_LL`。本项目**明确不实现**全局钩子，
  只用窗口内 F12 紧急停止（见 README 的 Emergency Stop）。
- **播放引擎**：上游是「事件表 + 提前派发预算」的结构。本项目重写为
  「预编译 `PlaybackPlan` + 单调时钟索引推进」，并以 FakeClock 覆盖全部时序测试。

### 明确**没有**复用

GUI（Avalonia 主窗 / 卷帘编辑器 / 键位设置窗 / 悬浮窗）、MIDI 设备实时演奏、
试听（winmm）、自动更新、宏导出（G HUB / CSV）、预检、谱面编辑、
单音线提取（`MelodyExtractor`）、合奏合并。本项目只做
「DeltaMuse 导出的单声部 MIDI → 口琴键位 → 按时序播放」。

### MIT 许可要求

上游 MIT 许可要求：在软件的所有副本或实质部分中保留版权声明与许可声明。
本项目据此：

1. 仓库根的 `LICENSE` 在正文之后附上对上游 MIT 许可与版权人的引用；
2. 本文件完整列出复用点与来源 URL；
3. 上述文本通过 `AvaloniaResource` 嵌入发布 exe（`Docs/LICENSE`、`Docs/THIRD_PARTY_NOTICES.md`），
   随程序一起分发。

---

## 2. Avalonia（MIT）

- **Project**: Avalonia UI
- **Source URL**: <https://github.com/AvaloniaUI/Avalonia>
- **License**: MIT
- **用途**: 桌面界面框架（`Avalonia`、`Avalonia.Desktop`、`Avalonia.Themes.Fluent` 11.3.20）

## 3. Melanchall.DryWetMidi（MIT）

- **Project**: DryWetMidi
- **Source URL**: <https://github.com/melanchall/drywetmidi>
- **License**: MIT
- **用途**: MIDI 文件解析（note-on/note-off 配对、tempo map、tick→时间换算）7.2.0

## 4. xUnit.net（Apache-2.0）

- **Project**: xUnit.net（`xunit`、`xunit.runner.visualstudio`）
- **Source URL**: <https://github.com/xunit/xunit>
- **License**: Apache License 2.0
- **用途**: 纯逻辑单元测试（仅在测试工程中引用，不进入发布包）

## 5. Microsoft.NET.Test.Sdk（MIT）

- **Project**: Microsoft.NET.Test.Sdk
- **Source URL**: <https://github.com/microsoft/vstest>
- **License**: MIT
- **用途**: 测试宿主（仅在测试工程中引用，不进入发布包）

---

## 声明

本项目不包含、不调用、不链接任何游戏客户端代码；不读写游戏内存；
不使用驱动、不做进程注入、不做反作弊绕过。
唯一的输出是普通 Windows 用户态的合成键鼠输入（`SendInput`）。
