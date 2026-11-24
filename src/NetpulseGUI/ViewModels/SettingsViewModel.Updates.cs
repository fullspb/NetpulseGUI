using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetpulseGUI.Core.Updates;
using NetpulseGUI.Localization;
using NetpulseGUI.Services;

namespace NetpulseGUI.ViewModels;

/// <summary>Блок «О программе»: проверка, скачивание и установка обновлений с GitHub.</summary>
public sealed partial class SettingsViewModel
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateService _updates = new();
    private DispatcherTimer? _updateTimer;
    private UpdateInfo? _update;

    [ObservableProperty] private bool _checkUpdates;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateTitle = "";
    [ObservableProperty] private string _updateHint = "";
    [ObservableProperty] private bool _updateBusy;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _updateProgressVisible;
    [ObservableProperty] private string _updateStatus = "";

    public string RepositoryUrl => UpdateService.RepositoryUrl;

    /// <summary>Вызывается из AppHost.Start: первая проверка через 20 с, затем каждые 6 часов.</summary>
    public void StartUpdateChecks()
    {
        CheckUpdates = _host.Settings.Current.CheckUpdates;
        _updateTimer = new DispatcherTimer { Interval = FirstCheckDelay };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = CheckInterval;
            if (_host.Settings.Current.CheckUpdates && !UpdateBusy)
                await CheckForUpdatesCoreAsync(userInitiated: false);
        };
        _updateTimer.Start();
    }

    partial void OnCheckUpdatesChanged(bool value)
    {
        if (_loading || _updateTimer == null) return;
        _host.Settings.Update(s => s.CheckUpdates = value);
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckForUpdatesCoreAsync(userInitiated: true);

    private async Task CheckForUpdatesCoreAsync(bool userInitiated)
    {
        if (UpdateBusy) return;
        UpdateBusy = true;
        if (userInitiated) UpdateStatus = Loc.T("Update_Checking");
        try
        {
            var info = await _updates.CheckAsync();
            SetUpdate(info);
            UpdateStatus = info == null && userInitiated ? Loc.T("Update_UpToDate") : "";
            if (info != null && !userInitiated) NotifyOnce(info);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Проверка обновлений не удалась");
            UpdateStatus = userInitiated ? Loc.F("Update_CheckFailed", ex.Message) : "";
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    private void SetUpdate(UpdateInfo? info)
    {
        _update = info;
        UpdateAvailable = info != null;
        _host.MainVm.UpdateAvailable = info != null;
        if (info == null) return;
        UpdateTitle = Loc.F("Update_Available", info.Version.ToString(3));
        UpdateHint = Loc.F("Update_AvailableHint", Version);
    }

    /// <summary>Тост о новой версии — один раз на версию.</summary>
    private void NotifyOnce(UpdateInfo info)
    {
        var v = info.Version.ToString(3);
        if (_host.Settings.Current.NotifiedUpdateVersion == v) return;
        _host.Settings.Update(s => s.NotifiedUpdateVersion = v);
        _host.Notifications.Show(Loc.F("Update_ToastTitle", v), Loc.T("Update_ToastText"), "update");
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_update == null || UpdateBusy) return;
        var info = _update;
        UpdateBusy = true;
        UpdateProgressVisible = true;
        UpdateProgress = 0;
        UpdateStatus = Loc.F("Update_Downloading", "0%");
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p;
                UpdateStatus = Loc.F("Update_Downloading", $"{p:P0}");
            });
            var file = await _updates.DownloadAsync(info, progress);
            UpdateStatus = Loc.T("Update_Installing");
            await _host.Engine.FlushAsync();
            _updates.Apply(info, file);
            _host.Exit();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось установить обновление {Version}", info.Version);
            UpdateStatus = "";
            UpdateProgressVisible = false;
            _host.MainVm.ShowError(Loc.T("Update_Failed"), ex.Message);
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    [RelayCommand]
    private void OpenRepository() => UpdateService.OpenUrl(UpdateService.RepositoryUrl);

    [RelayCommand]
    private void OpenReleaseNotes() => UpdateService.OpenUrl(_update?.ReleaseUrl ?? UpdateService.RepositoryUrl + "/releases");
}
