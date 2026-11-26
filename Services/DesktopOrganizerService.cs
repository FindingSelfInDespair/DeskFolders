using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskFolders.Models;
using DeskFolders.Native;

namespace DeskFolders.Services;

public sealed class OrganizerTargetOption
{
    public string DisplayName { get; init; } = string.Empty;
    public FolderTileConfig? ExistingTile { get; init; }
    public FolderCategory CategoryToCreate { get; init; }
    public string DefaultFolderName { get; init; } = string.Empty;
    public string DefaultTint { get; init; } = "Dark";
    public override string ToString() => DisplayName;
}

public sealed class DesktopSortPlanItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsFromCommonDesktop { get; init; }
    public string Reason { get; init; } = string.Empty;
    public BitmapSource? Icon { get; set; }
    public OrganizerTargetOption? TargetOption { get; set; }
    public bool IsSelected { get; set; } = true;
}

public sealed class DesktopOrganizerService : IDisposable
{
    private static readonly (FolderCategory Category, string Name, string Tint)[] DefaultFolderSpecs =
    {
        (FolderCategory.Games, "Игры", "Purple"),
        (FolderCategory.Dev, "Код", "Emerald"),
        (FolderCategory.Music, "Музыка", "Rose"),
        (FolderCategory.Media, "Видео и Дизайн", "Amber"),
        (FolderCategory.Software, "Софт", "Blue"),
        (FolderCategory.Documents, "Документы", "Dark"),
        (FolderCategory.Other, "Прочее", "Dark")
    };

    private static readonly string[] MusicPhrases =
    {
        "fl studio", "fl projects", "image-line", "ableton", "analog lab", "soniccat",
        "native instruments", "kontakt", "arturia", "xln audio", "fabfilter", "valhalla",
        "soundtoys", "izotope", "guitar rig", "neural dsp", "bias fx", "amplitube",
        "yandex music", "яндекс музыка", "musicbee", "apple music", "cableguys",
        "shaperbox", "omnisphere", "spectrasonics", "keyscape", "trilian", "u-he",
        "melodyne", "celemony", "antares", "auto-tune", "autotune", "studio one",
        "pro tools", "steinberg", "audio", "synthesizer"
    };

    private static readonly Regex MusicTokens = new(
        @"\b(xo|m1|korg|ampbox|purity|serum|vital|nexus|spire|sylenth|massive|fm8|absynth|diva|zebra|reaper|cubase|bitwig|reason|audacity|spotify|aimp|foobar|voicemod|spotube|splice|arcade|ozone|neutron|nectar|waves|kontakt|daw|vst|vst3|asio)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] MediaPhrases =
    {
        "adobe", "photoshop", "premiere", "after effects", "illustrator", "lightroom",
        "davinci", "blackmagic", "capcut", "sony vegas", "streamlabs", "wallpaper engine",
        "cinema 4d", "paint.net", "mpc-hc", "k-lite"
    };

    private static readonly Regex MediaTokens = new(
        @"\b(obs|blender|aseprite|krita|gimp|zbrush|vegas|vlc|potplayer|handbrake|figma)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] GamePhrases =
    {
        "epic games", "epicgames", "riot games", "riot client", "league of legends",
        "grand theft auto", "rockstar games", "elder scrolls", "electronic arts", "ea app",
        "battle.net", "curseforge", "prism launcher", "legacy launcher", "horizon launcher",
        "nova rp", "rage multiplayer", "majestic", "arizona", "start-game", "steamapps"
    };

    private static readonly Regex GameTokens = new(
        @"\b(steam|valorant|minecraft|roblox|bloxstrap|hydra|gta|uplay|ubisoft|origin|ea|blizzard|gog|xbox|modrinth|tlauncher|faceit|cs2|dota|fortnite|genshin|honkai|overwatch|warframe|apex|cyberpunk|witcher|valve|mojang|launcher)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] DevPhrases =
    {
        "visual studio", "vs code", "github desktop", "git bash", "git gui", "android studio",
        "mysql workbench", "oracle vm", "virtualbox", "notepad++", "open code"
    };

    private static readonly Regex DevTokens = new(
        @"\b(vscode|jetbrains|intellij|pycharm|rider|webstorm|clion|datagrip|github|docker|postman|insomnia|dbeaver|mysql|postgres|mongodb|redis|python|nodejs|anaconda|unity|unreal|godot|mcreator|blockbench|xampp|opencode|sublime|cursor|windsurf|terminal|powershell|wsl|putty|winscp|filezilla|vmware)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SoftPhrases =
    {
        "google chrome", "microsoft edge", "custom cursor", "winaero", "msi afterburner",
        "samsung magician", "zet gaming", "7-zip"
    };

    private static readonly Regex SoftTokens = new(
        @"\b(discord|telegram|ayugram|whatsapp|viber|skype|zoom|teamspeak|chrome|firefox|opera|edge|yandex|brave|vivaldi|qbittorrent|utorrent|winrar|anydesk|teamviewer|rustdesk|parsec|vpn|amnezia|happ|hiddify|clash|nekoray|nekobox|v2ray|tgwsproxy|wireguard|openvpn|windscribe|netfixer|hwinfo|crystaldiskinfo|wiztree|taskbarx|rufus|everything|sharex|lightshot|notion|obsidian|bitwarden|keepass|1password|nvidia|ds4windows|autoclicker|word|excel|powerpoint|aula|machenike|tetrardrop|tetradrop|tetrarain)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".mid", ".midi", ".flp", ".als", ".rpp", ".fst", ".nki"
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".psd", ".aep", ".prproj", ".blend", ".ase", ".aseprite"
    };

    private static readonly HashSet<string> DevExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".cpp", ".c", ".h", ".java", ".go", ".rs", ".json", ".yaml", ".yml", ".xml", ".sql", ".sln", ".csproj", ".html", ".css", ".sh"
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".rtf", ".csv", ".odt"
    };

    private readonly AppConfig _config;
    private readonly ShellIconExtractor _iconExtractor;
    private FileSystemWatcher? _desktopWatcher;
    private readonly DispatcherTimer _autoDebounceTimer;

    public event Action<List<FolderTileConfig>, List<FolderTileConfig>>? AutoOrganizedTiles;

    public DesktopOrganizerService(AppConfig config, ShellIconExtractor iconExtractor)
    {
        _config = config;
        _iconExtractor = iconExtractor;

        _autoDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _autoDebounceTimer.Tick += async (_, _) =>
        {
            _autoDebounceTimer.Stop();
            await RunSilentAutoOrganizeAsync();
        };
    }

    public static string GetManagedFoldersRoot()
    {
        string localRoot = Path.Combine(AppContext.BaseDirectory, "Folders");
        try
        {
            Directory.CreateDirectory(localRoot);
            string probe = Path.Combine(localRoot, ".write_test");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return localRoot;
        }
        catch
        {
            string docsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DeskFolders",
                "Folders");
            Directory.CreateDirectory(docsRoot);
            return docsRoot;
        }
    }

    public static bool EnsureDefaultCategories(AppConfig config)
    {
        bool changed = false;
        foreach (var tile in config.Tiles)
        {
            if (tile.Category != FolderCategory.Unassigned)
                continue;

            tile.Category = InferCategoryFromFolderName(tile.Name);
            changed = true;
        }
        return changed;
    }

    public static FolderCategory InferCategoryFromFolderName(string name)
    {
        string lower = name.Trim().ToLowerInvariant();
        if (lower.Contains("игр") || lower.Contains("game"))
            return FolderCategory.Games;
        if (lower.Contains("код") || lower.Contains("разраб") || lower.Contains("dev") || lower.Contains("code") || lower.Contains("програм"))
            return FolderCategory.Dev;
        if (lower.Contains("музык") || lower.Contains("music") || lower.Contains("звук") || lower.Contains("audio") || lower.Contains("daw"))
            return FolderCategory.Music;
        if (lower.Contains("видео") || lower.Contains("медиа") || lower.Contains("фото") || lower.Contains("дизайн") || lower.Contains("media") || lower.Contains("video"))
            return FolderCategory.Media;
        if (lower.Contains("софт") || lower.Contains("прог") || lower.Contains("утил") || lower.Contains("инет") || lower.Contains("интернет") || lower.Contains("soft") || lower.Contains("app") || lower.Contains("tool"))
            return FolderCategory.Software;
        if (lower.Contains("док") || lower.Contains("учеб") || lower.Contains("учёб") || lower.Contains("книг") || lower.Contains("doc"))
            return FolderCategory.Documents;
        if (lower.Contains("проч") || lower.Contains("разн") || lower.Contains("мусор") || lower.Contains("вход") || lower.Contains("other") || lower.Contains("misc") || lower.Contains("inbox"))
            return FolderCategory.Other;

        return FolderCategory.None;
    }

    public List<OrganizerTargetOption> BuildTargetOptions()
    {
        var options = new List<OrganizerTargetOption>();

        foreach (var tile in _config.Tiles)
        {
            if (!Directory.Exists(tile.FolderPath))
                continue;

            options.Add(new OrganizerTargetOption
            {
                DisplayName = tile.Name,
                ExistingTile = tile,
                CategoryToCreate = tile.Category,
                DefaultFolderName = tile.Name,
                DefaultTint = tile.TintName
            });
        }

        foreach (var (cat, name, tint) in DefaultFolderSpecs)
        {
            bool alreadyHasTile = _config.Tiles.Any(t => Directory.Exists(t.FolderPath) && t.Category == cat);
            if (!alreadyHasTile)
            {
                options.Add(new OrganizerTargetOption
                {
                    DisplayName = $"+ Создать «{name}»",
                    ExistingTile = null,
                    CategoryToCreate = cat,
                    DefaultFolderName = name,
                    DefaultTint = tint
                });
            }
        }

        return options;
    }

    public void UpdateAutoWatcher()
    {
        if (!_config.AutoOrganizeDesktop)
        {
            _autoDebounceTimer.Stop();
            _desktopWatcher?.Dispose();
            _desktopWatcher = null;
            return;
        }

        if (_desktopWatcher != null)
            return;

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop))
            return;

        try
        {
            _desktopWatcher = new FileSystemWatcher(desktop)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            _desktopWatcher.Created += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(RestartAutoTimer);
            _desktopWatcher.Renamed += (_, _) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(RestartAutoTimer);
            _desktopWatcher.EnableRaisingEvents = true;
        }
        catch
        {
        }
    }

    private void RestartAutoTimer()
    {
        _autoDebounceTimer.Stop();
        _autoDebounceTimer.Start();
    }

    private async Task RunSilentAutoOrganizeAsync()
    {
        if (!_config.AutoOrganizeDesktop)
            return;

        var options = BuildTargetOptions();
        var plan = await BuildPlanAsync(options, includeCommonDesktop: false);
        var valid = plan.Where(p => p.TargetOption != null).ToList();
        if (valid.Count == 0)
            return;

        var (created, touched) = ExecutePlan(valid);
        if (created.Count > 0 || touched.Count > 0)
            AutoOrganizedTiles?.Invoke(created, touched);
    }

    public async Task<List<DesktopSortPlanItem>> BuildPlanAsync(
        List<OrganizerTargetOption> options,
        bool includeCommonDesktop = true)
    {
        EnsureDefaultCategories(_config);

        var rawList = await Task.Run(() => ScanDesktopEntries(options, includeCommonDesktop));
        var tasks = rawList.Select(async item =>
        {
            item.Icon = await _iconExtractor.GetIconAsync(item.FullPath, 48);
            return item;
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    public (List<FolderTileConfig> CreatedTiles, List<FolderTileConfig> TouchedTiles) ExecutePlan(
        IEnumerable<DesktopSortPlanItem> items)
    {
        var created = new List<FolderTileConfig>();
        var touched = new HashSet<FolderTileConfig>();
        var createdByCategory = new Dictionary<FolderCategory, FolderTileConfig>();
        string root = GetManagedFoldersRoot();

        foreach (var item in items)
        {
            if (!item.IsSelected || item.TargetOption == null)
                continue;

            FolderTileConfig? targetTile = item.TargetOption.ExistingTile;
            if (targetTile == null)
            {
                var cat = item.TargetOption.CategoryToCreate;
                if (!createdByCategory.TryGetValue(cat, out targetTile))
                {
                    string folderPath = Path.Combine(root, item.TargetOption.DefaultFolderName);
                    Directory.CreateDirectory(folderPath);

                    targetTile = new FolderTileConfig
                    {
                        Name = item.TargetOption.DefaultFolderName,
                        FolderPath = folderPath,
                        TintName = item.TargetOption.DefaultTint,
                        Category = cat
                    };
                    createdByCategory[cat] = targetTile;
                    created.Add(targetTile);
                }
            }

            string targetFolder = targetTile.FolderPath;
            if (!Directory.Exists(targetFolder))
                continue;

            try
            {
                if (item.IsFromCommonDesktop)
                {
                    try
                    {
                        ShortcutCreator.MoveEntryToFolder(item.FullPath, targetFolder);
                    }
                    catch
                    {
                        if (File.Exists(item.FullPath))
                            ShortcutCreator.HandleDroppedFile(item.FullPath, targetFolder, forceCopy: true);
                    }
                }
                else
                {
                    ShortcutCreator.MoveEntryToFolder(item.FullPath, targetFolder);
                }

                touched.Add(targetTile);
            }
            catch
            {
            }
        }

        return (created, touched.ToList());
    }

    private List<DesktopSortPlanItem> ScanDesktopEntries(
        List<OrganizerTargetOption> options,
        bool includeCommonDesktop)
    {
        var result = new List<DesktopSortPlanItem>();
        var activeTiles = _config.Tiles.Where(t => Directory.Exists(t.FolderPath)).ToList();

        var tileFolderPaths = new HashSet<string>(
            activeTiles.Select(t => t.FolderPath.TrimEnd('\\')),
            StringComparer.OrdinalIgnoreCase);

        var existingFileNamesInTiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tile in activeTiles)
        {
            try
            {
                foreach (var f in Directory.EnumerateFileSystemEntries(tile.FolderPath))
                    existingFileNamesInTiles.Add(Path.GetFileName(f));
            }
            catch
            {
            }
        }

        var dirsToScan = new List<(string DirPath, bool IsCommon)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false)
        };

        if (includeCommonDesktop)
            dirsToScan.Add((Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), true));

        string selfExe = Environment.ProcessPath ?? string.Empty;

        foreach (var (dirPath, isCommon) in dirsToScan)
        {
            if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath))
                continue;

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dirPath).EnumerateFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;

                if (entry.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entry is DirectoryInfo && tileFolderPaths.Contains(entry.FullName.TrimEnd('\\')))
                    continue;

                if (isCommon && existingFileNamesInTiles.Contains(entry.Name))
                    continue;

                if (!string.IsNullOrEmpty(selfExe) &&
                    entry.FullName.Equals(selfExe, StringComparison.OrdinalIgnoreCase))
                    continue;

                var (cat, reason) = ClassifyEntry(entry, activeTiles, out FolderTileConfig? customTile);
                OrganizerTargetOption? targetOpt = null;

                if (customTile != null)
                    targetOpt = options.FirstOrDefault(o => o.ExistingTile == customTile);

                targetOpt ??= options.FirstOrDefault(o => o.CategoryToCreate == cat)
                           ?? (cat == FolderCategory.Music ? options.FirstOrDefault(o => o.CategoryToCreate == FolderCategory.Media) : null)
                           ?? options.FirstOrDefault(o => o.CategoryToCreate == FolderCategory.Other)
                           ?? options.FirstOrDefault();

                if (targetOpt == null)
                    continue;

                string displayName = entry is FileInfo &&
                    (entry.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                     entry.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                     entry.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    ? Path.GetFileNameWithoutExtension(entry.Name)
                    : entry.Name;

                result.Add(new DesktopSortPlanItem
                {
                    Name = displayName,
                    FullPath = entry.FullName,
                    IsFromCommonDesktop = isCommon,
                    Reason = reason,
                    TargetOption = targetOpt,
                    IsSelected = true
                });
            }
        }

        return result.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static (FolderCategory Category, string Reason) ClassifyEntry(
        FileSystemInfo entry,
        List<FolderTileConfig> tiles,
        out FolderTileConfig? customMatchedTile)
    {
        customMatchedTile = null;

        string name = Path.GetFileNameWithoutExtension(entry.Name);
        string ext = entry.Extension;
        string targetPath = string.Empty;
        string urlTarget = string.Empty;
        string versionText = string.Empty;

        if (entry is FileInfo)
        {
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                targetPath = ReadShortcutTarget(entry.FullName);
            else if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                urlTarget = ReadUrlFileTarget(entry.FullName);
            else if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                targetPath = entry.FullName;
        }

        if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(targetPath);
                versionText = $"{vi.CompanyName} {vi.ProductName} {vi.FileDescription}";
            }
            catch
            {
            }
        }

        string normalizedPath = targetPath.Replace('\\', ' ').Replace('/', ' ').Replace('_', ' ').Replace('-', ' ');
        string normalizedName = name.Replace('_', ' ').Replace('-', ' ');
        string combined = $"{normalizedName} {normalizedPath} {urlTarget} {versionText}";
        string lowerCombined = combined.ToLowerInvariant();

        // 1. Пользовательские теги
        foreach (var tile in tiles)
        {
            if (string.IsNullOrWhiteSpace(tile.CustomKeywords))
                continue;

            var words = tile.CustomKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var w in words)
            {
                if (lowerCombined.Contains(w.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    customMatchedTile = tile;
                    return (tile.Category, $"Тег «{w}»");
                }
            }
        }

        // 2. Игровые протоколы (.url)
        if (!string.IsNullOrEmpty(urlTarget))
        {
            string u = urlTarget.ToLowerInvariant();
            if (u.StartsWith("steam://") || u.StartsWith("com.epicgames.launcher://") ||
                u.StartsWith("uplay://") || u.StartsWith("battlenet://") || u.StartsWith("roblox"))
                return (FolderCategory.Games, "Игровой лаунчер");
        }

        // 3. Музыка / DAW / Плагины (приоритет выше игр, чтобы "FL Studio Discord RPC" шло в Музыку)
        if (ContainsAny(lowerCombined, MusicPhrases) || MusicTokens.IsMatch(combined))
            return (FolderCategory.Music, "Музыка / DAW / Плагин");

        // 4. Разработка и код
        if (ContainsAny(lowerCombined, DevPhrases) || DevTokens.IsMatch(combined))
            return (FolderCategory.Dev, "Разработка / Код");

        // 5. Видео / Графика
        if (ContainsAny(lowerCombined, MediaPhrases) || MediaTokens.IsMatch(combined))
            return (FolderCategory.Media, "Видео / Графика");

        // 6. Игры и лаунчеры
        if (ContainsAny(lowerCombined, GamePhrases) || GameTokens.IsMatch(combined))
            return (FolderCategory.Games, "Игра / Лаунчер");

        // 7. Софт и общение
        if (ContainsAny(lowerCombined, SoftPhrases) || SoftTokens.IsMatch(combined))
            return (FolderCategory.Software, "Софт / Общение");

        // 8. По расширению файла
        if (entry is FileInfo)
        {
            if (MusicExtensions.Contains(ext))
                return (FolderCategory.Music, $"Аудио / Проект ({ext})");

            if (MediaExtensions.Contains(ext))
                return (FolderCategory.Media, $"Медиа ({ext})");

            if (DevExtensions.Contains(ext))
                return (FolderCategory.Dev, $"Код ({ext})");

            if (DocExtensions.Contains(ext))
                return (FolderCategory.Documents, $"Документ ({ext})");

            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                return (FolderCategory.Software, "Программа / Утилита");
        }

        if (entry is DirectoryInfo)
            return (FolderCategory.Other, "Папка с рабочего стола");

        return (FolderCategory.Other, "Прочее");
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string ReadShortcutTarget(string lnkPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return string.Empty;

            shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return string.Empty;

            dynamic dynShell = shell;
            shortcut = dynShell.CreateShortcut(lnkPath);
            if (shortcut == null)
                return string.Empty;

            dynamic dynShortcut = shortcut;
            return (string?)dynShortcut.TargetPath ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string ReadUrlFileTarget(string urlPath)
    {
        try
        {
            foreach (string line in File.ReadLines(urlPath))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    return line[4..].Trim();
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    public void Dispose()
    {
        _autoDebounceTimer.Stop();
        _desktopWatcher?.Dispose();
        _desktopWatcher = null;
    }
}