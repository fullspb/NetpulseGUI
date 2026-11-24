using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using NetpulseGUI.Core.Updates;

namespace NetpulseGUI.Services;

/// <summary>
/// Автообновление через GitHub Releases.
/// Установленная версия (рядом лежит unins000.exe) обновляется тихим запуском установщика,
/// портативная — заменой exe на месте с перезапуском.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "fullspb/NetpulseGUI";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NetpulseGUI.exe");

    /// <summary>Lite-сборка (без встроенного .NET) — помечается атрибутом при публикации.</summary>
    public static bool IsLite { get; } = typeof(App).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .Any(a => a.Key == "NetpulseGUI.Flavor" && a.Value == "lite");

    /// <summary>Приложение установлено установщиком Inno Setup (а не запущено как портативный exe).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(Path.GetDirectoryName(CurrentExe)!, "unins000.exe"));

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetpulseGUI", CurrentVersion.ToString()));
        return http;
    }

    /// <summary>Возвращает обновление, если на GitHub есть версия новее текущей, иначе null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
        return ReleaseParser.Parse(doc.RootElement, CurrentVersion, IsInstalled, RepositoryUrl, IsLite);
    }

    /// <summary>Скачивает файл обновления во временную папку и проверяет размер и SHA-256.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NetpulseGUI-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{info.Version}-{info.AssetName}");
        var partial = target + ".part";

        using (var response = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.AssetSize;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var length = new FileInfo(partial).Length;
        if (info.AssetSize > 0 && length != info.AssetSize)
            throw new InvalidDataException($"Размер файла {length} не совпадает с ожидаемым {info.AssetSize}");

        if (info.Sha256 != null)
        {
            await using var check = File.OpenRead(partial);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (hash != info.Sha256)
                throw new InvalidDataException("Контрольная сумма SHA-256 не совпадает — файл повреждён");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Запускает установку. После вызова приложение должно завершиться (AppHost.Exit),
    /// чтобы установщик или новый exe смогли занять его место.
    /// </summary>
    public void Apply(UpdateInfo info, string downloadedFile)
    {
        if (info.IsInstaller)
        {
            // Пауза ~3 с, чтобы мы успели корректно выйти (запись буфера, остановка ETW).
            // «start» запускает установщик через ShellExecute — при необходимости появится запрос UAC.
            // Установщик в тихом режиме сам запускает приложение после установки.
            var args = $"/c ping -n 4 127.0.0.1 >nul & start \"\" \"{downloadedFile}\" /SILENT /SUPPRESSMSGBOXES /NORESTART";
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            Serilog.Log.Information("Обновление {Version}: запущен установщик {File}", info.Version, downloadedFile);
            return;
        }

        // Lite-версия приходит в zip: достаём из архива exe.
        if (downloadedFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            downloadedFile = ExtractExe(downloadedFile);

        // Портативная версия: запущенный exe можно переименовать, затем кладём новый на его место.
        var exe = CurrentExe;
        var old = exe + ".old";
        File.Move(exe, old, overwrite: true);
        try
        {
            File.Move(downloadedFile, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }

        Process.Start(new ProcessStartInfo(exe, ElevationService.RestartedArg)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        });
        Serilog.Log.Information("Обновление {Version}: exe заменён, перезапуск", info.Version);
    }

    private static string ExtractExe(string zipPath)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                    && !e.FullName.Contains('/') && !e.FullName.Contains('\\'))
                    ?? throw new InvalidDataException("В архиве обновления нет exe");
        var target = Path.Combine(Path.GetDirectoryName(zipPath)!, Path.GetFileNameWithoutExtension(zipPath) + ".exe");
        System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, target, overwrite: true);
        return target;
    }

    /// <summary>Удаляет остатки прошлого обновления: старый exe и скачанные файлы.</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var old = CurrentExe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Не удалось удалить старый exe");
        }

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "NetpulseGUI-update");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Не удалось удалить папку загрузки обновлений");
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            // Через explorer.exe: браузер откроется без прав администратора, даже если NetpulseGUI запущен с ними.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть {Url}", url);
        }
    }
}
