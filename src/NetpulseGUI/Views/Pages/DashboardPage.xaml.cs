using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NetpulseGUI.ViewModels;

namespace NetpulseGUI.Views.Pages;

public partial class DashboardPage : Page
{
    private readonly DashboardViewModel _vm;

    public DashboardPage(DashboardViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        // Скорости меняются каждую секунду — включаем «живую» сортировку,
        // чтобы строки переставлялись без повторного клика по заголовку.
        if (CollectionViewSource.GetDefaultView(vm.TopProcesses) is ListCollectionView view)
        {
            view.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.TotalSpeed), ListSortDirection.Descending));
            view.IsLiveSorting = true;
        }

        Loaded += (_, _) => vm.SetActive(true);
        Unloaded += (_, _) => vm.SetActive(false);
    }

    private void OnTopDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TopGrid.SelectedItem is ProcessItem item) _vm.OpenProcessCommand.Execute(item);
    }

    private void OnTopKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TopGrid.SelectedItem is ProcessItem item)
        {
            _vm.OpenProcessCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>Таблица не прокручивается сама — колесо мыши отдаём прокрутке страницы.</summary>
    private void OnGridPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var parent = (UIElement)((FrameworkElement)sender).Parent;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }
}
