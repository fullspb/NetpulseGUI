using Microsoft.Win32;

namespace NetpulseGUI.Core.Updates;

/// <summary>Проверка, установлен ли в системе .NET Desktop Runtime (нужен lite-сборке).</summary>
public static class DotNetRuntimeProbe
{
    private const string DesktopFramework = "Microsoft.WindowsDesktop.App";

    /// <summary>Есть ли в системе x64 .NET Desktop Runtime версии не ниже <paramref name="minMajor"/>.</summary>
    public static bool HasDesktopRuntime(int minMajor) => HasMajorAtLeast(InstalledDesktopVersions(), minMajor);

    /// <summary>Есть ли среди версий хотя бы одна с мажорной версией не ниже заданной (суффиксы preview/rc игнорируются).</summary>
    public static bool HasMajorAtLeast(IEnumerable<string> versions, int minMajor)
    {
        foreach (var raw in versions)
        {
            var v = raw.Split('-', '+')[0];
            if (Version.TryParse(v, out var parsed) && parsed.Major >= minMajor) return true;
        }
        return false;
    }

    public static IReadOnlyList<string> InstalledDesktopVersions()
    {
        var result = new List<string>();

        // Реестр, который ведут установщики .NET.
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey($@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\{DesktopFramework}");
            if (key != null) result.AddRange(key.GetValueNames());
        }
        catch (Exception)
        {
        }

        // Папка shared — на случай, если запись в реестре отсутствует (распаковка вручную, частичные установки).
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", DesktopFramework);
            if (Directory.Exists(root))
                result.AddRange(Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => n != null)!);
        }
        catch (Exception)
        {
        }

        return result;
    }
}
