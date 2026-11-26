using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskFolders.Models;
using DeskFolders.Native;
using DeskFolders.Services;
using Microsoft.Win32;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace DeskFolders.Views;

public partial class FolderTileWindow : Window
{
    private readonly FolderTileConfig _config;
    private readonly AppConfig _appConfig;
    private readonly FolderContentService _contentService;
    private readonly ShellIconExtractor _iconExtractor;
    private readonly DesktopLayerService _desktopLayer;
    private readonly SoundService _soundService;
    private readonly Action _saveConfigCallback;
    private readonly Action<FolderTileWindow> _removeCallback;
    private readonly Action<string, double, double> _createTileCallback;
    private readonly Func<SnapOverlayWindow> _getSnapOverlay;
    private readonly Action? _onPopupItemLaunched;

    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounceTimer;
    private HwndSource? _hwndSource;
    private TileThemePalette _palette;

    private List<FolderItemInfo> _cachedAllItems = new();
    private FolderPopupWindow? _cachedPopup;

    private bool _isDragging;
    private bool _isAnimatingMove;
    private int _animMoveGen;
    private NativeMethods.POINT _dragStartCursorPx;
    private double _dragStartLeft;
    private double _dragStartTop;
    private Point _pendingSnapTarget;
    private bool _hasPendingSnap;

    private static readonly SolidColorBrush DropBorder = new(Color.FromArgb(0xCC, 0x5A, 0xAF, 0xFF));

    static FolderTileWindow()
    {
        DropBorder.Freeze();
    }

    public FolderTileWindow(
        FolderTileConfig config,
        AppConfig appConfig,
        FolderContentService contentService,
        ShellIconExtractor iconExtractor,
        DesktopLayerService desktopLayer,
        SoundService soundService,
        Action saveConfigCallback,
        Action<FolderTileWindow> removeCallback,
        Action<string, double, double> createTileCallback,
        Func<SnapOverlayWindow> getSnapOverlay,
        Action? onPopupItemLaunched = null)
    {
        InitializeComponent();

        _config = config;
        _appConfig = appConfig;
        _contentService = contentService;
        _iconExtractor = iconExtractor;
        _desktopLayer = desktopLayer;
        _soundService = soundService;
        _saveConfigCallback = saveConfigCallback;
        _removeCallback = removeCallback;
        _createTileCallback = createTileCallback;
        _getSnapOverlay = getSnapOverlay;
        _onPopupItemLaunched = onPopupItemLaunched;
        _palette = TileThemeService.Get(_config.TintName, _appConfig.TileOpacity);

        Left = _config.X;
        Top = _config.Y;
        TitleText.Text = _config.Name;

        ApplyAppearance(refreshIcons: false);

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await RefreshPreviewAsync();
        };

        Loaded += async (_, _) =>
        {
            InitializeWatcher();
            await RefreshPreviewAsync();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, EnsureCachedPopupCreated);
        };

        MouseLeftButtonDown += OnTileMouseLeftButtonDown;
        MouseMove += OnTileMouseMove;
        MouseLeftButtonUp += OnTileMouseLeftButtonUp;
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
                OnOpenExplorerClick(this, new RoutedEventArgs());
        };
        LostMouseCapture += (_, _) =>
        {
            if (_isDragging)
                FinishTileDrag();
        };

        DragEnter += OnTileDragEnter;
        DragOver += OnTileDragEnter;
        DragLeave += (_, _) => TileBorder.BorderBrush = _palette.BorderBrush;
        Drop += OnTileDrop;
    }

    public FolderTileConfig Config => _config;
    public AppConfig AppConfig => _appConfig;
    public IReadOnlyList<FolderItemInfo> CachedItems => _cachedAllItems;
    public ImageSource? CustomCoverSource => CustomCoverImage.Visibility == Visibility.Visible ? CustomCoverImage.Source : null;

    public void UpdateTitle(string newName)
    {
        _config.Name = newName;
        TitleText.Text = newName;
    }

    public void RebindWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        InitializeWatcher();
    }

    public void CloseActivePopup(bool immediate = false)
    {
        if (_cachedPopup == null || !_cachedPopup.IsOpenOrShowing)
            return;

        if (immediate)
            _cachedPopup.HideImmediate();
        else
            _cachedPopup.CloseSafely();
    }

    public void ApplyAppearance(bool refreshIcons = true)
    {
        _palette = TileThemeService.Get(_config.TintName, _appConfig.TileOpacity);
        var (winW, winH, boxSize, _, fontSize) = GridSnapService.GetMetrics(_appConfig.TileSize, _appConfig.ShowTileLabels);

        double maxRadius = boxSize / 2.0;
        double cornerRadius = Math.Clamp(_appConfig.TileCornerRadius, 4, maxRadius);

        Width = winW;
        Height = winH;
        TileBorder.Width = boxSize;
        TileBorder.Height = boxSize;
        TileBorder.CornerRadius = new CornerRadius(cornerRadius);
        HoverOverlay.CornerRadius = new CornerRadius(cornerRadius);
        double padVal = !string.IsNullOrWhiteSpace(_config.CustomIconPath) ? 14 : (_appConfig.PreviewGridSize == 3 ? 8 : 10);
        TileBorder.Padding = new Thickness(padVal);
        HoverOverlay.Margin = new Thickness(-padVal);
        TileBorder.Background = _palette.TileBackground;
        TileBorder.BorderBrush = _palette.BorderBrush;

        TitleText.Width = winW - 4;
        TitleText.FontSize = fontSize;
        TitleText.Visibility = _appConfig.ShowTileLabels ? Visibility.Visible : Visibility.Collapsed;

        PreviewGrid.Rows = _appConfig.PreviewGridSize;
        PreviewGrid.Columns = _appConfig.PreviewGridSize;

        _cachedPopup?.ApplySettingsAndTheme();

        if (refreshIcons)
            _ = RefreshPreviewAsync();
    }

    public void ShowWithFade()
    {
        BeginAnimation(OpacityProperty, null);
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(160)));
    }

    public void HideWithFade()
    {
        CloseActivePopup(immediate: true);
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        anim.Completed += (_, _) =>
        {
            if (Opacity <= 0.01)
                Hide();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public void AnimateMoveTo(double targetX, double targetY)
    {
        double startX = Left;
        double startY = Top;

        _config.X = targetX;
        _config.Y = targetY;

        int gen = ++_animMoveGen;
        _isAnimatingMove = true;

        Left = targetX;
        Top = targetY;

        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(150);

        var animX = new DoubleAnimation(startX, targetX, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var animY = new DoubleAnimation(startY, targetY, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        animX.Completed += (_, _) =>
        {
            if (_animMoveGen == gen)
                _isAnimatingMove = false;
        };

        BeginAnimation(LeftProperty, animX);
        BeginAnimation(TopProperty, animY);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~NativeMethods.WS_MINIMIZEBOX;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        _desktopLayer.Register(this);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_SYSCOMMAND:
                if ((wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_MINIMIZE)
                    handled = true;
                break;

            case NativeMethods.WM_WINDOWPOSCHANGING:
                if (lParam != IntPtr.Zero)
                {
                    var pos = Marshal.PtrToStructure<NativeMethods.WINDOWPOS>(lParam);
                    bool modified = false;

                    if (pos.x <= -32000 || pos.y <= -32000)
                    {
                        pos.flags |= NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE;
                        modified = true;
                    }

                    if (_isDragging || _isAnimatingMove)
                    {
                        pos.flags |= NativeMethods.SWP_NOZORDER;
                        modified = true;
                    }
                    else if (!_desktopLayer.IsLaunchpadActive &&
                             (pos.flags & NativeMethods.SWP_NOZORDER) == 0 &&
                             pos.hwndInsertAfter != NativeMethods.HWND_BOTTOM)
                    {
                        pos.hwndInsertAfter = NativeMethods.HWND_BOTTOM;
                        modified = true;
                    }

                    if (modified)
                        Marshal.StructureToPtr(pos, lParam, false);
                }
                break;
        }

        return IntPtr.Zero;
    }

    private void InitializeWatcher()
    {
        if (!Directory.Exists(_config.FolderPath))
            return;

        try
        {
            _watcher = new FileSystemWatcher(_config.FolderPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };

            FileSystemEventHandler onChanged = (_, _) => Dispatcher.InvokeAsync(ScheduleRefresh);
            RenamedEventHandler onRenamed = (_, _) => Dispatcher.InvokeAsync(ScheduleRefresh);

            _watcher.Created += onChanged;
            _watcher.Deleted += onChanged;
            _watcher.Changed += onChanged;
            _watcher.Renamed += onRenamed;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
        }
    }

    private void ScheduleRefresh()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    public async Task RefreshPreviewAsync()
    {
        if (!Directory.Exists(_config.FolderPath))
        {
            _cachedAllItems = new List<FolderItemInfo>();
            PreviewGrid.Children.Clear();
            CustomCoverImage.Visibility = Visibility.Collapsed;
            MissingBadge.Visibility = Visibility.Visible;
            TileBorder.ToolTip = $"Папка не найдена:\n{_config.FolderPath}\nНажмите, чтобы указать новый путь";
            return;
        }

        MissingBadge.Visibility = Visibility.Collapsed;
        TileBorder.ToolTip = null;

        _cachedAllItems = await _contentService.LoadItemsAsync(
            _config.FolderPath,
            64,
            maxCount: null,
            hiddenItems: _config.HiddenItems,
            sortMode: _config.SortMode,
            customOrder: _config.CustomOrder);

        _cachedPopup?.SyncFromTileCache(_cachedAllItems);

        if (!string.IsNullOrWhiteSpace(_config.CustomIconPath) && File.Exists(_config.CustomIconPath))
        {
            var customImg = await LoadCustomCoverAsync(_config.CustomIconPath);
            if (customImg != null)
            {
                PreviewGrid.Visibility = Visibility.Collapsed;
                CustomCoverImage.Source = customImg;
                CustomCoverImage.Visibility = Visibility.Visible;
                return;
            }
        }

        CustomCoverImage.Visibility = Visibility.Collapsed;
        CustomCoverImage.Source = null;
        PreviewGrid.Visibility = Visibility.Visible;

        int grid = _appConfig.PreviewGridSize == 3 ? 3 : 2;
        int maxIcons = grid * grid;

        var (_, _, boxSize, _, _) = GridSnapService.GetMetrics(_appConfig.TileSize, _appConfig.ShowTileLabels);
        double padding = grid == 3 ? 16 : 20;
        double cellTotal = (boxSize - padding) / grid;
        double margin = grid == 3 ? 1.5 : 3;
        double iconDim = Math.Max(14, cellTotal - margin * 2);

        PreviewGrid.Children.Clear();
        for (int i = 0; i < maxIcons; i++)
        {
            if (i < _cachedAllItems.Count && _cachedAllItems[i].Icon != null)
            {
                var img = new Image
                {
                    Source = _cachedAllItems[i].Icon,
                    Width = iconDim,
                    Height = iconDim,
                    Margin = new Thickness(margin),
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                PreviewGrid.Children.Add(img);
            }
            else
            {
                PreviewGrid.Children.Add(new Border
                {
                    Width = iconDim,
                    Height = iconDim,
                    Margin = new Thickness(margin),
                    CornerRadius = new CornerRadius(grid == 3 ? 4 : 6),
                    Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF))
                });
            }
        }
    }

    private void EnsureCachedPopupCreated()
    {
        if (_cachedPopup != null)
            return;

        _cachedPopup = new FolderPopupWindow(
            _config,
            _appConfig,
            _contentService,
            _soundService,
            this,
            _saveConfigCallback,
            _onPopupItemLaunched);
        _cachedPopup.SyncFromTileCache(_cachedAllItems);
    }

    private async Task<BitmapSource?> LoadCustomCoverAsync(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return await Task.Run(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 128;
                    bmp.EndInit();
                    bmp.Freeze();
                    return (BitmapSource)bmp;
                }
                catch
                {
                    return null;
                }
            });
        }

        return await _iconExtractor.GetIconAsync(path, 128);
    }

    private void AnimateScale(double toScale, int ms)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(ms);
        TileScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(toScale, dur) { EasingFunction = ease });
        TileScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(toScale, dur) { EasingFunction = ease });
    }

    private void OnTileMouseEnter(object sender, MouseEventArgs e)
    {
        HoverOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(130)));
        if (!_isDragging)
            AnimateScale(1.045, 140);
    }

    private void OnTileMouseLeave(object sender, MouseEventArgs e)
    {
        HoverOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        if (!_isDragging)
            AnimateScale(1.0, 150);
    }

    private void OnTileMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _hasPendingSnap = false;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        NativeMethods.GetCursorPos(out _dragStartCursorPx);
        AnimateScale(0.95, 80);
        CaptureMouse();
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (_appConfig.LockPositions || _desktopLayer.IsLaunchpadActive)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        NativeMethods.GetCursorPos(out var currentPx);

        double dx = (currentPx.X - _dragStartCursorPx.X) / dpi.DpiScaleX;
        double dy = (currentPx.Y - _dragStartCursorPx.Y) / dpi.DpiScaleY;

        if (!_isDragging && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5))
        {
            _isDragging = true;
            AnimateScale(1.04, 100);
        }

        if (!_isDragging)
            return;

        double rawX = _dragStartLeft + dx;
        double rawY = _dragStartTop + dy;

        if (_appConfig.SnapToGrid)
        {
            Left = rawX;
            Top = rawY;
            _pendingSnapTarget = GridSnapService.FindNearestFreeCell(
                rawX, rawY, dpi.DpiScaleX, dpi.DpiScaleY, _appConfig.TileSize, _appConfig.ShowTileLabels, _appConfig.Tiles, _config);
            _hasPendingSnap = true;
            _getSnapOverlay().ShowAt(_pendingSnapTarget.X, _pendingSnapTarget.Y, _appConfig.TileSize, _desktopLayer, _appConfig.ShowTileLabels, new WindowInteropHelper(this).Handle);
        }
        else
        {
            var mag = GridSnapService.ApplyMagneticAlignment(
                rawX, rawY, dpi.DpiScaleX, dpi.DpiScaleY, _appConfig.TileSize, _appConfig.ShowTileLabels, _appConfig.Tiles, _config);
            Left = mag.X;
            Top = mag.Y;
        }
    }

    private void OnTileMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured && !_isDragging)
            return;

        if (!_isDragging)
        {
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            AnimateScale(IsMouseOver ? 1.045 : 1.0, 120);
            OpenPopup();
            return;
        }

        FinishTileDrag();
    }

    private void FinishTileDrag()
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        bool hasSnap = _hasPendingSnap;
        _hasPendingSnap = false;

        if (IsMouseCaptured)
            ReleaseMouseCapture();

        AnimateScale(IsMouseOver ? 1.045 : 1.0, 120);
        _getSnapOverlay().Hide();

        if (hasSnap)
        {
            AnimateMoveTo(_pendingSnapTarget.X, _pendingSnapTarget.Y);
            _soundService.PlaySnap();
        }
        else
        {
            ClampToWorkArea();
            _config.X = Left;
            _config.Y = Top;
        }

        _saveConfigCallback();
    }

    private void ClampToWorkArea()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var centerPx = new Drawing.Point(
            (int)((Left + Width / 2) * dpi.DpiScaleX),
            (int)((Top + Height / 2) * dpi.DpiScaleY));

        var workPx = WinForms.Screen.FromPoint(centerPx).WorkingArea;
        double minX = workPx.Left / dpi.DpiScaleX;
        double minY = workPx.Top / dpi.DpiScaleY;
        double maxX = workPx.Right / dpi.DpiScaleX - Width;
        double maxY = workPx.Bottom / dpi.DpiScaleY - Height;

        Left = Math.Clamp(Left, minX, Math.Max(minX, maxX));
        Top = Math.Clamp(Top, minY, Math.Max(minY, maxY));
    }

    private void OpenPopup()
    {
        if (_cachedPopup != null && _cachedPopup.IsOpenOrShowing)
        {
            _soundService.PlayTick();
            _cachedPopup.CloseSafely();
            return;
        }

        if (!Directory.Exists(_config.FolderPath))
        {
            var res = MessageBox.Show(
                $"Папка «{_config.Name}» не найдена по пути:\n{_config.FolderPath}\n\nВыбрать новую папку для этой плитки?",
                "DeskFolders",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res == MessageBoxResult.Yes)
                ChangeFolderInteractive();
            return;
        }

        _soundService.PlayPop();
        EnsureCachedPopupCreated();
        _cachedPopup!.OpenInstant(_cachedAllItems);
    }

    private void OnTileDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DeskFolders_SourceFolder"))
        {
            string? srcFolder = e.Data.GetData("DeskFolders_SourceFolder") as string;
            if (string.Equals(srcFolder?.TrimEnd('\\'), _config.FolderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                e.Effects = DragDropEffects.None;
            }
            else
            {
                e.Effects = DragDropEffects.Move;
                TileBorder.BorderBrush = DropBorder;
            }
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey
                ? DragDropEffects.Copy
                : (DragDropEffects.Move | DragDropEffects.Copy);
            TileBorder.BorderBrush = DropBorder;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnTileDrop(object sender, DragEventArgs e)
    {
        TileBorder.BorderBrush = _palette.BorderBrush;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        bool isMoveFromPopup = e.Data.GetDataPresent("DeskFolders_SourceFolder");
        bool forceCopy = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;

        double offsetX = Width + 16;
        foreach (string path in paths)
        {
            if (isMoveFromPopup)
            {
                ShortcutCreator.MoveEntryToFolder(path, _config.FolderPath);
            }
            else if (Directory.Exists(path))
            {
                _createTileCallback(path, Left + offsetX, Top);
                offsetX += Width + 16;
            }
            else if (File.Exists(path))
            {
                ShortcutCreator.HandleDroppedFile(path, _config.FolderPath, forceCopy);
            }
        }

        _soundService.PlaySnap();
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var itemStyle = (Style)FindResource("DarkMenuItemStyle");

        ColorSubMenu.Items.Clear();
        foreach (var (key, name) in TileThemeService.PresetInfos)
        {
            var mi = new MenuItem
            {
                Header = name,
                Style = itemStyle,
                IsChecked = string.Equals(_config.TintName, key, StringComparison.OrdinalIgnoreCase)
            };
            mi.Click += (_, _) =>
            {
                _config.TintName = key;
                ApplyAppearance(refreshIcons: false);
                _saveConfigCallback();
            };
            ColorSubMenu.Items.Add(mi);
        }

        bool isCustomHex = !string.IsNullOrWhiteSpace(_config.TintName) && _config.TintName.StartsWith('#');
        var customColorItem = new MenuItem
        {
            Header = isCustomHex ? $"Свой цвет ({_config.TintName})..." : "Свой цвет...",
            Style = itemStyle,
            IsChecked = isCustomHex
        };
        customColorItem.Click += (_, _) =>
        {
            CloseActivePopup(immediate: true);
            var picker = new ColorPickerDialog(_config.TintName);
            if (picker.ShowDialog() == true)
            {
                _config.TintName = picker.SelectedHex;
                ApplyAppearance(refreshIcons: false);
                _saveConfigCallback();
            }
        };
        ColorSubMenu.Items.Add(customColorItem);

        CoverSubMenu.Items.Clear();
        bool hasCustomCover = !string.IsNullOrWhiteSpace(_config.CustomIconPath);
        var autoGridItem = new MenuItem
        {
            Header = "Сетка мини-иконок",
            Style = itemStyle,
            IsChecked = !hasCustomCover
        };
        autoGridItem.Click += async (_, _) =>
        {
            _config.CustomIconPath = null;
            ApplyAppearance(refreshIcons: false);
            _saveConfigCallback();
            await RefreshPreviewAsync();
        };

        var pickCoverItem = new MenuItem
        {
            Header = "Выбрать свою картинку / иконку...",
            Style = itemStyle,
            IsChecked = hasCustomCover
        };
        pickCoverItem.Click += async (_, _) =>
        {
            CloseActivePopup(immediate: true);
            var dlg = new OpenFileDialog
            {
                Title = "Выберите обложку для плитки",
                Filter = "Изображения и иконки|*.png;*.jpg;*.jpeg;*.ico;*.exe;*.bmp;*.webp|Все файлы|*.*"
            };
            if (dlg.ShowDialog() == true && File.Exists(dlg.FileName))
            {
                _config.CustomIconPath = dlg.FileName;
                ApplyAppearance(refreshIcons: false);
                _saveConfigCallback();
                await RefreshPreviewAsync();
            }
        };
        CoverSubMenu.Items.Add(autoGridItem);
        CoverSubMenu.Items.Add(pickCoverItem);

        SortSubMenu.Items.Clear();
        var sortModes = new (FolderSortMode Mode, string Label)[]
        {
            (FolderSortMode.Auto, "Авто (программы сверху)"),
            (FolderSortMode.NameAsc, "По имени (А → Я)"),
            (FolderSortMode.DateNewest, "Сначала новые"),
            (FolderSortMode.Custom, "Ручной порядок")
        };

        foreach (var (mode, label) in sortModes)
        {
            var sm = new MenuItem
            {
                Header = label,
                Style = itemStyle,
                IsChecked = _config.SortMode == mode
            };
            sm.Click += async (_, _) =>
            {
                _config.SortMode = mode;
                _saveConfigCallback();
                await RefreshPreviewAsync();
            };
            SortSubMenu.Items.Add(sm);
        }
    }

    private void OnChangeFolderClick(object sender, RoutedEventArgs e) => ChangeFolderInteractive();

    private async void ChangeFolderInteractive()
    {
        CloseActivePopup(immediate: true);
        var dialog = new OpenFolderDialog
        {
            Title = $"Выберите новую папку для «{_config.Name}»",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _config.FolderPath = dialog.FolderName;
            _config.HiddenItems.Clear();
            _saveConfigCallback();
            RebindWatcher();
            await RefreshPreviewAsync();
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        CloseActivePopup(immediate: true);
        var dialog = new RenameDialog(_config.Name);
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
        {
            UpdateTitle(dialog.NewName.Trim());
            _saveConfigCallback();
        }
    }

    private void OnOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_config.FolderPath))
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_config.FolderPath}\"")
        {
            UseShellExecute = true
        });
    }

    private void OnRemoveTileClick(object sender, RoutedEventArgs e) => _removeCallback(this);

    protected override void OnClosed(EventArgs e)
    {
        _cachedPopup?.ForceDestroy();
        _cachedPopup = null;

        _debounceTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        _desktopLayer.Unregister(this);
        base.OnClosed(e);
    }
}