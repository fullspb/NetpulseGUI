using System.IO;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;
using NetpulseGUI.Core.Config;

namespace NetpulseGUI.Services;

/// <summary>
/// Автозапуск через Планировщик заданий: триггер «при входе пользователя», «выполнять с наивысшими правами».
/// В отличие от ключа Run, задача запускает приложение с правами администратора без запроса UAC.
/// Создание/удаление задачи с наивысшими правами требует, чтобы NetpulseGUI был запущен от администратора.
/// </summary>
public sealed class AutoStartService
{
    public const string MinimizedArg = "--minimized";

    private static string UserName => WindowsIdentity.GetCurrent().Name;

    private static string TaskName => $"{AppPaths.AutoStartTaskName} ({UserName.Replace('\\', '_')})";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NetpulseGUI.exe");

    public bool IsEnabled()
    {
        try
        {
            using var ts = new TaskService();
            var task = ts.GetTask(TaskName);
            return task is { Enabled: true };
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать задачу автозапуска");
            return false;
        }
    }

    /// <summary>Если exe перемещён — обновить путь в существующей задаче.</summary>
    public void RepairPathIfNeeded()
    {
        try
        {
            using var ts = new TaskService();
            // Задача от версии с прежним именем NetPulse: заменяем её новой.
            var legacyName = $"{AppPaths.LegacyAutoStartTaskName} ({UserName.Replace('\\', '_')})";
            if (ts.GetTask(legacyName) != null)
            {
                ts.RootFolder.DeleteTask(legacyName, exceptionOnNotExists: false);
                Enable();
                return;
            }
            var task = ts.GetTask(TaskName);
            if (task == null) return;
            var action = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
            if (action != null && !string.Equals(action.Path, ExePath, StringComparison.OrdinalIgnoreCase))
                Enable();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось проверить задачу автозапуска");
        }
    }

    public void Enable()
    {
        using var ts = new TaskService();
        var td = ts.NewTask();
        td.RegistrationInfo.Description = "NetpulseGUI — монитор сетевого трафика. Запуск при входе пользователя.";
        td.RegistrationInfo.Author = "NetpulseGUI";
        td.Principal.UserId = UserName;
        td.Principal.LogonType = TaskLogonType.InteractiveToken;
        td.Principal.RunLevel = TaskRunLevel.Highest;

        td.Triggers.Add(new LogonTrigger { UserId = UserName, Delay = TimeSpan.FromSeconds(5) });
        td.Actions.Add(new ExecAction(ExePath, MinimizedArg, Path.GetDirectoryName(ExePath)));

        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries = false;
        td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        td.Settings.StartWhenAvailable = true;
        td.Settings.AllowHardTerminate = true;
        td.Settings.Priority = System.Diagnostics.ProcessPriorityClass.Normal;

        ts.RootFolder.RegisterTaskDefinition(TaskName, td, TaskCreation.CreateOrUpdate, UserName, null, TaskLogonType.InteractiveToken);
        Serilog.Log.Information("Автозапуск включён: {Task}", TaskName);
    }

    public void Disable()
    {
        using var ts = new TaskService();
        if (ts.GetTask(TaskName) != null)
            ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
        Serilog.Log.Information("Автозапуск выключен");
    }
}
