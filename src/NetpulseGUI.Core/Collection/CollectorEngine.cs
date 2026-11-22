using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Processes;
using NetpulseGUI.Core.Storage;

namespace NetpulseGUI.Core.Collection;

public sealed class CollectorOptions
{
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Период пакетной записи в БД (10–30 сек).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan CompactionInterval { get; set; } = TimeSpan.FromHours(1);
    public bool EnableProcessTracking { get; set; } = true;
    public string EtwSessionName { get; set; } = Config.AppPaths.EtwSessionName;
}

/// <summary>
/// Фоновый цикл сбора: раз в секунду опрашивает адаптеры и ETW, формирует <see cref="LiveSnapshot"/>,
/// буферизует данные и периодически пишет их в БД; раз в час запускает компакцию.
/// Работает в собственном фоновом потоке и никогда не обращается к UI.
/// </summary>
public sealed class CollectorEngine : IAsyncDisposable
{
    private readonly AdapterMonitor _adapters;
    private readonly TrafficRecorder _recorder;
    private readonly Compactor _compactor;
    private readonly CollectorOptions _options;
    private ProcessTrafficMonitor? _processes;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _paused;
    private volatile bool _resetRequested;
    private volatile LiveSnapshot _last = LiveSnapshot.Empty;

    public CollectorEngine(Database db, CollectorOptions? options = null, INetworkCounterSource? source = null)
    {
        _options = options ?? new CollectorOptions();
        _adapters = new AdapterMonitor(source);
        _recorder = new TrafficRecorder(db);
        _compactor = new Compactor(db);
    }

    /// <summary>Новый снимок (вызывается в фоновом потоке сбора).</summary>
    public event Action<LiveSnapshot>? SnapshotReady;

    /// <summary>Данные записаны в БД (фоновый поток).</summary>
    public event Action? Flushed;

    public event Action<string, Exception?>? Log;

    public LiveSnapshot Last => _last;
    public TrafficRecorder Recorder => _recorder;
    public bool ProcessTrackingActive => _processes?.IsRunning == true;
    public string? ProcessTrackingError => _processes?.LastError;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public AdapterFilter Filter
    {
        get => _adapters.Filter;
        set => _adapters.Filter = value;
    }

    /// <summary>Сбросить опорные значения счётчиков (выход из сна, смена сети).</summary>
    public void RequestBaselineReset() => _resetRequested = true;

    public void Start()
    {
        if (_loop != null) return;

        if (_options.EnableProcessTracking && ProcessTrafficMonitor.IsElevated())
        {
            _processes = new ProcessTrafficMonitor(_options.EtwSessionName);
            _processes.Log += (m, e) => Log?.Invoke(m, e);
            if (!_processes.Start())
                Log?.Invoke("Учёт по процессам недоступен: " + _processes.LastError, null);
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Factory.StartNew(() => RunAsync(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.TickInterval);
        var lastFlush = DateTime.UtcNow;
        var lastCompaction = DateTime.UtcNow - _options.CompactionInterval + TimeSpan.FromMinutes(2); // первая компакция через 2 минуты
        var lastEtwRetry = DateTime.UtcNow;

        _adapters.Tick(DateTime.UtcNow); // базовый замер
        _processes?.Drain();

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = DateTime.UtcNow;
                try
                {
                    if (_resetRequested)
                    {
                        _resetRequested = false;
                        _adapters.ResetBaseline();
                    }
                    Tick(now);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Ошибка цикла сбора", ex);
                }

                if (now - lastFlush >= _options.FlushInterval)
                {
                    lastFlush = now;
                    FlushSafe();
                }

                if (now - lastCompaction >= _options.CompactionInterval)
                {
                    lastCompaction = now;
                    try
                    {
                        var r = _compactor.Compact(now);
                        Log?.Invoke($"Компакция: raw={r.RawDeleted}, minute={r.MinuteDeleted}, appRaw={r.AppRawDeleted}, appMinute={r.AppMinuteDeleted}", null);
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke("Ошибка компакции", ex);
                    }
                }

                // ETW-сессия могла упасть (например, после сна) — перезапускаем раз в минуту.
                if (_processes != null && !_processes.IsRunning && now - lastEtwRetry > TimeSpan.FromMinutes(1))
                {
                    lastEtwRetry = now;
                    _processes.Start();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Tick(DateTime now)
    {
        var tick = _adapters.Tick(now);
        var apps = _processes?.IsRunning == true ? _processes.Drain() : new List<AppUsage>();

        if (tick.IsBaseline || _paused)
        {
            _last = new LiveSnapshot
            {
                TimestampUtc = now,
                Paused = _paused,
                ProcessTrackingAvailable = ProcessTrackingActive
            };
            SnapshotReady?.Invoke(_last);
            return;
        }

        var secs = Math.Max(tick.ElapsedSeconds, 0.001);
        _recorder.AddAdapterTick(now, tick.Deltas.Select(d => d));
        if (apps.Count > 0)
            _recorder.AddAppTick(now, apps.Select(a => new AppDelta(a.Name, a.Path, a.Rx, a.Tx)));

        var processes = apps
            .Where(a => a.Rx + a.Tx > 0)
            .OrderByDescending(a => a.Rx + a.Tx)
            .Select(a => new ProcessRate
            {
                Name = a.Name,
                Path = a.Path,
                InstanceCount = a.Pids.Count,
                DownBytesPerSec = a.Rx / secs,
                UpBytesPerSec = a.Tx / secs
            })
            .ToList();

        _last = new LiveSnapshot
        {
            TimestampUtc = now,
            IntervalSeconds = secs,
            DownBytesPerSec = tick.TotalRx / secs,
            UpBytesPerSec = tick.TotalTx / secs,
            Adapters = tick.Deltas.Select(d => new AdapterRate
            {
                Id = d.Id,
                Name = d.Name,
                DownBytesPerSec = d.Rx / secs,
                UpBytesPerSec = d.Tx / secs
            }).ToList(),
            Processes = processes,
            ProcessTrackingAvailable = ProcessTrackingActive
        };
        SnapshotReady?.Invoke(_last);
    }

    /// <summary>Немедленно записать буфер (например, перед построением отчёта или выходом).</summary>
    public Task FlushAsync() => Task.Run(FlushSafe);

    private void FlushSafe()
    {
        try
        {
            if (_recorder.Flush() > 0) Flushed?.Invoke();
        }
        catch (Exception ex)
        {
            Log?.Invoke("Ошибка записи в БД", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _processes?.Stop();
        FlushSafe();
        _cts?.Dispose();
    }
}
