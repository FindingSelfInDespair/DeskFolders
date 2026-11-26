using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using DeskFolders.Models;
using DeskFolders.Native;
using DeskFolders.Services;

namespace DeskFolders.Views;

public partial class SnapOverlayWindow : Window
{
    private double _lastX = double.NaN;
    private double _lastY = double.NaN;
    private TileSizePreset? _lastPreset;
    private bool _shownOnce;
    private bool _isActiveVisible;

    public SnapOverlayWindow()
    {
        InitializeComponent();
        Opacity = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    public void ShowAt(double x, double y, TileSizePreset preset, DesktopLayerService desktopLayer, bool showLabels, IntPtr draggedTileHwnd)
    {
        if (_isActiveVisible &&
            _lastPreset == preset &&
            Math.Abs(_lastX - x) < 0.5 &&
            Math.Abs(_lastY - y) < 0.5)
        {
            return;
        }

        double prevX = _lastX;
        double prevY = _lastY;
        _lastX = x;
        _lastY = y;
        _lastPreset = preset;

        var (winW, winH, boxSize, cornerRadius, _) = GridSnapService.GetMetrics(preset, showLabels);
        Width = winW;
        Height = winH;
        SlotBorder.Width = boxSize;
        SlotBorder.Height = boxSize;
        SlotBorder.CornerRadius = new CornerRadius(cornerRadius);

        if (!_shownOnce)
        {
            _shownOnce = true;
            _isActiveVisible = true;
            Left = x;
            Top = y;
            Opacity = 0;
            Show();

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && draggedTileHwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    hwnd, draggedTileHwnd, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
            return;
        }

        if (!_isActiveVisible)
        {
            _isActiveVisible = true;
            Left = x;
            Top = y;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && draggedTileHwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    hwnd, draggedTileHwnd, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
            return;
        }

        if (!double.IsNaN(prevX) && !double.IsNaN(prevY))
        {
            Left = x;
            Top = y;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(95);
            BeginAnimation(LeftProperty, new DoubleAnimation(prevX, x, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            BeginAnimation(TopProperty, new DoubleAnimation(prevY, y, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        }
    }

    public new void Hide()
    {
        if (!_isActiveVisible)
            return;

        _isActiveVisible = false;
        _lastX = double.NaN;
        _lastY = double.NaN;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(90)));
    }
}