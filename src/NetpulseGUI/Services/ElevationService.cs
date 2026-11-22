using System.ComponentModel;
using System.Diagnostics;
using NetpulseGUI.Core.Processes;

namespace NetpulseGUI.Services;

public static class ElevationService
{
    public const string RestartedArg = "--restarted";

    public static bool IsElevated { get; } = ProcessTrafficMonitor.IsElevated();

    /// <summary>Перезапуск с правами администратора. Возвращает false, если пользователь отказался в UAC.</summary>
    public static bool TryRestartElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe, RestartedArg)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }
}
