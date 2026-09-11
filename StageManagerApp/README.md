# Stage Manager for Windows (台前调度)

将 macOS 台前调度 (Stage Manager) 的优雅窗口管理体验带到 Windows 平台，以**极致的性能**和**原生的渲染**重塑你的桌面工作流。

![.NET](https://img.shields.io/badge/.NET%2010-WPF-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

---

## ✨ 核心特性

- **左侧长廊缩略图**：所有后台窗口以实时缩略图的形式整齐排列在屏幕左侧，一目了然。
- **一键无缝切换**：点击长廊中的缩略图瞬间切换到对应窗口，旧的前景窗口自动优雅退至后台。
- **智能避让 (Smart Occlusion)**：当任何软件进入**最大化**或**无边框全屏**（如看视频、打游戏）时，侧边栏会极其懂事地自动隐藏；当你还原窗口时，侧边栏自然回归。
- **跨应用自由组合**：按住 Shift 点击缩略图，将不同应用的窗口合并为一个工作组。
- **贴边悬停呼出**：在侧边栏隐藏状态下，只需将鼠标撞击屏幕最左侧（3像素触发区），即可瞬间召唤长廊。

---

## 🆚 与 macOS 原生台前调度的异同 (Diff)

本工具致力于在遵循 Windows 用户习惯的前提下，还原 macOS 台前调度的精髓。

### 我们完美还原的体验：
1. **背景分组**与**前台舞台**的二元化管理概念。
2. 窗口切换时的流畅感与视觉一致性。
3. 全屏应用下的自动隐藏与避让机制。

### 我们不同的地方：
1. **交互逻辑重构**：macOS 支持直接将桌面文件拖拽进特定的窗口分组；由于底层架构差异，本工具目前的拖拽逻辑更侧重于窗口焦点的纯粹切换。
2. **视觉排版差异**：macOS 的同组窗口在侧边栏会保持错落有致的三维重叠；由于 Windows DWM API 限制，本工具采用平铺/层叠排列展示同组窗口。
3. **桌面点击响应**：macOS 点击空白桌面会隐藏所有窗口；本工具更符合 Windows 原生习惯，始终保持一个活跃的前景舞台。

---

## 🏗️ 极致性能黑科技架构

传统的桌面管理工具往往通过高频暴力轮询（Polling，如 `CompositionTarget.Rendering` 60FPS 循环）来监控窗口状态，会导致严重的 CPU 损耗。本项目的设计哲学是：**完全事件驱动（Event-Driven）**与**零无用开销**。

| 核心技术点 | 实现原理 | 性能收益 |
|------|------|------|
| **零轮询焦点追踪** | 摒弃帧循环，改用底层 Shell 钩子 (`HSHELL_WINDOWACTIVATED`)。系统只在用户真实切换窗口瞬间执行逻辑。 | **CPU 占用无限接近 0.00%** |
| **极速最大化侦测** | 使用耗时仅一微秒的 `GetWindowPlacement` API 直接获取前台窗口状态，替代繁重的全量窗口坐标对比。 | **算法开销趋近于 0** |
| **原生 DWM 渲染** | 缩略图由 Windows Desktop Window Manager 直接合成。不是静态截图，而是原生渲染管线的活体映射（视频/游戏实时播放）。 | **零 GPU/CPU 额外绘制开销** |
| **异步残影消除** | 引入 150ms 异步延时，完美避开 Windows 默认窗口最小化动画时的扭曲帧，保证缩略图截取绝对精准完美。 | **丝滑无闪烁的视觉体验** |

---

## 🚀 快速开始

### 环境要求
- Windows 10 / Windows 11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### 编译与运行
```powershell
git clone https://github.com/QSC707/StageManagerApp.git
cd StageManagerApp
dotnet build -c Release
./bin/Release/net10.0-windows/StageManagerApp.exe
```

---

## 📁 项目核心结构

```
StageManagerApp/
├── App.xaml / App.xaml.cs        # 应用入口
├── MainWindow.xaml / .cs         # 主窗口 UI，处理智能避让与悬停动画
├── WindowManager.cs              # 核心窗口管理器（Shell 钩子、分组逻辑）
├── DwmThumbnailControl.xaml/.cs  # DWM 实时活体缩略图控件
├── CascadingPanel.cs             # 组合窗口层叠布局引擎
├── Win32.cs                      # Win32 原生 API P/Invoke 声明
└── StageManagerApp.csproj        # 项目配置
```

---

## 📜 License
[MIT](LICENSE) - 欢迎对 Windows 桌面美化和极客开发有兴趣的开发者提交 PR！
