using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskFolders.Native;
using DeskFolders.Services;

namespace DeskFolders.Views;

public sealed class LaunchpadSearchEntry
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string FolderName { get; init; } = string.Empty;
    public BitmapSource? Icon { get; init; }
}

public partial class LaunchpadBackdropWindow : Window
{
    private readonly Func<Task<List<LaunchpadSearchEntry>>> _loadItemsAsync;
    private readonly Action _raiseTilesCallback;
    private List<LaunchpadSearchEntry> _allEntries = new();
    private List<LaunchpadSearchEntry> _filteredEntries = new();
    private int _selectedIndex = -1;
    private HwndSource? _hwndSource;
    private readonly DispatcherTimer _focusGuardTimer;

    public event Action? DismissRequested;
    public event Action? ItemLaunched;

    public LaunchpadBackdropWindow(
        Func<Task<List<LaunchpadSearchEntry>>> loadItemsAsync,
        Action raiseTilesCallback)
    {
        InitializeComponent();
        SmoothScrollHelper.Attach(ResultsScrollViewer);
        _loadItemsAsync = loadItemsAsync;
        _raiseTilesCallback = raiseTilesCallback;

        Opacity = 0;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _focusGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _focusGuardTimer.Tick += (_, _) =>
        {
            _focusGuardTimer.Stop();
            ForceFocusSearchBox();
        };

        Loaded += async (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            _raiseTilesCallback();
            FocusSearchImmediately();
            _allEntries = await _loadItemsAsync();
        };

        Activated += (_, _) =>
        {
            _raiseTilesCallback();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        };

        MouseLeftButtonDown += (_, _) => DismissRequested?.Invoke();
        MouseRightButtonDown += (_, _) => DismissRequested?.Invoke();
        PreviewKeyDown += OnBackdropPreviewKeyDown;
        PreviewTextInput += OnBackdropPreviewTextInput;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Блокируем перехват фокуса в невидимое системное меню при отпускании Alt после Alt+~
        if (msg == NativeMethods.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == NativeMethods.SC_KEYMENU)
        {
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    public void FocusSearchImmediately()
    {
        ForceFocusSearchBox();
        _focusGuardTimer.Stop();
        _focusGuardTimer.Start();
    }

    private void ForceFocusSearchBox()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0;
            uint curThread = NativeMethods.GetCurrentThreadId();

            if (fgThread != 0 && fgThread != curThread)
            {
                NativeMethods.AttachThreadInput(curThread, fgThread, true);
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.AttachThreadInput(curThread, fgThread, false);
            }
            else
            {
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.BringWindowToTop(hwnd);
            }
        }

        Activate();
        Focus();
        FocusManager.SetFocusedElement(this, SearchBox);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        _raiseTilesCallback();
    }

    private void OnSearchAreaClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SearchBox.Focus();
    }

    private void OnBackdropPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (SearchBox.IsFocused || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
            return;

        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    private void OnBackdropPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = string.Empty;
                e.Handled = true;
                return;
            }
            DismissRequested?.Invoke();
        }
        else if (e.Key == Key.Down && _filteredEntries.Count > 0)
        {
            _selectedIndex = Math.Clamp(_selectedIndex + 1, 0, _filteredEntries.Count - 1);
            UpdateResultSelectionVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _filteredEntries.Count > 0)
        {
            _selectedIndex = Math.Clamp(_selectedIndex - 1, 0, _filteredEntries.Count - 1);
            UpdateResultSelectionVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _filteredEntries.Count > 0 && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            int idx = _selectedIndex >= 0 && _selectedIndex < _filteredEntries.Count ? _selectedIndex : 0;
            LaunchEntry(_filteredEntries[idx]);
            e.Handled = true;
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        PlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrEmpty(query))
        {
            _filteredEntries.Clear();
            _selectedIndex = -1;
            ResultsBorder.Visibility = Visibility.Collapsed;
            return;
        }

        _filteredEntries = _allEntries
            .Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                     || x.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(x => x.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Take(20)
            .ToList();

        _selectedIndex = _filteredEntries.Count > 0 ? 0 : -1;
        ResultsList.ItemsSource = _filteredEntries;
        NoMatchesText.Visibility = _filteredEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsBorder.Visibility = Visibility.Visible;

        UpdateResultSelectionVisual();
    }

    private void UpdateResultSelectionVisual()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            for (int i = 0; i < _filteredEntries.Count; i++)
            {
                if (ResultsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                    continue;

                if (VisualTreeHelper.GetChildrenCount(container) > 0 &&
                    VisualTreeHelper.GetChild(container, 0) is Border bd)
                {
                    if (i == _selectedIndex)
                    {
                        bd.Background = new SolidColorBrush(Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF));
                        container.BringIntoView();
                    }
                    else
                    {
                        bd.Background = Brushes.Transparent;
                    }
                }
            }
        });
    }

    private void OnResultMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border bd)
            bd.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    }

    private void OnResultMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border bd)
            bd.Background = Brushes.Transparent;
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is Border { Tag: LaunchpadSearchEntry entry })
            LaunchEntry(entry);
    }

    private void LaunchEntry(LaunchpadSearchEntry entry)
    {
        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(entry.FullPath) ?? string.Empty
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить:\n{ex.Message}", "DeskFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ItemLaunched?.Invoke();
    }

    public void FadeOutAndClose()
    {
        _focusGuardTimer.Stop();
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        anim.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, anim);
    }

    protected override void OnClosed(EventArgs e)
    {
        _focusGuardTimer.Stop();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        base.OnClosed(e);
    }
}