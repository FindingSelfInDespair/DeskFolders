using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskFolders.Models;
using DeskFolders.Services;
using Microsoft.Win32;

namespace DeskFolders.Views;

public sealed record CategoryChoice(FolderCategory Category, string Label)
{
    public override string ToString() => Label;
}

public sealed class TileManageRow
{
    public FolderTileConfig Config { get; }
    public string Name => Config.Name;
    public string FolderPath => Config.FolderPath;
    public Brush SwatchBackground => TileThemeService.Get(Config.TintName, 0.92).TileBackground;
    public Brush SwatchBorder => TileThemeService.Get(Config.TintName, 0.92).BorderBrush;
    public string InitialLetter => string.IsNullOrWhiteSpace(Config.Name) ? "📁" : Config.Name[..1].ToUpperInvariant();

    public ImageSource? CustomCover { get; }
    public ImageSource? Mini0 { get; }
    public ImageSource? Mini1 { get; }
    public ImageSource? Mini2 { get; }
    public ImageSource? Mini3 { get; }

    public Visibility CoverImageVisibility => CustomCover != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MiniGridVisibility => CustomCover == null && Mini0 != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FallbackLetterVisibility => CustomCover == null && Mini0 == null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasCustomCoverVisibility => !string.IsNullOrWhiteSpace(Config.CustomIconPath) ? Visibility.Visible : Visibility.Collapsed;
    public FolderCategory Category
    {
        get => Config.Category;
        set => Config.Category = value;
    }

    public string CustomKeywords
    {
        get => Config.CustomKeywords;
        set => Config.CustomKeywords = value;
    }

    public Visibility HasHiddenVisibility => Config.HiddenItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string HiddenCountLabel => $"Вернуть скрытые ({Config.HiddenItems.Count})";

    public TileManageRow(FolderTileConfig config, ImageSource? cover, System.Collections.Generic.IReadOnlyList<FolderItemInfo>? items)
    {
        Config = config;
        CustomCover = cover;
        if (items != null)
        {
            Mini0 = items.Count > 0 ? items[0].Icon : null;
            Mini1 = items.Count > 1 ? items[1].Icon : null;
            Mini2 = items.Count > 2 ? items[2].Icon : null;
            Mini3 = items.Count > 3 ? items[3].Icon : null;
        }
    }
}

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly StartupService _startupService;
    private readonly SoundService _soundService;
    private readonly Func<FolderTileConfig, (ImageSource? Cover, System.Collections.Generic.IReadOnlyList<FolderItemInfo>? Items)> _getPreviewData;
    private readonly Func<LaunchpadHotkeyPreset, bool> _applyHotkey;
    private readonly Action<bool> _applyAppearanceAll;
    private readonly Action _alignGridAll;
    private readonly Action _addFolder;
    private readonly Action<FolderTileConfig> _removeTileByConfig;
    private readonly Action<FolderTileConfig> _refreshSingleTile;
    private readonly Action _toggleLaunchpad;
    private readonly Action _organizeDesktop;
    private readonly Action _updateAutoOrganize;
    private readonly Action _saveConfig;

    private bool _isInitializing = true;
    private bool _isClosing;
    private readonly DispatcherTimer _saveDebounce;

    public SettingsWindow(
        AppConfig config,
        StartupService startupService,
        Func<FolderTileConfig, (ImageSource? Cover, System.Collections.Generic.IReadOnlyList<FolderItemInfo>? Items)> getPreviewData,
        SoundService soundService,
        Func<LaunchpadHotkeyPreset, bool> applyHotkey,
        Action<bool> applyAppearanceAll,
        Action alignGridAll,
        Action addFolder,
        Action<FolderTileConfig> removeTileByConfig,
        Action<FolderTileConfig> refreshSingleTile,
        Action toggleLaunchpad,
        Action organizeDesktop,
        Action updateAutoOrganize,
        Action saveConfig)
    {
        InitializeComponent();

        _config = config;
        _startupService = startupService;
        _getPreviewData = getPreviewData;
        _soundService = soundService;
        _applyHotkey = applyHotkey;
        _applyAppearanceAll = applyAppearanceAll;
        _alignGridAll = alignGridAll;
        _addFolder = addFolder;
        _removeTileByConfig = removeTileByConfig;
        _refreshSingleTile = refreshSingleTile;
        _toggleLaunchpad = toggleLaunchpad;
        _organizeDesktop = organizeDesktop;
        _updateAutoOrganize = updateAutoOrganize;
        _saveConfig = saveConfig;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            _saveConfig();
        };

        Opacity = 0;
        SyncControlsFromConfig();
        _isInitializing = false;

        Loaded += (_, _) =>
        {
            AttachSmoothScrollRecursive(this);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(180);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1.0, dur) { EasingFunction = ease });
            ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1.0, dur) { EasingFunction = ease });
        };
    }

    public System.Collections.Generic.IReadOnlyList<CategoryChoice> CategoryChoices { get; } = new CategoryChoice[]
    {
        new(FolderCategory.Games, "Категория: Игры"),
        new(FolderCategory.Dev, "Категория: Код"),
        new(FolderCategory.Music, "Категория: Музыка"),
        new(FolderCategory.Media, "Категория: Видео и Дизайн"),
        new(FolderCategory.Software, "Категория: Софт"),
        new(FolderCategory.Documents, "Категория: Документы"),
        new(FolderCategory.Other, "Категория: Прочее"),
        new(FolderCategory.None, "Не автосортировать")
    };

    public void RefreshFoldersList()
    {
        FoldersList.ItemsSource = _config.Tiles.Select(t =>
        {
            var (cover, items) = _getPreviewData(t);
            return new TileManageRow(t, cover, items);
        }).ToList();
    }

    private void SyncControlsFromConfig()
    {
        DataContext = this;
        RefreshFoldersList();

        OpacitySlider.Value = Math.Clamp((int)Math.Round(_config.TileOpacity * 100), 30, 100);
        RadiusSlider.Value = Math.Clamp(_config.TileCornerRadius, 4, 32);

        SizeCompactRb.IsChecked = _config.TileSize == TileSizePreset.Compact;
        SizeStdRb.IsChecked = _config.TileSize == TileSizePreset.Standard;
        SizeLargeRb.IsChecked = _config.TileSize == TileSizePreset.Large;

        Grid2Rb.IsChecked = _config.PreviewGridSize == 2;
        Grid3Rb.IsChecked = _config.PreviewGridSize == 3;
        ShowTileLabelsChk.IsChecked = _config.ShowTileLabels;

        Col4Rb.IsChecked = _config.PopupColumns == 4;
        Col5Rb.IsChecked = _config.PopupColumns == 5;
        Col6Rb.IsChecked = _config.PopupColumns == 6;

        ViewScrollRb.IsChecked = !_config.UsePagination;
        ViewPagesRb.IsChecked = _config.UsePagination;

        LblTruncRb.IsChecked = _config.PopupLabelMode == PopupLabelMode.Truncated;
        LblFullRb.IsChecked = _config.PopupLabelMode == PopupLabelMode.Full;
        LblHideRb.IsChecked = _config.PopupLabelMode == PopupLabelMode.Hidden;

        HkAltTildeRb.IsChecked = _config.LaunchpadHotkey == LaunchpadHotkeyPreset.AltTilde;
        HkCtrlSpaceRb.IsChecked = _config.LaunchpadHotkey == LaunchpadHotkeyPreset.CtrlSpace;
        HkCtrlShiftSpaceRb.IsChecked = _config.LaunchpadHotkey == LaunchpadHotkeyPreset.CtrlShiftSpace;
        HkAltQRb.IsChecked = _config.LaunchpadHotkey == LaunchpadHotkeyPreset.AltQ;

        SnapChk.IsChecked = _config.SnapToGrid;
        LockChk.IsChecked = _config.LockPositions;
        SoundsChk.IsChecked = _config.EnableSounds;
        AutoOrganizeChk.IsChecked = _config.AutoOrganizeDesktop;
        AutostartChk.IsChecked = _startupService.IsEnabled();

        DelRecycleRb.IsChecked = _config.DeleteToRecycleBin;
        DelHideRb.IsChecked = !_config.DeleteToRecycleBin;

        UpdateLivePreviewTile();
    }

    private void UpdateLivePreviewTile()
    {
        var palette = TileThemeService.Get("Dark", _config.TileOpacity);
        double size = _config.TileSize == TileSizePreset.Compact ? 68
            : _config.TileSize == TileSizePreset.Large ? 88 : 78;

        LivePreviewTile.Width = size;
        LivePreviewTile.Height = size;
        LivePreviewTile.Background = palette.TileBackground;
        LivePreviewTile.BorderBrush = palette.BorderBrush;
        LivePreviewTile.CornerRadius = new CornerRadius(Math.Clamp(_config.TileCornerRadius, 4, size / 2.0));
        LivePreviewLabel.Visibility = _config.ShowTileLabels ? Visibility.Visible : Visibility.Collapsed;

        int grid = _config.PreviewGridSize == 3 ? 3 : 2;
        if (LivePreviewGrid.Rows != grid)
        {
            LivePreviewGrid.Rows = grid;
            LivePreviewGrid.Columns = grid;
            LivePreviewGrid.Children.Clear();

            var colors = new[]
            {
                Color.FromRgb(0x5A, 0xAF, 0xFF), Color.FromRgb(0x6E, 0xDC, 0x8C),
                Color.FromRgb(0xFF, 0xBE, 0x55), Color.FromRgb(0xF0, 0x6E, 0x96),
                Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x38, 0xBD, 0xF8),
                Color.FromRgb(0xFB, 0x71, 0x85), Color.FromRgb(0x34, 0xD3, 0x99),
                Color.FromRgb(0xFA, 0xCC, 0x15)
            };

            for (int i = 0; i < grid * grid; i++)
            {
                LivePreviewGrid.Children.Add(new Border
                {
                    Margin = new Thickness(grid == 3 ? 1.5 : 2),
                    CornerRadius = new CornerRadius(grid == 3 ? 3.5 : 5),
                    Background = new SolidColorBrush(colors[i % colors.Length])
                });
            }
        }

        OpacityValueText.Text = $"{(int)Math.Round(_config.TileOpacity * 100)}%";
        RadiusValueText.Text = $"{_config.TileCornerRadius} px";
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_isClosing)
            return;

        _isClosing = true;
        _saveDebounce.Stop();
        _saveConfig();

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var dur = TimeSpan.FromMilliseconds(120);
        var fade = new DoubleAnimation(0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();

        BeginAnimation(OpacityProperty, fade);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, dur) { EasingFunction = ease });
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, dur) { EasingFunction = ease });
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (TabFolders == null)
            return;

        TabFolders.Visibility = NavFolders.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TabAppearance.Visibility = NavAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TabBehavior.Visibility = NavBehavior.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TabAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        SectionTitle.Text = NavFolders.IsChecked == true ? "Папки на рабочем столе"
            : NavAppearance.IsChecked == true ? "Внешний вид"
            : NavBehavior.IsChecked == true ? "Управление и Launchpad"
            : "О программе";

        if (!_isInitializing)
        {
            _soundService.PlayTick();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(170);
            ContentStage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1.0, dur) { EasingFunction = ease });
            StageSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, dur) { EasingFunction = ease });
        }
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        _addFolder();
        DataContext = this;
        RefreshFoldersList();
    }

    private void OnOrganizeDesktopClick(object sender, RoutedEventArgs e) => _organizeDesktop();

    private void OnTileCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;
        _saveConfig();
    }

    private void OnTileKeywordsLostFocus(object sender, RoutedEventArgs e) => _saveConfig();

    private void OnAutoOrganizeChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;
        _config.AutoOrganizeDesktop = AutoOrganizeChk.IsChecked == true;
        _saveConfig();
        _updateAutoOrganize();
    }

    private void OnAlignGridClick(object sender, RoutedEventArgs e) => _alignGridAll();

    private void OnTestLaunchpadClick(object sender, RoutedEventArgs e)
    {
        Close();
        _toggleLaunchpad();
    }

    private void OnTestSoundClick(object sender, RoutedEventArgs e) => _soundService.PlayPop();

    private void OnPickTileColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TileManageRow row })
            return;

        var picker = new ColorPickerDialog(row.Config.TintName);
        if (picker.ShowDialog() == true)
        {
            row.Config.TintName = picker.SelectedHex;
            _saveConfig();
            _refreshSingleTile(row.Config);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshFoldersList);
            DataContext = this;
        RefreshFoldersList();
        }
    }

    private void OnPickTileCoverClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TileManageRow row })
            return;

        var dlg = new OpenFileDialog
        {
            Title = $"Выберите обложку для «{row.Name}»",
            Filter = "Изображения и иконки|*.png;*.jpg;*.jpeg;*.ico;*.exe;*.bmp;*.webp|Все файлы|*.*"
        };
        if (dlg.ShowDialog() == true && File.Exists(dlg.FileName))
        {
            row.Config.CustomIconPath = dlg.FileName;
            _saveConfig();
            _refreshSingleTile(row.Config);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshFoldersList);
            DataContext = this;
        RefreshFoldersList();
        }
    }

    private void OnResetTileCoverClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TileManageRow row })
            return;

        row.Config.CustomIconPath = null;
        _saveConfig();
        _refreshSingleTile(row.Config);
        DataContext = this;
        RefreshFoldersList();
    }

    private void OnRenameTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TileManageRow row })
            return;

        var dlg = new RenameDialog(row.Config.Name);
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.NewName))
        {
            row.Config.Name = dlg.NewName.Trim();
            _saveConfig();
            _refreshSingleTile(row.Config);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshFoldersList);
            DataContext = this;
        RefreshFoldersList();
        }
    }

    private void OnChangeTilePathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TileManageRow row })
            return;

        var dlg = new OpenFolderDialog { Title = $"Новая папка для «{row.Name}»" };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            row.Config.FolderPath = dlg.FolderName;
            row.Config.HiddenItems.Clear();
            _saveConfig();
            _refreshSingleTile(row.Config);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RefreshFoldersList);
            DataContext = this;
        RefreshFoldersList();
        }
    }

    private void OnRestoreTileHiddenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TileManageRow row })
            return;

        row.Config.HiddenItems.Clear();
        _saveConfig();
        _refreshSingleTile(row.Config);
        DataContext = this;
        RefreshFoldersList();
    }

    private void OnRemoveTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TileManageRow row })
            return;

        _removeTileByConfig(row.Config);
        DataContext = this;
        RefreshFoldersList();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing)
            return;

        _config.TileOpacity = Math.Clamp(e.NewValue / 100.0, 0.30, 1.0);
        UpdateLivePreviewTile();
        _applyAppearanceAll(false);
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void OnRadiusChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing)
            return;

        _config.TileCornerRadius = (int)Math.Round(e.NewValue);
        UpdateLivePreviewTile();
        _applyAppearanceAll(false);
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void OnTileSizeRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.TileSize = SizeCompactRb.IsChecked == true ? TileSizePreset.Compact
            : SizeLargeRb.IsChecked == true ? TileSizePreset.Large
            : TileSizePreset.Standard;

        UpdateLivePreviewTile();
        _saveConfig();
        _applyAppearanceAll(true);
        if (_config.SnapToGrid)
            _alignGridAll();
    }

    private void OnGridRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.PreviewGridSize = Grid3Rb.IsChecked == true ? 3 : 2;
        UpdateLivePreviewTile();
        _saveConfig();
        _applyAppearanceAll(true);
    }

    private void OnShowTileLabelsChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.ShowTileLabels = ShowTileLabelsChk.IsChecked == true;
        UpdateLivePreviewTile();
        _saveConfig();
        _applyAppearanceAll(true);
        if (_config.SnapToGrid)
            _alignGridAll();
    }

    private void OnColumnsRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.PopupColumns = Col5Rb.IsChecked == true ? 5
            : Col6Rb.IsChecked == true ? 6 : 4;
        _saveConfig();
        _applyAppearanceAll(true);
    }

    private void OnPaginationRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.UsePagination = ViewPagesRb.IsChecked == true;
        _saveConfig();
        _applyAppearanceAll(true);
    }

    private void OnPopupLabelRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.PopupLabelMode = LblFullRb.IsChecked == true ? PopupLabelMode.Full
            : LblHideRb.IsChecked == true ? PopupLabelMode.Hidden
            : PopupLabelMode.Truncated;
        _saveConfig();
        _applyAppearanceAll(true);
    }

    private void OnHotkeyRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        var preset = HkCtrlSpaceRb.IsChecked == true ? LaunchpadHotkeyPreset.CtrlSpace
            : HkCtrlShiftSpaceRb.IsChecked == true ? LaunchpadHotkeyPreset.CtrlShiftSpace
            : HkAltQRb.IsChecked == true ? LaunchpadHotkeyPreset.AltQ
            : LaunchpadHotkeyPreset.AltTilde;

        _config.LaunchpadHotkey = preset;
        bool ok = _applyHotkey(preset);
        HotkeyStatusText.Text = ok
            ? $"Комбинация «{DesktopLayerService.GetHotkeyLabel(preset)}» активна."
            : $"Не удалось занять «{DesktopLayerService.GetHotkeyLabel(preset)}» — возможно, занята другой программой.";
        _saveConfig();
    }

    private void OnSnapChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.SnapToGrid = SnapChk.IsChecked == true;
        _saveConfig();
        _applyAppearanceAll(false);
    }

    private void OnLockChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.LockPositions = LockChk.IsChecked == true;
        _saveConfig();
        _applyAppearanceAll(false);
    }

    private void OnSoundsChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.EnableSounds = SoundsChk.IsChecked == true;
        _saveConfig();
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _startupService.SetEnabled(AutostartChk.IsChecked == true);
    }

    private void OnDeleteRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _config.DeleteToRecycleBin = DelRecycleRb.IsChecked == true;
        _saveConfig();
        _applyAppearanceAll(false);
    }

    private static void AttachSmoothScrollRecursive(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                SmoothScrollHelper.Attach(sv);
            AttachSmoothScrollRecursive(child);
        }
    }

    private void OnOpenAppFolderClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppContext.BaseDirectory}\"")
        {
            UseShellExecute = true
        });
    }
}