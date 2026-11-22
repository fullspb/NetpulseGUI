namespace NetpulseGUI.Core.Models;

/// <summary>Мгновенный срез трафика за последний интервал сбора (обычно 1 сек).</summary>
public sealed class LiveSnapshot
{
    public static LiveSnapshot Empty { get; } = new() { TimestampUtc = DateTime.UtcNow };

    public DateTime TimestampUtc { get; init; }

    /// <summary>Фактическая длительность интервала в секундах.</summary>
    public double IntervalSeconds { get; init; } = 1;

    public double DownBytesPerSec { get; init; }
    public double UpBytesPerSec { get; init; }

    public IReadOnlyList<AdapterRate> Adapters { get; init; } = Array.Empty<AdapterRate>();

    /// <summary>Скорости процессов (процессы с одинаковым exe объединены), по убыванию суммарной скорости.</summary>
    public IReadOnlyList<ProcessRate> Processes { get; init; } = Array.Empty<ProcessRate>();

    public bool Paused { get; init; }
    public bool ProcessTrackingAvailable { get; init; }
}

public sealed class AdapterRate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public double DownBytesPerSec { get; init; }
    public double UpBytesPerSec { get; init; }
}

public sealed class ProcessRate
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary>Число процессов (PID) с этим exe, участвовавших в обмене.</summary>
    public int InstanceCount { get; init; }
    public double DownBytesPerSec { get; init; }
    public double UpBytesPerSec { get; init; }
    public double TotalBytesPerSec => DownBytesPerSec + UpBytesPerSec;

    /// <summary>Ключ приложения: путь к exe, а если он неизвестен — имя процесса.</summary>
    public string Key => string.IsNullOrEmpty(Path) ? Name : Path;
}
