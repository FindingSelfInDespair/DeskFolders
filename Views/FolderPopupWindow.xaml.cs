using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskFolders.Models;
using DeskFolders.Services;
using Drawing = System.Drawing;
using Path = System.IO.Path;
using VBFileIO = Microsoft.VisualBasic.FileIO;
using WinForms = System.Windows.Forms;

namespace DeskFolders.Views;

public partial class FolderPopupWindow : Window, INotifyPropertyChanged
{
    private readonly FolderTileConfig _config;
    private readonly AppConfig _appConfig;
    private readonly FolderContentService _contentService;
    private readonly SoundService _soundService;
    private readonly FolderTileWindow _anchorTile;
    private readonly Action _saveConfigCallback;
    private readonly Action? _onItemLaunchedCallback;

    private readonly Stack<string> _folderHistory = new();
    private string _currentFolderPath;

    private List<FolderItemInfo> _allItems = new();
    private List<FolderItemInfo> _filteredItems = new();
    private readonly ObservableCollection<FolderItemInfo> _displayedItems = new();

    private bool _isClosing;
    private bool _isOpen;
    private bool _allowRealClose;
    private bool _isEditMode;
    private bool _isPinned;
    private bool _isRenaming;
    private bool _suppressDeactivateClose;
    private bool _dataDirty = true;

    private int _currentPage;
    private int _totalPages = 1;
    private int _selectedIndex = -1;

    private readonly DispatcherTimer _holdTimer;
    private FolderItemInfo? _pressedItem;
    private Point _pressPoint;
    private bool _isDraggingItem;
    private bool _orderChangedDuringDrag;
    private DateTime _lastReorderUtc = DateTime.MinValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Brush PrimaryTextBrush { get; private set; } = Brushes.White;
    public bool IsOpenOrShowing => _isOpen && !_isClosing;
    public Visibility EditBadgeVisibility => _isEditMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ItemLabelVisibility =>
        _appConfig.PopupLabelMode == PopupLabelMode.Hidden ? Visibility.Collapsed : Visibility.Visible;

    public double ItemLabelMaxHeight =>
        _appConfig.PopupLabelMode == PopupLabelMode.Full ? double.PositiveInfinity : 28;

    public TextTrimming ItemLabelTrimming =>
        _appConfig.PopupLabelMode == PopupLabelMode.Full ? TextTrimming.None : TextTrimming.CharacterEllipsis;

    public double ItemCardMinHeight =>
        _appConfig.PopupLabelMode == PopupLabelMode.Hidden ? 64 : 88;

    public Thickness IconStackMargin =>
        _appConfig.PopupLabelMode == PopupLabelMode.Hidden ? new Thickness(4, 10, 4, 6) : new Thickness(4, 7, 4, 0);

    public FolderPopupWindow(
        FolderTileConfig config,
        AppConfig appConfig,
        FolderContentService contentService,
        SoundService soundService,
        FolderTileWindow anchorTile,
        Action saveConfigCallback,
        Action? onItemLaunchedCallback = null)
    {
        InitializeComponent();

        _config = config;
        _currentFolderPath = config.FolderPath;
        _appConfig = appConfig;
        _contentService = contentService;
        _soundService = soundService;
        _anchorTile = anchorTile;
        _saveConfigCallback = saveConfigCallback;
        _onItemLaunchedCallback = onItemLaunchedCallback;

        DataContext = this;
        ItemsList.ItemsSource = _displayedItems;
        SmoothScrollHelper.Attach(PopupScrollViewer);

        ApplySettingsAndTheme();

        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (_pressedItem != null && Mouse.LeftButton == MouseButtonState.Pressed)
                BeginItemDrag(_pressedItem, Mouse.GetPosition(ContentHost));
        };

        ContentHost.PreviewMouseMove += OnHostMouseMove;
        ContentHost.PreviewMouseLeftButtonUp += OnHostMouseUp;
        ContentHost.LostMouseCapture += (_, _) =>
        {
            if (!_suppressDeactivateClose)
                CancelDragState();
        };

        Deactivated += (_, _) =>
        {
            if (!_isOpen || _suppressDeactivateClose || _isPinned)
                return;

            CommitInlineRename();
            CancelDragState();
            CloseSafely();
        };

        Closing += (_, e) =>
        {
            if (!_allowRealClose)
            {
                e.Cancel = true;
                CloseSafely();
            }
        };

        PreviewKeyDown += OnPopupPreviewKeyDown;
        PreviewTextInput += OnPopupPreviewTextInput;
        PreviewMouseWheel += OnPopupMouseWheel;

        DragEnter += OnPopupDragOver;
        DragOver += OnPopupDragOver;
        Drop += OnPopupDrop;
    }

    public void ApplySettingsAndTheme()
    {
        var palette = TileThemeService.Get(_config.TintName, _appConfig.TileOpacity);
        var brush = new SolidColorBrush(palette.PrimaryText);
        brush.Freeze();
        PrimaryTextBrush = brush;

        int cols = Math.Clamp(_appConfig.PopupColumns, 4, 6);
        Width = cols * 80 + 52 + 52;

        PopupScrollViewer.VerticalScrollBarVisibility = _appConfig.UsePagination
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;

        RootBorder.Background = palette.PopupBackground;
        RootBorder.BorderBrush = palette.BorderBrush;
        HeaderText.Foreground = PrimaryTextBrush;

        var secBrush = new SolidColorBrush(palette.SecondaryText);
        secBrush.Freeze();
        CountText.Foreground = secBrush;
        BackBtn.Foreground = secBrush;
        SortBtn.Foreground = secBrush;
        EditModeBtn.Foreground = secBrush;
        PinBtn.Foreground = secBrush;
        OpenExplorerBtn.Foreground = secBrush;

        TrashZoneLabel.Text = _appConfig.DeleteToRecycleBin
            ? "Перетащите сюда, чтобы удалить в Корзину"
            : "Перетащите сюда, чтобы скрыть из плитки";

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryTextBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemLabelVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemLabelMaxHeight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemLabelTrimming)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemCardMinHeight)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconStackMargin)));

        _dataDirty = true;
    }

    public void SyncFromTileCache(IReadOnlyList<FolderItemInfo> cachedItems)
    {
        if (!IsRootFolder)
            return;

        _allItems = cachedItems.ToList();
        _dataDirty = true;

        if (_isOpen && !_isClosing)
        {
            ApplyFilter();
            UpdateLayout();
        }
    }

    public void OpenInstant(IReadOnlyList<FolderItemInfo> cachedItems)
    {
        _isClosing = false;
        _isOpen = true;
        _isPinned = false;
        _isRenaming = false;
        _folderHistory.Clear();
        _currentFolderPath = _config.FolderPath;

        PinBtn.Background = Brushes.Transparent;
        PinBtn.ToolTip = "Закрепить окно папки открытым";

        if (_isEditMode)
            ToggleEditMode(false);

        SearchBox.Text = string.Empty;
        SearchBar.Visibility = Visibility.Collapsed;
        PopupScrollViewer.ScrollToVerticalOffset(0);

        if (_dataDirty || _allItems.Count != cachedItems.Count)
        {
            _allItems = cachedItems.ToList();
            BackBtn.Visibility = Visibility.Collapsed;
            HeaderText.Text = _config.Name;
            HeaderText.Cursor = Cursors.IBeam;
            ApplyFilter();
            _dataDirty = false;
        }
        else
        {
            HeaderText.Text = _config.Name;
            _selectedIndex = -1;
            UpdateKeyboardSelectionVisual();
        }

        // Позиционируем ДО показа и анимации (0 мс ожидания!)
        BeginAnimation(OpacityProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        Opacity = 0;
        RootScale.ScaleX = 0.88;
        RootScale.ScaleY = 0.88;

        if (!IsVisible)
            Show();

        UpdateLayout();
        PositionNearAnchor();
        Activate();
        PlayOpenAnimation();
    }

    public void HideImmediate()
    {
        _isClosing = false;
        _isOpen = false;
        _isPinned = false;
        _holdTimer.Stop();
        CancelDragState();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    public void ForceDestroy()
    {
        _allowRealClose = true;
        _holdTimer.Stop();
        Close();
    }

    private bool IsRootFolder => _folderHistory.Count == 0;

    private async Task LoadContentAsync()
    {
        BackBtn.Visibility = IsRootFolder ? Visibility.Collapsed : Visibility.Visible;
        HeaderText.Text = IsRootFolder ? _config.Name : new DirectoryInfo(_currentFolderPath).Name;
        HeaderText.Cursor = IsRootFolder ? Cursors.IBeam : Cursors.Arrow;

        _allItems = await _contentService.LoadItemsAsync(
            _currentFolderPath,
            64,
            hiddenItems: IsRootFolder ? _config.HiddenItems : null,
            sortMode: IsRootFolder ? _config.SortMode : FolderSortMode.Auto,
            customOrder: IsRootFolder ? _config.CustomOrder : null);

        ApplyFilter();
        UpdateLayout();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        _filteredItems = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        CountText.Text = _filteredItems.Count.ToString();
        EmptyText.Text = string.IsNullOrEmpty(query) ? "Папка пуста" : "Ничего не найдено";
        EmptyText.Visibility = _filteredItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _currentPage = 0;
        _selectedIndex = -1;
        RenderCurrentPage();
    }

    private void RenderCurrentPage()
    {
        _displayedItems.Clear();

        if (!_appConfig.UsePagination)
        {
            PaginationBar.Visibility = Visibility.Collapsed;
            foreach (var item in _filteredItems)
                _displayedItems.Add(item);
            UpdateKeyboardSelectionVisual();
            return;
        }

        int cols = Math.Clamp(_appConfig.PopupColumns, 4, 6);
        int pageSize = cols * 4;
        _totalPages = Math.Max(1, (int)Math.Ceiling(_filteredItems.Count / (double)pageSize));
        _currentPage = Math.Clamp(_currentPage, 0, _totalPages - 1);

        foreach (var item in _filteredItems.Skip(_currentPage * pageSize).Take(pageSize))
            _displayedItems.Add(item);

        PaginationBar.Children.Clear();
        if (_totalPages > 1 && !_isEditMode)
        {
            PaginationBar.Visibility = Visibility.Visible;
            for (int i = 0; i < _totalPages; i++)
            {
                int pageIndex = i;
                var dot = new Ellipse
                {
                    Width = i == _currentPage ? 7 : 5.5,
                    Height = i == _currentPage ? 7 : 5.5,
                    Margin = new Thickness(4, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    Fill = new SolidColorBrush(i == _currentPage
                        ? Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF))
                };
                dot.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    SwitchPage(pageIndex);
                };
                PaginationBar.Children.Add(dot);
            }
        }
        else
        {
            PaginationBar.Visibility = Visibility.Collapsed;
        }

        UpdateKeyboardSelectionVisual();
    }

    private void SwitchPage(int newPage)
    {
        int target = Math.Clamp(newPage, 0, _totalPages - 1);
        if (target == _currentPage)
            return;

        _currentPage = target;
        _selectedIndex = -1;
        RenderCurrentPage();

        ItemsList.Opacity = 0.35;
        ItemsList.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    private void OnPopupMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_appConfig.UsePagination || _totalPages <= 1)
            return;

        if (e.Delta < 0)
            SwitchPage(_currentPage + 1);
        else if (e.Delta > 0)
            SwitchPage(_currentPage - 1);

        e.Handled = true;
    }

    private void PositionNearAnchor()
    {
        var dpi = VisualTreeHelper.GetDpi(_anchorTile);
        var centerPx = new Drawing.Point(
            (int)((_anchorTile.Left + _anchorTile.Width / 2) * dpi.DpiScaleX),
            (int)((_anchorTile.Top + _anchorTile.Height / 2) * dpi.DpiScaleY));

        var workPx = WinForms.Screen.FromPoint(centerPx).WorkingArea;
        double workLeft = workPx.Left / dpi.DpiScaleX;
        double workTop = workPx.Top / dpi.DpiScaleY;
        double workRight = workPx.Right / dpi.DpiScaleX;
        double workBottom = workPx.Bottom / dpi.DpiScaleY;

        const double padX = 26;
        const double padTop = 20;
        const double padBottom = 28;

        double visibleW = Math.Max(100, ActualWidth - padX * 2);
        double visibleH = Math.Max(100, ActualHeight - padTop - padBottom);

        double anchorCenterX = _anchorTile.Left + _anchorTile.Width / 2;
        double desiredVisLeft = anchorCenterX - visibleW / 2;
        double desiredVisTop = _anchorTile.Top + _anchorTile.Height - 2;
        bool openedAbove = false;

        if (desiredVisTop + visibleH > workBottom - 8)
        {
            desiredVisTop = _anchorTile.Top + 4 - visibleH;
            openedAbove = true;
        }

        double clampedVisLeft = Math.Clamp(desiredVisLeft, workLeft + 8, Math.Max(workLeft + 8, workRight - visibleW - 8));
        double clampedVisTop = Math.Clamp(desiredVisTop, workTop + 8, Math.Max(workTop + 8, workBottom - visibleH - 8));

        Left = clampedVisLeft - padX;
        Top = clampedVisTop - padTop;

        double originX = Math.Clamp((anchorCenterX - clampedVisLeft) / Math.Max(1, visibleW), 0.1, 0.9);
        RootBorder.RenderTransformOrigin = new Point(originX, openedAbove ? 1.0 : 0.0);
    }

    private void PlayOpenAnimation()
    {
        var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = fadeEase });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(185)) { EasingFunction = spring });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(185)) { EasingFunction = spring });
    }

    private async void OnBackClick(object sender, RoutedEventArgs e) => await NavigateBackAsync();

    private async Task NavigateBackAsync()
    {
        if (_folderHistory.Count == 0)
            return;

        _soundService.PlayTick();
        _currentFolderPath = _folderHistory.Pop();
        SearchBox.Text = string.Empty;
        SearchBar.Visibility = Visibility.Collapsed;
        await LoadContentAsync();
        PositionNearAnchor();

        ItemsList.Opacity = 0.35;
        ItemsList.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    private void OnTogglePinClick(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        _soundService.PlayTick();
        PinBtn.Background = _isPinned
            ? new SolidColorBrush(Color.FromArgb(0x66, 0x0A, 0x84, 0xFF))
            : Brushes.Transparent;
        PinBtn.ToolTip = _isPinned ? "Открепить окно папки" : "Закрепить окно папки открытым";
    }

    private void OnHeaderTextClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsRootFolder)
            return;

        e.Handled = true;
        _isRenaming = true;
        HeaderText.Visibility = Visibility.Collapsed;
        HeaderEditBox.Text = _config.Name;
        HeaderEditBox.Visibility = Visibility.Visible;
        HeaderEditBox.Focus();
        HeaderEditBox.SelectAll();
    }

    private void OnHeaderEditKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitInlineRename();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineRename();
        }
    }

    private void OnHeaderEditLostFocus(object sender, RoutedEventArgs e) => CommitInlineRename();

    private void CommitInlineRename()
    {
        if (!_isRenaming)
            return;

        _isRenaming = false;
        string candidate = HeaderEditBox.Text.Trim();
        HeaderEditBox.Visibility = Visibility.Collapsed;
        HeaderText.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(candidate) && candidate != _config.Name)
        {
            HeaderText.Text = candidate;
            _anchorTile.UpdateTitle(candidate);
            _saveConfigCallback();
        }
    }

    private void CancelInlineRename()
    {
        if (!_isRenaming)
            return;

        _isRenaming = false;
        HeaderEditBox.Visibility = Visibility.Collapsed;
        HeaderText.Visibility = Visibility.Visible;
    }

    private async void OnPopupPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isRenaming)
            return;

        int cols = Math.Clamp(_appConfig.PopupColumns, 4, 6);

        if (e.Key == Key.Back && !SearchBox.IsFocused && !IsRootFolder)
        {
            e.Handled = true;
            await NavigateBackAsync();
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (SearchBar.Visibility == Visibility.Visible)
            {
                SearchBox.Text = string.Empty;
                SearchBar.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }

            if (_isDraggingItem)
            {
                CancelDragState();
                e.Handled = true;
                return;
            }

            if (_isEditMode)
            {
                ToggleEditMode(false);
                e.Handled = true;
                return;
            }

            _isPinned = false;
            CloseSafely();
            return;
        }

        if (_displayedItems.Count == 0)
            return;

        if (e.Key is Key.Right or Key.Left or Key.Down or Key.Up)
        {
            int delta = e.Key switch
            {
                Key.Right => 1,
                Key.Left => -1,
                Key.Down => cols,
                Key.Up => -cols,
                _ => 0
            };

            _selectedIndex = _selectedIndex < 0
                ? 0
                : Math.Clamp(_selectedIndex + delta, 0, _displayedItems.Count - 1);

            UpdateKeyboardSelectionVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            int idx = _selectedIndex >= 0 && _selectedIndex < _displayedItems.Count ? _selectedIndex : 0;
            if (SearchBar.Visibility == Visibility.Visible || _selectedIndex >= 0)
            {
                bool keepOpen = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                await OpenOrLaunchItemAsync(_displayedItems[idx], runAsAdmin: false, keepOpen: keepOpen);
                e.Handled = true;
            }
        }
    }

    private void UpdateKeyboardSelectionVisual()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            for (int i = 0; i < _displayedItems.Count; i++)
            {
                if (ItemsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                    continue;

                if (FindDescendant<Border>(container) is not Border card)
                    continue;

                if (i == _selectedIndex)
                {
                    card.Background = new SolidColorBrush(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF));
                    card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
                    card.BorderThickness = new Thickness(1);
                    container.BringIntoView();
                }
                else
                {
                    card.Background = Brushes.Transparent;
                    card.BorderThickness = new Thickness(0);
                }
            }
        });
    }

    private void OnPopupPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_isRenaming || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || SearchBox.IsFocused)
            return;

        if (SearchBar.Visibility != Visibility.Visible)
        {
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.Text = e.Text;
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(SearchBox.Text))
            SearchBar.Visibility = Visibility.Collapsed;

        ApplyFilter();
    }

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenuStyle"),
            PlacementTarget = SortBtn,
            Placement = PlacementMode.Bottom
        };

        var itemStyle = (Style)FindResource("DarkMenuItemStyle");
        var sortModes = new (FolderSortMode Mode, string Label)[]
        {
            (FolderSortMode.Auto, "Авто (программы сверху)"),
            (FolderSortMode.NameAsc, "По имени (А → Я)"),
            (FolderSortMode.DateNewest, "Сначала новые"),
            (FolderSortMode.Custom, "Ручной порядок")
        };

        _suppressDeactivateClose = true;
        menu.Closed += (_, _) => _suppressDeactivateClose = false;

        foreach (var (mode, label) in sortModes)
        {
            string prefix = _config.SortMode == mode ? "✓  " : "     ";
            var mi = new MenuItem { Header = prefix + label, Style = itemStyle };
            mi.Click += async (_, _) =>
            {
                _config.SortMode = mode;
                _saveConfigCallback();
                await LoadContentAsync();
                await _anchorTile.RefreshPreviewAsync();
            };
            menu.Items.Add(mi);
        }

        menu.IsOpen = true;
    }

    private void OnToggleEditModeClick(object sender, RoutedEventArgs e) => ToggleEditMode(!_isEditMode);

    private void ToggleEditMode(bool enabled)
    {
        _isEditMode = enabled;
        TrashDropZone.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        EditModeBtn.Background = enabled
            ? new SolidColorBrush(Color.FromArgb(0x55, 0x0A, 0x84, 0xFF))
            : Brushes.Transparent;

        RenderCurrentPage();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditBadgeVisibility)));
    }

    private void OnOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_currentFolderPath))
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_currentFolderPath}\"")
        {
            UseShellExecute = true
        });
        if (!_isPinned)
            CloseSafely();
    }

    private void OnItemMouseEnter(object sender, MouseEventArgs e)
    {
        if (_isDraggingItem || sender is not Border card)
            return;

        card.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        AnimateIconScale(card, 1.07, 120);
    }

    private void OnItemMouseLeave(object sender, MouseEventArgs e)
    {
        if (_isDraggingItem || sender is not Border card)
            return;

        card.Background = Brushes.Transparent;
        AnimateIconScale(card, 1.0, 130);
    }

    private static void AnimateIconScale(Border card, double toScale, int ms)
    {
        if (FindDescendant<Image>(card) is not Image img)
            return;

        if (img.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            st = new ScaleTransform(1, 1);
            img.RenderTransform = st;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(ms);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(toScale, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(toScale, dur) { EasingFunction = ease });
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FolderItemInfo item })
            return;

        _pressedItem = item;
        _pressPoint = e.GetPosition(ContentHost);
        _isDraggingItem = false;
        _orderChangedDuringDrag = false;

        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingItem || _pressedItem == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (_isEditMode)
        {
            Point pos = e.GetPosition(ContentHost);
            if (Math.Abs(pos.X - _pressPoint.X) > 6 || Math.Abs(pos.Y - _pressPoint.Y) > 6)
            {
                _holdTimer.Stop();
                BeginItemDrag(_pressedItem, pos);
            }
        }
    }

    private async void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingItem)
            return;

        _holdTimer.Stop();
        var item = _pressedItem;
        _pressedItem = null;

        if (item != null && !_isEditMode)
        {
            bool keepOpen = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            await OpenOrLaunchItemAsync(item, runAsAdmin: false, keepOpen: keepOpen);
        }
    }

    private void BeginItemDrag(FolderItemInfo item, Point pos)
    {
        if (_isDraggingItem)
            return;

        _isDraggingItem = true;
        if (!_isEditMode)
            ToggleEditMode(true);

        SetDraggedCardOpacity(item, 0.25);
        ContentHost.CaptureMouse();
        DragGhostImage.Source = item.Icon;
        DragGhostImage.Visibility = Visibility.Visible;
        UpdateLayout();
        UpdateGhostPosition(pos);
    }

    private async void OnHostMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingItem || _pressedItem == null)
            return;

        Point posInCard = e.GetPosition(RootBorder);
        bool outsidePopup =
            posInCard.X < -6 ||
            posInCard.Y < -6 ||
            posInCard.X > RootBorder.ActualWidth + 6 ||
            posInCard.Y > RootBorder.ActualHeight + 6;

        if (outsidePopup)
        {
            var draggedItem = _pressedItem;
            await StartExternalOleDragAsync(draggedItem);
            return;
        }

        Point posInHost = e.GetPosition(ContentHost);
        UpdateGhostPosition(posInHost);

        if (IsRootFolder && string.IsNullOrEmpty(SearchBox.Text) && !IsPointOverTrashZone(posInHost))
            TryLiveReorderAt(posInHost);
    }

    private void TryLiveReorderAt(Point posInContentHost)
    {
        if (_pressedItem == null)
            return;

        if ((DateTime.UtcNow - _lastReorderUtc).TotalMilliseconds < 70)
            return;

        for (int i = 0; i < _displayedItems.Count; i++)
        {
            if (ReferenceEquals(_displayedItems[i], _pressedItem))
                continue;

            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            Point topLeft = container.TranslatePoint(new Point(0, 0), ContentHost);
            var rect = new Rect(topLeft.X, topLeft.Y, container.ActualWidth, container.ActualHeight);
            if (!rect.Contains(posInContentHost))
                continue;

            int oldDispIdx = _displayedItems.IndexOf(_pressedItem);
            if (oldDispIdx < 0 || oldDispIdx == i)
                break;

            var targetItem = _displayedItems[i];
            _displayedItems.Move(oldDispIdx, i);

            int oldAllIdx = _allItems.IndexOf(_pressedItem);
            int targetAllIdx = _allItems.IndexOf(targetItem);
            if (oldAllIdx >= 0 && targetAllIdx >= 0)
            {
                _allItems.RemoveAt(oldAllIdx);
                _allItems.Insert(targetAllIdx, _pressedItem);
            }

            _config.SortMode = FolderSortMode.Custom;
            _config.CustomOrder = _allItems.Select(x => Path.GetFileName(x.FullPath)).ToList();
            _orderChangedDuringDrag = true;
            _lastReorderUtc = DateTime.UtcNow;
            _soundService.PlayTick();

            UpdateLayout();
            SetDraggedCardOpacity(_pressedItem, 0.25);
            break;
        }
    }

    private void SetDraggedCardOpacity(FolderItemInfo? target, double opacity)
    {
        for (int i = 0; i < _displayedItems.Count; i++)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                bool match = target != null && ReferenceEquals(_displayedItems[i], target);
                container.Opacity = match ? opacity : 1.0;
            }
        }
    }

    private async Task StartExternalOleDragAsync(FolderItemInfo item)
    {
        _suppressDeactivateClose = true;
        CancelDragState();

        var dataObj = new DataObject();
        dataObj.SetData(DataFormats.FileDrop, new[] { item.FullPath });
        dataObj.SetData("DeskFolders_SourceFolder", _currentFolderPath);

        try
        {
            var result = DragDrop.DoDragDrop(this, dataObj, DragDropEffects.Move | DragDropEffects.Copy);
            if (result != DragDropEffects.None)
            {
                _soundService.PlaySnap();
                await Task.Delay(120);
                await LoadContentAsync();
                await _anchorTile.RefreshPreviewAsync();
                if (!_isPinned)
                    CloseSafely();
                return;
            }
        }
        finally
        {
            _suppressDeactivateClose = false;
        }

        if (!IsActive && !_isPinned)
            CloseSafely();
    }

    private async void OnHostMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingItem)
            return;

        e.Handled = true;
        Point pos = e.GetPosition(ContentHost);
        bool droppedInTrash = IsPointOverTrashZone(pos);
        var itemToRemove = _pressedItem;
        bool needSaveOrder = _orderChangedDuringDrag;

        CancelDragState();

        if (droppedInTrash && itemToRemove != null)
        {
            await RemoveItemAsync(itemToRemove);
        }
        else if (needSaveOrder)
        {
            _saveConfigCallback();
            await _anchorTile.RefreshPreviewAsync();
        }
    }

    private void CancelDragState()
    {
        _holdTimer.Stop();
        _isDraggingItem = false;
        _pressedItem = null;
        _orderChangedDuringDrag = false;
        SetDraggedCardOpacity(null, 1.0);
        DragGhostImage.Visibility = Visibility.Collapsed;
        TrashDropZone.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0x45, 0x3A));

        if (ContentHost.IsMouseCaptured)
            ContentHost.ReleaseMouseCapture();
    }

    private void UpdateGhostPosition(Point pos)
    {
        Canvas.SetLeft(DragGhostImage, pos.X - DragGhostImage.Width / 2);
        Canvas.SetTop(DragGhostImage, pos.Y - DragGhostImage.Height / 2);

        bool overTrash = IsPointOverTrashZone(pos);
        TrashDropZone.Background = new SolidColorBrush(
            overTrash ? Color.FromArgb(0x78, 0xFF, 0x45, 0x3A) : Color.FromArgb(0x28, 0xFF, 0x45, 0x3A));
    }

    private bool IsPointOverTrashZone(Point posInContentHost)
    {
        if (TrashDropZone.Visibility != Visibility.Visible)
            return false;

        Point topLeft = TrashDropZone.TranslatePoint(new Point(0, 0), ContentHost);
        var rect = new Rect(topLeft.X, topLeft.Y, TrashDropZone.ActualWidth, TrashDropZone.ActualHeight);
        rect.Inflate(8, 8);
        return rect.Contains(posInContentHost);
    }

    private async void OnDeleteBadgeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _holdTimer.Stop();
        _pressedItem = null;

        if (sender is FrameworkElement { DataContext: FolderItemInfo item })
            await RemoveItemAsync(item);
    }

    private FolderItemInfo? GetItemFromContextMenuSender(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { Tag: FolderItemInfo item } } })
            return item;
        return null;
    }

    private async void OnCtxRunClick(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenuSender(sender) is FolderItemInfo item)
            await OpenOrLaunchItemAsync(item, runAsAdmin: false, keepOpen: false);
    }

    private void OnCtxRunAdminClick(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenuSender(sender) is FolderItemInfo item)
            LaunchExternalItem(item, runAsAdmin: true, keepOpen: false);
    }

    private async void OnCtxRenameClick(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenuSender(sender) is not FolderItemInfo item)
            return;

        _suppressDeactivateClose = true;
        try
        {
            var dlg = new RenameDialog(item.Name, "Переименовать элемент");
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.NewName))
                return;

            string cleanName = dlg.NewName.Trim();
            foreach (char ch in Path.GetInvalidFileNameChars())
                cleanName = cleanName.Replace(ch.ToString(), string.Empty);

            if (string.IsNullOrWhiteSpace(cleanName) || cleanName == item.Name)
                return;

            string? parentDir = Path.GetDirectoryName(item.FullPath);
            if (string.IsNullOrWhiteSpace(parentDir))
                return;

            string oldFileName = Path.GetFileName(item.FullPath);
            string ext = item.IsDirectory ? string.Empty : Path.GetExtension(item.FullPath);
            string newFileName = item.IsDirectory
                ? cleanName
                : (cleanName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? cleanName : cleanName + ext);

            string newPath = Path.Combine(parentDir, newFileName);
            if (string.Equals(item.FullPath, newPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (File.Exists(newPath) || Directory.Exists(newPath))
            {
                MessageBox.Show("Элемент с таким именем уже существует.", "DeskFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (item.IsDirectory && Directory.Exists(item.FullPath))
                Directory.Move(item.FullPath, newPath);
            else if (File.Exists(item.FullPath))
                File.Move(item.FullPath, newPath);

            if (IsRootFolder && _config.SortMode == FolderSortMode.Custom)
            {
                int idx = _config.CustomOrder.FindIndex(x => string.Equals(x, oldFileName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    _config.CustomOrder[idx] = newFileName;
                _saveConfigCallback();
            }

            _soundService.PlayTick();
            await LoadContentAsync();
            await _anchorTile.RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось переименовать:\n{ex.Message}", "DeskFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _suppressDeactivateClose = false;
        }
    }

    private void OnCtxOpenLocationClick(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenuSender(sender) is FolderItemInfo item &&
            (File.Exists(item.FullPath) || Directory.Exists(item.FullPath)))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\"")
            {
                UseShellExecute = true
            });
            if (!_isPinned)
                CloseSafely();
        }
    }

    private async void OnCtxDeleteClick(object sender, RoutedEventArgs e)
    {
        if (GetItemFromContextMenuSender(sender) is FolderItemInfo item)
            await RemoveItemAsync(item);
    }

    private async Task OpenOrLaunchItemAsync(FolderItemInfo item, bool runAsAdmin, bool keepOpen)
    {
        if (item.IsDirectory && !runAsAdmin && Directory.Exists(item.FullPath))
        {
            _soundService.PlayTick();
            _folderHistory.Push(_currentFolderPath);
            _currentFolderPath = item.FullPath;
            SearchBox.Text = string.Empty;
            SearchBar.Visibility = Visibility.Collapsed;
            await LoadContentAsync();
            PositionNearAnchor();

            ItemsList.Opacity = 0.35;
            ItemsList.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            return;
        }

        LaunchExternalItem(item, runAsAdmin, keepOpen);
    }

    private void LaunchExternalItem(FolderItemInfo item, bool runAsAdmin, bool keepOpen)
    {
        _soundService.PlayTick();
        try
        {
            var psi = new ProcessStartInfo(item.FullPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(item.FullPath) ?? _currentFolderPath
            };
            if (runAsAdmin)
                psi.Verb = "runas";

            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть элемент:\n{ex.Message}", "DeskFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (!keepOpen && !_isPinned)
        {
            _onItemLaunchedCallback?.Invoke();
            CloseSafely();
        }
    }

    private async Task RemoveItemAsync(FolderItemInfo item)
    {
        _soundService.PlaySnap();
        try
        {
            if (_appConfig.DeleteToRecycleBin || !IsRootFolder)
            {
                if (item.IsDirectory && Directory.Exists(item.FullPath))
                    VBFileIO.FileSystem.DeleteDirectory(item.FullPath, VBFileIO.UIOption.OnlyErrorDialogs, VBFileIO.RecycleOption.SendToRecycleBin);
                else if (File.Exists(item.FullPath))
                    VBFileIO.FileSystem.DeleteFile(item.FullPath, VBFileIO.UIOption.OnlyErrorDialogs, VBFileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                string fileName = Path.GetFileName(item.FullPath);
                if (!_config.HiddenItems.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    _config.HiddenItems.Add(fileName);
                _saveConfigCallback();
            }

            await LoadContentAsync();
            await _anchorTile.RefreshPreviewAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось удалить элемент:\n{ex.Message}", "DeskFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPopupDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DeskFolders_SourceFolder"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPopupDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        foreach (string path in paths)
        {
            if (File.Exists(path))
                ShortcutCreator.HandleDroppedFile(path, _currentFolderPath, (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey);
        }

        _soundService.PlaySnap();
        await LoadContentAsync();
        await _anchorTile.RefreshPreviewAsync();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                return typed;
            var res = FindDescendant<T>(child);
            if (res != null)
                return res;
        }
        return null;
    }

    public void CloseSafely()
    {
        if (!_isOpen || _isClosing)
            return;

        _isClosing = true;
        _holdTimer.Stop();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(125);

        var fade = new DoubleAnimation(0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (_isClosing)
            {
                _isOpen = false;
                _isClosing = false;
                Hide();
            }
        };

        BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, dur) { EasingFunction = ease });
    }
}