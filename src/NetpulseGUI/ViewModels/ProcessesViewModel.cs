using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Time;
using NetpulseGUI.Localization;
using NetpulseGUI.Views.Pages;

namespace NetpulseGUI.ViewModels;

public enum ProcessPeriod
{
    Today,
    Yesterday,
    Week,
    Month,
    AllTime
}

public sealed record PeriodOption(ProcessPeriod Kind, string Label);

public sealed partial class ProcessesViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly Dictionary<string, ProcessItem> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _active;
    private int _loading;
    private DateTime _lastLoadUtc = DateTime.MinValue;

    public ProcessesViewModel(AppHost host)
    {
        _host = host;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessItem.PeriodTotal), ListSortDirection.Descending));
        if (View is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
            live.IsLiveSorting = true;

        BuildPeriods();
        _selectedPeriod = Periods[0];
        host.Live.Tick += OnTick;
        host.Engine.Flushed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_active && DateTime.UtcNow - _lastLoadUtc > TimeSpan.FromSeconds(10)) _ = LoadAsync();
        });
        Loc.Instance.LanguageChanged += () =>
        {
            var kind = SelectedPeriod.Kind;
            BuildPeriods();
            SelectedPeriod = Periods.First(p => p.Kind == kind);
            foreach (var r in Rows) r.RefreshTexts();
        };
    }

    public ObservableCollection<ProcessItem> Rows { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<PeriodOption> Periods { get; } = new();

    [ObservableProperty] private PeriodOption _selectedPeriod;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _processTrackingAvailable;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _summary = "";

    private void BuildPeriods()
    {
        Periods.Clear();
        Periods.Add(new PeriodOption(ProcessPeriod.Today, Loc.T("Period_Today")));
        Periods.Add(new PeriodOption(ProcessPeriod.Yesterday, Loc.T("Period_Yesterday")));
        Periods.Add(new PeriodOption(ProcessPeriod.Week, Loc.T("Period_Week")));
        Periods.Add(new PeriodOption(ProcessPeriod.Month, Loc.T("Period_Month")));
        Periods.Add(new PeriodOption(ProcessPeriod.AllTime, Loc.T("Period_AllTime")));
    }

    partial void OnSelectedPeriodChanged(PeriodOption value)
    {
        if (value != null) _ = LoadAsync();
    }

    partial void OnSearchChanged(string value) => View.Refresh();

    private bool Filter(object o)
    {
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var r = (ProcessItem)o;
        return r.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
               || r.Path.Contains(Search, StringComparison.CurrentCultureIgnoreCase);
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active) _ = LoadAsync();
    }

    private (DateOnly from, DateOnly to) Range()
    {
        var today = TimeBuckets.Today();
        return SelectedPeriod.Kind switch
        {
            ProcessPeriod.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            ProcessPeriod.Week => (today.AddDays(-6), today),
            ProcessPeriod.Month => (BillingPeriod.For(today, _host.Settings.Current.BillingStartDay).Start, today),
            ProcessPeriod.AllTime => (new DateOnly(2000, 1, 1), today),
            _ => (today, today)
        };
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        IsLoading = Rows.Count == 0;
        try
        {
            await _host.Engine.FlushAsync();
            var (from, to) = Range();
            var totals = await Task.Run(() => _host.Repository.GetAppTotalsAsync(from, to));
            _lastLoadUtc = DateTime.UtcNow;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in totals)
            {
                var row = GetOrAdd(t.Path, t.ExeName, LooksLikePath(t.Path) ? t.Path : "");
                row.AppId = t.AppId;
                row.PeriodDown = t.Rx;
                row.PeriodUp = t.Tx;
                seen.Add(row.Key);
            }
            // Строки без трафика за выбранный период и без текущей активности убираем.
            foreach (var row in Rows.Where(r => !seen.Contains(r.Key) && r.TotalSpeed <= 0).ToList())
            {
                Rows.Remove(row);
                _byKey.Remove(row.Key);
            }
            foreach (var row in Rows.Where(r => !seen.Contains(r.Key)))
            {
                row.PeriodDown = 0;
                row.PeriodUp = 0;
            }
            UpdateSummary();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось загрузить список процессов");
        }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    private static bool LooksLikePath(string s) => s.Length > 2 && (s[1] == ':' || s.StartsWith(@"\\", StringComparison.Ordinal));

    private ProcessItem GetOrAdd(string key, string name, string path)
    {
        if (_byKey.TryGetValue(key, out var row)) return row;
        row = new ProcessItem(key, name, path) { Icon = _host.Icons.TryGet(path) };
        if (row.Icon == null && path.Length > 0)
            _ = Task.Run(async () =>
            {
                var icon = await _host.Icons.GetAsync(path);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => row.Icon = icon);
            });
        _byKey[key] = row;
        Rows.Add(row);
        return row;
    }

    private void OnTick(LiveSnapshot s)
    {
        ProcessTrackingAvailable = s.ProcessTrackingAvailable;
        if (!_active) return;

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in s.Processes)
        {
            var row = GetOrAdd(p.Key, p.Name, p.Path);
            row.DownSpeed = p.DownBytesPerSec;
            row.UpSpeed = p.UpBytesPerSec;
            row.Instances = p.InstanceCount;
            live.Add(p.Key);
        }
        foreach (var row in Rows)
        {
            if (!live.Contains(row.Key) && row.TotalSpeed > 0)
            {
                row.DownSpeed = 0;
                row.UpSpeed = 0;
            }
            row.Spark = _host.Live.GetSparkline(row.Key);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var active = Rows.Count(r => r.TotalSpeed > 0);
        Summary = Loc.F("Processes_Summary", Rows.Count, active);
    }

    [RelayCommand]
    private void OpenDetails(ProcessItem? item)
    {
        if (item == null) return;
        _host.ProcessDetailsVm.Load(item.Key, item.Name, item.Path);
        _host.Navigate(typeof(ProcessDetailsPage));
    }
}
