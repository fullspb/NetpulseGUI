using System.Windows.Controls;
using NetpulseGUI.ViewModels;

namespace NetpulseGUI.Views.Pages;

public partial class ProcessDetailsPage : Page
{
    public ProcessDetailsPage(ProcessDetailsViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
