using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;

namespace StageManagerApp
{
    public partial class DwmThumbnailControl : UserControl
    {
        private IntPtr _dwmThumbnail = IntPtr.Zero;
        private Window? _window;

        public static readonly DependencyProperty TargetHwndProperty = DependencyProperty.Register(
            nameof(TargetHwnd),
            typeof(IntPtr),
            typeof(DwmThumbnailControl),
            new PropertyMetadata(IntPtr.Zero, OnTargetHwndChanged));

        public IntPtr TargetHwnd
        {
            get => (IntPtr)GetValue(TargetHwndProperty);
            set => SetValue(TargetHwndProperty, value);
        }

        public DwmThumbnailControl()
        {
            InitializeComponent();
            this.LayoutUpdated += DwmThumbnailControl_LayoutUpdated;
            this.Loaded += DwmThumbnailControl_Loaded;
            this.Unloaded += DwmThumbnailControl_Unloaded;
        }

        private static void OnTargetHwndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DwmThumbnailControl control)
            {
                control.RegisterThumbnail();
            }
        }

        private void DwmThumbnailControl_Loaded(object sender, RoutedEventArgs e)
        {
            _window = Window.GetWindow(this);
            RegisterThumbnail();
        }

        private void DwmThumbnailControl_Unloaded(object sender, RoutedEventArgs e)
        {
            UnregisterThumbnail();
        }

        private void DwmThumbnailControl_LayoutUpdated(object? sender, EventArgs e)
        {
            UpdateThumbnail();
        }

        private Win32.RECT _lastRect;

        private void RegisterThumbnail()
        {
            UnregisterThumbnail();
            _lastRect = default;

            if (TargetHwnd == IntPtr.Zero || _window == null) return;

            var windowInterop = new WindowInteropHelper(_window);
            if (windowInterop.Handle == IntPtr.Zero) return;

            try
            {
                var hr = Win32.DwmRegisterThumbnail(windowInterop.Handle, TargetHwnd, out _dwmThumbnail);
                if (hr == 0)
                {
                    UpdateThumbnail();
                }
                else
                {
                    Log.Warning($"DwmRegisterThumbnail failed with code {hr} for Hwnd {TargetHwnd}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to register DWM thumbnail.");
            }
        }

        private void UnregisterThumbnail()
        {
            if (_dwmThumbnail != IntPtr.Zero)
            {
                try
                {
                    Win32.DwmUnregisterThumbnail(_dwmThumbnail);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to unregister DWM thumbnail.");
                }
                finally
                {
                    _dwmThumbnail = IntPtr.Zero;
                }
            }
        }

        private void UpdateThumbnail()
        {
            if (_dwmThumbnail == IntPtr.Zero || _window == null) return;
            if (!this.IsVisible) return;

            try
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;

                var dpiX = source.CompositionTarget.TransformToDevice.M11;
                var dpiY = source.CompositionTarget.TransformToDevice.M22;

                var transform = this.TransformToVisual(_window);
                var bounds = transform.TransformBounds(new Rect(0, 0, this.ActualWidth, this.ActualHeight));

                Win32.RECT destRect = new Win32.RECT
                {
                    Left = (int)(bounds.Left * dpiX),
                    Top = (int)(bounds.Top * dpiY),
                    Right = (int)(bounds.Right * dpiX),
                    Bottom = (int)(bounds.Bottom * dpiY)
                };

                // 如果坐标和大小毫无变化，立刻拦截，跳过所有 DWM 高级通信开销！
                if (destRect.Left == _lastRect.Left && destRect.Top == _lastRect.Top && destRect.Right == _lastRect.Right && destRect.Bottom == _lastRect.Bottom)
                {
                    return;
                }

                // Preserve aspect ratio by resizing the WPF control itself (so parent Border shrinks to fit)
                if (Win32.DwmQueryThumbnailSourceSize(_dwmThumbnail, out Win32.PSIZE srcSize) == 0 && srcSize.x > 0 && srcSize.y > 0)
                {
                    double targetRatio = (double)srcSize.x / srcSize.y;
                    double maxWidth = 124;
                    double maxHeight = 64;
                    double containerRatio = maxWidth / maxHeight;

                    double newWidth, newHeight;
                    if (targetRatio > containerRatio)
                    {
                        newWidth = maxWidth;
                        newHeight = maxWidth / targetRatio;
                    }
                    else
                    {
                        newHeight = maxHeight;
                        newWidth = maxHeight * targetRatio;
                    }

                    if (double.IsNaN(this.Width) || Math.Abs(this.Width - newWidth) > 1 || Math.Abs(this.Height - newHeight) > 1)
                    {
                        this.Width = newWidth;
                        this.Height = newHeight;
                        return; // Let the layout system run a new pass with the new size
                    }
                }

                _lastRect = destRect;

                var props = new Win32.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = Win32.DWM_TNP_VISIBLE | Win32.DWM_TNP_OPACITY | Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
                    opacity = 255,
                    fVisible = 1,
                    rcDestination = destRect,
                    fSourceClientAreaOnly = 1 // Use client area only to avoid glitchy minimized window frames
                };

                Win32.DwmUpdateThumbnailProperties(_dwmThumbnail, ref props);
            }
            catch (Exception ex)
            {
                // UI updates can sometimes cause transient errors if visuals disconnect
                Log.Debug(ex, "Failed to update DWM thumbnail properties.");
            }
        }
    }
}
