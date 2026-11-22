using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Time;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;
using NetpulseGUI.Views.Pages;

namespace NetpulseGUI.ViewModels;

public enum DetailsRange
{
    Hour,
    Day,
    Week,
    Month
}

public sealed partial class ProcessDetailsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly ObservableCollection<double> _liveDown = new();
    private readonly ObservableCollection<double> _liveUp = new();
    private string _key = "";
    private long _appId;
    private CancellationTokenSource? _cts;

    public ProcessDetailsViewModel(AppHost host)
    {
        _host = host;
        for (var i = 0; i < 60; i++)
        {
            _liveDown.Add(0);
            _liveUp.Add(0);
        }
        host.Live.Tick += OnTick;
        host.Theme.ThemeChanged += () => _ = LoadChartAsync();
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string _downSpeed = "—";
    [ObservableProperty] private string _upSpeed = "—";
    [ObservableProperty] private string _today = "—";
    [ObservableProperty] private string _month = "—";
    [ObservableProperty] private string _allTime = "—";
    [ObservableProperty] private string _rangeTotal = "";
    [ObservableProperty] private ISeries[] _series = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _xAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _yAxes = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _liveSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _liveYAxes = Array.Empty<Axis>();
    [ObservableProperty] private DetailsRange _range = DetailsRange.Day;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _canOpenFolder;

    public Axis[] HiddenX { get; } = ChartFactory.HiddenXAxis();

    public bool RangeHour { get => Range == DetailsRange.Hour; set { if (value) Range = DetailsRange.Hour; } }
    public bool RangeDay { get => Range == DetailsRange.Day; set { if (value) Range = DetailsRange.Day; } }
    public bool RangeWeek { get => Range == DetailsRange.Week; set { if (value) Range = DetailsRange.Week; } }
    public bool RangeMonth { get => Range == DetailsRange.Month; set { if (value) Range = DetailsRange.Month; } }

    partial void OnRangeChanged(DetailsRange value)
    {
        OnPropertyChanged(nameof(RangeHour));
        OnPropertyChanged(nameof(RangeDay));
        OnPropertyChanged(nameof(RangeWeek));
        OnPropertyChanged(nameof(RangeMonth));
        _ = LoadChartAsync();
    }

    public void Load(string key, string name, string path)
    {
        _key = key;
        _appId = 0;
        Name = name;
        Path = path;
        CanOpenFolder = path.Length > 2 && path[1] == ':';
        Icon = _host.Icons.TryGet(path);
        if (Icon == null) _ = LoadIconAsync(path);
        DownSpeed = UpSpeed = "—";
        Today = Month = AllTime = "—";
        for (var i = 0; i < 60; i++)
        {
            _liveDown[i] = 0;
            _liveUp[i] = 0;
        }
        LiveSeries = new ISeries[]
        {
            ChartFactory.LiveLine(_host.Theme, _liveDown, true, Loc.T("Common_Download")),
            ChartFactory.LiveLine(_host.Theme, _liveUp, false, Loc.T("Common_Upload"))
        };
        LiveYAxes = ChartFactory.SpeedYAxis(_host.Theme);
        _ = LoadAllAsync();
    }

    private async Task LoadIconAsync(string path) => Icon = await _host.Icons.GetAsync(path);

    private async Task LoadAllAsync()
    {
        try
        {
            await _host.Engine.FlushAsync();
            var app = await _host.Repository.FindAppAsync(_key);
            _appId = app?.AppId ?? 0;
            if (_appId != 0)
            {
                var today = TimeBuckets.Today();
                var period = BillingPeriod.For(today, _host.Settings.Current.BillingStartDay);
                var t = await _host.Repository.GetAppTotalsAsync(_appId, today, today);
                var m = await _host.Repository.GetAppTotalsAsync(_appId, period.Start, period.LastDay);
                var a = await _host.Repository.GetAppTotalsAsync(_appId, new DateOnly(2000, 1, 1), today);
                Today = Fmt.Bytes(t.Total);
                Month = Fmt.Bytes(m.Total);
                AllTime = Fmt.Bytes(a.Total);
            }
            await LoadChartAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось загрузить данные процесса");
        }
    }

    private async Task LoadChartAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        if (_appId == 0)
        {
            HasData = false;
            Series = Array.Empty<ISeries>();
            RangeTotal = "";
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var labels = new List<string>();
            var down = new List<double>();
            var up = new List<double>();
            var culture = Loc.Instance.Culture;

            if (Range == DetailsRange.Month)
            {
                var today = TimeBuckets.Today();
                var from = today.AddDays(-29);
                var rows = (await _host.Repository.GetAppDailySeriesAsync(_appId, from, today, ct)).ToDictionary(r => r.Day, r => r.Totals);
                for (var d = from; d <= today; d = d.AddDays(1))
                {
                    rows.TryGetValue(d, out var v);
                    labels.Add(d.ToString("dd.MM", culture));
                    down.Add(v.Rx);
                    up.Add(v.Tx);
                }
            }
            else
            {
                var (span, step, granularity, format) = Range switch
                {
                    DetailsRange.Hour => (TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), Granularity.Minute, "HH:mm"),
                    DetailsRange.Week => (TimeSpan.FromDays(7), TimeSpan.FromHours(1), Granularity.Hour, "dd.MM HH:00"),
                    _ => (TimeSpan.FromDays(1), TimeSpan.FromHours(1), Granularity.Hour, "HH:00")
                };
                var stepSec = (long)step.TotalSeconds;
                var endTs = (TimeBuckets.ToUnix(now) / stepSec + 1) * stepSec;
                var startTs = endTs - (long)span.TotalSeconds;
                var rows = (await _host.Repository.GetAppSeriesAsync(_appId, granularity, TimeBuckets.FromUnix(startTs), TimeBuckets.FromUnix(endTs), ct))
                    .ToDictionary(r => TimeBuckets.ToUnix(r.TimeUtc), r => r);
                for (var ts = startTs; ts < endTs; ts += stepSec)
                {
                    rows.TryGetValue(ts, out var v);
                    labels.Add(TimeBuckets.FromUnix(ts).ToLocalTime().ToString(format, culture));
                    down.Add(v.Rx);
                    up.Add(v.Tx);
                }
            }

            if (ct.IsCancellationRequested) return;
            HasData = down.Any(v => v > 0) || up.Any(v => v > 0);
            RangeTotal = Loc.F("Details_RangeTotal", Fmt.Bytes(down.Sum()), Fmt.Bytes(up.Sum()));
            Series = new ISeries[]
            {
                ChartFactory.Columns(_host.Theme, down, true, Loc.T("Common_Download"), v => Fmt.Bytes(v)),
                ChartFactory.Columns(_host.Theme, up, false, Loc.T("Common_Upload"), v => Fmt.Bytes(v))
            };
            XAxes = ChartFactory.LabelsXAxis(_host.Theme, labels);
            YAxes = ChartFactory.BytesYAxis(_host.Theme);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось построить график процесса");
        }
    }

    private void OnTick(LiveSnapshot s)
    {
        if (_key.Length == 0) return;
        var p = s.Processes.FirstOrDefault(x => string.Equals(x.Key, _key, StringComparison.OrdinalIgnoreCase));
        var d = p?.DownBytesPerSec ?? 0;
        var u = p?.UpBytesPerSec ?? 0;
        DownSpeed = Fmt.Speed(d);
        UpSpeed = Fmt.Speed(u);
        _liveDown.RemoveAt(0);
        _liveDown.Add(d);
        _liveUp.RemoveAt(0);
        _liveUp.Add(u);
    }

    [RelayCommand]
    private void Back() => _host.Navigate(typeof(ProcessesPage));

    [RelayCommand]
    private void OpenFolder()
    {
        if (!CanOpenFolder || !File.Exists(Path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path}\"") { UseShellExecute = true });
    }
}
