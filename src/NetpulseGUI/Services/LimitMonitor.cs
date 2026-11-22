using NetpulseGUI.Core.Storage;
using NetpulseGUI.Core.Time;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;
using NetpulseGUI.Settings;

namespace NetpulseGUI.Services;

/// <summary>
/// Следит за дневным и месячным лимитами и показывает уведомления на 80/90/100%.
/// Каждый порог срабатывает один раз за сутки/расчётный период; состояние сохраняется в настройках.
/// </summary>
public sealed class LimitMonitor
{
    private static readonly int[] Levels = { 80, 90, 100 };
    private const double Gb = 1024d * 1024 * 1024;

    private readonly SettingsStore _settings;
    private readonly NotificationService _notifications;
    private readonly TrafficRepository _repo;

    public LimitMonitor(SettingsStore settings, NotificationService notifications, TrafficRepository repo)
    {
        _settings = settings;
        _notifications = notifications;
        _repo = repo;
    }

    public void Check(TotalsInfo totals)
    {
        var s = _settings.Current;
        var dayKey = TimeBuckets.DayKey(TimeBuckets.Today());
        var periodKey = TimeBuckets.DayKey(totals.BillingPeriod.Start);
        var changed = false;

        if (s.LimitDayKey != dayKey)
        {
            s.LimitDayKey = dayKey;
            s.LimitDayFired = 0;
            changed = true;
        }
        if (s.LimitPeriodKey != periodKey)
        {
            s.LimitPeriodKey = periodKey;
            s.LimitPeriodFired = 0;
            changed = true;
        }

        if (s.DailyLimitEnabled && s.DailyLimitGb > 0)
        {
            var used = s.LimitCountsUpload ? totals.Today.Total : totals.Today.Rx;
            var fired = Evaluate(used, s.DailyLimitGb * Gb, s.LimitDayFired, Loc.T("Limit_Daily"));
            if (fired != s.LimitDayFired) { s.LimitDayFired = fired; changed = true; }
        }

        if (s.MonthlyLimitEnabled && s.MonthlyLimitGb > 0)
        {
            var used = s.LimitCountsUpload ? totals.Period.Total : totals.Period.Rx;
            var fired = Evaluate(used, s.MonthlyLimitGb * Gb, s.LimitPeriodFired, Loc.T("Limit_Monthly"));
            if (fired != s.LimitPeriodFired) { s.LimitPeriodFired = fired; changed = true; }
        }

        if (changed) _settings.Save();
    }

    private int Evaluate(long used, double limit, int alreadyFired, string limitName)
    {
        var pct = used / limit * 100;
        var s = _settings.Current;
        var highest = alreadyFired;
        foreach (var level in Levels)
        {
            if (pct < level || level <= alreadyFired) continue;
            highest = level;
        }
        if (highest == alreadyFired) return alreadyFired;

        // Уведомляем только о самом высоком из впервые достигнутых порогов.
        var enabled = highest switch { 80 => s.Notify80, 90 => s.Notify90, _ => s.Notify100 };
        if (enabled)
        {
            var title = highest >= 100 ? Loc.F("Limit_ExceededTitle", limitName) : Loc.F("Limit_ReachedTitle", limitName, highest);
            var message = Loc.F("Limit_Message", Fmt.Bytes(used), Fmt.Bytes(limit), Fmt.Percent(used / limit));
            _notifications.Show(title, message, "limits");
            _ = _repo.AddAlertAsync(highest >= 100 ? "limit-exceeded" : "limit-" + highest, title + ". " + message);
        }
        return highest;
    }
}
