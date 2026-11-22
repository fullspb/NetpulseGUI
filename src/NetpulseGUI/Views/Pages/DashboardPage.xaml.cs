using System.Windows.Controls;
using NetpulseGUI.ViewModels;

namespace NetpulseGUI.Views.Pages;

public partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => vm.SetActive(true);
        Unloaded += (_, _) => vm.SetActive(false);
    }
}
