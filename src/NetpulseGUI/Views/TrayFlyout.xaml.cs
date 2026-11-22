using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using NetpulseGUI.Helpers;
using NetpulseGUI.ViewModels;
using Wpf.Ui.Controls;

namespace NetpulseGUI.Views;

/// <summary>
/// Всплывающее окно над треем в стиле системных flyout: появляется у края рабочей области
/// того монитора, где сейчас курсор (учитывая положение панели задач и DPI монитора), и скрывается при потере фокуса.
/// </summary>
public partial class TrayFlyout : FluentWindow
{
    private readonly FlyoutViewModel _vm;

    public DateTime ShownAtUtc { get; private set; }
    public DateTime HiddenAtUtc { get; private set; }

    public TrayFlyout(FlyoutViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };
        // Окно не должно закрываться — только скрываться.
        Closing += (_, e) =>
        {
            if (Application.Current?.ShutdownMode == ShutdownMode.OnExplicitShutdown && !_allowClose)
            {
                e.Cancel = true;
                HideFlyout();
            }
        };
    }

    private bool _allowClose;

    public new void Close()
    {
        _allowClose = true;
        base.Close();
    }

    private void HideFlyout()
    {
        if (!IsVisible) return;
        _vm.IsVisible = false;
        HiddenAtUtc = DateTime.UtcNow;
        Hide();
    }

    public void ShowNearTray()
    {
        _vm.IsVisible = true;
        Opacity = 0;
        // Сначала показываем за пределами экрана, чтобы получить HWND и реальный размер.
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();
        PositionWindow();
        Opacity = 1;
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
        ShownAtUtc = DateTime.UtcNow;
    }

    private void PositionWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.GetCursorPos(out var cursor);
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        var dpi = NativeMethods.GetMonitorDpi(monitor) / 96.0;
        var width = (int)Math.Ceiling(ActualWidth * dpi);
        var height = (int)Math.Ceiling(ActualHeight * dpi);
        var margin = (int)(12 * dpi);
        var work = info.rcWork;
        var mon = info.rcMonitor;

        int x, y;
        if (work.Top > mon.Top) // панель задач сверху
        {
            x = work.Right - width - margin;
            y = work.Top + margin;
        }
        else if (work.Left > mon.Left) // слева
        {
            x = work.Left + margin;
            y = work.Bottom - height - margin;
        }
        else if (work.Right < mon.Right) // справа
        {
            x = work.Right - width - margin;
            y = work.Bottom - height - margin;
        }
        else // снизу (по умолчанию) или автоскрытие
        {
            x = work.Right - width - margin;
            y = work.Bottom - height - margin;
        }

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0, 0x0001 /* SWP_NOSIZE */ | NativeMethods.SWP_NOACTIVATE);
    }
}
