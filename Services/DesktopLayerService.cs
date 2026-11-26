using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskFolders.Models;
using DeskFolders.Native;

namespace DeskFolders.Services;

public sealed class DesktopLayerService : IDisposable
{
    private const int LaunchpadHotKeyId = 0xDF01;

    private readonly List<Window> _windows = new();
    private readonly NativeMethods.WinEventDelegate _winEventDelegate;
    private readonly uint _currentPid = (uint)Environment.ProcessId;
    private readonly uint _taskbarCreatedMsg;
    private readonly HwndSource _messageSink;
    private readonly DispatcherTimer _explorerRetryTimer;
    private IntPtr _foregroundHook;
    private IntPtr _desktopOwnerHwnd;
    private bool _hotkeyRegistered;

    public bool IsLaunchpadActive { get; private set; }

    public event Action? ExplorerRestarted;
    public event Action? DisplayChanged;
    public event Action? LaunchpadHotkeyPressed;
    public event Action? LaunchpadExternalFocusLost;

    public DesktopLayerService()
    {
        _desktopOwnerHwnd = FindDesktopOwnerWindow();
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _winEventDelegate = OnWinEvent;

        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        var sinkParams = new HwndSourceParameters("DeskFoldersMessageSink")
        {
            PositionX = -32000,
            PositionY = -32000,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
        };
        _messageSink = new HwndSource(sinkParams);
        _messageSink.AddHook(MessageSinkProc);

// Хоткей регистрируется из App через UpdateLaunchpadHotkey

        _explorerRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _explorerRetryTimer.Tick += (_, _) =>
        {
            _explorerRetryTimer.Stop();
            _desktopOwnerHwnd = FindDesktopOwnerWindow();
            RefreshAll();
            ExplorerRestarted?.Invoke();
        };
    }

    public static string GetHotkeyLabel(LaunchpadHotkeyPreset preset) => preset switch
    {
        LaunchpadHotkeyPreset.CtrlSpace => "Ctrl + Space",
        LaunchpadHotkeyPreset.CtrlShiftSpace => "Ctrl + Shift + Space",
        LaunchpadHotkeyPreset.AltQ => "Alt + Q",
        _ => "Alt + ~"
    };

    public bool UpdateLaunchpadHotkey(LaunchpadHotkeyPreset preset)
    {
        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_messageSink.Handle, LaunchpadHotKeyId);
            _hotkeyRegistered = false;
        }

        (uint mods, uint vk) = preset switch
        {
            LaunchpadHotkeyPreset.CtrlSpace => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE),
            LaunchpadHotkeyPreset.CtrlShiftSpace => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE),
            LaunchpadHotkeyPreset.AltQ => (NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_Q),
            _ => (NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_OEM_3)
        };

        _hotkeyRegistered = NativeMethods.RegisterHotKey(_messageSink.Handle, LaunchpadHotKeyId, mods, vk);
        return _hotkeyRegistered;
    }

    public void SetLaunchpadMode(bool active)
    {
        IsLaunchpadActive = active;

        if (active)
        {
            RaiseTilesInLaunchpad();
            return;
        }

        foreach (var win in _windows)
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd == IntPtr.Zero)
                continue;

            win.Topmost = false;
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            AttachToDesktopOwner(win);
            EnsureAtDesktopBottom(win);
        }
    }

    public void RaiseTilesInLaunchpad()
    {
        if (!IsLaunchpadActive)
            return;

        foreach (var win in _windows)
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd == IntPtr.Zero)
                continue;

            win.Topmost = true;
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private IntPtr MessageSinkProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == LaunchpadHotKeyId)
        {
            LaunchpadHotkeyPressed?.Invoke();
            handled = true;
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            _desktopOwnerHwnd = FindDesktopOwnerWindow();
            RefreshAll();
            ExplorerRestarted?.Invoke();

            _explorerRetryTimer.Stop();
            _explorerRetryTimer.Start();
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            _desktopOwnerHwnd = FindDesktopOwnerWindow();
            RefreshAll();
            DisplayChanged?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void AttachToDesktopOwner(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (_desktopOwnerHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_desktopOwnerHwnd))
            _desktopOwnerHwnd = FindDesktopOwnerWindow();

        if (_desktopOwnerHwnd != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWLP_HWNDPARENT, _desktopOwnerHwnd);
    }

    public void Register(Window window)
    {
        if (!_windows.Contains(window))
            _windows.Add(window);

        AttachToDesktopOwner(window);
        if (!IsLaunchpadActive)
            EnsureAtDesktopBottom(window);
    }

    public void Unregister(Window window)
    {
        _windows.Remove(window);
    }

    public void RefreshAll()
    {
        if (_desktopOwnerHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_desktopOwnerHwnd))
            _desktopOwnerHwnd = FindDesktopOwnerWindow();

        foreach (var win in _windows)
        {
            AttachToDesktopOwner(win);
            if (!IsLaunchpadActive)
                EnsureAtDesktopBottom(win);
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _currentPid)
                return;

            if (IsLaunchpadActive)
            {
                LaunchpadExternalFocusLost?.Invoke();
                return;
            }
        }

        if (IsLaunchpadActive)
            return;

        if (_desktopOwnerHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_desktopOwnerHwnd))
            _desktopOwnerHwnd = FindDesktopOwnerWindow();

        foreach (var win in _windows)
            EnsureAtDesktopBottom(win);
    }

    private static void EnsureAtDesktopBottom(Window win)
    {
        var hwnd = new WindowInteropHelper(win).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_BOTTOM,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private static IntPtr FindDesktopOwnerWindow()
    {
        IntPtr progman = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null);
        if (progman != IntPtr.Zero)
        {
            IntPtr defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                return progman;
        }

        IntPtr foundWorker = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(32);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "WorkerW")
            {
                IntPtr defView = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    foundWorker = hwnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        return foundWorker != IntPtr.Zero ? foundWorker : progman;
    }

    public void Dispose()
    {
        _explorerRetryTimer.Stop();

        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_messageSink.Handle, LaunchpadHotKeyId);
            _hotkeyRegistered = false;
        }

        _messageSink.RemoveHook(MessageSinkProc);
        _messageSink.Dispose();

        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _windows.Clear();
    }
}