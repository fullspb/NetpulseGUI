namespace NetpulseGUI.Core.Models;

public enum Granularity
{
    Second,
    Minute,
    Hour,
    Day
}

public readonly record struct TrafficTotals(long Rx, long Tx)
{
    public long Total => Rx + Tx;
    public static TrafficTotals operator +(TrafficTotals a, TrafficTotals b) => new(a.Rx + b.Rx, a.Tx + b.Tx);
}

/// <summary>Точка временного ряда. Время — начало бакета (UTC).</summary>
public readonly record struct SeriesPoint(DateTime TimeUtc, long Rx, long Tx);

public sealed class AppTotal
{
    public long AppId { get; set; }
    public string ExeName { get; set; } = "";
    public string Path { get; set; } = "";
    public long Rx { get; set; }
    public long Tx { get; set; }
    public long Total => Rx + Tx;
}

public sealed class AdapterTotal
{
    public long AdapterId { get; set; }
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public long Rx { get; set; }
    public long Tx { get; set; }
}

public sealed class AlertEntry
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; }
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Дельта байтов по адаптеру за один интервал сбора.</summary>
public readonly record struct AdapterDelta(string Id, string Name, string Description, string Type, string Mac, long Rx, long Tx);

/// <summary>Дельта байтов по приложению за один интервал сбора.</summary>
public readonly record struct AppDelta(string ExeName, string Path, long Rx, long Tx);
