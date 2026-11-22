using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;
using NetpulseGUI.Services;

namespace NetpulseGUI.ViewModels;

/// <summary>Мини-окно над треем: скорость, график за минуту, итоги и топ-3 процессов.</summary>
public sealed partial class FlyoutViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly ObservableCollection<double> _down = new();
    private readonly ObservableCollection<double> _up = new();

    public FlyoutViewModel(AppHost host)
    {
        _host = host;
        for (var i = 0; i < 60; i++)
        {
            _down.Add(0);
            _up.Add(0);
        }
        BuildSeries();
        host.Theme.ThemeChanged += BuildSeries;
        host.Live.Tick += OnTick;
        host.Totals.Updated += OnTotals;
    }

    public bool IsVisible { get; set; }
    public ObservableCollection<ProcessItem> Top { get; } = new();
    public Axis[] XAxes { get; } = ChartFactory.HiddenXAxis();

    [ObservableProperty] private ISeries[] _series = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _yAxes = Array.Empty<Axis>();
    [ObservableProperty] private string _downSpeed = "—";
    [ObservableProperty] private string _upSpeed = "—";
    [ObservableProperty] private string _today = "—";
    [ObservableProperty] private string _month = "—";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _processTrackingAvailable;
    [ObservableProperty] private bool _hasTop;

    private void BuildSeries()
    {
        Series = new ISeries[]
        {
            ChartFactory.LiveLine(_host.Theme, _down, true, Loc.T("Common_Download")),
            ChartFactory.LiveLine(_host.Theme, _up, false, Loc.T("Common_Upload"))
        };
        YAxes = new[] { new Axis { IsVisible = false, MinLimit = 0 } };
    }

    /// <summary>Перед показом — заполнить график из истории.</summary>
    public void Refresh()
    {
        var d = new double[60];
        var u = new double[60];
        _host.Live.CopyHistory(60, d, u);
        for (var i = 0; i < 60; i++)
        {
            _down[i] = d[i];
            _up[i] = u[i];
        }
        OnTotals(_host.Totals.Current);
        Apply(_host.Live.Current);
    }

    private void OnTick(LiveSnapshot s)
    {
        if (!IsVisible) return;
        _down.RemoveAt(0);
        _down.Add(s.DownBytesPerSec);
        _up.RemoveAt(0);
        _up.Add(s.UpBytesPerSec);
        Apply(s);
    }

    private void Apply(LiveSnapshot s)
    {
        DownSpeed = Fmt.Speed(s.DownBytesPerSec);
        UpSpeed = Fmt.Speed(s.UpBytesPerSec);
        ProcessTrackingAvailable = s.ProcessTrackingAvailable;

        var top = s.Processes.Take(3).ToList();
        var max = top.Count > 0 ? top.Max(p => p.TotalBytesPerSec) : 0;
        while (Top.Count > top.Count) Top.RemoveAt(Top.Count - 1);
        for (var i = 0; i < top.Count; i++)
        {
            var p = top[i];
            if (i >= Top.Count || Top[i].Key != p.Key)
            {
                var item = new ProcessItem(p.Key, p.Name, p.Path) { Icon = _host.Icons.TryGet(p.Path) };
                if (item.Icon == null) _ = LoadIconAsync(item);
                if (i < Top.Count) Top[i] = item; else Top.Add(item);
            }
            Top[i].DownSpeed = p.DownBytesPerSec;
            Top[i].UpSpeed = p.UpBytesPerSec;
            Top[i].Share = max > 0 ? p.TotalBytesPerSec / max : 0;
        }
        HasTop = Top.Count > 0;
    }

    private async Task LoadIconAsync(ProcessItem item) => item.Icon = await _host.Icons.GetAsync(item.Path);

    private void OnTotals(TotalsInfo t)
    {
        Today = Fmt.Bytes(t.Today.Total);
        Month = Fmt.Bytes(t.Period.Total);
    }

    [RelayCommand]
    private void Open() => _host.ShowMainWindow();

    [RelayCommand]
    private void TogglePause() => _host.SetPaused(!IsPaused);

    [RelayCommand]
    private void OpenReport()
    {
        _host.ShowMainWindow(typeof(Views.Pages.ReportsPage));
        _host.ReportsVm.ShowToday();
    }
}
