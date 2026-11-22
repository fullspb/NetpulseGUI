using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Helpers;
using NetpulseGUI.Localization;

namespace NetpulseGUI.Services;

/// <summary>
/// Значок в области уведомлений: динамическая иконка со скоростью, тултип с итогами,
/// всплывающее окно по левому клику и контекстное меню по правому.
/// Каждый новый HICON освобождается через DestroyIcon сразу после замены — утечек GDI нет.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TrayIconRenderer _renderer = new();
    private TaskbarIcon? _icon;
    private IntPtr _currentHIcon;
    private System.Drawing.Icon? _currentIcon;
    private string _lastKey = "";
    private MenuItem? _pauseItem;

    public event Action? OpenRequested;
    public event Action? FlyoutRequested;
    public event Action? TodayReportRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PauseToggled;

    public void Create()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "NetpulseGUI",
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true
        };
        _icon.TrayLeftMouseUp += (_, _) => FlyoutRequested?.Invoke();
        _icon.TrayMouseDoubleClick += (_, _) => OpenRequested?.Invoke();
        BuildMenu(false);
        Loc.Instance.LanguageChanged += () => BuildMenu(_pauseItem?.IsChecked == true);
        Update(0, 0, false, force: true);
        _icon.ForceCreate(false);
    }

    private void BuildMenu(bool paused)
    {
        if (_icon == null) return;
        var menu = new ContextMenu();
        menu.Items.Add(Item(Loc.T("Tray_Open"), () => OpenRequested?.Invoke(), bold: true));
        _pauseItem = new MenuItem { Header = Loc.T("Tray_Pause"), IsCheckable = true, IsChecked = paused };
        _pauseItem.Click += (_, _) => PauseToggled?.Invoke(_pauseItem.IsChecked);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(Item(Loc.T("Tray_TodayReport"), () => TodayReportRequested?.Invoke()));
        menu.Items.Add(Item(Loc.T("Tray_Settings"), () => SettingsRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("Tray_Exit"), () => ExitRequested?.Invoke()));
        _icon.ContextMenu = menu;
    }

    private static MenuItem Item(string header, Action action, bool bold = false)
    {
        var mi = new MenuItem { Header = header };
        if (bold) mi.FontWeight = FontWeights.SemiBold;
        mi.Click += (_, _) => action();
        return mi;
    }

    public void SetPaused(bool paused)
    {
        if (_pauseItem != null) _pauseItem.IsChecked = paused;
    }

    /// <summary>Обновить иконку (только если изменились цифры/размер/состояние).</summary>
    public void Update(double down, double up, bool paused, bool force = false)
    {
        if (_icon == null) return;
        var d = Fmt.Tray(down);
        var u = Fmt.Tray(up);
        var key = $"{d}|{u}|{paused}|{_renderer.CurrentSize}|{NativeMethods.GetPrimaryMonitorDpi()}";
        if (!force && key == _lastKey) return;
        _lastKey = key;

        var hIcon = _renderer.Render(d, u, paused);
        if (hIcon == IntPtr.Zero) return;
        var newIcon = System.Drawing.Icon.FromHandle(hIcon); // не владеет handle
        try
        {
            _icon.UpdateIcon(newIcon);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Не удалось обновить иконку трея");
        }

        var oldHIcon = _currentHIcon;
        var oldIcon = _currentIcon;
        _currentHIcon = hIcon;
        _currentIcon = newIcon;
        oldIcon?.Dispose();
        if (oldHIcon != IntPtr.Zero) NativeMethods.DestroyIcon(oldHIcon);
    }

    public void UpdateToolTip(LiveSnapshot s, TotalsInfo totals)
    {
        if (_icon == null) return;
        var text = s.Paused
            ? $"NetpulseGUI — {Loc.T("Common_Paused")}"
            : $"NetpulseGUI\n↓ {Fmt.Speed(s.DownBytesPerSec)}   ↑ {Fmt.Speed(s.UpBytesPerSec)}\n" +
              $"{Loc.T("Common_Today")}: {Fmt.Bytes(totals.Today.Total)}\n" +
              $"{Loc.T("Common_Month")}: {Fmt.Bytes(totals.Period.Total)}";
        if (text.Length > 127) text = text[..127];
        if (_icon.ToolTipText != text) _icon.ToolTipText = text;
    }

    /// <summary>Перерисовать (смена DPI/темы панели задач).</summary>
    public void Invalidate()
    {
        _renderer.InvalidateTheme();
        _lastKey = "";
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(title, message, NotificationIcon.Info);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось показать всплывающее уведомление трея");
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
        _currentIcon?.Dispose();
        if (_currentHIcon != IntPtr.Zero) NativeMethods.DestroyIcon(_currentHIcon);
        _currentHIcon = IntPtr.Zero;
        _renderer.Dispose();
    }
}
