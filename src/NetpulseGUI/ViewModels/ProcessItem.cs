using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NetpulseGUI.Helpers;

namespace NetpulseGUI.ViewModels;

/// <summary>Строка процесса: живая скорость, объём за период, спарклайн.</summary>
public sealed partial class ProcessItem : ObservableObject
{
    public ProcessItem(string key, string name, string path)
    {
        Key = key;
        Name = name;
        Path = path;
    }

    public string Key { get; }
    public string Name { get; }
    public string Path { get; }
    public long AppId { get; set; }

    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private double _downSpeed;
    [ObservableProperty] private double _upSpeed;
    [ObservableProperty] private long _periodDown;
    [ObservableProperty] private long _periodUp;
    [ObservableProperty] private double[]? _spark;
    [ObservableProperty] private int _instances;
    [ObservableProperty] private double _share;

    public double TotalSpeed => DownSpeed + UpSpeed;
    public long PeriodTotal => PeriodDown + PeriodUp;

    public string DownSpeedText => DownSpeed > 0 ? Fmt.Speed(DownSpeed) : "—";
    public string UpSpeedText => UpSpeed > 0 ? Fmt.Speed(UpSpeed) : "—";
    public string TotalSpeedText => TotalSpeed > 0 ? Fmt.Speed(TotalSpeed) : "—";
    public string PeriodDownText => Fmt.Bytes(PeriodDown);
    public string PeriodUpText => Fmt.Bytes(PeriodUp);
    public string PeriodTotalText => Fmt.Bytes(PeriodTotal);
    public string ShareText => Fmt.Percent(Share);
    public string ToolTip => string.IsNullOrEmpty(Path) ? Name : Path;

    partial void OnDownSpeedChanged(double value)
    {
        OnPropertyChanged(nameof(DownSpeedText));
        OnPropertyChanged(nameof(TotalSpeed));
        OnPropertyChanged(nameof(TotalSpeedText));
    }

    partial void OnUpSpeedChanged(double value)
    {
        OnPropertyChanged(nameof(UpSpeedText));
        OnPropertyChanged(nameof(TotalSpeed));
        OnPropertyChanged(nameof(TotalSpeedText));
    }

    partial void OnPeriodDownChanged(long value)
    {
        OnPropertyChanged(nameof(PeriodDownText));
        OnPropertyChanged(nameof(PeriodTotal));
        OnPropertyChanged(nameof(PeriodTotalText));
    }

    partial void OnPeriodUpChanged(long value)
    {
        OnPropertyChanged(nameof(PeriodUpText));
        OnPropertyChanged(nameof(PeriodTotal));
        OnPropertyChanged(nameof(PeriodTotalText));
    }

    partial void OnShareChanged(double value) => OnPropertyChanged(nameof(ShareText));

    /// <summary>Перерисовать тексты после смены единиц/языка.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(DownSpeedText));
        OnPropertyChanged(nameof(UpSpeedText));
        OnPropertyChanged(nameof(PeriodDownText));
        OnPropertyChanged(nameof(PeriodUpText));
        OnPropertyChanged(nameof(PeriodTotalText));
        OnPropertyChanged(nameof(ShareText));
    }
}
