# Stage Manager for Windows (台前调度)

将 macOS 台前调度 (Stage Manager) 的优雅窗口管理体验带到 Windows 平台，以**极致的性能**和**原生的渲染**重塑你的桌面工作流。

[![Download Release](https://img.shields.io/github/v/release/QSC707/StageManagerApp?label=Download%20Release&color=brightgreen)](https://github.com/QSC707/StageManagerApp/releases/latest)
![.NET](https://img.shields.io/badge/.NET%2010-WPF-blue)
![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

---

## 📥 下载与各版本说明 (Downloads & Editions)

本项目提供多种构建版本，满足不同用户的环境与性能偏好：

👉 **[前往 GitHub Releases 查看并下载最新版本](https://github.com/QSC707/StageManagerApp/releases/latest)**

| 版本名称 | 资产文件名 | 大小 | 运行要求 | 详细说明与推荐人群 |
|---|---|---|---|---|
| 🟢 **独立免安装绿色版 (推荐)** | `StageManagerApp-win-x64-Standalone.zip` 或 `StageManagerApp.exe` | ~68 MB | **无**（解压即用） | **最适合绝大多数用户**。单文件内置 .NET 10 完整运行时，并开启了 LZ4 高效单文件压缩，任何电脑开箱即用，无需配置环境。 |
| ⚡ **ReadyToRun (R2R AOT) 极速版** | `StageManagerApp-win-x64-ReadyToRun-AOT.zip` | ~80 MB | **无**（解压即用） | **追求瞬时响应与极致低延迟的用户**。利用 .NET Crossgen2 提前将 IL 编译为 CPU 本地机器码，彻底消除首次运行 JIT 预热延迟，兼具 100% 稳定性与原生效能。 |
| 🪶 **超轻极速便携版** | `StageManagerApp-FrameworkDependent.zip` | **仅 ~200 KB** | 需安装 [.NET 10 运行时](https://dotnet.microsoft.com/download) | **极客/追求极小磁盘占用的用户**。仅包含本应用自身的核心逻辑与样式资源，体积羽量级，内存与硬盘开销极低。 |
| 🧪 **Native AOT 实验版** | `StageManagerApp-win-x64-NativeAOT-Experimental.zip` | ~18 MB | 纯二进制机器码 | **技术尝鲜版**。通过 C++ 原生链接器编译为无 VM 纯机器码（注：由于微软官方对经典 WPF 动态反射和 XAML 树的限制，此版本供底层探索，可能存在特定边缘情况）。 |

---

## ✨ 核心特性

- **左侧长廊缩略图**：所有后台窗口以实时缩略图的形式整齐排列在屏幕左侧，一目了然。
- **瞬切无顿挫**：基于 `SW_SHOWMINNOACTIVE` 最小化调度，杜绝焦点交接引起的 Z-order 频繁重排，实现类似 macOS 的瞬时丝滑切换。
- **智能避让 (Smart Occlusion)**：当任何前台应用进入**最大化**或**全屏**（如视频播放、游戏全屏）时，侧边栏会自动优雅退避隐藏；窗口还原时自动重现。
- **贴边悬停呼出**：侧边栏隐藏状态下，只需将鼠标移至屏幕左侧边缘（3 像素感应带），长廊即刻平滑展开。
- **一键快速隐藏**：左下角内置极简快捷隐藏按钮，点击即可临时收起长廊，不遮挡任何桌面图标。
- **跨应用自由组合**：按住 Shift 点击缩略图，将不同应用的窗口合并为一个工作组。

---

## 🏗️ 极致性能架构设计

传统的桌面管理工具往往通过高频暴力轮询（Polling，如 60FPS 帧循环）来监控窗口状态，造成高额的 CPU 和电池损耗。本项目采用**完全事件驱动（Event-Driven）**架构：

| 核心技术点 | 实现原理 | 性能收益 |
|---|---|---|
| **零轮询焦点追踪** | 采用 Windows 底层 Shell Hook (`HSHELL_WINDOWACTIVATED`)，只在真实窗口切换时触发。 | **CPU 占用趋近 0.00%** |
| **极速最大化侦测** | 使用微秒级 `GetWindowPlacement` API 直接获取前台窗口状态，替代全屏像素对比。 | **算法开销趋近于 0** |
| **原生 DWM 渲染** | 缩略图由 Windows Desktop Window Manager 直接硬件合成，视频与动态内容活体映射。 | **零 GPU/CPU 额外绘制开销** |
| **无顿挫激活调度** | 采用 `SW_SHOWMINNOACTIVE` 最小化后台窗口，彻底切断系统动画冲突与焦点抖动。 | **闪电般的瞬间切换体验** |

---

## 🚀 开发者快速开始

### 环境要求
- Windows 10 / Windows 11 (x64)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### 编译与运行
```powershell
# 克隆仓库
git clone https://github.com/QSC707/StageManagerApp.git
cd StageManagerApp/StageManagerApp

# 编译并运行
dotnet build -c Release
./bin/Release/net10.0-windows/StageManagerApp.exe
```

### 单文件独立发布
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

---

## 📁 项目核心结构

```
StageManagerApp/
├── StageManagerApp.csproj        # 项目工程配置 (.NET 10 WPF)
├── App.xaml / App.xaml.cs        # 应用入口与单例托盘
├── Core/
│   └── WindowManager.cs          # 核心窗口管理器（Shell 钩子、舞台分组、激活逻辑）
├── Views/
│   ├── MainWindow.xaml           # 长廊主界面（毛玻璃样式、触发区、隐显动画）
│   └── MainWindow.xaml.cs        # 视图逻辑（悬停侦测、避让计时器、穿透控制）
├── Controls/
│   ├── CascadingPanel.cs         # 组合窗口三维层叠布局引擎
│   ├── DwmThumbnailControl.xaml  # DWM 实时活体缩略图容器
│   └── DwmThumbnailControl.xaml.cs
└── Native/
    └── Win32.cs                  # Windows API P/Invoke 声明 (DWM, User32)
```

---

## 📜 License
[MIT](LICENSE) - 欢迎对 Windows 桌面美化和效率工具开发感兴趣的开发者提交 PR 与 Issue！
