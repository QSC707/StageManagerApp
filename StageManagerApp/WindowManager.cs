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

        public Guid Id { get; } = Guid.NewGuid();
        public List<WindowInfo> Windows { get; set; } = [];
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
        public StageGroup? ActiveStage { get; private set; }

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
                    ActiveStage = new StageGroup { Windows = [win], PrimaryHwnd = win.Hwnd, Icon = win.Icon };
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

        private void UngroupWindowFromActiveStage(WindowInfo win)
        {
            Log.Information($"Ungrouping window {win.Hwnd} from active stage.");
            ActiveStage!.Windows.Remove(win);
            if (ActiveStage.PrimaryHwnd == win.Hwnd && ActiveStage.Windows.Count > 0)
            {
                ActiveStage.PrimaryHwnd = ActiveStage.Windows[0].Hwnd;
            }

            var newGroup = new StageGroup
            {
                Windows = [win],
                PrimaryHwnd = win.Hwnd,
                Icon = _iconCache.GetValueOrDefault(win.Hwnd)
            };

            // 被拆解的窗口将被最小化并送入侧边栏顶端，而当前组保留在屏幕上
            if (Win32.IsWindowVisible(win.Hwnd) && !Win32.IsIconic(win.Hwnd))
            {
                Win32.ShowWindow(win.Hwnd, Win32.SW_MINIMIZE);
            }
            
            BackgroundGroups.Insert(0, newGroup);
            while (BackgroundGroups.Count > 6) BackgroundGroups.RemoveAt(BackgroundGroups.Count - 1);
        }

        private void OnForegroundWindowChanged(IntPtr hWnd)
        {
            if (_isSwitching) return; // 正在主动切换，忽略钩子事件

            // 状态审计：不论新窗口是否合法/可见，只要发生了焦点切换，我们就要回头检查当前 ActiveStage
            if (ActiveStage != null)
            {
                var minimizedWindows = ActiveStage.Windows.Where(w => Win32.IsIconic(w.Hwnd)).ToList();
                
                if (minimizedWindows.Count > 0)
                {
                    if (minimizedWindows.Count == ActiveStage.Windows.Count)
                    {
                        // 舞台所有窗口均被最小化，直接推入长廊
                        PushActiveStageToBackground();
                    }
                    else
                    {
                        // 多窗口组合中部分被最小化，执行拆解
                        foreach (var win in minimizedWindows)
                        {
                            ActiveStage.Windows.Remove(win);
                            if (ActiveStage.PrimaryHwnd == win.Hwnd && ActiveStage.Windows.Count > 0)
                            {
                                ActiveStage.PrimaryHwnd = ActiveStage.Windows[0].Hwnd;
                            }
                            
                            var newGroup = new StageGroup { Windows = [win], PrimaryHwnd = win.Hwnd, Icon = win.Icon };
                            BackgroundGroups.Insert(0, newGroup);
                        }
                        while (BackgroundGroups.Count > 6) BackgroundGroups.RemoveAt(BackgroundGroups.Count - 1);
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
                // 如果切换到了非合法的窗口（例如桌面、任务栏），检查是否是点击桌面
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
                
                // 白皮书规则 3：返回纯净桌面
                if (isDesktop)
                {
                    HandleDesktopClick();
                }
                return; 
            }

            // 白皮书规则 4：外部干扰与自然切换 (Alt-Tab / 任务栏点击)
            var bgGroup = BackgroundGroups.FirstOrDefault(g => g.Windows.Any(w => w.Hwnd == hWnd));
            if (bgGroup != null)
            {
                // 用户切换到了长廊里的某个应用
                BackgroundGroups.Remove(bgGroup);
                PushActiveStageToBackground();
                
                // 将被直接激活的窗口设为 PrimaryHwnd
                bgGroup.PrimaryHwnd = hWnd;
                var newlyActive = bgGroup.Windows.FirstOrDefault(w => w.Hwnd == hWnd);
                if (newlyActive != null && newlyActive.Icon != null) bgGroup.Icon = newlyActive.Icon;
                
                ActiveStage = bgGroup;
            }
            else
            {
                // 用户启动或切换到了一个全新的应用
                PushActiveStageToBackground();
                ActiveStage = new StageGroup 
                { 
                    Windows = [newWin], 
                    PrimaryHwnd = newWin.Hwnd,
                    Icon = ExtractWindowIcon(newWin.Hwnd)
                };
            }
        }

        private void HandleDesktopClick()
        {
            PushActiveStageToBackground();
        }

        private void PushActiveStageToBackground()
        {
            if (ActiveStage == null) return;
            
            // 正常最小化并推入长廊顶端
            foreach (var win in ActiveStage.Windows)
            {
                if (Win32.IsWindowVisible(win.Hwnd) && !Win32.IsIconic(win.Hwnd))
                {
                    Win32.ShowWindow(win.Hwnd, Win32.SW_MINIMIZE);
                }
            }
            
            BackgroundGroups.Insert(0, ActiveStage);
            
            // 保持最多6个后台任务
            while (BackgroundGroups.Count > 6) 
            {
                BackgroundGroups.RemoveAt(BackgroundGroups.Count - 1);
            }
            
            ActiveStage = null;
        }

        private void OnWindowDestroyed(IntPtr hWnd)
        {
            // 清理缓存
            _iconCache.Remove(hWnd);

            // 从侧边栏清理
            for (int i = BackgroundGroups.Count - 1; i >= 0; i--)
            {
                var group = BackgroundGroups[i];
                group.Windows.RemoveAll(w => w.Hwnd == hWnd);
                if (group.Windows.Count == 0)
                {
                    BackgroundGroups.RemoveAt(i);
                }
                else
                {
                    if (group.PrimaryHwnd == hWnd) group.PrimaryHwnd = group.Windows[0].Hwnd;
                }
            }

            // 白皮书规则 5：舞台窗口关闭
            // 如果舞台变空，我们只需设为 null（露出桌面），绝对不自动把侧边栏的东西拉过来！
            if (ActiveStage != null)
            {
                ActiveStage.Windows.RemoveAll(w => w.Hwnd == hWnd);
                if (ActiveStage.Windows.Count == 0)
                {
                    ActiveStage = null;
                }
                else
                {
                    if (ActiveStage.PrimaryHwnd == hWnd) ActiveStage.PrimaryHwnd = ActiveStage.Windows[0].Hwnd;
                }
            }
        }

        // 白皮书规则 1：从左侧长廊“唤醒”任务
        public async void SwitchToStage(StageGroup targetGroup)
        {
            if (ActiveStage == targetGroup) return;
            if (_isSwitching) return;
            
            _isSwitching = true;
            try
            {
                BackgroundGroups.Remove(targetGroup);
                PushActiveStageToBackground();
                
                ActiveStage = targetGroup;

                await RestoreStageGroupAsync(targetGroup);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception in SwitchToStage");
            }
            finally
            {
                _isSwitching = false;
            }
        }

        // 白皮书规则 2：跨应用自由组合
        public async void GroupWithStage(StageGroup targetGroup)
        {
            if (ActiveStage == null)
            {
                SwitchToStage(targetGroup);
                return;
            }
            if (ActiveStage == targetGroup) return;
            if (_isSwitching) return;
            
            _isSwitching = true;
            try
            {
                BackgroundGroups.Remove(targetGroup);
                ActiveStage.Windows.AddRange(targetGroup.Windows);
                
                await RestoreStageGroupAsync(targetGroup);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Exception in GroupWithStage");
            }
            finally
            {
                _isSwitching = false;
            }
        }

        private async Task RestoreStageGroupAsync(StageGroup targetGroup)
        {
            // 给 UI 引擎留出 50 毫秒的时间去销毁缩略图，彻底解决黑框闪烁问题
            await Task.Delay(50);

            if (targetGroup.Windows.Count == 0) return;

            if (targetGroup.Windows.Count == 1)
            {
                var win = targetGroup.Windows[0];
                Win32.ShowWindow(win.Hwnd, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(win.Hwnd);
            }
            else
            {
                // 按照窗口面积降序排列（大的垫底，小的在最顶上）
                int wpSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>();
                targetGroup.Windows.Sort((a, b) =>
                {
                    Win32.WINDOWPLACEMENT wpA = new Win32.WINDOWPLACEMENT { length = wpSize };
                    Win32.GetWindowPlacement(a.Hwnd, ref wpA);
                    long areaA = (wpA.rcNormalPosition.Right - wpA.rcNormalPosition.Left) * (wpA.rcNormalPosition.Bottom - wpA.rcNormalPosition.Top);

                    Win32.WINDOWPLACEMENT wpB = new Win32.WINDOWPLACEMENT { length = wpSize };
                    Win32.GetWindowPlacement(b.Hwnd, ref wpB);
                    long areaB = (wpB.rcNormalPosition.Right - wpB.rcNormalPosition.Left) * (wpB.rcNormalPosition.Bottom - wpB.rcNormalPosition.Top);

                    return areaB.CompareTo(areaA);
                });

                // 将最小的窗口设为焦点
                var smallest = targetGroup.Windows.LastOrDefault();
                if (smallest != null)
                {
                    targetGroup.PrimaryHwnd = smallest.Hwnd;
                    if (smallest.Icon != null) targetGroup.Icon = smallest.Icon;
                }

                IntPtr hdwp = Win32.BeginDeferWindowPos(targetGroup.Windows.Count);
                if (hdwp != IntPtr.Zero)
                {
                    // 反向遍历：从最小的(最后面的)开始放到顶部
                    IntPtr insertAfter = Win32.HWND_TOP;
                    
                    foreach (var win in targetGroup.Windows.AsEnumerable().Reverse())
                    {
                        // 先以无焦点方式恢复窗口
                        Win32.ShowWindow(win.Hwnd, Win32.SW_SHOWNOACTIVATE);
                        // 然后通过 DeferWindowPos 设置层级
                        hdwp = Win32.DeferWindowPos(hdwp, win.Hwnd, insertAfter, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                        if (hdwp == IntPtr.Zero) break;
                        insertAfter = win.Hwnd; // 保证下一个（较大的）窗口压在当前窗口下面
                    }

                    if (hdwp != IntPtr.Zero)
                    {
                        Win32.EndDeferWindowPos(hdwp);
                    }
                }
                else
                {
                    // Fallback
                    foreach (var win in targetGroup.Windows)
                    {
                        Win32.ShowWindow(win.Hwnd, Win32.SW_RESTORE);
                    }
                }
                
                Win32.SetForegroundWindow(targetGroup.PrimaryHwnd);
            }

            await Task.Delay(100); // 让 UI 和系统的焦点消息飞一会儿
        }

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
