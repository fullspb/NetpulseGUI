using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NetpulseGUI.Core.Collection;
using NetpulseGUI.Core.Config;
using NetpulseGUI.Core.Formatting;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;
using NetpulseGUI.Services;
using NetpulseGUI.Settings;

namespace NetpulseGUI.ViewModels;

public sealed partial class AdapterOption : ObservableObject
{
    private readonly Action<AdapterOption> _changed;

    public AdapterOption(AdapterDetails d, bool included, Action<AdapterOption> changed)
    {
        Id = d.Id;
        Name = d.Name;
        Description = d.Description;
        Kind = d.Kind;
        IsUp = d.IsUp;
        Details = string.Join("  ·  ", new[]
        {
            KindName(d.Kind),
            d.IsUp ? Loc.T("Adapter_Up") : Loc.T("Adapter_Down"),
            d.SpeedBitsPerSec > 0 ? UnitFormatter.FormatSpeed(d.SpeedBitsPerSec / 8.0, SpeedUnits.Bits) : null,
            d.IPv4.Count > 0 ? string.Join(", ", d.IPv4) : null,
            string.IsNullOrEmpty(d.Mac) ? null : d.Mac
        }.Where(x => !string.IsNullOrEmpty(x)));
        _isIncluded = included;
        _changed = changed;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public AdapterKind Kind { get; }
    public bool IsUp { get; }
    public string Details { get; }

    [ObservableProperty] private bool _isIncluded;

    partial void OnIsIncludedChanged(bool value) => _changed(this);

    private static string KindName(AdapterKind k) => k switch
    {
        AdapterKind.Wireless => "Wi-Fi",
        AdapterKind.Virtual => Loc.T("Adapter_Virtual"),
        AdapterKind.Vpn => "VPN",
        AdapterKind.Tunnel => Loc.T("Adapter_Tunnel"),
        _ => "Ethernet"
    };
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _loading = true;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        var s = host.Settings.Current;
        MinimizeToTray = s.MinimizeToTrayOnClose;
        StartMinimized = s.StartMinimized;
        ThemeIndex = (int)s.Theme;
        LanguageIndex = s.Language == "en" ? 1 : 0;
        UnitsIndex = s.Units == SpeedUnits.Bits ? 0 : 1;
        RefreshIndex = s.UiRefreshSeconds switch { 2 => 1, 5 => 2, _ => 0 };
        IncludeVirtual = s.IncludeVirtualAdapters;
        IncludeVpn = s.IncludeVpnAdapters;
        ProcessTracking = s.ProcessTrackingEnabled;
        IsElevated = ElevationService.IsElevated;
        DatabasePath = AppPaths.DatabasePath;
        Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        _loading = false;
        Loc.Instance.LanguageChanged += RefreshAdapters;
    }

    public ObservableCollection<AdapterOption> Adapters { get; } = new();

    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private int _unitsIndex;
    [ObservableProperty] private int _refreshIndex;
    [ObservableProperty] private bool _includeVirtual;
    [ObservableProperty] private bool _includeVpn;
    [ObservableProperty] private bool _processTracking;
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _databasePath = "";
    [ObservableProperty] private string _databaseSize = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _etwStatus = "";
    [ObservableProperty] private bool _restartRequired;

    public void OnShown()
    {
        _loading = true;
        AutoStart = IsElevated && _host.AutoStart.IsEnabled();
        _loading = false;
        RefreshAdapters();
        RefreshDbInfo();
        EtwStatus = _host.Engine.ProcessTrackingActive
            ? Loc.T("Settings_EtwActive")
            : !IsElevated ? Loc.T("Settings_EtwNeedsAdmin")
            : !ProcessTracking ? Loc.T("Settings_EtwDisabled")
            : Loc.F("Settings_EtwError", _host.Engine.ProcessTrackingError ?? "?");
    }

    private void RefreshDbInfo()
    {
        try
        {
            long size = 0;
            foreach (var f in new[] { DatabasePath, DatabasePath + "-wal" })
                if (File.Exists(f)) size += new FileInfo(f).Length;
            DatabaseSize = Fmt.Bytes(size);
        }
        catch (IOException)
        {
            DatabaseSize = "—";
        }
    }

    public void RefreshAdapters()
    {
        var excluded = new HashSet<string>(_host.Settings.Current.ExcludedAdapterIds, StringComparer.OrdinalIgnoreCase);
        Adapters.Clear();
        foreach (var d in AdapterInventory.GetAll())
            Adapters.Add(new AdapterOption(d, !excluded.Contains(d.Id), OnAdapterToggled));
    }

    private void OnAdapterToggled(AdapterOption a)
    {
        _host.Settings.Update(s =>
        {
            s.ExcludedAdapterIds.RemoveAll(id => string.Equals(id, a.Id, StringComparison.OrdinalIgnoreCase));
            if (!a.IsIncluded) s.ExcludedAdapterIds.Add(a.Id);
        });
        _host.ApplyAdapterFilter();
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_loading) return;
        try
        {
            if (value) _host.AutoStart.Enable();
            else _host.AutoStart.Disable();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Не удалось изменить автозапуск");
            _host.MainVm.ShowError(Loc.T("Settings_AutoStartFailed"), ex.Message);
            _loading = true;
            AutoStart = !value;
            _loading = false;
        }
    }

    partial void OnMinimizeToTrayChanged(bool value) { if (!_loading) _host.Settings.Update(s => s.MinimizeToTrayOnClose = value); }
    partial void OnStartMinimizedChanged(bool value) { if (!_loading) _host.Settings.Update(s => s.StartMinimized = value); }

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading) return;
        var mode = (ThemePreference)Math.Clamp(value, 0, 2);
        _host.Settings.Update(s => s.Theme = mode);
        _host.Theme.Apply(mode, System.Windows.Application.Current.MainWindow);
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (_loading) return;
        var lang = value == 1 ? "en" : "ru";
        _host.Settings.Update(s => s.Language = lang);
        Loc.Instance.SetLanguage(lang);
        OnShown();
    }

    partial void OnUnitsIndexChanged(int value)
    {
        if (_loading) return;
        var units = value == 0 ? SpeedUnits.Bits : SpeedUnits.Bytes;
        _host.Settings.Update(s => s.Units = units);
        Fmt.Units = units;
        _host.Tray.Invalidate();
    }

    partial void OnRefreshIndexChanged(int value)
    {
        if (_loading) return;
        var secs = value switch { 1 => 2, 2 => 5, _ => 1 };
        _host.Settings.Update(s => s.UiRefreshSeconds = secs);
    }

    partial void OnIncludeVirtualChanged(bool value)
    {
        if (_loading) return;
        _host.Settings.Update(s => s.IncludeVirtualAdapters = value);
        _host.ApplyAdapterFilter();
    }

    partial void OnIncludeVpnChanged(bool value)
    {
        if (_loading) return;
        _host.Settings.Update(s => s.IncludeVpnAdapters = value);
        _host.ApplyAdapterFilter();
    }

    partial void OnProcessTrackingChanged(bool value)
    {
        if (_loading) return;
        _host.Settings.Update(s => s.ProcessTrackingEnabled = value);
        RestartRequired = true;
    }

    [RelayCommand]
    private void RestartElevated() => _host.MainVm.RestartElevatedCommand.Execute(null);

    [RelayCommand]
    private void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        Process.Start(new ProcessStartInfo(exe, ElevationService.RestartedArg) { UseShellExecute = true });
        _host.Exit();
    }

    [RelayCommand]
    private async Task ResetStatsAsync()
    {
        if (!await _host.MainVm.ConfirmAsync(Loc.T("Settings_ResetTitle"), Loc.T("Settings_ResetMessage"), Loc.T("Settings_ResetConfirm")))
            return;
        try
        {
            // Сначала копия на всякий случай.
            Directory.CreateDirectory(AppPaths.BackupsDir);
            await _host.Repository.BackupAsync(Path.Combine(AppPaths.BackupsDir, $"before-reset-{DateTime.Now:yyyyMMdd-HHmmss}.db"));
            _host.Engine.Recorder.Discard();
            await _host.Repository.ResetAllAsync();
            _host.Engine.Recorder.InvalidateCaches();
            _host.Live.ResetPeaks();
            await _host.Totals.RefreshAsync();
            RefreshDbInfo();
            _host.MainVm.ShowMessage(Loc.T("Settings_ResetDone"), Loc.T("Settings_ResetDoneDetails"));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка сброса статистики");
            _host.MainVm.ShowError(Loc.T("Common_Error"), ex.Message);
        }
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"netpulse-backup-{DateTime.Now:yyyy-MM-dd}.db",
            Filter = "SQLite (*.db)|*.db",
            AddExtension = true
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await _host.Engine.FlushAsync();
            await _host.Repository.BackupAsync(dlg.FileName);
            _host.MainVm.ShowMessage(Loc.T("Settings_BackupDone"), dlg.FileName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка резервного копирования");
            _host.MainVm.ShowError(Loc.T("Common_Error"), ex.Message);
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var dlg = new OpenFileDialog { Filter = "SQLite (*.db)|*.db|*.*|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (!await _host.MainVm.ConfirmAsync(Loc.T("Settings_RestoreTitle"), Loc.T("Settings_RestoreMessage"), Loc.T("Settings_RestoreConfirm")))
            return;
        try
        {
            await _host.Engine.FlushAsync();
            _host.Engine.Recorder.Discard();
            await _host.Repository.RestoreAsync(dlg.FileName);
            _host.Engine.Recorder.InvalidateCaches();
            await _host.Totals.RefreshAsync();
            RefreshDbInfo();
            _host.MainVm.ShowMessage(Loc.T("Settings_RestoreDone"), dlg.FileName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка восстановления");
            _host.MainVm.ShowError(Loc.T("Common_Error"), ex.Message);
        }
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });

    [RelayCommand]
    private void OpenLogs() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDir}\"") { UseShellExecute = true });
}
