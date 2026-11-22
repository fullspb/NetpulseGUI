using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using NetpulseGUI.Core.Config;
using NetpulseGUI.Core.Processes;
using NetpulseGUI.Services;
using Serilog;

namespace NetpulseGUI;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private EventWaitHandle? _exitSignal;
    private RegisteredWaitHandle? _exitWait;
    private AppHost? _host;

    private const string MutexName = @"Local\NetpulseGUI.SingleInstance";
    private const string ShowEventName = @"Local\NetpulseGUI.Show";
    private const string ExitEventName = @"Local\NetpulseGUI.Exit";

    /// <summary>Аргумент: попросить запущенный экземпляр корректно завершиться (запись буфера, остановка ETW).</summary>
    public const string ShutdownArg = "--shutdown";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var migrated = !e.Args.Contains(ShutdownArg) && AppPaths.MigrateLegacyDataDir();
        Directory.CreateDirectory(AppPaths.LogsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppPaths.LogsDir, "netpulse-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        if (migrated) Log.Information("Данные перенесены из старой папки NetPulse в {Dir}", AppPaths.DataDir);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Необработанное исключение");
            ProcessTrafficMonitor.CleanupStaleSession(AppPaths.EtwSessionName);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Ненаблюдаемое исключение задачи");
            args.SetObserved();
        };

        if (e.Args.Contains(ShutdownArg))
        {
            SignalExisting(ExitEventName);
            Log.Information("Передана команда завершения запущенному экземпляру");
            Log.CloseAndFlush();
            Shutdown();
            return;
        }

        if (!AcquireSingleInstance(e.Args.Contains(ElevationService.RestartedArg)))
        {
            Shutdown();
            return;
        }

        Log.Information("NetpulseGUI {Version} запущен. Администратор: {Elevated}",
            typeof(App).Assembly.GetName().Version, ElevationService.IsElevated);

        LiveCharts.Configure(config => config.AddSkiaSharp().AddDefaultMappers());

        try
        {
            _host = new AppHost(Dispatcher);
            _host.Theme.Apply(_host.Settings.Current.Theme, null);
            var minimized = e.Args.Contains(AutoStartService.MinimizedArg) || _host.Settings.Current.StartMinimized;
            _host.Start(minimized);

            // --page=processes|reports|limits|settings — открыть нужную страницу (удобно для ярлыков).
            var pageArg = e.Args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
            if (pageArg != null && AppHost.PageFromName(pageArg["--page=".Length..]) is { } page)
                _host.ShowMainWindow(page);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Ошибка запуска");
            System.Windows.MessageBox.Show("NetpulseGUI: " + ex.Message, "NetpulseGUI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _singleInstance = new Mutex(false, MutexName);
        bool acquired;
        try
        {
            // После перезапуска с правами администратора ждём, пока старый экземпляр завершится.
            acquired = _singleInstance.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            SignalExisting(ShowEventName);
            Log.Information("NetpulseGUI уже запущен — выходим");
            return false;
        }

        _showSignal = CreateSharedEvent(ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) =>
            Dispatcher.BeginInvoke(() => _host?.ShowMainWindow()), null, Timeout.Infinite, false);
        _exitSignal = CreateSharedEvent(ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) =>
            Dispatcher.BeginInvoke(() => _host?.Exit()), null, Timeout.Infinite, true);
        return true;
    }

    /// <summary>
    /// Событие, которое может установить и процесс без прав администратора
    /// (иначе DACL по умолчанию у процесса с повышенными правами запретит доступ).
    /// </summary>
    private static EventWaitHandle CreateSharedEvent(string name)
    {
        var security = new EventWaitHandleSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new EventWaitHandleAccessRule(user,
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
    }

    private static void SignalExisting(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var ev))
            {
                ev.Set();
                ev.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось передать сигнал запущенному экземпляру");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение в потоке UI");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _exitWait?.Unregister(null);
        _exitSignal?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstance?.Dispose();
        Log.Information("NetpulseGUI остановлен");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
