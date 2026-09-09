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
        public MainWindow()
        {
            InitializeComponent();
            _windowManager = new WindowManager();
            _windowManager.OnForegroundWindowChangedEvent += WindowManager_OnForegroundWindowChangedEvent;
            this.DataContext = _windowManager;
            
            this.MouseEnter += MainWindow_MouseEnter;
            this.MouseLeave += MainWindow_MouseLeave;
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

        private void WindowManager_OnForegroundWindowChangedEvent(IntPtr foregroundHwnd)
        {
            this.Dispatcher.InvokeAsync(() =>
            {
                if (foregroundHwnd == IntPtr.Zero || foregroundHwnd == new System.Windows.Interop.WindowInteropHelper(this).Handle) 
                {
                    _isObscured = false;
                }
                else
                {
                    // 过滤掉桌面和任务栏的点击，不要因为点击桌面而隐藏长廊
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
                        _isObscured = false;
                    }
                    else
                    {
                        int wpSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>();
                        Win32.WINDOWPLACEMENT wp = new Win32.WINDOWPLACEMENT { length = wpSize };
                        if (Win32.GetWindowPlacement(foregroundHwnd, ref wp))
                        {
                            if (wp.showCmd == Win32.SW_SHOWMAXIMIZED)
                            {
                                _isObscured = true;
                            }
                            else
                            {
                                Win32.RECT rect = new Win32.RECT();
                                int hr = Win32.DwmGetWindowAttribute(foregroundHwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>());
                                if (hr != 0)
                                {
                                    Win32.GetWindowRect(foregroundHwnd, out rect);
                                }
                                // 换算 WPF 的 DIP 单位到物理像素 (适配高分屏)
                                var src = PresentationSource.FromVisual(this);
                                double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                                double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                                int myLeft = (int)(_visibleLeft * dpiX);
                                
                                // 定义长廊的“核心敏感区”宽度为 130 逻辑像素 (包含图标和左半边缩略图)
                                // 允许前台窗口与长廊的右侧有大约 90 像素的交叠而不触发隐藏，实现最平衡的体验
                                int criticalWidth = 130;
                                int criticalRight = (int)((_visibleLeft + criticalWidth) * dpiX);
                                
                                int myTop = (int)(this.Top * dpiY);
                                int myBottom = (int)((this.Top + this.Height) * dpiY);

                                // 只有当窗口侵入了核心敏感区，才算作遮挡
                                bool intersectX = rect.Left < criticalRight && rect.Right > myLeft;
                                bool intersectY = rect.Top < myBottom && rect.Bottom > myTop;
                                
                                _isObscured = intersectX && intersectY;
                            }
                        }
                        else
                        {
                            _isObscured = false;
                        }
                    }
                }

                // 核心修复：如果新窗口遮挡了我们，必须强制清除悬停状态，让长廊立刻躲避
                // （否则因为鼠标刚点击完缩略图还停留在长廊范围内，导致长廊一直不躲避）
                if (_isObscured)
                {
                    _isHovered = false;
                }

                UpdateVisibilityState();
            });
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
                        Log.Information($"[UI Click] Shift-Click icon. Extracting window.");
                        _windowManager.ExtractWindowFromGroup(group, winInfo);
                    }
                    else
                    {
                        Log.Information($"[UI Click] Click icon. Switching to stage and prioritizing window.");
                        group.PrimaryHwnd = winInfo.Hwnd;
                        group.Icon = winInfo.Icon;
                        _windowManager.SwitchToStage(group);
                    }
                }
            }
        }
    }
}
