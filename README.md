# DeltaMuse Player

把 DeltaMuse 导出的单声部 MIDI 译成《三角洲行动》口琴的键位，并按时序自动演奏。

**当前阶段：M2 —— Real Input 已接入（Windows 用户态 SendInput）。**
两种模式可用：**Preview / Trace**（默认，不发送任何输入）与 **Real Input**（发送真实键鼠事件）。

---

## What it does

Loads a MIDI melody and automatically plays the corresponding Delta Force harmonica keys.

## Designed for

MIDI exported by DeltaMuse. DeltaMuse 已经做完 MP3/WAV → 钢琴 MIDI → 主旋律提取 → 移调适配，
输出的就是单声部旋律。本工具**只**做三件事：

```
parse  →  map  →  play
```

不做：MP3 → MIDI、主旋律提取、AI 音乐识别、自动和声简化、自动移调、谱面编辑。

## Modes

| 模式 | 说明 | 默认 |
|---|---|---|
| **Preview / Trace** | 不发送任何真实输入，只把「将会发出的键鼠事件」按时间显示 / 打印出来 | ✅ 每次启动都是它 |
| **Real Input** | 用 Windows `SendInput` 发送真实键盘 / 鼠标事件 | 需要手动切换并确认风险 |

两种模式共用**同一份** `PlaybackPlan`、**同一个** `PlaybackEngine`、**同一个**调度器，
差别只有注入时用的 `IInputBackend` 实现：

```
PlaybackEngine(plan, TraceInputBackend)      // Preview
PlaybackEngine(plan, WindowsInputBackend)    // Real Input
```

### 安全设计

* 程序**每次启动都默认 Preview**，不记住上次的模式选择。
* 本次运行**第一次**切到 Real Input 时会弹一次风险确认（Cancel / I Understand），
  确认后本次会话不再弹；下次启动仍然从 Preview 开始。
* Real Input 播放前有**倒计时**（默认 3 秒，可设 0/1/2/3/5/10），给你时间切到目标窗口。
  倒计时期间 `position = 0`，真正的第 0 毫秒从倒计时结束那一刻算起，**不会改变 MIDI 时间轴**。
* 播放中右上角常驻醒目的 **REAL INPUT ACTIVE** 状态条（不闪烁、不侵入）。
* **F12** 紧急停止（窗口内），以及界面上的「紧急停止」按钮：立即 stop + 释放全部输入。
* 关闭窗口、进程退出：同样会 stop + release-all。

## Warning

Real Input sends synthetic keyboard and mouse events.

Automation in online games may violate the game's rules and may result in account penalties.

DeltaMuse Player does not bypass anti-cheat, inject code, read game memory, or use kernel drivers.

---

## 键位规则（全部来自 profile，不写死在代码里）

基础八个自然音键：

| lane | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| 键 | Z | X | C | V | B | N | M | , |
| 相对基准音的半音 | 0 | 2 | 4 | 5 | 7 | 9 | 11 | 12 |

* 基准音 `base_pitch` = 60（中音 C）
* 八度档位 `octave_offsets` = −1 / 0 / +1
* 修饰键：`octave_down` = 鼠标左键、`semitone` = 鼠标中键、`octave_up` = 鼠标右键

```
普通            Z
半音            中键 + Z
升八度          右键 + Z
降八度          左键 + Z
半音 + 升八度    中键 + 右键 + Z
```

这些都写在 `src/DeltaMusePlayer/profiles/delta_harmonica.json` 里。
换乐器只要换一份 JSON，播放器代码一行都不用改。

### canonical 选法

1. `|octave_offset|` 最小（优先不按八度键）
2. 不用半音键优先（白键优先，半音键只用来补黑键）
3. lane 较小优先
4. lane 相同再比键名（字符串序）—— 完全确定

### 弹不出来的音

**不猜、不改音高**：默认跳过并列出（界面里有独立的「不可演奏的音」清单：时间 / 音名 / 简谱 / 原因）。
`PlaybackConfig.Strict = true` 时直接拒绝编译。界面里的「输入时序档位」下拉框可切默认/稳健/紧凑三档。

---

## 时序模型

物理时间与音乐时间分开处理，这是整个项目的核心口径：

* **边沿间隔是物理毫秒**，**不随播放速度缩放**（目标程序按帧采样按键，间隔小于一帧就漏音）：
  修饰键提前量、修饰键释放延迟、修饰键切换间隔、音间间隔、同键重触发间隔、最短按住时长。
* **音符位置是音乐时间**：谱面上的 0.5 秒在 2× 速度下就是 0.25 秒。

计划对外交付**播放位置毫秒**（墙钟），`speed` 在编译期折算好。所有数值都在 `PlaybackConfig` 里。

默认档（`PlaybackConfig.Default`）：

| 参数 | 默认 | 来源 |
|---|---|---|
| `ModifierLeadMs` | 15 | 本项目新值 |
| `ModifierReleaseDelayMs` | 10 | 本项目新值 |
| `ModifierTransitionGapMs` | 5 | 本项目新值 |
| `ReleaseGapMs` | 15 | 本项目新值 |
| `SameKeyRetriggerGapMs` | 12 | 本项目新值 |
| `MinimumKeyHoldMs` | 30 | 本项目新值 |
| `FinishReleaseDelayMs` | 20 | 本项目新值 |
| `CountdownSeconds` | 3 | 需求指定 |

另有两档预设：

| 档位 | ModifierLead | Retrigger | MinHold | ReleaseGap | 来源 |
|---|---|---|---|---|---|
| 稳健（30fps / 掉帧） | 40 | 45 | 45 | 30 | 取自 upstream `InputTiming.Safe` 的量级 |
| 紧凑（高帧率） | 8 | 8 | 20 | 8 | 本项目新值 |

**这些都不是「最佳值」**，是在没有目标环境实测数据前的保守取值，需要用真实使用反馈来调。

### 修饰键状态机

* 相邻两音需要同一修饰键时，**一直按着**，中间不做无意义的 up/down。
* 换修饰键时：**先松旧的、再按新的**，两者至少隔一个切换间隔。
* 降八度与升八度**绝不共存**；反向切换时时间规整会额外让出「释放延迟 + 切换间隔 + 提前量」。
* 半音键可以和任一八度键同时按着。
* 半音先松、八度后松（由事件时刻决定，不靠排序兜底）。

### 同键重复 / 最短按住 / 重叠

* 相邻同键：`key_up` → 至少 `SameKeyRetriggerGapMs` → `key_down`，不会被系统粘成长按。
* 过短的 MIDI 音拉到 `MinimumKeyHoldMs`，绝不产生零时长按键。
* 重叠（复音）：给 Warning，默认 `Serialize`（后音顺延，不缩短前音）；可切 `Truncate`。
* 口琴是单音乐器：任何时刻最多一个音键按着。

---

## Real Input 实现

### `WindowsInputBackend`

只做一件事：收到逻辑动作 → 调 Win32 注入。**不参与** timing / mapping / note planning。

```
IWin32InputApi                 ← 抽象层（可替换，便于测试）
   ├── Win32InputApi           ← 生产：SendInput + MapVirtualKeyW
   └── FakeWin32InputApi       ← 测试：只记录调用，绝不注入
```

### 用到的 Win32 API（就这两个）

| API | 用途 |
|---|---|
| `SendInput` | 注入键盘 / 鼠标事件 |
| `MapVirtualKeyW` | 虚拟键码 → 扫描码（`MAPVK_VK_TO_VSC`） |

键盘一律走**扫描码模式**：`wVk = 0`、`dwFlags |= KEYEVENTF_SCANCODE`，抬起再加 `KEYEVENTF_KEYUP`，
增强键（方向键 / PgUp / 小键盘除号等）加 `KEYEVENTF_EXTENDEDKEY`。
这与 upstream midikey-player 已验证的做法一致（它的注释也写了「很多目标程序 / DirectInput 只认扫描码」）。

鼠标走 `INPUT_MOUSE` + `MOUSEEVENTF_{LEFT,MIDDLE,RIGHT}{DOWN,UP}`。

**不做**：DLL 注入、`OpenProcess`、`ReadProcessMemory`、`WriteProcessMemory`、`CreateRemoteThread`、
读游戏内存、DirectInput/DirectX hook、内核/过滤驱动、虚拟 HID、反作弊绕过或抑制、进程隐藏、二进制 patch。

### 扫描码与逗号键

**扫描码不硬编码**：每次都问 `MapVirtualKeyW`。硬编码的 OEM 标点扫描码在不同键盘布局上会错。

虚拟键码表里明确处理了标点：

| 逻辑键 | 虚拟键码 | 说明 |
|---|---|---|
| `,` | **`VK_OEM_COMMA` = 0xBC** | 美式布局的逗号键（profile 的第 8 个音键） |
| `Z X C V B N M` | 0x5A / 0x58 / 0x43 / 0x56 / 0x42 / 0x4E / 0x4D | 字母键的 VK 就是 ASCII 大写 |

实测（本机 US 布局）：`Z→SC 0x2C`、`X→0x2D`、`C→0x2E`、`V→0x2F`、`B→0x30`、`N→0x31`、`M→0x32`、
`,`→**0x33**（既不是 0xBE 的句点键，也不是别的标点）。用 `keymap` 子命令可以自己核对：

```
DeltaMusePlayer.exe keymap
  Z        VK=0x5A ( 90)  SC=0x2C ( 44)  ext=no   字母键
  ...
  ,        VK=0xBC (188)  SC=0x33 ( 51)  ext=no   US 布局的逗号键（VK_OEM_COMMA）
```

### Held-state 记账

后端自己维护 `HeldKeys` / `HeldMouseButtons`：

* **只释放自己确认按下过的输入**。绝不会「Stop 时把所有键都发一遍 key-up」——
  用户物理按住的键不归本程序管。
* 重复按下同一个键：记 warning，当成 no-op，**不再往系统里塞一次**，避免状态失真。
* 抬起一个没按下的键：同样记 warning 并跳过。
* 按钮的记账在注入**成功之后**才生效；失败就不记账。

### 失败处理

* 每次 `SendInput` 都检查返回数量。只要不为 1 就抛 `WindowsInputException`，
  带上 `action` / `input` / `Win32 错误码`，不吞错误。
* 键盘抬起失败时**把该键留在账上**（因为它可能还在系统里按着），这样后续 `ReleaseAll` 还能再抬。
* `ReleaseAll()` 是 **best-effort**：先取一笔快照，逐个尝试一遍，
  **任何一个失败都不影响后面的**，最后汇总成一个 `ReleaseAllException`。
  失败的输入放回账上，下次调用还能再试。**不会**因为第一个失败就死循环或提前收工。

### Pause / Stop / Seek 的真实输入行为

| 操作 | 真实输入行为 |
|---|---|
| **Pause** | 立即 `ReleaseAll`（修饰键 + 音键全放），但保留逻辑播放位置 |
| **Resume** | 从「不早于当前位置的第一颗音」的起点用 `MouseDown`/`KeyDown` 重建所需输入，**不重放已经结束的音** |
| **Stop** | 停止调度、位置归零、`ReleaseAll`、清空账本与待发事件，状态回到 Idle，可再次 Play |
| **Seek** | **先** `ReleaseAll` 放掉旧输入，**再**重建目标位置那颗音需要的修饰键与音键（顺序不能反，否则刚重建的会被抬掉） |
| **Speed** | 不改物理间隔，只改音乐时间轴（M1 已定规则，M2 未改） |

### 倒计时被打断的两个坑（真实 bug，已修）

**一、倒计时用错了取消信号。** 倒计时原本拿「停止按钮是否可用」当取消依据，
而「换计划 → `StopPlayback()`」会把那个按钮置灰。于是启动时载入 `--midi`
触发的那次计划重建，会让紧接着的倒计时在**第一秒**就自我中止，
引擎状态回到 Idle、一个事件都没派发，界面上只剩一句

```
播放结束：已派发 0 条，全部输入已释放。
```

用户看到的就是「倒计时一结束就立刻播放结束」。日志里能直接看到证据：

```
倒计时被中止（还剩 3 秒）：停止按钮可用=False，引擎是否还是同一个=True
```

现在取消信号是一个**显式令牌**（`_countdownToken` / `_countdownCancelled`），
只有真正的取消动作（换计划 / 停止 / 紧急停止 / 关窗）才会作废当前倒计时会话。
倒计时期间也**不改** MIDI 时间轴，只是把墙钟锚点往后推，所以节奏不受影响。

**二、「已派发 N 条」播放中恒为 0。** 界面读的是日志对象里只在**停止时**才写的计数。
播放中它自然是 0，用户就会以为「一个键都没弹」。现在播放中读引擎的实时计数
（`PlaybackEngine.DispatchedCount`），停止后二者必须相等（有测试钉住）。

### 时序诊断（默认关闭）

`WindowsInputBackend.TimingSink` 非 null 时才启用。它记录每条派发的：

```
planned  1000.000ms   actual  1000.812ms   jitter  +0.812ms   Z DOWN   扣偏后 +0.000ms
```

一次播放结束后汇总：事件数、**时间轴抖动**（均值 / p95 / 最大）与**整体起始偏移**。
只在诊断路径上开启，不在播放过程中写大量日志。调度器仍是 M1 的单调时钟 + 索引推进，
没有 busy loop，也没有改用 `DispatcherTimer` 累加。

#### 为什么「抖动」要扣掉整体偏移

倒计时、计划构建、首次唤醒这些**预滚**只会让事件比计划更晚，而且是把**全部**事件
整体后移一个恒定量。这不是抖动：整段音乐只是晚一点开始，节奏完全没变。

如果不扣，3 秒倒计时会直接污染统计。修复前的真实日志长这样：

```
派发 #1 计划 00:00.450 实际 00:03.481 抖动 +3031.924ms
派发 #2 计划 00:00.569 实际 00:03.601 抖动 +3031.920ms
...
```

每一条都顶着 +3031ms，看起来像时序坏了，其实只是晚开始了 3 秒，
而「均值 / p95 / 最大」全被抬到 3000ms 量级，指标彻底失效。

现在的口径：

* **整体偏移** = 所有样本误差的**最小值**。预滚不可能让事件更早，所以最小值就是
  「零额外延迟」那条事件量出来的纯预滚量（用中位数估算在小样本下会被大抖动带偏）。
* **抖动** = 各样本误差减去整体偏移后的残差，因此恒为非负、最小值恒为 0。

同一份日志在修复后（同一个 MIDI、同一个 3 秒倒计时）：

```
派发 #1 计划 00:00.450 实际 00:03.491 抖动 0.000ms（原始误差 +3041.171ms）Z UP (C4)（起始偏移 +3041.171ms，...）
派发 #2 计划 00:00.569 实际 00:03.610 抖动 0.000ms（原始误差 +3041.169ms）X DOWN (D4)
```

原始误差仍然照实报出来（供人工核对预滚量），只是不再冒充抖动。

界面上的「已派发 N 条」在播放过程中取自引擎的实时计数（`PlaybackEngine.DispatchedCount`），
不是只在停止时才更新的日志计数 —— 否则播放中会一直显示 0，
看起来像「一个键都没弹」，而这个观感正是排查时最容易误判的地方。

---

## 架构

```
MidiParser          MIDI 文件 → PlaybackNote（秒时间轴）
   ↓
NoteMapper          pitch → HarmonicaBinding（键位方案派生）
   ↓
PlaybackPlanner     → PlaybackPlan（排好序的 InputEvent[]）
   ↓
PlaybackEngine      IPlaybackClock 驱动，按 index 推进
   ↓
IInputBackend       TraceInputBackend（Preview） / WindowsInputBackend（Real）
```

`PlaybackEngine` / `PlaybackPlanner` / `MidiParser` 里**没有**任何 Win32 调用 —— 全部集中在 `WindowsInputBackend`。

```
src/DeltaMusePlayer/
  Cli/CliRunner.cs                    命令行前端
  Core/                               音符、输入标识、时钟、日志、会话
  Midi/MidiParser.cs                  DryWetMidi 解析 → 秒
  Profiles/InstrumentProfile.cs       键位方案 + JSON 校验
  Mapping/                            NoteMapper、HarmonicaBinding
  Playback/                           Config / Plan / Planner / Engine
  Input/
    IInputBackend.cs                  后端接口
    TraceInputBackend.cs              只记录（Preview）
    NoRealInputBackend.cs             一律拒绝（保险）
    WindowsInputBackend.cs            ★ M2：真实注入 + held 记账 + best-effort 释放
    WindowsInputException.cs          带 action / input / Win32 错误的异常
    DispatchTiming.cs                 时序诊断与统计
    Win32/
      IWin32InputApi.cs               抽象层
      Win32InputApi.cs                SendInput / MapVirtualKeyW
      VirtualKeyMap.cs                逻辑键 → VK（含 VK_OEM_COMMA）
      RealInputSelfCheck.cs           离线键位表 + 诊断计划
  Views/MainWindow.axaml              Preview / Real Input 界面
  Views/RealInputConsentDialog.cs     首次 Real Input 风险确认
  profiles/delta_harmonica.json       键位方案
tests/DeltaMusePlayer.Tests/          161 条纯逻辑测试
tests/fixtures/                       固定的 DeltaMuse 风格 MIDI fixture
```

---

## 使用

### 图形界面

```
DeltaMusePlayer.exe
```

1. 选 MIDI 文件 → 选声轨（会自动挑音符最多的非鼓轨）。
2. 选 **Preview**（默认）或 **Real Input**（会先弹一次风险确认）。
3. 调速度 / 倒计时 / 输入时序档位。
4. Play。Real Input 会先倒计时，请在这个时间里切到目标窗口。
5. 出问题按 **F12**。

排查用的两个启动参数（都会**跳过**一次鼠标点击，但不会跳过确认框，除 `--diagnose` 外）：

```
DeltaMusePlayer.exe --midi song.mid              启动就载入这个文件
DeltaMusePlayer.exe --midi song.mid --diagnose   载入后自动走一遍「Real Input → 倒计时 → 播放」
```

`--diagnose` 的用途是：在没有可靠 UI 自动化的环境里，也能确定性地复现整条真实播放路径，
并把每一步（倒计时、`Play()`、采样、`release-all`）都写进 `play.log`。
它仍然是 Real Input、仍然真的发输入、仍然有 3 秒倒计时；只是不需要人去点按钮。

### 命令行

```
DeltaMusePlayer.exe info     song.mid                 只列声轨与统计
DeltaMusePlayer.exe dump     song.mid [--limit 40]    编译计划并打印事件 trace
DeltaMusePlayer.exe validate song.mid                 统计可演奏比例（不修改 MIDI）
DeltaMusePlayer.exe simulate song.mid                 假时钟跑一遍调度，校验时序与 release-all
DeltaMusePlayer.exe profiles                          列出键位方案
DeltaMusePlayer.exe keymap                            逻辑键 → VK → 扫描码 对照表（不发输入）
DeltaMusePlayer.exe dryrun  [--with-octave|--test-mouse]
                                                      打印「真实输入将会发出的 Win32 调用」（不发输入）
DeltaMusePlayer.exe send    [--countdown 3] [--note-ms 150] [--with-octave]
                                                      真实发送！Notepad 验收用的极短序列
```

`keymap` / `dryrun` **永远不会**发送任何输入；只有 `send` 会。
`send --test-mouse` 会真的点左/中/右键，请谨慎。

退出码：`0` 成功；`1` 用法/参数错误；`2` 文件或键位方案错误；`3` 编译期拒绝；`4` 真实输入失败。

### Notepad 键盘验收（推荐先做这个）

```
DeltaMusePlayer.exe dryrun          # 先离线核对：确认要发的是一串 SendInput KEYBD
# 打开 Notepad，点一下它的编辑区
DeltaMusePlayer.exe send --countdown 3
# 在倒计时结束前点回 Notepad
```

预期 Notepad 里出现：

```
zxcvbnm,
```

（这串正好覆盖：键盘发送、扫描码、**OEM 逗号键**、release、以及连续 8 个不同键的边界。）

跑完程序会打印时序统计，例如：

```
时序诊断：事件 16 条；时间轴抖动 均值 8.467ms / p95 16.560ms / 最大 16.560ms（已扣除整体偏移 3041.171ms，该偏移只影响开始时刻，不影响节奏）；扣偏后有符号残差 0.000 ~ +16.560ms
```

（`send` 有倒计时，所以「整体偏移」会是一个 3000ms 以上的数。它被单独报出来，
不会混进抖动里 —— 理由见上面「为什么『抖动』要扣掉整体偏移」。）

### 目标环境测试（谨慎）

先跑界面里的短旋律（比如 C4 D4 E4 F4 G4 → C5 C#5 D5 C3），确认普通键 / 半音 / 升八度 / 降八度都对得上，
再上完整曲子。程序**不会**启动游戏、**不会**检测游戏进程、**不会**抢焦点 —— 切窗口是你自己的事。

---

## 权限

* `app.manifest` 是 **`asInvoker`**：不弹 UAC、不自动提权，**默认不需要管理员**。
* 如果目标程序以更高权限运行，Windows UIPI 会拦下合成输入。此时界面会提示：
  「如果目标程序以更高权限运行…请关闭 DeltaMuse Player，手动以管理员身份运行」。
* **不会**自动提权、不会自动重启成管理员模式、不会做任何 UIPI 绕过。

---

## 明确不做的事

global low-level keyboard hook、进程检测、自动抢焦点、自动启动游戏、反作弊兼容技巧、
驱动输入、虚拟 HID、宏导出、MIDI 编辑器、音频转录、多游戏支持。

也不为了绕过反作弊去伪装硬件、改输入设备驱动、隐藏进程或混淆行为。

本项目就是：**MIDI → 普通 Windows 用户态合成输入**。

---

## 构建与发布

需要 .NET 8 SDK。

```powershell
dotnet build  DeltaMusePlayer.sln -c Release
dotnet test   tests\DeltaMusePlayer.Tests\DeltaMusePlayer.Tests.csproj -c Release
powershell -File scripts\publish.ps1          # → artifacts\win-x64\DeltaMusePlayer.exe
```

`publish.ps1` 产出 **win-x64 自包含单文件**（不需要用户装 .NET），并开启裁剪。
Real Input 没有引入任何新的第三方依赖。

---

## 第三方与许可

本项目用 MIT 许可，见 `LICENSE`。

复用 / 改编了 [ChickenD233/midikey-player](https://github.com/ChickenD233/midikey-player)（MIT）的部分实现
（含 M2 的 `InputSender` 注入形态），逐项清单见 `THIRD_PARTY_NOTICES.md`。
两份文本都会嵌入发布产物。

---

## 免责声明

本工具仅供学习和个人使用。在联机游戏中使用自动化可能违反游戏规则并导致账号处罚，
后果由使用者承担。
