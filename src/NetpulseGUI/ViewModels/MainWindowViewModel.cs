using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetpulseGUI.Localization;
using NetpulseGUI.Services;
using Wpf.Ui.Controls;

namespace NetpulseGUI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppHost _host;
    private CancellationTokenSource? _infoCts;

    public MainWindowViewModel(AppHost host)
    {
        _host = host;
        ShowAdminBanner = !ElevationService.IsElevated;
    }

    [ObservableProperty] private bool _showAdminBanner;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _infoOpen;
    [ObservableProperty] private string _infoTitle = "";
    [ObservableProperty] private string _infoMessage = "";
    [ObservableProperty] private InfoBarSeverity _infoSeverity = InfoBarSeverity.Success;

    [RelayCommand]
    private void RestartElevated()
    {
        if (ElevationService.TryRestartElevated())
            _host.Exit();
        else
            ShowError(Loc.T("Admin_RestartCancelled"), Loc.T("Admin_RestartCancelledDetails"));
    }

    [RelayCommand]
    private void Resume() => _host.SetPaused(false);

    public void ShowMessage(string title, string message) => ShowInfo(title, message, InfoBarSeverity.Success);

    public void ShowError(string title, string message) => ShowInfo(title, message, InfoBarSeverity.Error);

    private async void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        _infoCts?.Cancel();
        var cts = _infoCts = new CancellationTokenSource();
        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        InfoOpen = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 12 : 6), cts.Token);
            InfoOpen = false;
        }
        catch (TaskCanceledException)
        {
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = Loc.T("Common_Cancel")
        };
        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
