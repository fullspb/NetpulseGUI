using System.Windows;
using NetpulseGUI.ViewModels;
using NetpulseGUI.Views.Pages;
using Wpf.Ui.Controls;

namespace NetpulseGUI.Views;

public partial class MainWindow : FluentWindow
{
    private readonly AppHost _host;
    private Type? _pendingPage;

    public MainWindow(MainWindowViewModel vm, AppHost host)
    {
        _host = host;
        DataContext = vm;
        InitializeComponent();

        var s = host.Settings.Current;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (s.WindowLeft is { } left && s.WindowTop is { } top && IsOnScreen(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        RootNavigation.SetPageProviderService(host);
        Loaded += (_, _) => Navigate(_pendingPage ?? typeof(DashboardPage));
    }

    public void Navigate(Type page)
    {
        if (!IsLoaded)
        {
            _pendingPage = page;
            return;
        }
        RootNavigation.Navigate(page);
    }

    /// <summary>Не восстанавливать окно за пределами экранов (монитор отключён).</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vr = vl + SystemParameters.VirtualScreenWidth;
        var vb = vt + SystemParameters.VirtualScreenHeight;
        return left + width * 0.3 > vl && left + width * 0.3 < vr && top >= vt - 10 && top + 40 < vb;
    }
}
