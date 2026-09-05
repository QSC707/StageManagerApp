# Stage Manager for Windows

将 macOS 台前调度 (Stage Manager) 的窗口管理体验带到 Windows 平台。

![.NET](https://img.shields.io/badge/.NET%2010-WPF-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

## ✨ 功能特性

- **左侧长廊缩略图**：所有后台窗口以实时缩略图的形式整齐排列在屏幕左侧，一目了然
- **一键切换**：点击长廊中的缩略图即可瞬间切换到对应窗口
- **跨应用自由组合**：按住 Shift 点击缩略图，将不同应用的窗口合并为一个工作组
- **智能拆解**：最小化组合中的某个窗口，自动将其从组合中拆出并归入长廊
- **桌面回归**：点击桌面时，所有窗口优雅地收入长廊，还你一个干净的桌面
- **零钩子架构**：不使用任何 `SetWinEventHook` 等系统级钩子，仅依赖 Shell 原生消息 + 焦点切换时的状态审计，对系统性能零侵入

## 🏗️ 技术架构

| 层级 | 技术 |
|------|------|
| UI 框架 | WPF (.NET 10) |
| 缩略图渲染 | DWM Thumbnail API (实时画面，非截图) |
| 窗口管理 | Win32 Shell Hook + 焦点审计 |
| 窗口层级 | 基于窗口面积的智能排序 |

### 核心设计理念

1. **被动感知，而非主动监控**：程序不轮询、不挂钩、不拦截。仅在系统焦点自然发生切换时，被动地"回头审计"窗口状态
2. **极致轻量**：数据结构精简至仅保留窗口句柄和图标，无多余的字符串分配和进程查询
3. **DWM 原生渲染**：缩略图由 Windows 桌面窗口管理器 (DWM) 直接合成，零 CPU 绘制开销

## 🚀 快速开始

### 环境要求

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### 运行

```bash
git clone https://github.com/QSC707/StageManagerApp.git
cd StageManagerApp
dotnet run
```

## 📁 项目结构

```
StageManagerApp/
├── App.xaml / App.xaml.cs        # 应用入口
├── MainWindow.xaml / .cs         # 主窗口 UI 与交互逻辑
├── WindowManager.cs              # 核心窗口管理器（焦点审计、组合/拆解）
├── DwmThumbnailControl.xaml / .cs # DWM 实时缩略图控件
├── CascadingPanel.cs             # 组合窗口层叠布局面板
├── Win32.cs                      # Win32 API 声明
└── StageManagerApp.csproj        # 项目配置
```

## 📜 License

[MIT](LICENSE)
