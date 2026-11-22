using System.Globalization;
using System.Text;

namespace NetpulseGUI.Core.Storage;

/// <summary>Экспорт отчётов в CSV (разделитель «;», UTF-8 с BOM — корректно открывается в Excel).</summary>
public static class CsvExporter
{
    public static async Task ExportDailyAsync(TrafficRepository repo, DateOnly from, DateOnly toInclusive, string path, CancellationToken ct = default)
    {
        var rows = await repo.GetDailySeriesAsync(from, toInclusive, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine("date;download_bytes;upload_bytes;total_bytes;download_mb;upload_mb");
        foreach (var (day, t) in rows)
        {
            sb.Append(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(';')
              .Append(t.Rx).Append(';').Append(t.Tx).Append(';').Append(t.Total).Append(';')
              .Append((t.Rx / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture)).Append(';')
              .Append((t.Tx / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture)).AppendLine();
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), ct).ConfigureAwait(false);
    }

    public static async Task ExportAppsAsync(TrafficRepository repo, DateOnly from, DateOnly toInclusive, string path, CancellationToken ct = default)
    {
        var rows = await repo.GetAppTotalsAsync(from, toInclusive, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine("app;path;download_bytes;upload_bytes;total_bytes");
        foreach (var a in rows)
        {
            sb.Append(Escape(a.ExeName)).Append(';').Append(Escape(a.Path)).Append(';')
              .Append(a.Rx).Append(';').Append(a.Tx).Append(';').Append(a.Total).AppendLine();
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), ct).ConfigureAwait(false);
    }

    public static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ';', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
