using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskFolders.Services;

public static class ShortcutCreator
{
    public static void HandleDroppedFile(string sourcePath, string targetFolder, bool forceCopy = false)
    {
        if (!File.Exists(sourcePath) || !Directory.Exists(targetFolder))
            return;

        string? sourceDir = Path.GetDirectoryName(sourcePath);
        if (string.Equals(sourceDir?.TrimEnd('\\'), targetFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return;

        string ext = Path.GetExtension(sourcePath);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            string lnkPath = GetUniqueFilePath(targetFolder, baseName + ".lnk");
            CreateShortcut(sourcePath, lnkPath);
            return;
        }

        bool isShortcut = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                       || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);

        bool isFromDesktop = IsOnDesktop(sourceDir);

        if (!forceCopy && (isShortcut || isFromDesktop))
        {
            try
            {
                MoveEntryToFolder(sourcePath, targetFolder);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // Например, ярлык с Общего рабочего стола — копируем как фолбэк
            }
            catch (IOException)
            {
                // Если файл занят — копируем
            }
        }

        string destPath = GetUniqueFilePath(targetFolder, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destPath, overwrite: false);
    }

    public static void MoveEntryToFolder(string sourcePath, string targetFolder)
    {
        if (!Directory.Exists(targetFolder))
            return;

        string? sourceDir = Path.GetDirectoryName(sourcePath);
        if (string.Equals(sourceDir?.TrimEnd('\\'), targetFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return;

        if (File.Exists(sourcePath))
        {
            string destPath = GetUniqueFilePath(targetFolder, Path.GetFileName(sourcePath));
            File.Move(sourcePath, destPath);
        }
        else if (Directory.Exists(sourcePath))
        {
            string destPath = GetUniqueDirPath(targetFolder, Path.GetFileName(sourcePath));
            Directory.Move(sourcePath, destPath);
        }
    }

    private static bool IsOnDesktop(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath))
            return false;

        string normalized = dirPath.TrimEnd('\\');
        string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory).TrimEnd('\\');
        string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory).TrimEnd('\\');

        return string.Equals(normalized, userDesktop, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, commonDesktop, StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateShortcut(string targetExePath, string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return;

        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return;

            dynamic dynShell = shell;
            shortcut = dynShell.CreateShortcut(shortcutPath);
            if (shortcut == null)
                return;

            dynamic dynShortcut = shortcut;
            dynShortcut.TargetPath = targetExePath;
            dynShortcut.WorkingDirectory = Path.GetDirectoryName(targetExePath) ?? string.Empty;
            dynShortcut.Save();
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);

            if (shell != null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string GetUniqueFilePath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        string name = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int counter = 2;

        while (true)
        {
            candidate = Path.Combine(folder, $"{name} ({counter}){ext}");
            if (!File.Exists(candidate))
                return candidate;
            counter++;
        }
    }

    private static string GetUniqueDirPath(string folder, string dirName)
    {
        string candidate = Path.Combine(folder, dirName);
        if (!Directory.Exists(candidate))
            return candidate;

        int counter = 2;
        while (true)
        {
            candidate = Path.Combine(folder, $"{dirName} ({counter})");
            if (!Directory.Exists(candidate))
                return candidate;
            counter++;
        }
    }
}