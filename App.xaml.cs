using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DeskFolders.Models;
using DeskFolders.Native;
using DeskFolders.Services;
using DeskFolders.Views;
using Microsoft.Win32;

namespace DeskFolders;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ConfigService _configService = null!;
    private AppConfig _config = null!;
    private ShellIconExtractor _iconExtractor = null!;
    private FolderContentService _contentService = null!;
    private DesktopLayerService _desktopLayer = null!;
    private StartupService _startupService = null!;
    private SoundService _soundService = null!;
    private DesktopOrganizerService _organizerService = null!;
    private TrayService _trayService = null!;
    private SnapOverlayWindow? _snapOverlay;
    private LaunchpadBackdropWindow? _launchpadBackdrop;
    private SettingsWindow? _settingsWindow;
    private DesktopOrganizerWindow? _organizerWindow;
    private bool _tilesWereHiddenBeforeLaunchpad;

    private readonly List<FolderTileWindow> _tileWindows = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, exArgs) =>
        {
            string msg = exArgs.Exception.ToString();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), msg);
            Console.Error.WriteLine(msg);
        };

        _singleInstanceMutex = new Mutex(true, "DeskFolders_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _configService = new ConfigService();
        _config = _configService.Load();

        _iconExtractor = new ShellIconExtractor();
        _contentService = new FolderContentService(_iconExtractor);
        _desktopLayer = new DesktopLayerService();
        _startupService = new StartupService();
        _soundService = new SoundService(_config);

        if (DesktopOrganizerService.EnsureDefaultCategories(_config))
            SaveConfig();

        _organizerService = new DesktopOrganizerService(_config, _iconExtractor);
        _organizerService.AutoOrganizedTiles += ApplyOrganizerResults;
        _organizerService.UpdateAutoWatcher();

        _desktopLayer.UpdateLaunchpadHotkey(_config.LaunchpadHotkey);
        _desktopLayer.ExplorerRestarted += () => _trayService.RecreateIcon();
        _desktopLayer.DisplayChanged += EnsureAllTilesOnScreen;
        _desktopLayer.LaunchpadHotkeyPressed += ToggleLaunchpad;
        _desktopLayer.LaunchpadExternalFocusLost += ExitLaunchpad;

        _trayService = new TrayService(_config, _startupService.IsEnabled());
        _trayService.OpenSettingsRequested += OpenSettingsWindow;
        _trayService.AddFolderRequested += OnAddFolderRequested;
        _trayService.OrganizeDesktopRequested += OpenOrganizerWindow;
        _trayService.ToggleLaunchpadRequested += ToggleLaunchpad;
        _trayService.ToggleVisibilityRequested += OnToggleVisibilityRequested;
        _trayService.AlignAllNowRequested += AlignAllTilesToGrid;
        _trayService.ToggleSnapRequested += OnToggleSnapRequested;
        _trayService.ToggleLockRequested += OnToggleLockRequested;
        _trayService.ToggleAutostartRequested += OnToggleAutostartRequested;
        _trayService.ExitRequested += () => Shutdown();

        foreach (var tileConfig in _config.Tiles.ToList())
            CreateAndShowTileWindow(tileConfig);

        EnsureAllTilesOnScreen();

        if (!_configService.ConfigExistedOnStartup && _config.Tiles.Count == 0)
        {
            _trayService.ShowBalloon("DeskFolders", "Разберите рабочий стол в один клик или добавьте папки!");
            OpenOrganizerWindow();
        }
    }

    private void OpenSettingsWindow()
    {
        if (_desktopLayer.IsLaunchpadActive)
            ExitLaunchpad();

        if (_settingsWindow != null)
        {
            _settingsWindow.RefreshFoldersList();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _config,
            _startupService,
            cfg =>
            {
                var win = _tileWindows.FirstOrDefault(w => w.Config == cfg);
                return win != null ? (win.CustomCoverSource, win.CachedItems) : (null, null);
            },
            _soundService,
            preset =>
            {
                bool ok = _desktopLayer.UpdateLaunchpadHotkey(preset);
                _trayService.SyncFromConfig(_config);
                return ok;
            },
            refreshIcons =>
            {
                _trayService.SyncFromConfig(_config);
                foreach (var tile in _tileWindows)
                    tile.ApplyAppearance(refreshIcons);
            },
            AlignAllTilesToGrid,
            OnAddFolderRequested,
            cfg =>
            {
                var win = _tileWindows.FirstOrDefault(w => w.Config == cfg);
                if (win != null)
                    RemoveTile(win);
            },
            async cfg =>
            {
                var win = _tileWindows.FirstOrDefault(w => w.Config == cfg);
                if (win != null)
                {
                    win.UpdateTitle(cfg.Name);
                    win.RebindWatcher();
                    win.ApplyAppearance(refreshIcons: false);
                    await win.RefreshPreviewAsync();
                    _settingsWindow?.RefreshFoldersList();
                }
            },
            ToggleLaunchpad,
            OpenOrganizerWindow,
            () => _organizerService.UpdateAutoWatcher(),
            SaveConfig);

        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenOrganizerWindow()
    {
        if (_desktopLayer.IsLaunchpadActive)
            ExitLaunchpad();

        if (_organizerWindow != null)
        {
            _organizerWindow.Activate();
            return;
        }

        _organizerWindow = new DesktopOrganizerWindow(_organizerService, ApplyOrganizerResults);
        _organizerWindow.Closed += (_, _) => _organizerWindow = null;
        _organizerWindow.Show();
        _organizerWindow.Activate();
    }

    private async void ApplyOrganizerResults(List<FolderTileConfig> createdTiles, List<FolderTileConfig> touchedTiles)
    {
        _soundService.PlaySnap();

        foreach (var newCfg in createdTiles)
        {
            var pt = GridSnapService.FindNearestFreeCell(
                GridSnapService.MarginX,
                GridSnapService.MarginY,
                1.0, 1.0,
                _config.TileSize,
                _config.ShowTileLabels,
                _config.Tiles);
            newCfg.X = pt.X;
            newCfg.Y = pt.Y;
            _config.Tiles.Add(newCfg);
            CreateAndShowTileWindow(newCfg);
        }

        if (createdTiles.Count > 0 && !_config.TilesVisible)
        {
            _config.TilesVisible = true;
            _trayService.SyncFromConfig(_config);
            foreach (var w in _tileWindows)
                w.Show();
        }

        SaveConfig();

        foreach (var tile in _tileWindows.Where(w => touchedTiles.Contains(w.Config) && !createdTiles.Contains(w.Config)))
            await tile.RefreshPreviewAsync();

        _settingsWindow?.RefreshFoldersList();
    }

    private SnapOverlayWindow GetSnapOverlay()
    {
        _snapOverlay ??= new SnapOverlayWindow();
        return _snapOverlay;
    }

    private void CreateAndShowTileWindow(FolderTileConfig tileConfig)
    {
        var tileWindow = new FolderTileWindow(
            tileConfig,
            _config,
            _contentService,
            _iconExtractor,
            _desktopLayer,
            _soundService,
            SaveConfig,
            RemoveTile,
            AddTileFromPath,
            GetSnapOverlay,
            () => { if (_desktopLayer.IsLaunchpadActive) ExitLaunchpad(); });

        _tileWindows.Add(tileWindow);

        if (_config.TilesVisible || _desktopLayer.IsLaunchpadActive)
            tileWindow.Show();
    }

    private void ToggleLaunchpad()
    {
        if (_desktopLayer.IsLaunchpadActive)
            ExitLaunchpad();
        else
            EnterLaunchpad();
    }

    private void EnterLaunchpad()
    {
        if (_desktopLayer.IsLaunchpadActive)
            return;

        _soundService.PlayLaunchpad();

        _tilesWereHiddenBeforeLaunchpad = !_config.TilesVisible;
        if (_tilesWereHiddenBeforeLaunchpad)
        {
            foreach (var tile in _tileWindows)
                tile.Show();
        }

        _launchpadBackdrop = new LaunchpadBackdropWindow(
            LoadAllLaunchpadItemsAsync,
            () => _desktopLayer.RaiseTilesInLaunchpad());
        _launchpadBackdrop.DismissRequested += ExitLaunchpad;
        _launchpadBackdrop.ItemLaunched += ExitLaunchpad;
        _launchpadBackdrop.Show();
        var lpHwnd = new System.Windows.Interop.WindowInteropHelper(_launchpadBackdrop).Handle;
        if (lpHwnd != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(lpHwnd);
        _launchpadBackdrop.Activate();
        _launchpadBackdrop.FocusSearchImmediately();

        _desktopLayer.SetLaunchpadMode(true);
    }

    private Task<List<LaunchpadSearchEntry>> LoadAllLaunchpadItemsAsync()
    {
        var all = new List<LaunchpadSearchEntry>();
        foreach (var tile in _tileWindows)
        {
            foreach (var item in tile.CachedItems)
            {
                all.Add(new LaunchpadSearchEntry
                {
                    Name = item.Name,
                    FullPath = item.FullPath,
                    FolderName = tile.Config.Name,
                    Icon = item.Icon
                });
            }
        }

        return Task.FromResult(all.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    private void ExitLaunchpad()
    {
        if (!_desktopLayer.IsLaunchpadActive)
            return;

        foreach (var tile in _tileWindows)
            tile.CloseActivePopup();

        _desktopLayer.SetLaunchpadMode(false);

        _launchpadBackdrop?.FadeOutAndClose();
        _launchpadBackdrop = null;

        if (_tilesWereHiddenBeforeLaunchpad)
        {
            foreach (var tile in _tileWindows)
                tile.Hide();
            _tilesWereHiddenBeforeLaunchpad = false;
        }
    }

    private void OnAddFolderRequested()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку для виджета рабочего стола",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            var pt = GridSnapService.FindNearestFreeCell(
                GridSnapService.MarginX,
                GridSnapService.MarginY,
                1.0,
                1.0,
                _config.TileSize,
                _config.ShowTileLabels,
                _config.Tiles);
            AddTileFromPath(dialog.FolderName, pt.X, pt.Y);
        }
    }

    private void AddTileFromPath(string folderPath, double x, double y)
    {
        if (!Directory.Exists(folderPath))
            return;

        string folderName = new DirectoryInfo(folderPath).Name;
        if (string.IsNullOrWhiteSpace(folderName))
            folderName = folderPath;

        if (_config.SnapToGrid)
        {
            var snapped = GridSnapService.FindNearestFreeCell(
                x, y, 1.0, 1.0, _config.TileSize, _config.ShowTileLabels, _config.Tiles);
            x = snapped.X;
            y = snapped.Y;
        }

        var tileConfig = new FolderTileConfig
        {
            Name = folderName,
            FolderPath = folderPath,
            X = x,
            Y = y,
            Category = DesktopOrganizerService.InferCategoryFromFolderName(folderName)
        };

        _config.Tiles.Add(tileConfig);
        if (!_config.TilesVisible)
        {
            _config.TilesVisible = true;
            _trayService.SyncFromConfig(_config);
            foreach (var w in _tileWindows)
                w.Show();
        }

        SaveConfig();
        CreateAndShowTileWindow(tileConfig);
        _settingsWindow?.RefreshFoldersList();
    }

    private void RemoveTile(FolderTileWindow tileWindow)
    {
        _tileWindows.Remove(tileWindow);
        _config.Tiles.Remove(tileWindow.Config);
        SaveConfig();
        tileWindow.Close();
        _settingsWindow?.RefreshFoldersList();
    }

    private void OnToggleVisibilityRequested()
    {
        if (_desktopLayer.IsLaunchpadActive)
            ExitLaunchpad();

        _config.TilesVisible = !_config.TilesVisible;
        _trayService.SyncFromConfig(_config);
        SaveConfig();

        foreach (var tile in _tileWindows)
        {
            if (_config.TilesVisible)
                tile.ShowWithFade();
            else
                tile.HideWithFade();
        }

        if (_config.TilesVisible)
            _desktopLayer.RefreshAll();
    }

    private void OnToggleSnapRequested()
    {
        _config.SnapToGrid = !_config.SnapToGrid;
        _trayService.SyncFromConfig(_config);
        SaveConfig();
    }

    private void OnToggleLockRequested()
    {
        _config.LockPositions = !_config.LockPositions;
        _trayService.SyncFromConfig(_config);
        SaveConfig();
    }

    private void AlignAllTilesToGrid()
    {
        var placed = new List<FolderTileConfig>();
        foreach (var tile in _tileWindows)
        {
            var dpi = VisualTreeHelper.GetDpi(tile);
            var pt = GridSnapService.FindNearestFreeCell(
                tile.Left, tile.Top,
                dpi.DpiScaleX, dpi.DpiScaleY,
                _config.TileSize,
                _config.ShowTileLabels,
                placed);

            placed.Add(tile.Config);
            tile.AnimateMoveTo(pt.X, pt.Y);
        }

        _soundService.PlaySnap();
        SaveConfig();
    }

    private void EnsureAllTilesOnScreen()
    {
        bool changed = false;
        foreach (var tile in _tileWindows)
        {
            var dpi = VisualTreeHelper.GetDpi(tile);
            var fixedPt = GridSnapService.EnsureVisibleOnScreens(
                tile.Left, tile.Top,
                dpi.DpiScaleX, dpi.DpiScaleY,
                _config.TileSize,
                _config.ShowTileLabels,
                _config.SnapToGrid,
                _config.Tiles,
                tile.Config);

            if (fixedPt.HasValue)
            {
                tile.AnimateMoveTo(fixedPt.Value.X, fixedPt.Value.Y);
                changed = true;
            }
        }

        if (changed)
            SaveConfig();
    }

    private void OnToggleAutostartRequested()
    {
        bool newState = !_startupService.IsEnabled();
        _startupService.SetEnabled(newState);
        _trayService.UpdateAutostartState(_startupService.IsEnabled());
    }

    private void SaveConfig()
    {
        _configService.Save(_config);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _organizerWindow?.Close();
        _settingsWindow?.Close();
        _launchpadBackdrop?.Close();
        _snapOverlay?.Close();
        foreach (var tile in _tileWindows.ToList())
            tile.Close();
        _tileWindows.Clear();

        _organizerService?.Dispose();
        _trayService?.Dispose();
        _soundService?.Dispose();
        _desktopLayer?.Dispose();
        _iconExtractor?.Dispose();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}