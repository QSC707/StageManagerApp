using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Serilog;

namespace StageManagerApp
{
    public partial class MainWindow : Window
    {
        private WindowManager _windowManager;

        private bool _isHovered = false;
        private double _visibleLeft;
        private double _hiddenLeft;

        public MainWindow()
        {
            InitializeComponent();
            _windowManager = new WindowManager();
            this.DataContext = _windowManager;
            
            this.MouseEnter += MainWindow_MouseEnter;
            this.MouseLeave += MainWindow_MouseLeave;

            _windowManager.OnForegroundWindowChangedEvent += WindowManager_OnForegroundWindowChangedEvent;
        }

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

            _locChangeDelegate = new Win32.WinEventDelegate(WinEventCallback);
            _occlusionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _occlusionTimer.Tick += (s, args) => {
                _occlusionTimer.Stop();
                CheckOcclusion();
            };
            
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero) WindowManager_OnForegroundWindowChangedEvent(fg);

            UpdateVisibilityState();
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
        private Win32.WinEventDelegate? _locChangeDelegate;
        private IntPtr _winEventHook;
        private System.Windows.Threading.DispatcherTimer _occlusionTimer;
        private bool _isOccluded;

        private void WindowManager_OnForegroundWindowChangedEvent(IntPtr foregroundHwnd)
        {
            this.Dispatcher.InvokeAsync(() =>
            {
                _currentForegroundHwnd = foregroundHwnd;
                UpdateForegroundHook();
                CheckOcclusion();
            });
        }

        private void UpdateForegroundHook()
        {
            if (_winEventHook != IntPtr.Zero)
            {
                Win32.UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }

            if (_currentForegroundHwnd != IntPtr.Zero)
            {
                uint threadId = Win32.GetWindowThreadProcessId(_currentForegroundHwnd, out uint processId);
                if (threadId != 0)
                {
                    _winEventHook = Win32.SetWinEventHook(
                        Win32.EVENT_OBJECT_LOCATIONCHANGE, 
                        Win32.EVENT_OBJECT_LOCATIONCHANGE, 
                        IntPtr.Zero, 
                        _locChangeDelegate!, 
                        processId, 
                        threadId, 
                        Win32.WINEVENT_OUTOFCONTEXT);
                }
            }
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != 0 /* OBJID_WINDOW */) return;
            _occlusionTimer.Stop();
            _occlusionTimer.Start();
        }

        private void CheckOcclusion()
        {
            if (_currentForegroundHwnd == IntPtr.Zero)
            {
                SetOccluded(false);
                return;
            }

            Span<char> buffer = stackalloc char[256];
            int len = Win32.GetClassName(_currentForegroundHwnd, buffer, 256);
            if (len > 0)
            {
                ReadOnlySpan<char> span = buffer.Slice(0, len);
                if (span is "Progman" or "WorkerW" or "Shell_TrayWnd")
                {
                    SetOccluded(false);
                    return;
                }
            }

            // 获取窗口状态，判断是否最大化或全屏
            Win32.WINDOWPLACEMENT placement = new Win32.WINDOWPLACEMENT();
            placement.length = Marshal.SizeOf(typeof(Win32.WINDOWPLACEMENT));
            if (Win32.GetWindowPlacement(_currentForegroundHwnd, ref placement))
            {
                bool isMaximized = placement.showCmd == Win32.SW_SHOWMAXIMIZED;
                
                // 补充判断：部分无边框窗口全屏时（如视频全屏、游戏全屏），并非标准的最大化状态，
                // 此时其窗口大小等于屏幕/工作区大小。
                if (!isMaximized && Win32.GetWindowRect(_currentForegroundHwnd, out Win32.RECT rect))
                {
                    var workArea = SystemParameters.WorkArea;
                    if (rect.Left <= 0 && rect.Top <= 0 && 
                        (rect.Right - rect.Left) >= workArea.Width && 
                        (rect.Bottom - rect.Top) >= workArea.Height)
                    {
                        isMaximized = true; // 视为全屏遮挡
                    }
                }

                SetOccluded(isMaximized);
            }
        }

        private void SetOccluded(bool occluded)
        {
            if (_isOccluded != occluded)
            {
                _isOccluded = occluded;
                UpdateVisibilityState();
            }
        }

        private void UpdateVisibilityState()
        {
            bool isVisible = _isHovered || _isDragging || !_isOccluded;
            double targetLeft = isVisible ? _visibleLeft : _hiddenLeft;
            
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = targetLeft,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = isVisible ? System.Windows.Media.Animation.EasingMode.EaseOut : System.Windows.Media.Animation.EasingMode.EaseIn }
            };
            this.BeginAnimation(Window.LeftProperty, anim);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (_winEventHook != IntPtr.Zero)
            {
                Win32.UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
            _windowManager.Dispose();
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
                        _isHovered = false;
                        UpdateVisibilityState();
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
                    
                    // 点击目标后，立刻强制收起侧边栏
                    _isHovered = false;
                    UpdateVisibilityState();
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
                    
                    // 点击目标后，立刻强制收起侧边栏
                    _isHovered = false;
                    UpdateVisibilityState();
                }
            }
        }
    }
}
