using System.Windows.Controls;
using NetpulseGUI.ViewModels;

namespace NetpulseGUI.Views.Pages;

public partial class LimitsPage : Page
{
    public LimitsPage(LimitsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => _ = vm.LoadAlertsAsync();
    }
}
