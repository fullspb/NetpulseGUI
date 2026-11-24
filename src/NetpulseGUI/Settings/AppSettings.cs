using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetpulseGUI.Core.Config;
using NetpulseGUI.Core.Formatting;

namespace NetpulseGUI.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    // Общие
    public string Language { get; set; } = "ru";
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public SpeedUnits Units { get; set; } = SpeedUnits.Bits;
    /// <summary>Период обновления интерфейса, сек (сбор всегда раз в секунду).</summary>
    public int UiRefreshSeconds { get; set; } = 1;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool ProcessTrackingEnabled { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Версия, о которой уже показывали уведомление (чтобы не повторять его).</summary>
    public string? NotifiedUpdateVersion { get; set; }

    // Адаптеры
    public List<string> ExcludedAdapterIds { get; set; } = new();
    public bool IncludeVirtualAdapters { get; set; }
    public bool IncludeVpnAdapters { get; set; }

    // Период и лимиты
    public int BillingStartDay { get; set; } = 1;
    public bool DailyLimitEnabled { get; set; }
    public double DailyLimitGb { get; set; } = 5;
    public bool MonthlyLimitEnabled { get; set; }
    public double MonthlyLimitGb { get; set; } = 100;
    /// <summary>Учитывать отдачу в лимите (большинство провайдеров считают ↓+↑).</summary>
    public bool LimitCountsUpload { get; set; } = true;
    public bool Notify80 { get; set; } = true;
    public bool Notify90 { get; set; } = true;
    public bool Notify100 { get; set; } = true;

    // Состояние сработавших порогов (чтобы не повторять уведомления после перезапуска)
    public string? LimitDayKey { get; set; }
    public int LimitDayFired { get; set; }
    public string? LimitPeriodKey { get; set; }
    public int LimitPeriodFired { get; set; }

    // Окно
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 780;
    public bool WindowMaximized { get; set; }
    public int DashboardRangeSeconds { get; set; } = 60;
}

/// <summary>Загрузка/сохранение настроек в %LOCALAPPDATA%\NetpulseGUI\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _sync = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsPath;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    /// <summary>Вызывается после сохранения (на потоке вызывающего).</summary>
    public event Action<AppSettings>? Changed;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options);
                if (s != null)
                {
                    s.BillingStartDay = Math.Clamp(s.BillingStartDay, 1, 31);
                    s.UiRefreshSeconds = Math.Clamp(s.UiRefreshSeconds, 1, 5);
                    s.ExcludedAdapterIds ??= new List<string>();
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать настройки, используются значения по умолчанию");
        }
        return new AppSettings();
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_sync)
        {
            change(Current);
            Save();
        }
        Changed?.Invoke(Current);
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Не удалось сохранить настройки");
            }
        }
    }
}
