# DeltaMuse Player v0.2.0

把 DeltaMuse 导出的**单声部 MIDI** 映射到《三角洲行动》口琴键位，并按 MIDI 时序自动演奏。
本版本是 **M2 里程碑（Windows 用户态真实输入）** 的冻结点。

---

## 这个版本能做什么

```
MIDI 文件 → 解析 → 音高映射 → 预编译 PlaybackPlan → 调度器 → 输入后端
                                                              ├─ Preview（默认，不发输入）
                                                              └─ Real Input（SendInput 真实键鼠）
```

- **Preview 模式**：完整走一遍解析 / 映射 / 时序编译 / 调度，但只记录不注入，用来核对「会弹什么」。
- **Real Input 模式**：用 `SendInput` 发出真实键鼠事件。首次使用需在确认框里明确同意，之后有倒计时。
- **两模式共用同一份 `PlaybackPlan` 与同一个调度器**，只有输入后端不同（有契约测试钉住这一点）。

## M2 新增

- `WindowsInputBackend`：真实键鼠注入。键盘**一律走扫描码**（`wVk = 0` + `KEYEVENTF_SCANCODE`），
  扫描码由 `MapVirtualKeyW` 从系统取得，不硬编码；鼠标走 `MOUSEEVENTF_*`。
- `IWin32InputApi` 抽象 + `FakeWin32InputApi`：真实后端逻辑能在**不发一个按键**的前提下被完整单测。
- 安全设计：
  - 每次启动**默认 Preview**，Real Input 必须手动切 + 一次确认框；
  - 倒计时可选 0/1/2/3/5/10 秒（默认 3），且**不改动 MIDI 时间轴**；
  - 播放中常驻 **REAL INPUT ACTIVE** 横幅；
  - **F12** 或 Stop 按钮紧急停止，**不使用**任何低层全局钩子；
  - held-state 记账：只释放**本程序自己按过**的键/鼠标键；
  - 任何异常路径（Pause / Stop / Seek / 派发失败 / 关窗）都 best-effort `ReleaseAll`；
  - 全程 `asInvoker`，**不**自我提权。
- `WindowsInputException`：携带 action / input / Win32 错误码。
- `,`（OEM 逗号）键显式处理 → `VK_OEM_COMMA (0xBC)` / `SC 0x33`。
- 时序诊断：逐条记录 planned vs actual，并区分**时间轴抖动**与**整体起始偏移**（见下）。
- 诊断/验收工具：`keymap`、`dryrun`、`send`、`--diagnose`（详见 README）。

## 本版修掉的两个真实 bug

**一、倒计时被误判为「被取消」。** 倒计时原来拿「停止按钮是否可用」当取消信号，
而计划重建会把这个按钮置灰，导致倒计时在**第一秒**自我中止：引擎回到 Idle、0 事件派发，
界面上只剩 `播放结束：已派发 0 条，全部输入已释放。` —— 表现就是「倒计时一结束就立刻播放结束」。
现在取消信号是显式令牌，只有真正的取消动作才能作废倒计时会话。

**二、「抖动」指标被倒计时污染。** 预滚（倒计时 / 计划构建）会把**全部**事件整体后移一个恒定量，
那不是抖动，但对听感毫无影响。修复前每条都报 `+3031.92ms`，把均值 / p95 / 最大全抬到 3000ms 量级。
现在口径是：**整体偏移 = 所有误差的最小值**（预滚不可能让事件更早），**抖动 = 扣偏后的残差**。
同一份文件修复后：

```
派发 #1 计划 00:00.450 实际 00:03.491 抖动 0.000ms（原始误差 +3041.171ms）Z UP (C4)
派发 #2 计划 00:00.569 实际 00:03.610 抖动 0.000ms（原始误差 +3041.169ms）X DOWN (D4)
```

原始误差仍照实报出，只是不再冒充抖动。

## 验证状态（请按真实程度读）

| 项目 | 状态 |
|---|---|
| 单元测试 | **177 / 177 通过**（xUnit，含 10 个时序诊断回归用例） |
| 计划编译 / 时序 / release-all | 已由 FakeClock 全量覆盖，无 `Thread.Sleep` |
| `SendInput` 被系统接受 | ✅ 实测：`send` 16/16 事件被接受，held 为空 |
| 时序精度 | ✅ 实测：抖动均值 0.411ms / p95 2.325ms / 最大 5.192ms |
| 真实文件解析 | ✅ 已用一份 DeltaMuse 风格的 8 音符文件跑通全链路（19 事件全派发，自然结束并释放） |
| **Notepad 键盘验收** | ❌ **未完成** |
| **鼠标点击验收** | ❌ **未完成** |
| **游戏内实测** | ❌ **未完成** |

最后三项没做，原因是开发环境**没有可靠的窗口焦点控制**（`SetForegroundWindow` 返回 False，
也没有交互式桌面）。必须说清楚：「SendInput 被系统接受」**不等于**「目标窗口确实收到了这个字符」。
在你自己机器上完成 Notepad 验收（README 有步骤）之前，请不要把真实输入当成已验证。

## 已知限制

- **仓库里的 MIDI 是自建替代品**：`tests/fixtures/deltamuse-style-melody.mid` 是按 DeltaMuse 导出风格
  手工构造的（format 0、24 音符、100% 可演奏），**不是** DeltaMuse 的真实导出文件。
  开发环境没有 DeltaMuse，也没有可达的上游仓库，所以这个缺口如实标注、没有掩饰。
- **无真实游戏内验证**：程序不启动游戏、不检测游戏进程、不抢焦点；切窗口是使用者自己的事。
- 发布 exe 是裁剪（trimmed）自包含单文件，构建时 `ILLink` 会对 Avalonia 反射绑定 / DryWetMidi / JSON
  报 `IL2026` 警告。构建**不失败**，但这里不宣称「零警告」。

## 明确不做的事

不注入进程、不读写游戏内存、不使用驱动、不做反作弊绕过、不装任何全局钩子。
唯一的输出是普通 Windows 用户态的合成键鼠输入（`SendInput`）。

## 下载

| 文件 | 说明 |
|---|---|
| `DeltaMusePlayer.exe` | Windows x64 自包含单文件（**不需要**预装 .NET），约 21 MB |
| `LICENSE` | 本项目许可（MIT），含上游 MIT 许可与版权声明引用 |
| `THIRD_PARTY_NOTICES.md` | 第三方声明与逐文件复用说明 |

解压后请**整个文件夹一起用**（`Profiles\delta_harmonica.json` 是键位方案，不能只拷 exe）。

## 从源码构建

```
dotnet build DeltaMusePlayer.sln -c Release
dotnet test  tests/DeltaMusePlayer.Tests/DeltaMusePlayer.Tests.csproj -c Release
powershell -File scripts/publish.ps1        # 产出 artifacts/win-x64/DeltaMusePlayer.exe
```

需要 .NET SDK 8。除 Avalonia 与 DryWetMidi（均为 MIT）外无其他运行时依赖。

## 第三方

键位方案、时序预算口径与 `SendInput` 注入形态**改编**自
[ChickenD233/midikey-player](https://github.com/ChickenD233/midikey-player)（MIT）。
逐文件的复用范围、与上游的差异、以及明确没有复用的部分，都写在 `THIRD_PARTY_NOTICES.md` 里。

## 免责声明

本工具只是把 MIDI 转成键鼠输入。使用者需自行确认在其所在环境中这样做的合规性，
并自行承担使用后果。请优先在记事本等无害窗口完成验收。
