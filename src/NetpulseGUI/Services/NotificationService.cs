using Microsoft.Toolkit.Uwp.Notifications;

namespace NetpulseGUI.Services;

/// <summary>Тост-уведомления Windows; при недоступности — всплывающая подсказка трея.</summary>
public sealed class NotificationService
{
    private readonly TrayService _tray;
    private bool _toastsBroken;

    public NotificationService(TrayService tray) => _tray = tray;

    public event Action<string>? Activated;

    public void Initialize()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += args => Activated?.Invoke(args.Argument);
        }
        catch (Exception ex)
        {
            _toastsBroken = true;
            Serilog.Log.Warning(ex, "Тосты недоступны, будут использоваться уведомления трея");
        }
    }

    public void Show(string title, string message, string action = "open")
    {
        if (!_toastsBroken)
        {
            try
            {
                new ToastContentBuilder()
                    .AddArgument("action", action)
                    .AddText(title)
                    .AddText(message)
                    .AddAttributionText("NetpulseGUI")
                    .Show();
                return;
            }
            catch (Exception ex)
            {
                _toastsBroken = true;
                Serilog.Log.Warning(ex, "Не удалось показать тост, переключаемся на уведомления трея");
            }
        }
        _tray.ShowBalloon(title, message);
    }
}
