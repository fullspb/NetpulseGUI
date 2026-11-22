using NetpulseGUI.Core.Counters;
using NetpulseGUI.Core.Models;

namespace NetpulseGUI.Core.Collection;

public sealed record AdapterFilter(IReadOnlySet<string> ExcludedIds, bool IncludeVirtual, bool IncludeVpn)
{
    public static AdapterFilter Default { get; } = new(new HashSet<string>(), false, false);

    public bool Includes(string id, AdapterKind kind) =>
        !ExcludedIds.Contains(id) && AdapterClassifier.ShouldInclude(kind, IncludeVirtual, IncludeVpn);

    public static AdapterFilter Create(IEnumerable<string> excludedIds, bool includeVirtual, bool includeVpn) =>
        new(new HashSet<string>(excludedIds, StringComparer.OrdinalIgnoreCase), includeVirtual, includeVpn);
}

public sealed class AdapterTickResult
{
    public DateTime TimestampUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public List<AdapterDelta> Deltas { get; } = new();
    public long TotalRx { get; set; }
    public long TotalTx { get; set; }
    /// <summary>Первый замер после старта/сброса — дельт нет.</summary>
    public bool IsBaseline { get; init; }
}

/// <summary>
/// Опрашивает счётчики адаптеров и вычисляет дельты между замерами.
/// Учитывает переполнение 32-битных счётчиков, сброс счётчиков, появление и исчезновение адаптеров.
/// </summary>
public sealed class AdapterMonitor
{
    private sealed class State
    {
        public long Rx;
        public long Tx;
    }

    private readonly INetworkCounterSource _source;
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastTickUtc = DateTime.MinValue;
    private volatile AdapterFilter _filter = AdapterFilter.Default;

    /// <summary>Если между замерами прошло больше этого времени (сон/гибернация), замер считается базовым.</summary>
    public TimeSpan MaxGap { get; set; } = TimeSpan.FromMinutes(5);

    public AdapterMonitor(INetworkCounterSource? source = null)
    {
        _source = source ?? CreateDefaultSource();
    }

    private static INetworkCounterSource CreateDefaultSource() =>
        OperatingSystem.IsWindows() ? new IfTableCounterSource() : new SystemNetworkCounterSource();

    public AdapterFilter Filter
    {
        get => _filter;
        set => _filter = value ?? AdapterFilter.Default;
    }

    /// <summary>Сбросить опорные значения (например, после выхода из сна). Следующий Tick будет базовым.</summary>
    public void ResetBaseline()
    {
        lock (_states)
        {
            _states.Clear();
            _lastTickUtc = DateTime.MinValue;
        }
    }

    public AdapterTickResult Tick(DateTime nowUtc)
    {
        var counters = _source.Read();
        lock (_states)
        {
            var gap = nowUtc - _lastTickUtc;
            var baseline = _lastTickUtc == DateTime.MinValue || gap > MaxGap || gap < TimeSpan.Zero;
            var elapsed = baseline ? 0 : Math.Max(gap.TotalSeconds, 0.001);
            _lastTickUtc = nowUtc;

            var result = new AdapterTickResult { TimestampUtc = nowUtc, ElapsedSeconds = elapsed, IsBaseline = baseline };
            var filter = _filter;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in counters)
            {
                seen.Add(c.Id);
                if (!_states.TryGetValue(c.Id, out var st))
                {
                    // Новый (или пересозданный) адаптер — только запоминаем опорные значения.
                    _states[c.Id] = new State { Rx = c.BytesReceived, Tx = c.BytesSent };
                    continue;
                }

                if (baseline)
                {
                    st.Rx = c.BytesReceived;
                    st.Tx = c.BytesSent;
                    continue;
                }

                var rx = CounterMath.ComputeDelta(st.Rx, c.BytesReceived, elapsed, c.SpeedBitsPerSec);
                var tx = CounterMath.ComputeDelta(st.Tx, c.BytesSent, elapsed, c.SpeedBitsPerSec);
                st.Rx = c.BytesReceived;
                st.Tx = c.BytesSent;

                var kind = AdapterClassifier.Classify(c.Name, c.Description, c.Type);
                if (!filter.Includes(c.Id, kind)) continue;

                result.Deltas.Add(new AdapterDelta(c.Id, c.Name, c.Description, c.Type.ToString(), c.Mac, rx.Delta, tx.Delta));
                result.TotalRx += rx.Delta;
                result.TotalTx += tx.Delta;
            }

            // Удаляем исчезнувшие адаптеры: при повторном появлении начнём с нового базового значения.
            if (_states.Count != seen.Count)
            {
                foreach (var key in _states.Keys.Where(k => !seen.Contains(k)).ToList())
                    _states.Remove(key);
            }

            return result;
        }
    }
}
