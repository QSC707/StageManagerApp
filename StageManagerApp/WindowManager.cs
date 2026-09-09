using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using Serilog;

namespace StageManagerApp
{
    public class WindowInfo
    {
        public IntPtr Hwnd { get; set; }
        public ImageSource? Icon { get; set; }
    }

    public class StageGroup : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private Guid _id = Guid.NewGuid();
        public Guid Id { get => _id; set { _id = value; OnPropertyChanged(nameof(Id)); } }
        
        private List<WindowInfo> _windows = [];
        public List<WindowInfo> Windows 
        { 
            get => _windows; 
            set { _windows = value; OnPropertyChanged(nameof(Windows)); } 
        }
        public IntPtr PrimaryHwnd { get; set; }

        private ImageSource? _icon;
        public ImageSource? Icon 
        { 
            get => _icon; 
            set { if (_icon != value) { _icon = value; OnPropertyChanged(nameof(Icon)); } } 
        }
    }

    public class WindowManager
    {
        private static readonly uint CurrentPid = (uint)Process.GetCurrentProcess().Id;
        private uint _wmShellHook;
        private HwndSource _msgSource = null!;
        private IntPtr _msgHwnd;


        private readonly Dictionary<IntPtr, ImageSource> _iconCache = [];

        private bool _isSwitching;

        public ObservableCollection<StageGroup> BackgroundGroups { get; } = [];
        public StageGroup ActiveStage { get; private set; } = new StageGroup();

        public event Action<IntPtr>? OnForegroundWindowChangedEvent;

        public WindowManager()
        {
        }

        public void Initialize()
        {
            Log.Information("Initializing WindowManager...");
            _wmShellHook = Win32.RegisterWindowMessage("SHELLHOOK");

            var parameters = new HwndSourceParameters("StageManagerMsgOnly")
            {
                WindowStyle = 0, Width = 0, Height = 0, ParentWindow = new IntPtr(-3)
            };
            
            _msgSource = new HwndSource(parameters);
            _msgHwnd = _msgSource.Handle;
            _msgSource.AddHook(WndProc);

            Win32.RegisterShellHookWindow(_msgHwnd);

            // 初始状态拉取
            PopulateInitialState();
        }

        public void Shutdown()
        {
            Log.Information("Shutting down WindowManager...");
            if (_msgHwnd != IntPtr.Zero)
            {
                Win32.DeregisterShellHookWindow(_msgHwnd);
                _msgSource.Dispose();
                _msgHwnd = IntPtr.Zero;
            }
        }

        public async void GroupWithStage(StageGroup targetGroup)
        {
            Log.Information($"[GroupWithStage] Grouping {targetGroup.Id} into ActiveStage {ActiveStage.Id}");
            var merged = ActiveStage.Windows.ToList();
            merged.AddRange(targetGroup.Windows);
            ActiveStage.Windows = merged;
            
            if (ActiveStage.PrimaryHwnd == IntPtr.Zero && targetGroup.PrimaryHwnd != IntPtr.Zero)
            {
                ActiveStage.PrimaryHwnd = targetGroup.PrimaryHwnd;
                ActiveStage.Icon = targetGroup.Icon;
            }

            BackgroundGroups.Remove(targetGroup);

            await RestoreStageGroupAsync(targetGroup);
        }

        public async void SwitchToStage(StageGroup targetGroup)
        {
            if (_isSwitching) return;
            _isSwitching = true;
            Log.Information($"[SwitchToStage] Switching to group {targetGroup.Id}. ActiveStage is {ActiveStage.Id}");

            if (targetGroup.Id == ActiveStage.Id)
            {
                Log.Information($"[SwitchToStage] Target matches ActiveStage ID! Merging back!");
                var merged = ActiveStage.Windows.ToList();
                merged.AddRange(targetGroup.Windows);
                ActiveStage.Windows = merged;

                BackgroundGroups.Remove(targetGroup);

                await RestoreStageGroupAsync(targetGroup);
                
                await Task.Delay(200);
                _isSwitching = false;
                return;
            }

            if (ActiveStage.Windows.Count > 0)
            {
                PushActiveStageToBackground();
                // 核心优化：将延迟设为 60 毫秒
                await Task.Delay(60);
            }

            BackgroundGroups.Remove(targetGroup);

            ActiveStage.Id = targetGroup.Id;
            ActiveStage.Windows = targetGroup.Windows.ToList();
            ActiveStage.PrimaryHwnd = targetGroup.PrimaryHwnd;
            ActiveStage.Icon = targetGroup.Icon;

            await RestoreStageGroupAsync(ActiveStage);

            await Task.Delay(200);
            _isSwitching = false;
        }

        public async void ExtractWindowFromGroup(StageGroup sourceGroup, WindowInfo targetWin)
        {
            Log.Information($"[ExtractWindowFromGroup] Extracting {targetWin.Hwnd} from group {sourceGroup.Id}");
            if (_isSwitching) return;
            _isSwitching = true;

            if (ActiveStage.Windows.Count > 0)
            {
                PushActiveStageToBackground();
                // 核心优化：将延迟设为 75 毫秒
                await Task.Delay(75);
            }

            // 完全拆分为独立的新分组，赋予全新 ID，防止之后又被错误合并！
            ActiveStage.Id = Guid.NewGuid();
            ActiveStage.Windows = new List<WindowInfo> { targetWin };
            ActiveStage.PrimaryHwnd = targetWin.Hwnd;
            ActiveStage.Icon = targetWin.Icon;

            var newList = sourceGroup.Windows.ToList();
            newList.Remove(targetWin);
            sourceGroup.Windows = newList;

            if (sourceGroup.Windows.Count == 0)
            {
                BackgroundGroups.Remove(sourceGroup);
            }

            if (Win32.IsIconic(targetWin.Hwnd))
            {
                Win32.ShowWindow(targetWin.Hwnd, Win32.SW_RESTORE);
            }
            Win32.SetForegroundWindow(targetWin.Hwnd);

            await Task.Delay(200);
            _isSwitching = false;
        }

        private async Task RestoreStageGroupAsync(StageGroup targetGroup)
        {
            if (targetGroup.Windows.Count == 0) return;

            // 1. 找到“主角窗口”（被点击的那个，或者列表第一个）
            var primaryHwnd = targetGroup.PrimaryHwnd != IntPtr.Zero ? targetGroup.PrimaryHwnd : targetGroup.Windows[0].Hwnd;

            // 2. 率先且唯一地用原生的 SW_RESTORE 恢复主角窗口，它将光明正大地升起并获得焦点
            Win32.ShowWindow(primaryHwnd, Win32.SW_RESTORE);

            // 3. 其他非主角窗口，用 SW_SHOWNOACTIVATE 默默在后台恢复其原样，绝不抢主角的镜头，也绝不覆盖主角
            foreach (var win in targetGroup.Windows)
            {
                if (win.Hwnd != primaryHwnd && Win32.IsIconic(win.Hwnd))
                {
                    Win32.ShowWindow(win.Hwnd, Win32.SW_SHOWNOACTIVATE);
                }
            }
        }

        private void PopulateInitialState()
        {
            IntPtr fgHwnd = Win32.GetForegroundWindow();
            List<WindowInfo> validWindows = [];

            Win32.EnumWindows((hWnd, _) =>
            {
                if (IsValidStageWindow(hWnd, out var win) && win != null)
                {
                    validWindows.Add(win);
                }
                return true;
            }, IntPtr.Zero);

            foreach (var win in validWindows)
            {
                if (win.Hwnd == fgHwnd)
                {
                    ActiveStage.Windows = [win];
                    ActiveStage.PrimaryHwnd = win.Hwnd;
                    ActiveStage.Icon = win.Icon;
                }
                else
                {
                    BackgroundGroups.Add(new StageGroup { Windows = [win], PrimaryHwnd = win.Hwnd, Icon = win.Icon });
                }
            }

            while (BackgroundGroups.Count > 6)
            {
                BackgroundGroups.RemoveAt(BackgroundGroups.Count - 1);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == _wmShellHook)
            {
                int eventCode = wParam.ToInt32();
                if (eventCode == Win32.HSHELL_WINDOWACTIVATED || eventCode == 32772 /* HSHELL_RUDEAPPACTIVATED */)
                {
                    OnForegroundWindowChanged(lParam);
                }
                else if (eventCode == Win32.HSHELL_WINDOWDESTROYED)
                {
                    OnWindowDestroyed(lParam);
                }
            }
            return IntPtr.Zero;
        }

        private void OnForegroundWindowChanged(IntPtr hWnd)
        {
            OnForegroundWindowChangedEvent?.Invoke(hWnd);

            if (_isSwitching) return;

            var minimizedWindows = ActiveStage.Windows.Where(w => Win32.IsIconic(w.Hwnd)).ToList();
            if (minimizedWindows.Count > 0)
            {
                Log.Information($"[Audit ActiveStage] Total windows: {ActiveStage.Windows.Count}, Minimized count: {minimizedWindows.Count}");
                
                foreach(var w in minimizedWindows)
                {
                    ActiveStage.Windows.Remove(w);
                }

                // 放入同名长廊文件夹
                var existingFolder = BackgroundGroups.FirstOrDefault(g => g.Id == ActiveStage.Id);
                if (existingFolder != null)
                {
                    Log.Information($"[Minimize] Returning {minimizedWindows.Count} minimized windows to existing folder {ActiveStage.Id}");
                    var merged = existingFolder.Windows.ToList();
                    merged.AddRange(minimizedWindows);
                    existingFolder.Windows = merged;
                }
                else
                {
                    Log.Information($"[Minimize] Creating new folder {ActiveStage.Id} in sidebar for minimized windows");
                    var newGroup = new StageGroup { Id = ActiveStage.Id, Windows = minimizedWindows, PrimaryHwnd = minimizedWindows.First().Hwnd, Icon = minimizedWindows.First().Icon };
                    BackgroundGroups.Insert(0, newGroup);
                }

                // 触发UI更新
                ActiveStage.Windows = ActiveStage.Windows.ToList();

                if (ActiveStage.Windows.Count == 0)
                {
                    Log.Information($"[Minimize] ActiveStage is empty. Generating new ID for the next stage.");
                    ActiveStage.Id = Guid.NewGuid();
                    ActiveStage.PrimaryHwnd = IntPtr.Zero;
                    ActiveStage.Icon = null;
                }
                else
                {
                    if (minimizedWindows.Any(w => w.Hwnd == ActiveStage.PrimaryHwnd))
                    {
                        var firstVisible = ActiveStage.Windows.FirstOrDefault();
                        if (firstVisible != null)
                        {
                            ActiveStage.PrimaryHwnd = firstVisible.Hwnd;
                            ActiveStage.Icon = firstVisible.Icon;
                        }
                    }
                }
            }

            if (!Win32.IsWindow(hWnd) || !Win32.IsWindowVisible(hWnd)) return; // 提前拦截非可见窗口

            // 如果该窗口已经在当前的活跃舞台中，不做处理
            if (ActiveStage != null && ActiveStage.Windows.Any(w => w.Hwnd == hWnd))
            {
                return;
            }

            if (!IsValidStageWindow(hWnd, out var newWin) || newWin == null)
            {
                bool isDesktop = false;
                unsafe
                {
                    char* buffer = stackalloc char[256];
                    int len = Win32.GetClassName(hWnd, buffer, 256);
                    if (len > 0) 
                    {
                        ReadOnlySpan<char> span = new ReadOnlySpan<char>(buffer, len);
                        if (span is "Progman" or "WorkerW") isDesktop = true;
                    }
                }
                
                if (isDesktop)
                {
                    HandleDesktopClick();
                }
                return; 
            }

            var bgGroup = BackgroundGroups.FirstOrDefault(g => g.Windows.Any(w => w.Hwnd == hWnd));
            if (bgGroup != null)
            {
                var targetWin = bgGroup.Windows.First(w => w.Hwnd == hWnd);
                ExtractWindowFromGroup(bgGroup, targetWin);
            }
            else
            {
                if (ActiveStage != null && ActiveStage.Windows.Count > 0)
                {
                    var newList = ActiveStage.Windows.ToList();
                    newList.Add(newWin);
                    ActiveStage.Windows = newList;
                    
                    ActiveStage.PrimaryHwnd = newWin.Hwnd;
                    if (newWin.Icon != null) ActiveStage.Icon = newWin.Icon;
                }
                else
                {
                    // Ensure new stage starts fresh
                    ActiveStage.Windows = [newWin];
                    ActiveStage.PrimaryHwnd = newWin.Hwnd;
                    ActiveStage.Icon = newWin.Icon;
                }
            }
        }

        private void HandleDesktopClick()
        {
            if (_isSwitching) return;
            _isSwitching = true;
            try
            {
                PushActiveStageToBackground();
            }
            finally
            {
                _isSwitching = false;
            }
        }

        private void PushActiveStageToBackground()
        {
            if (ActiveStage.Windows.Count == 0) return;

            // 正常推入长廊顶端
            foreach (var win in ActiveStage.Windows)
            {
                if (Win32.IsWindowVisible(win.Hwnd) && !Win32.IsIconic(win.Hwnd))
                {
                    // 放弃发送消息的破坏性方案，使用微软官方专门提供的异线程强制最小化标志 (SW_FORCEMINIMIZE)
                    // 它专门用来在不影响目标程序内部逻辑的情况下，强制将其最小化（对相册等 UWP 应用完美生效且安全）
                    Win32.ShowWindow(win.Hwnd, Win32.SW_FORCEMINIMIZE);
                }
            }

            var existingFolder = BackgroundGroups.FirstOrDefault(g => g.Id == ActiveStage.Id);
            if (existingFolder != null)
            {
                Log.Information($"[PushActiveStage] Merging ActiveStage {ActiveStage.Id} into existing folder in sidebar.");
                var merged = existingFolder.Windows.ToList();
                merged.AddRange(ActiveStage.Windows);
                existingFolder.Windows = merged;
                existingFolder.PrimaryHwnd = ActiveStage.PrimaryHwnd;
                existingFolder.Icon = ActiveStage.Icon;
                
                BackgroundGroups.Remove(existingFolder);
                BackgroundGroups.Insert(0, existingFolder);
            }
            else
            {
                Log.Information($"[PushActiveStage] Creating new folder {ActiveStage.Id} in sidebar.");
                var newGroup = new StageGroup
                {
                    Id = ActiveStage.Id,
                    Windows = ActiveStage.Windows.ToList(),
                    PrimaryHwnd = ActiveStage.PrimaryHwnd,
                    Icon = ActiveStage.Icon
                };
                BackgroundGroups.Insert(0, newGroup);
            }
            
            while (BackgroundGroups.Count > 6) 
            {
                BackgroundGroups.RemoveAt(BackgroundGroups.Count - 1);
            }

            ActiveStage.Id = Guid.NewGuid(); // Give the next empty stage a fresh ID!
            ActiveStage.Windows = new List<WindowInfo>();
            ActiveStage.PrimaryHwnd = IntPtr.Zero;
            ActiveStage.Icon = null;
        }

        private void OnWindowDestroyed(IntPtr hWnd)
        {
            _iconCache.Remove(hWnd);

            for (int i = BackgroundGroups.Count - 1; i >= 0; i--)
            {
                var group = BackgroundGroups[i];
                var newList = group.Windows.ToList();
                if (newList.RemoveAll(w => w.Hwnd == hWnd) > 0)
                {
                    group.Windows = newList;
                }
                
                if (group.Windows.Count == 0)
                {
                    BackgroundGroups.RemoveAt(i);
                }
                else
                {
                    if (group.PrimaryHwnd == hWnd) group.PrimaryHwnd = group.Windows[0].Hwnd;
                }
            }

            if (ActiveStage.Windows.Count > 0)
            {
                var newList = ActiveStage.Windows.ToList();
                if (newList.RemoveAll(w => w.Hwnd == hWnd) > 0)
                {
                    ActiveStage.Windows = newList;
                }
                
                if (ActiveStage.Windows.Count == 0)
                {
                    ActiveStage.Id = Guid.NewGuid();
                    ActiveStage.PrimaryHwnd = IntPtr.Zero;
                    ActiveStage.Icon = null;
                }
                else
                {
                    if (ActiveStage.PrimaryHwnd == hWnd) ActiveStage.PrimaryHwnd = ActiveStage.Windows[0].Hwnd;
                }
            }
        }

        // Duplicate methods removed.

        private Dictionary<string, ImageSource> _exeIconCache = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _exeIconCacheKeys = new List<string>();

        private ImageSource? ExtractWindowIcon(IntPtr hwnd)
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            IntPtr hProcess = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            
            if (hProcess != IntPtr.Zero)
            {
                uint size = 1024;
                unsafe
                {
                    char* buffer = stackalloc char[1024];
                    if (Win32.QueryFullProcessImageName(hProcess, 0, buffer, ref size) && size > 0)
                    {
                        Win32.CloseHandle(hProcess);
                        ReadOnlySpan<char> span = new ReadOnlySpan<char>(buffer, (int)size);

                        var lookup = _exeIconCache.GetAlternateLookup<ReadOnlySpan<char>>();
                        if (lookup.TryGetValue(span, out var cachedIcon))
                        {
                            return cachedIcon;
                        }

                        string processPath = new string(buffer, 0, (int)size);
                        Win32.SHFILEINFO shinfo = new Win32.SHFILEINFO();
                        IntPtr hImg = Win32.SHGetFileInfo(processPath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON);
                        
                        if (shinfo.hIcon != IntPtr.Zero)
                        {
                            try
                            {
                                var imageSource = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                                imageSource.Freeze();
                                
                                _exeIconCache[processPath] = imageSource;
                                _exeIconCacheKeys.Add(processPath);
                                
                                // 熔断机制：最多保留 100 个图标，采用简单的 FIFO 防止内存泄漏
                                if (_exeIconCacheKeys.Count > 100)
                                {
                                    var oldest = _exeIconCacheKeys[0];
                                    _exeIconCacheKeys.RemoveAt(0);
                                    _exeIconCache.Remove(oldest);
                                }
                                
                                return imageSource;
                            }
                            catch { }
                            finally
                            {
                                Win32.DestroyIcon(shinfo.hIcon);
                            }
                        }
                    }
                    else
                    {
                        Win32.CloseHandle(hProcess);
                    }
                }
            }

            // Fallback for system windows or when path is inaccessible
            IntPtr hClassIcon = Win32.GetClassLongPtr(hwnd, Win32.GCLP_HICON);
            if (hClassIcon != IntPtr.Zero)
            {
                try {
                    var imageSource = Imaging.CreateBitmapSourceFromHIcon(hClassIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    imageSource.Freeze();
                    return imageSource;
                } 
                catch { }
            }

            return null;
        }

        private bool IsValidStageWindow(IntPtr hWnd, out WindowInfo? info)
        {
            info = null;
            if (!Win32.IsWindow(hWnd) || !Win32.IsWindowVisible(hWnd)) return false;

            bool isExcluded = false;
            unsafe
            {
                char* buffer = stackalloc char[256];
                int len = Win32.GetClassName(hWnd, buffer, 256);
                if (len > 0) 
                {
                    ReadOnlySpan<char> span = new ReadOnlySpan<char>(buffer, len);
                    if (span is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow" or "StageManagerApp") 
                    {
                        isExcluded = true;
                    }
                }
            }
            if (isExcluded) return false;

            long exStyle = Win32.GetWindowLongPtr(hWnd, Win32.GWL_EXSTYLE).ToInt64();
            if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0) return false;

            IntPtr owner = Win32.GetWindow(hWnd, Win32.GW_OWNER);
            if (owner != IntPtr.Zero && (exStyle & Win32.WS_EX_APPWINDOW) == 0) return false;

            int hr = Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (hr == 0 && cloaked != 0) return false;

            Win32.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == CurrentPid || pid == 0) return false;

            info = new WindowInfo
            {
                Hwnd = hWnd,
                Icon = _iconCache.TryGetValue(hWnd, out var cachedIcon) ? cachedIcon : ExtractWindowIcon(hWnd)
            };
            if (info.Icon != null) _iconCache[hWnd] = info.Icon;
            return true;
        }
    }
}
