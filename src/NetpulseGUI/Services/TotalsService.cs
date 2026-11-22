using System.Windows.Threading;
using NetpulseGUI.Core.Collection;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Storage;
using NetpulseGUI.Core.Time;
using NetpulseGUI.Settings;

namespace NetpulseGUI.Services;

public sealed record TotalsInfo(
    TrafficTotals Today,
    TrafficTotals Period,
    TrafficTotals AllTime,
    BillingPeriod BillingPeriod,
    long PeriodForecast);

/// <summary>
/// Итоги «Сегодня / Месяц / Всего». Запрашиваются из БД в фоне каждые несколько секунд
/// и дополняются ещё не записанным буфером, чтобы цифры не «прыгали» в момент записи.
/// </summary>
public sealed class TotalsService
{
    private readonly TrafficRepository _repo;
    private readonly CollectorEngine _engine;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private int _busy;

    public TotalsService(TrafficRepository repo, CollectorEngine engine, SettingsStore settings, Dispatcher dispatcher)
    {
        _repo = repo;
        _engine = engine;
        _settings = settings;
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => _ = RefreshAsync(), dispatcher);
    }

    public TotalsInfo Current { get; private set; } = new(default, default, default, BillingPeriod.Current(1), 0);

    /// <summary>Итоги обновлены (поток UI).</summary>
    public event Action<TotalsInfo>? Updated;

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var startDay = _settings.Current.BillingStartDay;
            var info = await Task.Run(async () =>
            {
                var today = TimeBuckets.Today();
                var period = BillingPeriod.For(today, startDay);
                var pending = _engine.Recorder.PendingTotals;
                var t = await _repo.GetTotalsAsync(today, today).ConfigureAwait(false) + pending;
                var p = await _repo.GetTotalsAsync(period.Start, period.LastDay).ConfigureAwait(false) + pending;
                var all = await _repo.GetAllTimeTotalsAsync().ConfigureAwait(false) + pending;
                var used = _settings.Current.LimitCountsUpload ? p.Total : p.Rx;
                return new TotalsInfo(t, p, all, period, period.Forecast(used, DateTime.Now));
            }).ConfigureAwait(true);

            Current = info;
            Updated?.Invoke(info);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось обновить итоги");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Stop() => _timer.Stop();
}
