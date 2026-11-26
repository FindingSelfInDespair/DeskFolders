using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeskFolders.Models;
using DeskFolders.Native;

namespace DeskFolders.Services;

public sealed class FolderContentService
{
    private static readonly HashSet<string> TrimExtensionSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".exe"
    };

    private static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db"
    };

    private readonly ShellIconExtractor _iconExtractor;

    public FolderContentService(ShellIconExtractor iconExtractor)
    {
        _iconExtractor = iconExtractor;
    }

    public async Task<List<FolderItemInfo>> LoadItemsAsync(
        string folderPath,
        int iconSize,
        int? maxCount = null,
        IReadOnlyCollection<string>? hiddenItems = null,
        FolderSortMode sortMode = FolderSortMode.Auto,
        IReadOnlyList<string>? customOrder = null)
    {
        if (!Directory.Exists(folderPath))
            return new List<FolderItemInfo>();

        var hiddenSet = hiddenItems != null && hiddenItems.Count > 0
            ? new HashSet<string>(hiddenItems, StringComparer.OrdinalIgnoreCase)
            : null;

        var rawItems = await Task.Run(() => EnumerateEntries(folderPath, maxCount, hiddenSet, sortMode, customOrder));

        var iconTasks = rawItems.Select(async item =>
        {
            item.Icon = await _iconExtractor.GetIconAsync(item.FullPath, iconSize);
            return item;
        });

        var result = await Task.WhenAll(iconTasks);
        return result.ToList();
    }

    private static List<FolderItemInfo> EnumerateEntries(
        string folderPath,
        int? maxCount,
        HashSet<string>? hiddenSet,
        FolderSortMode sortMode,
        IReadOnlyList<string>? customOrder)
    {
        var items = new List<FolderItemInfo>();

        try
        {
            var dirInfo = new DirectoryInfo(folderPath);
            var filtered = dirInfo.EnumerateFileSystemInfos()
                .Where(entry =>
                {
                    if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        return false;

                    if (entry is FileInfo && IgnoredFiles.Contains(entry.Name))
                        return false;

                    if (hiddenSet != null && (hiddenSet.Contains(entry.Name) || hiddenSet.Contains(entry.FullName)))
                        return false;

                    return true;
                });

            IEnumerable<FileSystemInfo> ordered = sortMode switch
            {
                FolderSortMode.NameAsc => filtered
                    .OrderBy(e => GetDisplayName(e), StringComparer.CurrentCultureIgnoreCase),

                FolderSortMode.DateNewest => filtered
                    .OrderByDescending(e => e.LastWriteTimeUtc)
                    .ThenBy(e => GetDisplayName(e), StringComparer.CurrentCultureIgnoreCase),

                FolderSortMode.Custom when customOrder != null && customOrder.Count > 0 =>
                    OrderByCustomList(filtered, customOrder),

                _ => filtered
                    .OrderBy(e => e is DirectoryInfo ? 0 : 1)
                    .ThenBy(e => GetPriority(e.Extension))
                    .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            };

            foreach (var entry in ordered)
            {
                items.Add(new FolderItemInfo
                {
                    Name = GetDisplayName(entry),
                    FullPath = entry.FullName,
                    IsDirectory = entry is DirectoryInfo
                });

                if (maxCount.HasValue && items.Count >= maxCount.Value)
                    break;
            }
        }
        catch
        {
            // Папка временно недоступна
        }

        return items;
    }

    private static IEnumerable<FileSystemInfo> OrderByCustomList(
        IEnumerable<FileSystemInfo> entries,
        IReadOnlyList<string> customOrder)
    {
        var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < customOrder.Count; i++)
        {
            if (!orderMap.ContainsKey(customOrder[i]))
                orderMap[customOrder[i]] = i;
        }

        return entries
            .OrderBy(e => orderMap.TryGetValue(e.Name, out int idx) ? idx : int.MaxValue)
            .ThenBy(e => e is DirectoryInfo ? 0 : 1)
            .ThenBy(e => GetPriority(e.Extension))
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static string GetDisplayName(FileSystemInfo entry)
    {
        if (entry is not DirectoryInfo && TrimExtensionSet.Contains(entry.Extension))
            return Path.GetFileNameWithoutExtension(entry.Name);

        return entry.Name;
    }

    private static int GetPriority(string ext) => ext.ToLowerInvariant() switch
    {
        ".lnk" => 0,
        ".exe" => 1,
        ".url" => 2,
        _ => 3
    };
}