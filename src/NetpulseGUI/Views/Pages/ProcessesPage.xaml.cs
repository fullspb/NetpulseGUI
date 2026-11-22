using System.Windows.Controls;
using System.Windows.Input;
using NetpulseGUI.ViewModels;

namespace NetpulseGUI.Views.Pages;

public partial class ProcessesPage : Page
{
    private readonly ProcessesViewModel _vm;

    public ProcessesPage(ProcessesViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => vm.SetActive(true);
        Unloaded += (_, _) => vm.SetActive(false);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is ProcessItem item) _vm.OpenDetailsCommand.Execute(item);
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Grid.SelectedItem is ProcessItem item)
        {
            _vm.OpenDetailsCommand.Execute(item);
            e.Handled = true;
        }
    }
}
