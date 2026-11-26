using System;
using Microsoft.Win32;

namespace DeskFolders.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "DeskFolders";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AppValueName) is string val && !string.IsNullOrWhiteSpace(val);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key == null)
            return;

        if (enabled)
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
                key.SetValue(AppValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppValueName, throwOnMissingValue: false);
        }
    }
}