namespace NetpulseGUI.Core.Counters;

public enum CounterChange
{
    /// <summary>Обычное монотонное приращение.</summary>
    Normal,
    /// <summary>32-битный счётчик перешёл через 2^32.</summary>
    Wrap32,
    /// <summary>Счётчик сбросился (адаптер пересоздан/переподключён, выход из сна).</summary>
    Reset,
    /// <summary>Значение неправдоподобно (больше физически возможного) — отброшено.</summary>
    Discarded
}

public readonly record struct CounterResult(long Delta, CounterChange Change);

/// <summary>
/// Вычисление дельты счётчиков байтов с учётом переполнения и сброса.
/// Windows отдаёт 64-битные счётчики (GetIfEntry2), но часть драйверов/виртуальных
/// адаптеров обновляет только младшие 32 бита, поэтому учитываются оба варианта.
/// </summary>
public static class CounterMath
{
    private const long UInt32Range = 1L << 32;

    /// <summary>Абсолютный потолок скорости для проверки правдоподобия, если скорость линка неизвестна: 100 Гбит/с.</summary>
    public const long DefaultMaxBytesPerSecond = 100_000_000_000L / 8;

    /// <param name="previous">Предыдущее значение счётчика.</param>
    /// <param name="current">Текущее значение счётчика.</param>
    /// <param name="elapsedSeconds">Прошедшее время между замерами.</param>
    /// <param name="linkSpeedBitsPerSec">Скорость линка (бит/с) или ≤0, если неизвестна.</param>
    public static CounterResult ComputeDelta(long previous, long current, double elapsedSeconds, long linkSpeedBitsPerSec = 0)
    {
        if (previous < 0 || current < 0)
            return new CounterResult(0, CounterChange.Discarded);

        var maxBytes = MaxPlausibleBytes(elapsedSeconds, linkSpeedBitsPerSec);

        if (current >= previous)
        {
            var delta = current - previous;
            return delta <= maxBytes
                ? new CounterResult(delta, CounterChange.Normal)
                : new CounterResult(0, CounterChange.Discarded);
        }

        // current < previous: либо переполнение 32-битного счётчика, либо сброс.
        if (previous < UInt32Range && current < UInt32Range)
        {
            var wrapped = UInt32Range - previous + current;
            if (wrapped <= maxBytes)
                return new CounterResult(wrapped, CounterChange.Wrap32);
        }

        // Сброс: всё, что набежало с момента сброса, — текущее значение (если правдоподобно).
        return current <= maxBytes
            ? new CounterResult(current, CounterChange.Reset)
            : new CounterResult(0, CounterChange.Reset);
    }

    public static long MaxPlausibleBytes(double elapsedSeconds, long linkSpeedBitsPerSec)
    {
        var secs = Math.Max(elapsedSeconds, 1.0);
        double bytesPerSec = linkSpeedBitsPerSec > 0
            ? Math.Min(linkSpeedBitsPerSec / 8.0 * 1.5, DefaultMaxBytesPerSecond) // запас 50% на агрегацию/погрешность
            : DefaultMaxBytesPerSecond;
        var max = bytesPerSec * secs;
        return max >= long.MaxValue ? long.MaxValue : (long)max;
    }
}
