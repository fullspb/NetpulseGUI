using System.Windows.Controls;
using NetpulseGUI.ViewModels;

namespace NetpulseGUI.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => vm.OnShown();
    }
}
