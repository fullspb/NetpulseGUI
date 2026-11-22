using System.Globalization;

namespace NetpulseGUI.Core.Formatting;

public enum SpeedUnits
{
    /// <summary>Бит/с (Кбит/с, Мбит/с…), десятичные множители — как у провайдеров.</summary>
    Bits,
    /// <summary>Байт/с (КБ/с, МБ/с…), двоичные множители — как в Проводнике.</summary>
    Bytes
}

/// <summary>Форматирование скоростей и объёмов с автоподбором единиц.</summary>
public static class UnitFormatter
{
    private static readonly string[] BytesRu = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };
    private static readonly string[] BytesEn = { "B", "KB", "MB", "GB", "TB", "PB" };
    private static readonly string[] BitsRu = { "бит/с", "Кбит/с", "Мбит/с", "Гбит/с", "Тбит/с" };
    private static readonly string[] BitsEn = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
    private static readonly string[] BytesPerSecRu = { "Б/с", "КБ/с", "МБ/с", "ГБ/с", "ТБ/с" };
    private static readonly string[] BytesPerSecEn = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };

    public static bool UseRussian { get; set; } = true;

    private static CultureInfo Culture => UseRussian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.GetCultureInfo("en-US");

    public static string FormatBytes(double bytes)
    {
        var units = UseRussian ? BytesRu : BytesEn;
        var (value, idx) = Scale(Math.Max(bytes, 0), 1024, units.Length);
        return string.Format(Culture, "{0} {1}", Number(value, idx), units[idx]);
    }

    public static string FormatSpeed(double bytesPerSec, SpeedUnits units)
    {
        bytesPerSec = Math.Max(bytesPerSec, 0);
        if (units == SpeedUnits.Bits)
        {
            var names = UseRussian ? BitsRu : BitsEn;
            var (v, i) = Scale(bytesPerSec * 8, 1000, names.Length);
            return string.Format(Culture, "{0} {1}", Number(v, i), names[i]);
        }
        else
        {
            var names = UseRussian ? BytesPerSecRu : BytesPerSecEn;
            var (v, i) = Scale(bytesPerSec, 1024, names.Length);
            return string.Format(Culture, "{0} {1}", Number(v, i), names[i]);
        }
    }

    /// <summary>
    /// Сверхкомпактная запись для иконки трея: не более 3 цифр и одна буква единиц (K/M/G),
    /// например «12M», «1.5K», «850», «0».
    /// </summary>
    public static string FormatTray(double bytesPerSec, SpeedUnits units)
    {
        var value = units == SpeedUnits.Bits ? bytesPerSec * 8 : bytesPerSec;
        var k = units == SpeedUnits.Bits ? 1000.0 : 1024.0;
        string[] suffix = { "", "K", "M", "G", "T" };
        var i = 0;
        while (value >= 999.5 && i < suffix.Length - 1)
        {
            value /= k;
            i++;
        }
        string num;
        if (value < 9.95 && i > 0) num = value.ToString("0.0", CultureInfo.InvariantCulture);
        else num = Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        if (num.EndsWith(".0", StringComparison.Ordinal)) num = num[..^2];
        return num + suffix[i];
    }

    private static (double value, int index) Scale(double value, double k, int count)
    {
        var i = 0;
        while (value >= k && i < count - 1)
        {
            value /= k;
            i++;
        }
        return (value, i);
    }

    private static string Number(double value, int idx)
    {
        if (idx == 0) return Math.Round(value).ToString("0", Culture);
        if (value >= 100) return value.ToString("0", Culture);
        if (value >= 10) return value.ToString("0.0", Culture);
        return value.ToString("0.00", Culture);
    }
}
