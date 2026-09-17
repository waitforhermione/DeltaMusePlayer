# DeltaMuse Player v0.2.0

把 DeltaMuse 导出的**单声部 MIDI** 映射到《三角洲行动》口琴键位，按时序自动演奏。
使用说明见 [README](https://github.com/waitforhermione/DeltaMusePlayer#readme)。

---

## 变更

**M2：Windows 用户态真实键鼠输入。**

- `WindowsInputBackend`：`SendInput` 注入，键盘走扫描码（`KEYEVENTF_SCANCODE`），扫描码由 `MapVirtualKeyW` 取得。
- 安全闸门：默认 Preview、Real Input 需一次确认、倒计时 0/1/2/3/5/10s（默认 3）、
  **REAL INPUT ACTIVE** 横幅、F12 紧急停止、只释放本程序按过的输入、异常路径 best-effort 释放。
- 全程 `asInvoker`，不自我提权，不使用任何全局钩子。
- 诊断命令：`keymap` / `dryrun` / `send` / `--diagnose`。

**修复两个真实 bug：**

1. 倒计时误用「停止按钮可用性」当取消信号，导致倒计时第一秒自我中止、派发 0 条 ——
   表现就是「倒计时一结束就立刻播放结束」。
2. 时序诊断把倒计时预滚当成抖动，均值 / p95 / 最大被抬到 3000ms 量级。
   现在「整体偏移（误差最小值）」与「抖动（扣偏后残差）」分开报告。

**文档：** README 精简为纯使用说明。

---

## 下载

| 文件 | 说明 |
|---|---|
| `DeltaMusePlayer.exe` | Windows x64 自包含单文件，**不需要**装 .NET，约 21 MB |
| `LICENSE` | MIT |
| `THIRD_PARTY_NOTICES.md` | 第三方声明与逐文件复用说明 |

解压后请**整个文件夹一起用** —— `Profiles\delta_harmonica.json` 是键位方案，不能只拷 exe。

---

## 验证状态

| 项目 | 状态 |
|---|---|
| 单元测试 | ✅ 177 / 177 |
| `SendInput` 被系统接受 | ✅ 16 / 16 事件 |
| 时序精度 | ✅ 抖动均值 0.411ms / p95 2.325ms |
| Notepad 键盘验收 | ❌ **未做** |
| 鼠标点击验收 | ❌ **未做** |
| 游戏内实测 | ❌ **未做** |

最后三项没做，原因是开发环境没有可靠的窗口焦点控制。
**「SendInput 被系统接受」不等于「目标窗口确实收到了字符」** ——
上游戏之前请先按 README 在记事本里自己验收一遍。

---

## 免责声明

本工具仅供学习和个人使用。在联机游戏中使用自动化可能违反游戏规则并导致账号处罚，后果由使用者承担。
