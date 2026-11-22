using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Win32;
using NetpulseGUI.Core.Time;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;
using NetpulseGUI.Services;
using SkiaSharp;

namespace NetpulseGUI.ViewModels;

public enum ReportKind
{
    Day,
    Week,
    Month,
    Custom
}

public sealed record ReportKindOption(ReportKind Kind, string Label);

public sealed partial class ReportsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private ReportData? _report;
    private CancellationTokenSource? _cts;
    private bool _suppress;

    public ReportsViewModel(AppHost host)
    {
        _host = host;
        BuildKinds();
        _selectedKind = Kinds[2];
        _date = DateTime.Today;
        _customFrom = DateTime.Today.AddDays(-6);
        _customTo = DateTime.Today;
        host.Theme.ThemeChanged += () => { if (_report != null) Render(_report); };
        Loc.Instance.LanguageChanged += () =>
        {
            var k = SelectedKind.Kind;
            _suppress = true;
            BuildKinds();
            SelectedKind = Kinds.First(x => x.Kind == k);
            _suppress = false;
            _ = LoadAsync();
        };
    }

    public ObservableCollection<ReportKindOption> Kinds { get; } = new();
    public ObservableCollection<ProcessItem> TopApps { get; } = new();

    [ObservableProperty] private ReportKindOption _selectedKind;
    [ObservableProperty] private DateTime _date;
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;
    [ObservableProperty] private bool _isCustom;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _periodText = "";
    [ObservableProperty] private string _prevPeriodText = "";
    [ObservableProperty] private string _totalDown = "—";
    [ObservableProperty] private string _totalUp = "—";
    [ObservableProperty] private string _total = "—";
    [ObservableProperty] private string _changeDown = "";
    [ObservableProperty] private string _changeUp = "";
    [ObservableProperty] private string _changeTotal = "";
    [ObservableProperty] private bool _totalGrew;
    [ObservableProperty] private string _average = "";
    [ObservableProperty] private string _peak = "";
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private ISeries[] _columnSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _xAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _yAxes = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _pieSeries = Array.Empty<ISeries>();
    [ObservableProperty] private bool _hasApps;
    [ObservableProperty] private SolidColorPaint _legendPaint = new(SKColors.Gray);

    private void BuildKinds()
    {
        Kinds.Clear();
        Kinds.Add(new ReportKindOption(ReportKind.Day, Loc.T("Report_Day")));
        Kinds.Add(new ReportKindOption(ReportKind.Week, Loc.T("Report_Week")));
        Kinds.Add(new ReportKindOption(ReportKind.Month, Loc.T("Report_Month")));
        Kinds.Add(new ReportKindOption(ReportKind.Custom, Loc.T("Report_Custom")));
    }

    partial void OnSelectedKindChanged(ReportKindOption value)
    {
        if (value == null) return;
        IsCustom = value.Kind == ReportKind.Custom;
        if (!_suppress) _ = LoadAsync();
    }

    partial void OnDateChanged(DateTime value) { if (!_suppress) _ = LoadAsync(); }
    partial void OnCustomFromChanged(DateTime value) { if (!_suppress && IsCustom) _ = LoadAsync(); }
    partial void OnCustomToChanged(DateTime value) { if (!_suppress && IsCustom) _ = LoadAsync(); }

    public void ShowToday()
    {
        _suppress = true;
        SelectedKind = Kinds.First(k => k.Kind == ReportKind.Day);
        Date = DateTime.Today;
        _suppress = false;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void Previous() => Shift(-1);

    [RelayCommand]
    private void Next() => Shift(1);

    private void Shift(int dir)
    {
        switch (SelectedKind.Kind)
        {
            case ReportKind.Day: Date = Date.AddDays(dir); break;
            case ReportKind.Week: Date = Date.AddDays(7 * dir); break;
            case ReportKind.Month:
                var p = BillingPeriod.For(DateOnly.FromDateTime(Date), _host.Settings.Current.BillingStartDay);
                Date = (dir < 0 ? p.Start.AddDays(-1) : p.EndExclusive).ToDateTime(TimeOnly.MinValue);
                break;
            case ReportKind.Custom:
                var len = (CustomTo - CustomFrom).Days + 1;
                _suppress = true;
                CustomFrom = CustomFrom.AddDays(len * dir);
                CustomTo = CustomTo.AddDays(len * dir);
                _suppress = false;
                _ = LoadAsync();
                break;
        }
    }

    private (DateOnly from, DateOnly to, DateOnly prevFrom, DateOnly prevTo) ComputeRange()
    {
        var d = DateOnly.FromDateTime(Date);
        switch (SelectedKind.Kind)
        {
            case ReportKind.Day:
                return (d, d, d.AddDays(-1), d.AddDays(-1));
            case ReportKind.Week:
                var offset = ((int)d.DayOfWeek + 6) % 7; // неделя с понедельника
                var monday = d.AddDays(-offset);
                return (monday, monday.AddDays(6), monday.AddDays(-7), monday.AddDays(-1));
            case ReportKind.Month:
                var startDay = _host.Settings.Current.BillingStartDay;
                var p = BillingPeriod.For(d, startDay);
                var prev = p.Previous(startDay);
                return (p.Start, p.LastDay, prev.Start, prev.LastDay);
            default:
                var from = DateOnly.FromDateTime(CustomFrom);
                var to = DateOnly.FromDateTime(CustomTo);
                if (to < from) (from, to) = (to, from);
                var len = to.DayNumber - from.DayNumber + 1;
                return (from, to, from.AddDays(-len), from.AddDays(-1));
        }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await _host.Engine.FlushAsync();
            var (from, to, pf, pt) = ComputeRange();
            var report = await Task.Run(() => _host.Reports.BuildAsync(from, to, pf, pt, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _report = report;
            Render(report);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка построения отчёта");
        }
        finally
        {
            if (ReferenceEquals(cts, _cts)) IsBusy = false;
        }
    }

    private void Render(ReportData r)
    {
        var c = Loc.Instance.Culture;
        PeriodText = r.From == r.To ? r.From.ToString("D", c) : $"{r.From.ToString("d", c)} — {r.To.ToString("d", c)}";
        PrevPeriodText = Loc.F("Report_ComparedTo", r.PrevFrom == r.PrevTo ? r.PrevFrom.ToString("d", c) : $"{r.PrevFrom.ToString("d", c)} — {r.PrevTo.ToString("d", c)}");
        TotalDown = Fmt.Bytes(r.Totals.Rx);
        TotalUp = Fmt.Bytes(r.Totals.Tx);
        Total = Fmt.Bytes(r.Totals.Total);
        ChangeDown = ChangeText(r.Totals.Rx, r.PrevTotals.Rx);
        ChangeUp = ChangeText(r.Totals.Tx, r.PrevTotals.Tx);
        ChangeTotal = ChangeText(r.Totals.Total, r.PrevTotals.Total);
        TotalGrew = r.Totals.Total > r.PrevTotals.Total;

        var peak = r.Buckets.OrderByDescending(b => b.Total).FirstOrDefault();
        if (r.Hourly)
        {
            Average = Loc.F("Report_AvgHour", Fmt.Bytes(r.Totals.Total / 24.0));
            Peak = peak is { Total: > 0 } ? Loc.F("Report_PeakHour", peak.Label + ":00", Fmt.Bytes(peak.Total)) : "";
            ChartTitle = Loc.T("Report_ByHours");
        }
        else
        {
            Average = Loc.F("Report_AvgDay", Fmt.Bytes(r.Totals.Total / (double)Math.Max(1, r.Days)));
            Peak = peak is { Total: > 0 } ? Loc.F("Report_PeakDay", peak.StartLocal.ToString("d", c), Fmt.Bytes(peak.Total)) : "";
            ChartTitle = Loc.T("Report_ByDays");
        }

        var theme = _host.Theme;
        ColumnSeries = new ISeries[]
        {
            ChartFactory.Columns(theme, r.Buckets.Select(b => (double)b.Rx).ToList(), true, Loc.T("Common_Download"), v => Fmt.Bytes(v)),
            ChartFactory.Columns(theme, r.Buckets.Select(b => (double)b.Tx).ToList(), false, Loc.T("Common_Upload"), v => Fmt.Bytes(v))
        };
        XAxes = ChartFactory.LabelsXAxis(theme, r.Buckets.Select(b => b.Label).ToList());
        YAxes = ChartFactory.BytesYAxis(theme);
        LegendPaint = new SolidColorPaint(theme.LabelColor);

        // Круговая диаграмма: топ-7 + «Остальные».
        var apps = r.Apps.Where(a => a.Total > 0).ToList();
        var appsTotal = Math.Max(1, apps.Sum(a => a.Total));
        var pie = new List<ISeries>();
        var top = apps.Take(7).ToList();
        for (var i = 0; i < top.Count; i++)
            pie.Add(Pie(top[i].ExeName, top[i].Total, ThemeService.PiePalette[i % ThemeService.PiePalette.Length], appsTotal));
        var rest = apps.Skip(7).Sum(a => a.Total);
        if (rest > 0) pie.Add(Pie(Loc.T("Report_Others"), rest, ThemeService.PiePalette[^1], appsTotal));
        PieSeries = pie.ToArray();

        TopApps.Clear();
        foreach (var a in apps.Take(15))
        {
            var path = a.Path.Length > 2 && a.Path[1] == ':' ? a.Path : "";
            var item = new ProcessItem(a.Path, a.ExeName, path)
            {
                AppId = a.AppId,
                PeriodDown = a.Rx,
                PeriodUp = a.Tx,
                Share = (double)a.Total / appsTotal,
                Icon = _host.Icons.TryGet(path)
            };
            if (item.Icon == null && path.Length > 0) _ = LoadIconAsync(item);
            TopApps.Add(item);
        }
        HasApps = TopApps.Count > 0;
    }

    private static string ChangeText(long current, long previous) =>
        previous <= 0 ? Loc.T("Report_NoPrev") : $"{Fmt.Change(current, previous)} {Loc.T("Report_VsPrev")}";

    private PieSeries<double> Pie(string name, long value, SKColor color, long total) => new()
    {
        Name = name,
        Values = new[] { (double)value },
        Fill = new SolidColorPaint(color),
        Stroke = null,
        InnerRadius = 60,
        HoverPushout = 6,
        ToolTipLabelFormatter = p => $"{Fmt.Bytes(p.Coordinate.PrimaryValue)} ({Fmt.Percent((double)value / total)})"
    };

    private async Task LoadIconAsync(ProcessItem item) => item.Icon = await _host.Icons.GetAsync(item.Path);

    [RelayCommand]
    private void OpenApp(ProcessItem? item)
    {
        if (item == null) return;
        _host.ProcessDetailsVm.Load(item.Key, item.Name, item.Path);
        _host.Navigate(typeof(Views.Pages.ProcessDetailsPage));
    }

    [RelayCommand]
    private async Task ExportCsvAsync() => await ExportAsync("csv");

    [RelayCommand]
    private async Task ExportXlsxAsync() => await ExportAsync("xlsx");

    private async Task ExportAsync(string ext)
    {
        if (_report == null) return;
        var dlg = new SaveFileDialog
        {
            FileName = $"NetpulseGUI_{_report.From:yyyy-MM-dd}_{_report.To:yyyy-MM-dd}.{ext}",
            Filter = ext == "csv" ? "CSV (*.csv)|*.csv" : "Excel (*.xlsx)|*.xlsx",
            AddExtension = true
        };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            if (ext == "csv") await ReportService.ExportCsvAsync(_report, dlg.FileName);
            else await ReportService.ExportXlsxAsync(_report, dlg.FileName);
            _host.MainVm.ShowMessage(Loc.T("Report_Exported"), dlg.FileName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Ошибка экспорта");
            _host.MainVm.ShowError(Loc.T("Report_ExportFailed"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
