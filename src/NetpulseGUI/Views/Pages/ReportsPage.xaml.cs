using System.Windows.Controls;
using NetpulseGUI.ViewModels;

namespace NetpulseGUI.Views.Pages;

public partial class ReportsPage : Page
{
    public ReportsPage(ReportsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => _ = vm.LoadAsync();
    }
}
