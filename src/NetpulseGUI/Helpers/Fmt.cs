using NetpulseGUI.Core.Formatting;

namespace NetpulseGUI.Helpers;

/// <summary>Форматирование с учётом выбранных в настройках единиц.</summary>
public static class Fmt
{
    public static SpeedUnits Units { get; set; } = SpeedUnits.Bits;

    public static string Speed(double bytesPerSec) => UnitFormatter.FormatSpeed(bytesPerSec, Units);

    public static string Bytes(double bytes) => UnitFormatter.FormatBytes(bytes);

    public static string Tray(double bytesPerSec) => UnitFormatter.FormatTray(bytesPerSec, Units);

    public static string Percent(double fraction) => (fraction * 100).ToString("0", Localization.Loc.Instance.Culture) + "%";

    /// <summary>Изменение в процентах со знаком: «+12%», «−5%», «—» если сравнивать не с чем.</summary>
    public static string Change(double current, double previous)
    {
        if (previous <= 0) return current > 0 ? "+∞" : "—";
        var pct = (current - previous) / previous * 100;
        var sign = pct > 0.5 ? "+" : pct < -0.5 ? "−" : "±";
        return sign + Math.Abs(pct).ToString("0", Localization.Loc.Instance.Culture) + "%";
    }
}
