using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Time;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;
using NetpulseGUI.Services;

namespace NetpulseGUI.ViewModels;

public sealed partial class LimitsViewModel : ObservableObject
{
    private const double Gb = 1024d * 1024 * 1024;
    private readonly AppHost _host;
    private bool _loading;

    public LimitsViewModel(AppHost host)
    {
        _host = host;
        var s = host.Settings.Current;
        _loading = true;
        DailyEnabled = s.DailyLimitEnabled;
        DailyGb = s.DailyLimitGb;
        MonthlyEnabled = s.MonthlyLimitEnabled;
        MonthlyGb = s.MonthlyLimitGb;
        BillingStartDay = s.BillingStartDay;
        CountUpload = s.LimitCountsUpload;
        Notify80 = s.Notify80;
        Notify90 = s.Notify90;
        Notify100 = s.Notify100;
        _loading = false;
        host.Totals.Updated += Update;
        Loc.Instance.LanguageChanged += () => Update(host.Totals.Current);
    }

    public ObservableCollection<AlertEntryView> Alerts { get; } = new();
    public IReadOnlyList<int> Days { get; } = Enumerable.Range(1, 31).ToList();

    [ObservableProperty] private bool _dailyEnabled;
    [ObservableProperty] private double _dailyGb;
    [ObservableProperty] private bool _monthlyEnabled;
    [ObservableProperty] private double _monthlyGb;
    [ObservableProperty] private int _billingStartDay = 1;
    [ObservableProperty] private bool _countUpload;
    [ObservableProperty] private bool _notify80;
    [ObservableProperty] private bool _notify90;
    [ObservableProperty] private bool _notify100;

    [ObservableProperty] private string _todayUsed = "";
    [ObservableProperty] private double _todayPercent;
    [ObservableProperty] private string _monthUsed = "";
    [ObservableProperty] private double _monthPercent;
    [ObservableProperty] private string _periodText = "";
    [ObservableProperty] private string _forecastText = "";
    [ObservableProperty] private bool _forecastExceeds;
    [ObservableProperty] private string _todayLeft = "";
    [ObservableProperty] private string _monthLeft = "";

    partial void OnDailyEnabledChanged(bool value) => Save(s => s.DailyLimitEnabled = value);
    partial void OnDailyGbChanged(double value) => Save(s => { s.DailyLimitGb = Math.Max(0.01, value); s.LimitDayFired = 0; });
    partial void OnMonthlyEnabledChanged(bool value) => Save(s => s.MonthlyLimitEnabled = value);
    partial void OnMonthlyGbChanged(double value) => Save(s => { s.MonthlyLimitGb = Math.Max(0.01, value); s.LimitPeriodFired = 0; });
    partial void OnCountUploadChanged(bool value) => Save(s => s.LimitCountsUpload = value);
    partial void OnNotify80Changed(bool value) => Save(s => s.Notify80 = value);
    partial void OnNotify90Changed(bool value) => Save(s => s.Notify90 = value);
    partial void OnNotify100Changed(bool value) => Save(s => s.Notify100 = value);

    partial void OnBillingStartDayChanged(int value)
    {
        Save(s => s.BillingStartDay = Math.Clamp(value, 1, 31));
        if (!_loading) _ = _host.Totals.RefreshAsync();
    }

    private void Save(Action<Settings.AppSettings> change)
    {
        if (_loading) return;
        _host.Settings.Update(change);
        // Проверка порогов произойдёт при следующем обновлении итогов (≤ 5 сек), а не на каждое нажатие клавиши.
        Update(_host.Totals.Current);
    }

    private void Update(TotalsInfo t)
    {
        var s = _host.Settings.Current;
        var c = Loc.Instance.Culture;
        var todayUsed = s.LimitCountsUpload ? t.Today.Total : t.Today.Rx;
        var periodUsed = s.LimitCountsUpload ? t.Period.Total : t.Period.Rx;
        var dailyLimit = s.DailyLimitGb * Gb;
        var monthlyLimit = s.MonthlyLimitGb * Gb;

        TodayUsed = Loc.F("Limits_UsedOf", Fmt.Bytes(todayUsed), Fmt.Bytes(dailyLimit));
        TodayPercent = dailyLimit > 0 ? Math.Min(100, todayUsed / dailyLimit * 100) : 0;
        TodayLeft = Loc.F("Limits_Left", Fmt.Bytes(Math.Max(0, dailyLimit - todayUsed)));
        MonthUsed = Loc.F("Limits_UsedOf", Fmt.Bytes(periodUsed), Fmt.Bytes(monthlyLimit));
        MonthPercent = monthlyLimit > 0 ? Math.Min(100, periodUsed / monthlyLimit * 100) : 0;
        MonthLeft = Loc.F("Limits_Left", Fmt.Bytes(Math.Max(0, monthlyLimit - periodUsed)));

        var period = t.BillingPeriod;
        var daysLeft = period.EndExclusive.DayNumber - TimeBuckets.Today().DayNumber;
        PeriodText = Loc.F("Limits_Period", period.Start.ToString("d", c), period.LastDay.ToString("d", c), daysLeft);
        ForecastText = Loc.F("Limits_Forecast", Fmt.Bytes(t.PeriodForecast));
        ForecastExceeds = s.MonthlyLimitEnabled && t.PeriodForecast > monthlyLimit;
    }

    public async Task LoadAlertsAsync()
    {
        try
        {
            var list = await Task.Run(() => _host.Repository.GetAlertsAsync(50));
            Alerts.Clear();
            foreach (var a in list)
                Alerts.Add(new AlertEntryView(a.TimeUtc.ToLocalTime().ToString("g", Loc.Instance.Culture), a.Message));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось загрузить журнал уведомлений");
        }
    }

    [RelayCommand]
    private void TestNotification() =>
        _host.Notifications.Show(Loc.T("Limits_TestTitle"), Loc.T("Limits_TestMessage"), "limits");
}

public sealed record AlertEntryView(string Time, string Message);
