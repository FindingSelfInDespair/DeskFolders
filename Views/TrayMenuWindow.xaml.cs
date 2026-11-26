using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DeskFolders.Models;
using DeskFolders.Native;
using DeskFolders.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DeskFolders.Views;

public partial class TrayMenuWindow : Window
{
    private bool _isClosing;

    public event Action? OpenSettingsClicked;
    public event Action? AddFolderClicked;
    public event Action? OrganizeClicked;
    public event Action? LaunchpadClicked;
    public event Action? ToggleVisibilityClicked;
    public event Action? AlignGridClicked;
    public event Action? ToggleSnapClicked;
    public event Action? ToggleLockClicked;
    public event Action? ToggleAutostartClicked;
    public event Action? ExitClicked;

    public TrayMenuWindow(AppConfig config, bool autostartEnabled)
    {
        InitializeComponent();

        HotkeyHintText.Text = DesktopLayerService.GetHotkeyLabel(config.LaunchpadHotkey);
        VisibilityLabel.Text = config.TilesVisible ? "👁  Скрыть плитки" : "👁  Показать плитки";
        SnapCheck.Visibility = config.SnapToGrid ? Visibility.Visible : Visibility.Collapsed;
        LockCheck.Visibility = config.LockPositions ? Visibility.Visible : Visibility.Collapsed;
        AutostartCheck.Visibility = autostartEnabled ? Visibility.Visible : Visibility.Collapsed;

        Opacity = 0;

        Deactivated += (_, _) => CloseSafely();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                CloseSafely();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    public void ShowAtCursor()
    {
        NativeMethods.GetCursorPos(out var cursorPx);
        Show();
        UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(this);
        var workPx = WinForms.Screen.FromPoint(new Drawing.Point(cursorPx.X, cursorPx.Y)).WorkingArea;

        double curX = cursorPx.X / dpi.DpiScaleX;
        double curY = cursorPx.Y / dpi.DpiScaleY;
        double workLeft = workPx.Left / dpi.DpiScaleX;
        double workTop = workPx.Top / dpi.DpiScaleY;
        double workRight = workPx.Right / dpi.DpiScaleX;
        double workBottom = workPx.Bottom / dpi.DpiScaleY;

        double targetLeft = Math.Clamp(curX - ActualWidth + 24, workLeft + 4, Math.Max(workLeft + 4, workRight - ActualWidth - 4));
        double targetTop = Math.Clamp(curY - ActualHeight + 10, workTop + 4, Math.Max(workTop + 4, workBottom - ActualHeight - 4));

        Left = targetLeft;
        Top = targetTop;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(hwnd);
        Activate();

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
    }

    private void CloseSafely()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        Close();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) { CloseSafely(); OpenSettingsClicked?.Invoke(); }
    private void OnOrganizeClick(object sender, RoutedEventArgs e) { CloseSafely(); OrganizeClicked?.Invoke(); }
    private void OnAddFolderClick(object sender, RoutedEventArgs e) { CloseSafely(); AddFolderClicked?.Invoke(); }
    private void OnLaunchpadClick(object sender, RoutedEventArgs e) { CloseSafely(); LaunchpadClicked?.Invoke(); }
    private void OnToggleVisibilityClick(object sender, RoutedEventArgs e) { CloseSafely(); ToggleVisibilityClicked?.Invoke(); }
    private void OnAlignGridClick(object sender, RoutedEventArgs e) { CloseSafely(); AlignGridClicked?.Invoke(); }
    private void OnToggleSnapClick(object sender, RoutedEventArgs e) { CloseSafely(); ToggleSnapClicked?.Invoke(); }
    private void OnToggleLockClick(object sender, RoutedEventArgs e) { CloseSafely(); ToggleLockClicked?.Invoke(); }
    private void OnToggleAutostartClick(object sender, RoutedEventArgs e) { CloseSafely(); ToggleAutostartClicked?.Invoke(); }
    private void OnExitClick(object sender, RoutedEventArgs e) { CloseSafely(); ExitClicked?.Invoke(); }
}