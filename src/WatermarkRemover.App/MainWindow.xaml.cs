using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;

namespace WatermarkRemover.App;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private bool _isMaximizedDragPending;
    private Point _maximizedDragStartPoint;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += Window_OnSourceInitialized;
        PreviewKeyDown += Window_OnPreviewKeyDown;
        UpdateWindowStateGlyph();
        UpdateChromeForState();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ClearPendingMaximizedDrag(sender as UIElement);
            ToggleWindowState();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            _isMaximizedDragPending = true;
            _maximizedDragStartPoint = e.GetPosition(this);
            (sender as UIElement)?.CaptureMouse();
            return;
        }

        TryDragMove();
    }

    private void TitleBar_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMaximizedDragPending)
        {
            return;
        }

        var titleBar = sender as UIElement;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ClearPendingMaximizedDrag(titleBar);
            return;
        }

        var currentPoint = e.GetPosition(this);
        var draggedFarEnough =
            Math.Abs(currentPoint.X - _maximizedDragStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(currentPoint.Y - _maximizedDragStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;

        if (!draggedFarEnough)
        {
            return;
        }

        ClearPendingMaximizedDrag(titleBar);
        RestoreFromMaximizedDrag(e);
    }

    private void TitleBar_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ClearPendingMaximizedDrag(sender as UIElement);
    }

    private void TitleBar_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ClearPendingMaximizedDrag(sender as UIElement);
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
        e.Handled = true;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void Window_OnStateChanged(object sender, EventArgs e)
    {
        UpdateWindowStateGlyph();
        UpdateChromeForState();
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.SystemKey != Key.Space || (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            return;
        }

        SystemCommands.ShowSystemMenu(this, PointToScreen(new Point(12, 12)));
        e.Handled = true;
    }

    private void ToggleWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            return;
        }

        SystemCommands.MaximizeWindow(this);
    }

    private void UpdateWindowStateGlyph()
    {
        if (MaximizeGlyphText is null)
        {
            return;
        }

        MaximizeGlyphText.Text = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void UpdateChromeForState()
    {
        if (RootChromeBorder is null)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            RootChromeBorder.Margin = new Thickness(0);
            RootChromeBorder.CornerRadius = new CornerRadius(0);
            RootChromeBorder.BorderThickness = new Thickness(0);
            return;
        }

        RootChromeBorder.Margin = new Thickness(10);
        RootChromeBorder.CornerRadius = new CornerRadius(26);
        RootChromeBorder.BorderThickness = new Thickness(1);
    }

    private void TryDragMove()
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Ignore transient input races; the next drag gesture will retry naturally.
        }
    }

    private void RestoreFromMaximizedDrag(MouseEventArgs e)
    {
        var restoreBounds = RestoreBounds;
        var restoreWidth = restoreBounds.Width > 0 ? restoreBounds.Width : Width;
        var horizontalRatio = ActualWidth > 0
            ? Math.Clamp(_maximizedDragStartPoint.X / ActualWidth, 0d, 1d)
            : 0.5d;
        var screenPoint = PointToScreen(e.GetPosition(this));

        WindowState = WindowState.Normal;
        Left = screenPoint.X - (restoreWidth * horizontalRatio);
        Top = screenPoint.Y - _maximizedDragStartPoint.Y;

        TryDragMove();
    }

    private void ClearPendingMaximizedDrag(UIElement? titleBar)
    {
        _isMaximizedDragPending = false;
        if (titleBar?.IsMouseCaptured == true)
        {
            titleBar.ReleaseMouseCapture();
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            UpdateMinMaxInfo(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void UpdateMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        minMaxInfo.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth);
        minMaxInfo.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight);

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };

            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var workArea = monitorInfo.rcWork;
                var monitorArea = monitorInfo.rcMonitor;
                minMaxInfo.ptMaxPosition.X = workArea.Left - monitorArea.Left;
                minMaxInfo.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
                minMaxInfo.ptMaxSize.X = workArea.Right - workArea.Left;
                minMaxInfo.ptMaxSize.Y = workArea.Bottom - workArea.Top;
            }
        }

        Marshal.StructureToPtr(minMaxInfo, lParam, false);
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
