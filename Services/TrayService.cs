using System;
using System.Windows;
using DeskFolders.Models;
using DeskFolders.Views;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DeskFolders.Services;

public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon _trayIcon;
    private AppConfig _config;
    private bool _autostartEnabled;
    private TrayMenuWindow? _activeMenu;

    public event Action? OpenSettingsRequested;
    public event Action? AddFolderRequested;
    public event Action? OrganizeDesktopRequested;
    public event Action? ToggleLaunchpadRequested;
    public event Action? ToggleVisibilityRequested;
    public event Action? ToggleAutostartRequested;
    public event Action? ToggleSnapRequested;
    public event Action? ToggleLockRequested;
    public event Action? AlignAllNowRequested;
    public event Action? ExitRequested;

    public TrayService(AppConfig config, bool initialAutostart)
    {
        _config = config;
        _autostartEnabled = initialAutostart;
        _trayIcon = LoadAppIcon();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = $"DeskFolders ({DesktopLayerService.GetHotkeyLabel(config.LaunchpadHotkey)})",
            Visible = true
        };

        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right)
                ShowDarkMenu();
        };

        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                OpenSettingsRequested?.Invoke();
        };
    }

    private void ShowDarkMenu()
    {
        _activeMenu?.Close();

        var menu = new TrayMenuWindow(_config, _autostartEnabled);
        menu.OpenSettingsClicked += () => OpenSettingsRequested?.Invoke();
        menu.AddFolderClicked += () => AddFolderRequested?.Invoke();
        menu.OrganizeClicked += () => OrganizeDesktopRequested?.Invoke();
        menu.LaunchpadClicked += () => ToggleLaunchpadRequested?.Invoke();
        menu.ToggleVisibilityClicked += () => ToggleVisibilityRequested?.Invoke();
        menu.AlignGridClicked += () => AlignAllNowRequested?.Invoke();
        menu.ToggleSnapClicked += () => ToggleSnapRequested?.Invoke();
        menu.ToggleLockClicked += () => ToggleLockRequested?.Invoke();
        menu.ToggleAutostartClicked += () => ToggleAutostartRequested?.Invoke();
        menu.ExitClicked += () => ExitRequested?.Invoke();
        menu.Closed += (_, _) =>
        {
            if (_activeMenu == menu)
                _activeMenu = null;
        };

        _activeMenu = menu;
        menu.ShowAtCursor();
    }

    public void RecreateIcon()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Visible = true;
    }

    public void SyncFromConfig(AppConfig config)
    {
        _config = config;
        _notifyIcon.Text = $"DeskFolders ({DesktopLayerService.GetHotkeyLabel(config.LaunchpadHotkey)})";
    }

    public void UpdateAutostartState(bool enabled)
    {
        _autostartEnabled = enabled;
    }

    public void ShowBalloon(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(3000, title, message, WinForms.ToolTipIcon.Info);
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info != null)
            {
                using var stream = info.Stream;
                return new Drawing.Icon(stream, 32, 32);
            }
        }
        catch
        {
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        _activeMenu?.Close();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }
}