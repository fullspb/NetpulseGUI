using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;
using NetpulseGUI.Services;
using NetpulseGUI.Views.Pages;

namespace NetpulseGUI.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly ObservableCollection<double> _down = new();
    private readonly ObservableCollection<double> _up = new();
    private int _bucketSeconds = 1;
    private int _bucketFill;
    private double _bucketDown;
    private double _bucketUp;
    private bool _active;

    public DashboardViewModel(AppHost host)
    {
        _host = host;
        RangeSeconds = host.Settings.Current.DashboardRangeSeconds is 60 or 300 or 3600 ? host.Settings.Current.DashboardRangeSeconds : 60;
        BuildSeries();
        host.Live.Tick += OnTick;
        host.Totals.Updated += OnTotals;
        host.Theme.ThemeChanged += BuildSeries;
        Loc.Instance.LanguageChanged += () =>
        {
            BuildSeries();
            OnTotals(host.Totals.Current);
            OnTick(host.Live.Current);
        };
    }

    public ObservableCollection<ProcessItem> TopProcesses { get; } = new();

    [ObservableProperty] private ISeries[] _series = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _xAxes = ChartFactory.HiddenXAxis();
    [ObservableProperty] private Axis[] _yAxes = Array.Empty<Axis>();

    [ObservableProperty] private string _downSpeed = "—";
    [ObservableProperty] private string _upSpeed = "—";
    [ObservableProperty] private string _peakDown = "—";
    [ObservableProperty] private string _peakUp = "—";
    [ObservableProperty] private string _todayTotal = "—";
    [ObservableProperty] private string _todayDetails = "";
    [ObservableProperty] private string _monthTotal = "—";
    [ObservableProperty] private string _monthDetails = "";
    [ObservableProperty] private string _allTotal = "—";
    [ObservableProperty] private string _allDetails = "";
    [ObservableProperty] private bool _processTrackingAvailable;
    [ObservableProperty] private bool _hasTopProcesses;
    [ObservableProperty] private int _rangeSeconds = 60;

    public bool Range60
    {
        get => RangeSeconds == 60;
        set { if (value) SetRange(60); }
    }

    public bool Range300
    {
        get => RangeSeconds == 300;
        set { if (value) SetRange(300); }
    }

    public bool Range3600
    {
        get => RangeSeconds == 3600;
        set { if (value) SetRange(3600); }
    }

    private void SetRange(int seconds)
    {
        if (RangeSeconds == seconds) return;
        RangeSeconds = seconds;
        _host.Settings.Update(s => s.DashboardRangeSeconds = seconds);
        OnPropertyChanged(nameof(Range60));
        OnPropertyChanged(nameof(Range300));
        OnPropertyChanged(nameof(Range3600));
        RebuildValues();
    }

    /// <summary>Пока окно скрыто, график не обновляется (экономия CPU), при показе — перестраивается из истории.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active) RebuildValues();
    }

    private void BuildSeries()
    {
        var theme = _host.Theme;
        Series = new ISeries[]
        {
            ChartFactory.LiveLine(theme, _down, true, Loc.T("Common_Download")),
            ChartFactory.LiveLine(theme, _up, false, Loc.T("Common_Upload"))
        };
        YAxes = ChartFactory.SpeedYAxis(theme);
        XAxes = ChartFactory.HiddenXAxis();
    }

    private void RebuildValues()
    {
        _bucketSeconds = RangeSeconds switch { 3600 => 10, _ => 1 };
        var points = RangeSeconds / _bucketSeconds;
        var down = new double[RangeSeconds];
        var up = new double[RangeSeconds];
        _host.Live.CopyHistory(RangeSeconds, down, up);

        _down.Clear();
        _up.Clear();
        for (var i = 0; i < points; i++)
        {
            double d = 0, u = 0;
            for (var j = 0; j < _bucketSeconds; j++)
            {
                d += down[i * _bucketSeconds + j];
                u += up[i * _bucketSeconds + j];
            }
            _down.Add(d / _bucketSeconds);
            _up.Add(u / _bucketSeconds);
        }
        _bucketFill = 0;
        _bucketDown = 0;
        _bucketUp = 0;
    }

    private void OnTick(LiveSnapshot s)
    {
        DownSpeed = Fmt.Speed(s.DownBytesPerSec);
        UpSpeed = Fmt.Speed(s.UpBytesPerSec);
        PeakDown = Fmt.Speed(_host.Live.PeakDown);
        PeakUp = Fmt.Speed(_host.Live.PeakUp);
        ProcessTrackingAvailable = s.ProcessTrackingAvailable;

        if (!_active) return;

        // График: при интервале UI > 1 сек точка всё равно добавляется за каждый тик (усреднение по ведру).
        _bucketDown += s.DownBytesPerSec;
        _bucketUp += s.UpBytesPerSec;
        _bucketFill++;
        if (_bucketFill >= _bucketSeconds || _bucketSeconds == 1)
        {
            if (_down.Count > 0) _down.RemoveAt(0);
            if (_up.Count > 0) _up.RemoveAt(0);
            _down.Add(_bucketDown / _bucketFill);
            _up.Add(_bucketUp / _bucketFill);
            _bucketFill = 0;
            _bucketDown = 0;
            _bucketUp = 0;
        }

        UpdateTop(s);
    }

    private void UpdateTop(LiveSnapshot s)
    {
        var top = s.Processes.Take(5).ToList();
        var max = top.Count > 0 ? top.Max(p => p.TotalBytesPerSec) : 0;
        while (TopProcesses.Count > top.Count) TopProcesses.RemoveAt(TopProcesses.Count - 1);
        for (var i = 0; i < top.Count; i++)
        {
            var p = top[i];
            if (i >= TopProcesses.Count || TopProcesses[i].Key != p.Key)
            {
                var item = new ProcessItem(p.Key, p.Name, p.Path) { Icon = _host.Icons.TryGet(p.Path) };
                if (item.Icon == null) _ = LoadIconAsync(item);
                if (i < TopProcesses.Count) TopProcesses[i] = item;
                else TopProcesses.Add(item);
            }
            var row = TopProcesses[i];
            row.DownSpeed = p.DownBytesPerSec;
            row.UpSpeed = p.UpBytesPerSec;
            row.Instances = p.InstanceCount;
            row.Share = max > 0 ? p.TotalBytesPerSec / max : 0;
        }
        HasTopProcesses = TopProcesses.Count > 0;
    }

    private async Task LoadIconAsync(ProcessItem item)
    {
        item.Icon = await _host.Icons.GetAsync(item.Path);
    }

    private void OnTotals(TotalsInfo t)
    {
        TodayTotal = Fmt.Bytes(t.Today.Total);
        TodayDetails = $"↓ {Fmt.Bytes(t.Today.Rx)}   ↑ {Fmt.Bytes(t.Today.Tx)}";
        MonthTotal = Fmt.Bytes(t.Period.Total);
        MonthDetails = Loc.F("Dashboard_MonthDetails",
            t.BillingPeriod.Start.ToString("d", Loc.Instance.Culture),
            t.BillingPeriod.LastDay.ToString("d", Loc.Instance.Culture),
            Fmt.Bytes(t.PeriodForecast));
        AllTotal = Fmt.Bytes(t.AllTime.Total);
        AllDetails = $"↓ {Fmt.Bytes(t.AllTime.Rx)}   ↑ {Fmt.Bytes(t.AllTime.Tx)}";
    }

    [RelayCommand]
    private void OpenProcesses() => _host.Navigate(typeof(ProcessesPage));

    [RelayCommand]
    private void OpenProcess(ProcessItem? item)
    {
        if (item == null) return;
        _host.ProcessDetailsVm.Load(item.Key, item.Name, item.Path);
        _host.Navigate(typeof(ProcessDetailsPage));
    }

    [RelayCommand]
    private void RestartElevated() => _host.MainVm.RestartElevatedCommand.Execute(null);
}
