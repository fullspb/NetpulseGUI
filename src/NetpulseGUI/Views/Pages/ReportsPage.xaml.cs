using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetpulseGUI.ViewModels;

namespace NetpulseGUI.Views.Pages;

public partial class ReportsPage : Page
{
    private readonly ReportsViewModel _vm;

    public ReportsPage(ReportsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => _ = vm.LoadAsync();
    }

    private void OnAppDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppsGrid.SelectedItem is ProcessItem item) _vm.OpenAppCommand.Execute(item);
    }

    private void OnAppKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AppsGrid.SelectedItem is ProcessItem item)
        {
            _vm.OpenAppCommand.Execute(item);
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
