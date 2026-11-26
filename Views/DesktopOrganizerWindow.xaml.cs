using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using DeskFolders.Models;
using DeskFolders.Services;

namespace DeskFolders.Views;

public partial class DesktopOrganizerWindow : Window
{
    private readonly DesktopOrganizerService _organizer;
    private readonly Action<List<FolderTileConfig>, List<FolderTileConfig>> _onApplied;
    private List<DesktopSortPlanItem> _items = new();

    public IReadOnlyList<OrganizerTargetOption> TargetOptions { get; }

    public DesktopOrganizerWindow(
        DesktopOrganizerService organizer,
        Action<List<FolderTileConfig>, List<FolderTileConfig>> onApplied)
    {
        InitializeComponent();

        _organizer = organizer;
        _onApplied = onApplied;
        TargetOptions = _organizer.BuildTargetOptions();
        DataContext = this;

        SmoothScrollHelper.Attach(PlanScrollViewer);
        Loaded += async (_, _) => await LoadPlanAsync();
    }

    private async System.Threading.Tasks.Task LoadPlanAsync()
    {
        _items = await _organizer.BuildPlanAsync(TargetOptions.ToList(), includeCommonDesktop: true);
        PlanList.ItemsSource = _items;

        if (_items.Count == 0)
        {
            EmptyStateText.Visibility = Visibility.Visible;
            SubtitleText.Text = "На рабочем столе чисто — новых файлов для разбора нет.";
            UpdateApplyButtonText();
        }
        else
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            SubtitleText.Text = $"Найдено элементов: {_items.Count}. Папки создадутся автоматически в папке программы:";
            UpdateApplyButtonText();
        }
    }

    private void OnItemSelectionChanged(object sender, RoutedEventArgs e) => UpdateApplyButtonText();

    private void UpdateApplyButtonText()
    {
        int count = _items.Count(x => x.IsSelected && x.TargetOption != null);
        ApplyBtn.Content = count > 0 ? $"Разложить ({count})" : "Нечего переносить";
        ApplyBtn.IsEnabled = count > 0;
        ApplyBtn.Opacity = count > 0 ? 1.0 : 0.55;
    }

    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var (created, touched) = _organizer.ExecutePlan(_items);
        _onApplied(created, touched);
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}