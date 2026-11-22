using System.Windows.Threading;
using NetpulseGUI.Core.Collection;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Settings;

namespace NetpulseGUI.Services;

/// <summary>
/// Мост между фоновым сбором и UI: хранит историю скоростей за последний час (кольцевой буфер),
/// историю приложений для спарклайнов и раз в N секунд передаёт свежий снимок в поток UI.
/// </summary>
public sealed class LiveDataService
{
    public const int HistorySeconds = 3600;
    public const int SparkPoints = 60;

    private readonly CollectorEngine _engine;
    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();
    private readonly double[] _down = new double[HistorySeconds];
    private readonly double[] _up = new double[HistorySeconds];
    private int _head;
    private int _count;
    private readonly Dictionary<string, double[]> _spark = new(StringComparer.OrdinalIgnoreCase);
    private int _sparkHead;
    private int _tickCounter;
    private int _uiPending;

    public LiveDataService(CollectorEngine engine, SettingsStore settings, Dispatcher dispatcher)
    {
        _engine = engine;
        _settings = settings;
        _dispatcher = dispatcher;
        _engine.SnapshotReady += OnSnapshot;
    }

    /// <summary>Свежий снимок в потоке UI (с учётом интервала обновления интерфейса).</summary>
    public event Action<LiveSnapshot>? Tick;

    public LiveSnapshot Current { get; private set; } = LiveSnapshot.Empty;
    public double PeakDown { get; private set; }
    public double PeakUp { get; private set; }

    private void OnSnapshot(LiveSnapshot s)
    {
        lock (_sync)
        {
            _down[_head] = s.DownBytesPerSec;
            _up[_head] = s.UpBytesPerSec;
            _head = (_head + 1) % HistorySeconds;
            if (_count < HistorySeconds) _count++;
            if (s.DownBytesPerSec > PeakDown) PeakDown = s.DownBytesPerSec;
            if (s.UpBytesPerSec > PeakUp) PeakUp = s.UpBytesPerSec;

            // Спарклайны: по точке в секунду для каждого активного приложения.
            foreach (var arr in _spark.Values) arr[_sparkHead] = 0;
            foreach (var p in s.Processes)
            {
                if (!_spark.TryGetValue(p.Key, out var arr))
                {
                    if (_spark.Count > 2000) PruneSparks();
                    arr = new double[SparkPoints];
                    _spark[p.Key] = arr;
                }
                arr[_sparkHead] = p.TotalBytesPerSec;
            }
            _sparkHead = (_sparkHead + 1) % SparkPoints;
            if (_sparkHead == 0) PruneSparks();
        }

        var every = Math.Clamp(_settings.Current.UiRefreshSeconds, 1, 5);
        if (++_tickCounter % every != 0) return;
        if (Interlocked.Exchange(ref _uiPending, 1) == 1) return; // UI ещё не обработал предыдущий тик

        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _uiPending, 0);
            Current = s;
            Tick?.Invoke(s);
        });
    }

    private void PruneSparks()
    {
        foreach (var key in _spark.Where(kv => kv.Value.All(v => v == 0)).Select(kv => kv.Key).ToList())
            _spark.Remove(key);
    }

    /// <summary>Последние <paramref name="seconds"/> секунд истории (старые → новые); недостающее дополняется нулями слева.</summary>
    public void CopyHistory(int seconds, double[] down, double[] up)
    {
        seconds = Math.Min(seconds, HistorySeconds);
        lock (_sync)
        {
            for (var i = 0; i < seconds; i++)
            {
                var age = seconds - 1 - i; // 0 — самая свежая
                if (age >= _count)
                {
                    down[i] = 0;
                    up[i] = 0;
                    continue;
                }
                var idx = (_head - 1 - age + HistorySeconds * 2) % HistorySeconds;
                down[i] = _down[idx];
                up[i] = _up[idx];
            }
        }
    }

    /// <summary>Спарклайн приложения за последние 60 сек (старые → новые).</summary>
    public double[] GetSparkline(string key)
    {
        var result = new double[SparkPoints];
        lock (_sync)
        {
            if (!_spark.TryGetValue(key, out var arr)) return result;
            for (var i = 0; i < SparkPoints; i++)
                result[i] = arr[(_sparkHead + i) % SparkPoints];
        }
        return result;
    }

    public void ResetPeaks()
    {
        lock (_sync)
        {
            PeakDown = 0;
            PeakUp = 0;
        }
    }
}
