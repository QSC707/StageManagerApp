using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Serilog;

namespace StageManagerApp
{
    public partial class MainWindow : Window
    {
        private WindowManager _windowManager;

        private bool _isObscured = false;
        private bool _isHovered = false;
        private double _visibleLeft;
        private double _hiddenLeft;

        // 缓存长廊的物理像素边界，避免在每次事件中重复计算
        private int _cachedMyLeft;
        private int _cachedCriticalRight;
        private int _cachedMyTop;
        private int _cachedMyBottom;

        public MainWindow()
        {
            InitializeComponent();
            _windowManager = new WindowManager();
            this.DataContext = _windowManager;
            
            this.MouseEnter += MainWindow_MouseEnter;
            this.MouseLeave += MainWindow_MouseLeave;

            _windowManager.OnForegroundWindowChangedEvent += WindowManager_OnForegroundWindowChangedEvent;

            _winEventDelegate = new Win32.WinEventDelegate(WinEventProc);
        }

        private Win32.WinEventDelegate _winEventDelegate;
        private IntPtr _locationChangeHook;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Left;
            this.Top = workArea.Top;
            this.Height = workArea.Height;

            _visibleLeft = workArea.Left;
            _hiddenLeft = workArea.Left - this.Width + 3; // 留 3 像素在外边用于触发 MouseEnter，增加稳定性

            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            int exStyle = (int)Win32.GetWindowLongPtr(helper.Handle, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(helper.Handle, Win32.GWL_EXSTYLE, (IntPtr)(exStyle | Win32.WS_EX_NOACTIVATE));

            _windowManager.Initialize();
            ThumbnailsList.ItemsSource = _windowManager.BackgroundGroups;

            // 预先计算长廊的物理像素边界，实现最高性能的碰撞检测
            var src = PresentationSource.FromVisual(this);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            _cachedMyLeft = (int)(_visibleLeft * dpiX);
            int criticalWidth = 130;
            _cachedCriticalRight = (int)((_visibleLeft + criticalWidth) * dpiX);
            _cachedMyTop = (int)(this.Top * dpiY);
            _cachedMyBottom = (int)((this.Top + this.Height) * dpiY);

            // 注册真正的帧同步回调
            System.Windows.Media.CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void CompositionTarget_Rendering(object? sender, EventArgs e)
        {
            if (_isOcclusionDirty)
            {
                _isOcclusionDirty = false;
                CheckOcclusion(_currentForegroundHwnd);
            }
        }

        private void MainWindow_MouseEnter(object sender, MouseEventArgs e)
        {
            _isHovered = true;
            UpdateVisibilityState();
        }

        private void MainWindow_MouseLeave(object sender, MouseEventArgs e)
        {
            _isHovered = false;
            UpdateVisibilityState();
        }

        private IntPtr _currentForegroundHwnd;
        private uint _currentThreadId;
        private long _hookGeneration;
        private bool _isOcclusionDirty;

        // 性能诊断计数器
        private int _rawEventCount = 0;
        private int _coalescedComputeCount = 0;
        private DateTime _lastLogTime = DateTime.Now;

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != Win32.OBJID_WINDOW) return;
            if (hwnd != _currentForegroundHwnd) return;

            System.Threading.Interlocked.Increment(ref _rawEventCount);
            long currentGeneration = _hookGeneration;

            // 事件合并 (Coalescing)：只标记脏状态，推迟到真实的渲染帧 (CompositionTarget.Rendering) 执行
            if (!_isOcclusionDirty)
            {
                _isOcclusionDirty = true;
                // 这里我们不再使用 Dispatcher.InvokeAsync，因为它会被 UI 线程瞬间消化，导致一秒执行几百次。
                // 而是依赖 CompositionTarget_Rendering 在显示器刷新时统一收割。
            }
        }

        private void WindowManager_OnForegroundWindowChangedEvent(IntPtr foregroundHwnd)
        {
            this.Dispatcher.InvokeAsync(() =>
            {
                _currentForegroundHwnd = foregroundHwnd;
                long newGeneration = ++_hookGeneration;
                
                if (foregroundHwnd != IntPtr.Zero)
                {
                    uint threadId = Win32.GetWindowThreadProcessId(foregroundHwnd, out uint processId);
                    
                    // 极致优化：如果新窗口和老窗口属于同一个线程，复用现有的 Hook，直接返回！
                    if (threadId == _currentThreadId && _locationChangeHook != IntPtr.Zero)
                    {
                        Log.Information("[HOOK] 焦点切换，复用同一线程 Hook (TID: {ThreadId})", threadId);
                        CheckOcclusion(foregroundHwnd);
                        return;
                    }

                    Log.Information("[HOOK] 跨线程焦点切换 (旧TID: {OldTID} -> 新TID: {NewTID})，重建 Hook", _currentThreadId, threadId);

                    // 否则，销毁旧的 Hook，创建新的
                    if (_locationChangeHook != IntPtr.Zero)
                    {
                        Win32.UnhookWinEvent(_locationChangeHook);
                        _locationChangeHook = IntPtr.Zero;
                    }

                    _currentThreadId = threadId;

                    if (threadId != 0 && processId != 0)
                    {
                        _locationChangeHook = Win32.SetWinEventHook(
                            Win32.EVENT_OBJECT_LOCATIONCHANGE,
                            Win32.EVENT_OBJECT_LOCATIONCHANGE,
                            IntPtr.Zero,
                            _winEventDelegate,
                            processId, threadId,
                            Win32.WINEVENT_OUTOFCONTEXT);
                    }
                }
                else
                {
                    if (_locationChangeHook != IntPtr.Zero)
                    {
                        Win32.UnhookWinEvent(_locationChangeHook);
                        _locationChangeHook = IntPtr.Zero;
                    }
                    _currentThreadId = 0;
                }

                CheckOcclusion(foregroundHwnd);
            });
        }

        private void CheckOcclusion(IntPtr foregroundHwnd)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Interlocked.Increment(ref _coalescedComputeCount);

            // 每秒打印一次性能报告
            if ((DateTime.Now - _lastLogTime).TotalSeconds >= 1.0)
            {
                int raw = _rawEventCount;
                int computed = _coalescedComputeCount;
                Log.Information("[PERF] 过去1秒 -> 拦截原始事件: {RawCount} 次 | 实际合并计算: {ComputedCount} 次 | 阻挡了 {BlockedCount} 次冗余计算!", raw, computed, raw - computed);
                _rawEventCount = 0;
                _coalescedComputeCount = 0;
                _lastLogTime = DateTime.Now;
            }

            bool newObscured = false;

            if (foregroundHwnd == IntPtr.Zero || foregroundHwnd == new System.Windows.Interop.WindowInteropHelper(this).Handle) 
            {
                newObscured = false;
            }
            else
            {
                // 过滤掉桌面和任务栏的点击
                bool isDesktop = false;
                unsafe
                {
                    char* buffer = stackalloc char[256];
                    int len = Win32.GetClassName(foregroundHwnd, buffer, 256);
                    if (len > 0)
                    {
                        var span = new ReadOnlySpan<char>(buffer, len);
                        if (span is "Progman" or "WorkerW" or "Shell_TrayWnd")
                        {
                            isDesktop = true;
                        }
                    }
                }

                if (isDesktop)
                {
                    newObscured = false;
                }
                else
                {
                    int wpSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>();
                    Win32.WINDOWPLACEMENT wp = new Win32.WINDOWPLACEMENT { length = wpSize };
                    if (Win32.GetWindowPlacement(foregroundHwnd, ref wp))
                    {
                        if (wp.showCmd == Win32.SW_SHOWMAXIMIZED)
                        {
                            newObscured = true;
                        }
                        else if (wp.showCmd == Win32.SW_MINIMIZE || wp.showCmd == Win32.SW_SHOWMINNOACTIVE)
                        {
                            newObscured = false; // 最小化的窗口不可能遮挡
                        }
                        else
                        {
                            Win32.RECT rect = new Win32.RECT();
                            int hr = Win32.DwmGetWindowAttribute(foregroundHwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>());
                            if (hr != 0)
                            {
                                Win32.GetWindowRect(foregroundHwnd, out rect);
                            }
                            
                            bool intersectX = rect.Left < _cachedCriticalRight && rect.Right > _cachedMyLeft;
                            bool intersectY = rect.Top < _cachedMyBottom && rect.Bottom > _cachedMyTop;
                            
                            newObscured = intersectX && intersectY;
                        }
                    }
                }
            }

            if (_isObscured != newObscured)
            {
                _isObscured = newObscured;
                if (_isObscured)
                {
                    _isHovered = false; // 强制清除悬停状态，让长廊立刻躲避
                }
                UpdateVisibilityState();
                Log.Information("[STATE] 长廊遮挡状态变更 -> {IsObscured}", _isObscured);
            }

            sw.Stop();
            if (sw.ElapsedTicks > 10000) // 只打印耗时较长的计算，10000 ticks = 1ms
            {
                Log.Warning("[PERF] CheckOcclusion 耗时异常: {ElapsedMs} ms", sw.Elapsed.TotalMilliseconds);
            }
        }

        private void UpdateVisibilityState()
        {
            double targetLeft = (_isHovered || !_isObscured || _isDragging) ? _visibleLeft : _hiddenLeft;
            
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = targetLeft,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            this.BeginAnimation(Window.LeftProperty, anim);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            System.Windows.Media.CompositionTarget.Rendering -= CompositionTarget_Rendering;
            if (_locationChangeHook != IntPtr.Zero)
            {
                Win32.UnhookWinEvent(_locationChangeHook);
                _locationChangeHook = IntPtr.Zero;
            }
            _windowManager.Shutdown();
        }

        private Point _dragStartPoint;
        private bool _isDragging;

        private void Thumbnail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(this);
            _isDragging = false;
        }

        private void Thumbnail_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement element)
            {
                var pos = e.GetPosition(this);
                Vector diff = pos - _dragStartPoint;
                if (!_isDragging && (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
                {
                    _isDragging = true;
                    element.CaptureMouse();
                }

                if (_isDragging)
                {
                    element.RenderTransform = new System.Windows.Media.TranslateTransform(diff.X, diff.Y);
                }
            }
        }

        private void Thumbnail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is StageGroup group)
            {
                element.ReleaseMouseCapture();

                if (_isDragging)
                {
                    _isDragging = false;
                    element.RenderTransform = null; // Snap back instantly

                    Point pos = e.GetPosition(this);
                    if (pos.X > this.ActualWidth || pos.X < 0 || pos.Y > this.ActualHeight || pos.Y < 0)
                    {
                        Log.Information($"[UI Click] Drag end on group {group.Id}, calling GroupWithStage");
                        _windowManager.GroupWithStage(group);
                    }
                    UpdateVisibilityState(); // 拖拽结束后重新评估可见性
                }
                else
                {
                    element.RenderTransform = null;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        Log.Information($"[UI Click] Shift-Click on group {group.Id}, calling GroupWithStage");
                        _windowManager.GroupWithStage(group);
                    }
                    else
                    {
                        Log.Information($"[UI Click] Click on group {group.Id}, calling SwitchToStage");
                        _windowManager.SwitchToStage(group);
                    }
                }
            }
        }

        private void Icon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // 阻止事件冒泡到 Thumbnail_MouseLeftButtonUp
            Log.Information($"[UI Click] Clicked icon.");
            if (sender is FrameworkElement element && element.DataContext is WindowInfo winInfo)
            {
                Log.Information($"[UI Click] Icon corresponds to Hwnd: {winInfo.Hwnd}");
                var group = _windowManager.BackgroundGroups.FirstOrDefault(g => g.Windows.Contains(winInfo))
                            ?? _windowManager.BackgroundGroups.FirstOrDefault(g => g.Windows.Any(w => w.Hwnd == winInfo.Hwnd));

                if (group != null)
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        Log.Information($"[UI Click] Shift-Click icon. Extracting window (BreakOut: true).");
                        _windowManager.ExtractWindowFromGroup(group, winInfo, breakOut: true);
                    }
                    else
                    {
                        Log.Information($"[UI Click] Click icon. Extracting window (BreakOut: false) to keep folder link.");
                        _windowManager.ExtractWindowFromGroup(group, winInfo, breakOut: false);
                    }
                }
            }
        }
    }
}
