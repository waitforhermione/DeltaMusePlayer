# DeltaMuse Player

把 DeltaMuse 导出的单声部 MIDI 译成《三角洲行动》口琴键位，按时序自动演奏。

```
parse → map → play
```

只做这三件事。不做 MP3 → MIDI、主旋律提取、移调、谱面编辑。

> 在线游戏中使用自动化可能违反游戏规则并导致账号处罚，请自行判断风险。

---

## 怎么用

### 图形界面

直接运行 `DeltaMusePlayer.exe`（解压后请**整个文件夹一起用**，`Profiles\delta_harmonica.json` 是键位方案，不能只拷 exe）。

1. 选 MIDI 文件 → 选声轨（默认自动挑音符最多的非鼓轨）。
2. （可选）填「演奏片段」的起止时间，只弹一段。
3. 选 **Preview**（默认）或 **Real Input**。
4. 调速度 / 倒计时 / 输入时序档位。
5. 点 **Play**。Real Input 会先倒计时，请在这段时间里切到目标窗口。
6. 出问题按 **F9**（或界面上的「紧急停止」）。

两个启动参数：

```
DeltaMusePlayer.exe --midi song.mid              启动就载入这个文件
DeltaMusePlayer.exe --midi song.mid --diagnose   载入后自动走一遍「Real Input → 倒计时 → 播放」
```

### 只弹某一段

不想弹全曲时，在「演奏片段」里填起止时间：

* 格式：`mm:ss.mmm`（如 `01:23.500`）、`1:23`、或直接写秒数（如 `83`）。
* **起点含、终点不含** —— 把曲子切成两段不会漏音也不会重音。
* **跨越终点的长音会被截断在终点**：口琴是单音乐器，正在吹的音必须在边界松开。
* 终点**留空 = 到曲尾**；两个都留空 = 整曲。
* 界面上的「从当前位置 / 前 30 秒 / 末 30 秒 / 终点 +30 秒」是快捷填充，填完仍可手改。

界面会实时显示「这一段 N 个音；实际弹到 mm:ss.mmm ~ mm:ss.mmm」。
时间读数仍按**源文件时间轴**，不平移到 0，方便对着谱子对号。

命令行对应参数是 `--range`：

```
DeltaMusePlayer.exe dump song.mid --range 0:30-1:15    第 30 秒到 1 分 15 秒
DeltaMusePlayer.exe dump song.mid --range 30-75        同上，用秒
DeltaMusePlayer.exe dump song.mid --range 1:30-        从 1 分 30 秒到曲尾
```

### 紧急停止（F9，全局有效）

`F9` 是**系统级热键**，所以切到游戏之后依然能按 —— 这一点很重要，因为真实用法的正常流程
就是「点播放 → 在倒计时里切走」，那时本窗口已经没有焦点了，窗口内的快捷键会变成聋子。

它用的是 User32 的 `RegisterHotKey`（系统热键注册），**不是**键盘钩子：没有
`WH_KEYBOARD_LL`、不注入进程、不装驱动、不轮询键盘状态。系统只在你按下这个组合键时
投递一条 `WM_HOTKEY`，其它按键一概不经过本程序。

* 可以反复按，不是一次性的。
* 关窗时会把 `F9` 还给系统。
* 如果 `F9` 已被别的程序占用，注册会失败。这时程序**不会假装热键可用**：
  界面提示会改成「仅本窗口有效」，状态栏与日志里写明原因。

想确认自己的环境里 `F9` 是否真的生效（合成按键无法触发系统热键，所以这一项只能由真人验证）：

```
DeltaMusePlayer.exe hotkey          注册真实系统热键，然后请你按一下 F9（默认等 10 秒）
DeltaMusePlayer.exe hotkey --wait 5 同上，只等 5 秒
```

### 两种模式

| 模式 | 说明 | 默认 |
|---|---|---|
| **Preview** | 不发任何真实输入，只把「将会发出的键鼠事件」显示出来 | ✅ 每次启动都是它 |
| **Real Input** | 用 `SendInput` 发真实键盘 / 鼠标事件 | 需手动切换 + 确认风险 |

安全设计：

* 每次启动**都默认 Preview**，不记住上次选择。
* 本次运行**第一次**切到 Real Input 时弹一次风险确认，确认后本次会话不再弹。
* Real Input 播放前有**倒计时**（默认 3 秒，可选 0/1/2/3/5/10），给你时间切窗口。倒计时不改动 MIDI 时间轴。
* 播放中右上角常驻 **REAL INPUT ACTIVE** 状态条。
* **F9** / 「紧急停止」按钮：立即停止并释放全部输入。关窗、退出进程同样会释放。

### 键位（Delta 口琴）

| 键 | Z | X | C | V | B | N | M | , |
|---|---|---|---|---|---|---|---|---|
| 相对基准音半音 | 0 | 2 | 4 | 5 | 7 | 9 | 11 | 12 |

基准音 C4；修饰键：**左键 = 降八度**、**中键 = 升半音**、**右键 = 升八度**。

```
普通            Z
半音            中键 + Z
升八度          右键 + Z
降八度          左键 + Z
半音 + 升八度    中键 + 右键 + Z
```

规则全部写在 `src/DeltaMusePlayer/Profiles/delta_harmonica.json`，换乐器只换这份 JSON。

**弹不出来的音**：不猜、不改音高，默认跳过并在界面上列出（时间 / 音名 / 原因）。
想要严格模式就在时序档位里切换。

### 命令行

```
DeltaMusePlayer.exe info     song.mid                 列出声轨与统计
DeltaMusePlayer.exe dump     song.mid [--limit 40]    编译计划并打印事件 trace
DeltaMusePlayer.exe validate song.mid                 统计可演奏比例（不修改文件）
DeltaMusePlayer.exe simulate song.mid                 假时钟跑一遍调度，校验时序与释放
DeltaMusePlayer.exe profiles                          列出键位方案
DeltaMusePlayer.exe keymap                            逻辑键 → VK → 扫描码 对照表（不发输入）
DeltaMusePlayer.exe hotkey   [--wait 5]               全局紧急停止热键自检（需真人按键）
DeltaMusePlayer.exe dryrun  [--with-octave|--test-mouse]
                                                      打印将要发出的 Win32 调用（不发输入）
DeltaMusePlayer.exe send    [--countdown 3] [--note-ms 150] [--with-octave]
                                                      真实发送！Notepad 验收用的极短序列
```

`info` / `dump` / `validate` / `simulate` 都支持 `--range <起点-终点>` 只处理某一段。

`keymap` / `dryrun` **永远不会**发送输入；`hotkey` 只注册热键、不发输入；只有 `send` 会
（`send --test-mouse` 会真的点鼠标）。

退出码：`0` 成功；`1` 用法错误；`2` 文件或键位方案错误；`3` 编译期拒绝；`4` 真实输入失败。

### 先做 Notepad 验收

上游戏之前，先在记事本确认键鼠真的能打进去：

```
DeltaMusePlayer.exe dryrun          # 先离线核对要发的是 SendInput 键盘事件
# 打开 Notepad，点一下编辑区
DeltaMusePlayer.exe send --countdown 3
# 在倒计时结束前点回 Notepad
```

记事本里应该出现：

```
zxcvbnm,
```

（这串覆盖了键盘发送、扫描码、逗号键、释放、以及连续 8 个不同键。）

顺带确认保险丝：**播放中点一下别的窗口**，然后在**不切回本程序**的情况下按 `F9`。
能立刻停住，说明全局热键在你的环境里生效了（这一步只能真人验，见上面「紧急停止」）。

跑完会打印时序统计，例如：

```
时序诊断：事件 16 条；时间轴抖动 均值 0.411ms / p95 2.325ms / 最大 5.192ms（已扣除整体偏移 2200.000ms，该偏移只影响开始时刻，不影响节奏）；扣偏后有符号残差 0.000 ~ +5.193ms
```

「整体偏移」就是倒计时那几秒，被单独列出来，不算作抖动。

### 权限

* 默认 **`asInvoker`**：不弹 UAC、不自动提权，**不需要管理员**。
* 如果目标程序权限更高，Windows UIPI 会拦下合成输入，界面会提示你手动以管理员身份运行。
  **不会**自动提权，也**不会**做任何 UIPI 绕过。

### 明确不做的事

全局低层键盘钩子、进程检测、自动抢焦点、自动启动游戏、反作弊绕过、驱动输入、虚拟 HID、
宏导出、MIDI 编辑器、音频转录、多游戏支持。

本项目就是：**MIDI → 普通 Windows 用户态合成输入**。

---

## 构建与发布

需要 .NET 8 SDK。

```powershell
dotnet build  DeltaMusePlayer.sln -c Release
dotnet test   tests\DeltaMusePlayer.Tests\DeltaMusePlayer.Tests.csproj -c Release
powershell -File scripts\publish.ps1          # → artifacts\win-x64\DeltaMusePlayer.exe
```

`publish.ps1` 产出 **win-x64 自包含单文件**（用户不需要装 .NET），开启裁剪。
真实输入没有引入任何新的第三方依赖。

---

## 第三方与许可

本项目用 MIT 许可，见 `LICENSE`。

复用 / 改编了 [ChickenD233/midikey-player](https://github.com/ChickenD233/midikey-player)（MIT）的部分实现
（含 `InputSender` 的注入形态），逐项清单见 `THIRD_PARTY_NOTICES.md`。
两份文本都会嵌入发布产物。

---

## 免责声明

本工具仅供学习和个人使用。在联机游戏中使用自动化可能违反游戏规则并导致账号处罚，后果由使用者承担。
