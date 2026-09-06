using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace StageManagerApp
{
    public partial class MainWindow : Window
    {
        private WindowManager _windowManager;

        public MainWindow()
        {
            InitializeComponent();
            _windowManager = new WindowManager();
            this.DataContext = _windowManager;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 閫傚簲宸ヤ綔鍖哄煙锛堥伩寮€浠诲姟鏍忥級锛屼繚璇佸眳涓拰杈硅窛鐨勫畬缇庢瘮渚?
            var workArea = SystemParameters.WorkArea;
            this.Left = workArea.Left;
            this.Top = workArea.Top;
            this.Height = workArea.Height;

            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            int exStyle = (int)Win32.GetWindowLongPtr(helper.Handle, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(helper.Handle, Win32.GWL_EXSTYLE, (IntPtr)(exStyle | Win32.WS_EX_NOACTIVATE));

            _windowManager.Initialize();
            // Directly bind the ObservableCollection. WPF handles all updates and animations without flickering!
            ThumbnailsList.ItemsSource = _windowManager.BackgroundGroups;
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
                        _windowManager.GroupWithStage(group);
                    }
                }
                else
                {
                    element.RenderTransform = null;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        _windowManager.GroupWithStage(group);
                    }
                    else
                    {
                        _windowManager.SwitchToStage(group);
                    }
                }
            }
        }
    }
}
