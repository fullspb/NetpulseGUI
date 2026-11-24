using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NetpulseGUI.Core.Collection;
using NetpulseGUI.Core.Config;
using NetpulseGUI.Core.Storage;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;
using NetpulseGUI.Services;
using NetpulseGUI.Settings;
using NetpulseGUI.ViewModels;
using NetpulseGUI.Views;
using NetpulseGUI.Views.Pages;
using Wpf.Ui.Abstractions;

namespace NetpulseGUI;

/// <summary>
/// Корень композиции: создаёт и связывает сервисы, страницы и модели представлений.
/// Сбор данных работает в фоне независимо от того, открыто ли главное окно.
/// </summary>
public sealed class AppHost : INavigationViewPageProvider
{
    public static AppHost Current { get; private set; } = null!;

    private readonly Dictionary<Type, Func<object>> _pageFactories;
    private readonly Dictionary<Type, object> _pages = new();
    private TrayFlyout? _flyout;
    private MainWindow? _mainWindow;
    private bool _exiting;

    public SettingsStore Settings { get; }
    public Database Database { get; }
    public TrafficRepository Repository { get; }
    public CollectorEngine Engine { get; }
    public LiveDataService Live { get; }
    public TotalsService Totals { get; }
    public IconCache Icons { get; } = new();
    public TrayService Tray { get; } = new();
    public NotificationService Notifications { get; }
    public LimitMonitor Limits { get; }
    public AutoStartService AutoStart { get; } = new();
    public ThemeService Theme { get; } = new();
    public ReportService Reports { get; }

    public DashboardViewModel DashboardVm { get; }
    public ProcessesViewModel ProcessesVm { get; }
    public ProcessDetailsViewModel ProcessDetailsVm { get; }
    public ReportsViewModel ReportsVm { get; }
    public LimitsViewModel LimitsVm { get; }
    public SettingsViewModel SettingsVm { get; }
    public FlyoutViewModel FlyoutVm { get; }
    public MainWindowViewModel MainVm { get; }

    public AppHost(Dispatcher dispatcher)
    {
        Current = this;
        Settings = new SettingsStore();
        Fmt.Units = Settings.Current.Units;
        Loc.Instance.SetLanguage(Settings.Current.Language);

        Database = new Database(AppPaths.DatabasePath);
        Database.EnsureCreated();
        Repository = new TrafficRepository(Database);

        Engine = new CollectorEngine(Database, new CollectorOptions
        {
            EnableProcessTracking = Settings.Current.ProcessTrackingEnabled,
            FlushInterval = TimeSpan.FromSeconds(15)
        });
        Engine.Log += (msg, ex) =>
        {
            if (ex == null) Serilog.Log.Information(msg);
            else Serilog.Log.Error(ex, msg);
        };
        ApplyAdapterFilter();

        Live = new LiveDataService(Engine, Settings, dispatcher);
        Totals = new TotalsService(Repository, Engine, Settings, dispatcher);
        Notifications = new NotificationService(Tray);
        Limits = new LimitMonitor(Settings, Notifications, Repository);
        Reports = new ReportService(Repository);

        DashboardVm = new DashboardViewModel(this);
        ProcessesVm = new ProcessesViewModel(this);
        ProcessDetailsVm = new ProcessDetailsViewModel(this);
        ReportsVm = new ReportsViewModel(this);
        LimitsVm = new LimitsViewModel(this);
        SettingsVm = new SettingsViewModel(this);
        FlyoutVm = new FlyoutViewModel(this);
        MainVm = new MainWindowViewModel(this);

        _pageFactories = new Dictionary<Type, Func<object>>
        {
            [typeof(DashboardPage)] = () => new DashboardPage(DashboardVm),
            [typeof(ProcessesPage)] = () => new ProcessesPage(ProcessesVm),
            [typeof(ProcessDetailsPage)] = () => new ProcessDetailsPage(ProcessDetailsVm),
            [typeof(ReportsPage)] = () => new ReportsPage(ReportsVm),
            [typeof(LimitsPage)] = () => new LimitsPage(LimitsVm),
            [typeof(SettingsPage)] = () => new SettingsPage(SettingsVm)
        };
    }

    public void Start(bool startMinimized)
    {
        Engine.Start();
        Totals.Start();
        Totals.Updated += t =>
        {
            Limits.Check(t);
            Tray.UpdateToolTip(Live.Current, t);
        };
        Live.Tick += s =>
        {
            Tray.Update(s.DownBytesPerSec, s.UpBytesPerSec, s.Paused);
            Tray.UpdateToolTip(s, Totals.Current);
        };
        Engine.Flushed += () => Application.Current?.Dispatcher.BeginInvoke(() => _ = Totals.RefreshAsync());

        Tray.OpenRequested += () => ShowMainWindow();
        Tray.FlyoutRequested += ToggleFlyout;
        Tray.TodayReportRequested += () =>
        {
            ShowMainWindow(typeof(ReportsPage));
            ReportsVm.ShowToday();
        };
        Tray.SettingsRequested += () => ShowMainWindow(typeof(SettingsPage));
        Tray.ExitRequested += Exit;
        Tray.PauseToggled += SetPaused;
        Tray.Create();

        Notifications.Initialize();
        Notifications.Activated += arg => Application.Current?.Dispatcher.BeginInvoke(() =>
            ShowMainWindow(arg.Contains("limits", StringComparison.Ordinal) ? typeof(LimitsPage)
                : arg.Contains("update", StringComparison.Ordinal) ? typeof(SettingsPage) : null));

        Task.Run(UpdateService.CleanupLeftovers);
        SettingsVm.StartUpdateChecks();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => Tray.Invalidate());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() => Tray.Invalidate());
        };
        SystemEvents.SessionEnding += (_, _) => Engine.FlushAsync().Wait(TimeSpan.FromSeconds(3));
        NetworkChange.NetworkAddressChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => SettingsVm.RefreshAdapters());

        if (ElevationService.IsElevated)
            Task.Run(AutoStart.RepairPathIfNeeded);

        if (!startMinimized) ShowMainWindow();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Serilog.Log.Information("Переход в сон — запись буфера");
                Engine.FlushAsync().Wait(TimeSpan.FromSeconds(3));
                break;
            case PowerModes.Resume:
                Serilog.Log.Information("Выход из сна — сброс опорных значений счётчиков");
                Engine.RequestBaselineReset();
                Application.Current?.Dispatcher.BeginInvoke(() => Tray.Invalidate());
                break;
        }
    }

    public void ApplyAdapterFilter()
    {
        var s = Settings.Current;
        Engine.Filter = AdapterFilter.Create(s.ExcludedAdapterIds, s.IncludeVirtualAdapters, s.IncludeVpnAdapters);
    }

    public void SetPaused(bool paused)
    {
        Engine.Paused = paused;
        Tray.SetPaused(paused);
        MainVm.IsPaused = paused;
        FlyoutVm.IsPaused = paused;
        Tray.Update(0, 0, paused, force: true);
    }

    // ---- Окна и навигация ----

    public object? GetPage(Type pageType)
    {
        if (_pages.TryGetValue(pageType, out var page)) return page;
        if (!_pageFactories.TryGetValue(pageType, out var factory)) return null;
        page = factory();
        _pages[pageType] = page;
        return page;
    }

    public void ShowMainWindow(Type? page = null)
    {
        _flyout?.Hide();
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(MainVm, this);
            _mainWindow.Closing += (_, e) =>
            {
                SaveWindowPlacement();
                if (!_exiting && Settings.Current.MinimizeToTrayOnClose)
                {
                    e.Cancel = true;
                    _mainWindow.Hide();
                    DashboardVm.SetActive(false);
                }
            };
            _mainWindow.Closed += (_, _) =>
            {
                _mainWindow = null;
                if (!_exiting) Exit();
            };
            Theme.Apply(Settings.Current.Theme, _mainWindow);
        }

        if (!_mainWindow.IsVisible) _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        DashboardVm.SetActive(true);
        if (page != null) _mainWindow.Navigate(page);
    }

    public void Navigate(Type page) => _mainWindow?.Navigate(page);

    public static Type? PageFromName(string name) => name.ToLowerInvariant() switch
    {
        "dashboard" => typeof(DashboardPage),
        "processes" => typeof(ProcessesPage),
        "reports" => typeof(ReportsPage),
        "limits" => typeof(LimitsPage),
        "settings" => typeof(SettingsPage),
        _ => null
    };

    private void SaveWindowPlacement()
    {
        if (_mainWindow == null) return;
        var w = _mainWindow;
        var s = Settings.Current;
        s.WindowMaximized = w.WindowState == WindowState.Maximized;
        var r = w.RestoreBounds;
        if (!r.IsEmpty && r.Width > 200 && r.Height > 200)
        {
            s.WindowLeft = r.Left;
            s.WindowTop = r.Top;
            s.WindowWidth = r.Width;
            s.WindowHeight = r.Height;
        }
        Settings.Save();
    }

    private void ToggleFlyout()
    {
        _flyout ??= new TrayFlyout(FlyoutVm);
        if (_flyout.IsVisible && (DateTime.UtcNow - _flyout.ShownAtUtc).TotalMilliseconds > 300)
        {
            _flyout.Hide();
            return;
        }
        if (_flyout.IsVisible) return;
        if ((DateTime.UtcNow - _flyout.HiddenAtUtc).TotalMilliseconds < 300) return; // клик по значку закрыл окно через Deactivated
        FlyoutVm.Refresh();
        _flyout.ShowNearTray();
    }

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        Serilog.Log.Information("Завершение работы");
        try
        {
            SaveWindowPlacement();
            Totals.Stop();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Tray.Dispose();
            _flyout?.Close();
            _mainWindow?.Close();
            Engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); // останавливает ETW-сессию и пишет буфер
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка при завершении");
        }
        Application.Current.Shutdown();
    }
}
