using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using NetpulseGUI.Core.Models;
using NetpulseGUI.Core.Storage;
using NetpulseGUI.Core.Time;
using NetpulseGUI.Localization;

namespace NetpulseGUI.Services;

public sealed record ReportBucket(DateTime StartLocal, string Label, long Rx, long Tx)
{
    public long Total => Rx + Tx;
}

public sealed class ReportData
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public DateOnly PrevFrom { get; init; }
    public DateOnly PrevTo { get; init; }
    public bool Hourly { get; init; }
    public IReadOnlyList<ReportBucket> Buckets { get; init; } = Array.Empty<ReportBucket>();
    public TrafficTotals Totals { get; init; }
    public TrafficTotals PrevTotals { get; init; }
    public IReadOnlyList<AppTotal> Apps { get; init; } = Array.Empty<AppTotal>();
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>Построение отчётов за период и экспорт в CSV/XLSX.</summary>
public sealed class ReportService
{
    private readonly TrafficRepository _repo;

    public ReportService(TrafficRepository repo) => _repo = repo;

    public async Task<ReportData> BuildAsync(DateOnly from, DateOnly to, DateOnly prevFrom, DateOnly prevTo, CancellationToken ct = default)
    {
        if (to < from) (from, to) = (to, from);
        var hourly = from == to;
        List<ReportBucket> buckets;

        if (hourly)
        {
            var startUtc = TimeBuckets.LocalDayStartUtc(from);
            var endUtc = TimeBuckets.LocalDayStartUtc(from.AddDays(1));
            var series = await _repo.GetSeriesAsync(Granularity.Hour, startUtc, endUtc, ct: ct).ConfigureAwait(false);
            var byHour = new long[24, 2];
            foreach (var p in series)
            {
                var local = p.TimeUtc.ToLocalTime();
                var h = Math.Clamp(local.Hour, 0, 23);
                byHour[h, 0] += p.Rx;
                byHour[h, 1] += p.Tx;
            }
            buckets = Enumerable.Range(0, 24)
                .Select(h => new ReportBucket(from.ToDateTime(new TimeOnly(h, 0)), h.ToString("00", CultureInfo.InvariantCulture), byHour[h, 0], byHour[h, 1]))
                .ToList();
        }
        else
        {
            var daily = (await _repo.GetDailySeriesAsync(from, to, ct).ConfigureAwait(false)).ToDictionary(d => d.Day, d => d.Totals);
            buckets = new List<ReportBucket>();
            var shortFormat = Loc.Instance.Culture.Name.StartsWith("ru", StringComparison.Ordinal) ? "dd.MM" : "MM/dd";
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                daily.TryGetValue(d, out var t);
                buckets.Add(new ReportBucket(d.ToDateTime(TimeOnly.MinValue), d.ToString(shortFormat, CultureInfo.InvariantCulture), t.Rx, t.Tx));
            }
        }

        var totals = await _repo.GetTotalsAsync(from, to, ct: ct).ConfigureAwait(false);
        var prev = await _repo.GetTotalsAsync(prevFrom, prevTo, ct: ct).ConfigureAwait(false);
        var apps = await _repo.GetAppTotalsAsync(from, to, ct).ConfigureAwait(false);

        return new ReportData
        {
            From = from,
            To = to,
            PrevFrom = prevFrom,
            PrevTo = prevTo,
            Hourly = hourly,
            Buckets = buckets,
            Totals = totals,
            PrevTotals = prev,
            Apps = apps
        };
    }

    public static Task ExportCsvAsync(ReportData report, string path) => Task.Run(() =>
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine($"# NetpulseGUI {report.From:yyyy-MM-dd} — {report.To:yyyy-MM-dd}");
        sb.AppendLine(report.Hourly ? "hour;download_bytes;upload_bytes;total_bytes" : "date;download_bytes;upload_bytes;total_bytes");
        foreach (var b in report.Buckets)
        {
            var label = report.Hourly ? b.StartLocal.ToString("yyyy-MM-dd HH:00", inv) : b.StartLocal.ToString("yyyy-MM-dd", inv);
            sb.Append(label).Append(';').Append(b.Rx).Append(';').Append(b.Tx).Append(';').Append(b.Total).AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("process;path;download_bytes;upload_bytes;total_bytes");
        foreach (var a in report.Apps)
            sb.Append(CsvExporter.Escape(a.ExeName)).Append(';').Append(CsvExporter.Escape(a.Path)).Append(';')
              .Append(a.Rx).Append(';').Append(a.Tx).Append(';').Append(a.Total).AppendLine();
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    });

    public static Task ExportXlsxAsync(ReportData report, string path) => Task.Run(() =>
    {
        const double mb = 1024d * 1024;
        const string mbFormat = "#,##0.00";
        using var wb = new XLWorkbook();

        // Сводка
        var s = wb.Worksheets.Add(Loc.T("Xlsx_Summary"));
        s.Cell(1, 1).Value = "NetpulseGUI";
        s.Cell(1, 1).Style.Font.Bold = true;
        s.Cell(1, 1).Style.Font.FontSize = 16;
        s.Cell(2, 1).Value = Loc.T("Xlsx_Period");
        s.Cell(2, 2).Value = $"{report.From:yyyy-MM-dd} — {report.To:yyyy-MM-dd}";
        s.Cell(3, 1).Value = Loc.T("Xlsx_PrevPeriod");
        s.Cell(3, 2).Value = $"{report.PrevFrom:yyyy-MM-dd} — {report.PrevTo:yyyy-MM-dd}";

        s.Cell(5, 2).Value = Loc.T("Xlsx_CurrentMb");
        s.Cell(5, 3).Value = Loc.T("Xlsx_PrevMb");
        s.Cell(5, 4).Value = Loc.T("Xlsx_Change");
        s.Range(5, 1, 5, 4).Style.Font.Bold = true;
        WriteCompare(s, 6, Loc.T("Common_Download"), report.Totals.Rx, report.PrevTotals.Rx);
        WriteCompare(s, 7, Loc.T("Common_Upload"), report.Totals.Tx, report.PrevTotals.Tx);
        WriteCompare(s, 8, Loc.T("Common_Total"), report.Totals.Total, report.PrevTotals.Total);
        s.Range(6, 2, 8, 3).Style.NumberFormat.Format = mbFormat;
        s.Range(6, 4, 8, 4).Style.NumberFormat.Format = "+0.0%;-0.0%;0%";
        s.Columns().AdjustToContents();

        // По дням / по часам
        var d = wb.Worksheets.Add(report.Hourly ? Loc.T("Xlsx_ByHour") : Loc.T("Xlsx_ByDay"));
        d.Cell(1, 1).Value = report.Hourly ? Loc.T("Xlsx_Hour") : Loc.T("Xlsx_Date");
        d.Cell(1, 2).Value = Loc.T("Common_Download") + ", MB";
        d.Cell(1, 3).Value = Loc.T("Common_Upload") + ", MB";
        d.Cell(1, 4).Value = Loc.T("Common_Total") + ", MB";
        var row = 2;
        foreach (var b in report.Buckets)
        {
            d.Cell(row, 1).Value = b.StartLocal;
            d.Cell(row, 1).Style.DateFormat.Format = report.Hourly ? "yyyy-mm-dd hh:mm" : "yyyy-mm-dd";
            d.Cell(row, 2).Value = b.Rx / mb;
            d.Cell(row, 3).Value = b.Tx / mb;
            d.Cell(row, 4).FormulaA1 = $"B{row}+C{row}";
            row++;
        }
        if (row > 2)
        {
            d.Range(2, 2, row - 1, 4).Style.NumberFormat.Format = mbFormat;
            var table = d.Range(1, 1, row - 1, 4).CreateTable("Traffic");
            table.ShowTotalsRow = true;
            table.Field(1).TotalsRowFunction = XLTotalsRowFunction.Sum;
            table.Field(2).TotalsRowFunction = XLTotalsRowFunction.Sum;
            table.Field(3).TotalsRowFunction = XLTotalsRowFunction.Sum;
        }
        d.Columns().AdjustToContents();

        // Процессы
        var p = wb.Worksheets.Add(Loc.T("Xlsx_Processes"));
        p.Cell(1, 1).Value = Loc.T("Common_Process");
        p.Cell(1, 2).Value = Loc.T("Common_Path");
        p.Cell(1, 3).Value = Loc.T("Common_Download") + ", MB";
        p.Cell(1, 4).Value = Loc.T("Common_Upload") + ", MB";
        p.Cell(1, 5).Value = Loc.T("Common_Total") + ", MB";
        p.Cell(1, 6).Value = Loc.T("Xlsx_Share");
        var total = Math.Max(1, report.Apps.Sum(a => a.Total));
        row = 2;
        foreach (var a in report.Apps)
        {
            p.Cell(row, 1).Value = a.ExeName;
            p.Cell(row, 2).Value = a.Path;
            p.Cell(row, 3).Value = a.Rx / mb;
            p.Cell(row, 4).Value = a.Tx / mb;
            p.Cell(row, 5).Value = a.Total / mb;
            p.Cell(row, 6).Value = (double)a.Total / total;
            row++;
        }
        if (row > 2)
        {
            p.Range(2, 3, row - 1, 5).Style.NumberFormat.Format = mbFormat;
            p.Range(2, 6, row - 1, 6).Style.NumberFormat.Format = "0.0%";
            p.Range(1, 1, row - 1, 6).CreateTable("Processes");
        }
        p.Columns().AdjustToContents();
        p.Column(2).Width = Math.Min(p.Column(2).Width, 70);

        wb.SaveAs(path);
    });

    private static void WriteCompare(IXLWorksheet s, int row, string name, long current, long previous)
    {
        const double mb = 1024d * 1024;
        s.Cell(row, 1).Value = name;
        s.Cell(row, 2).Value = current / mb;
        s.Cell(row, 3).Value = previous / mb;
        if (previous > 0) s.Cell(row, 4).Value = (current - previous) / (double)previous;
        else s.Cell(row, 4).Value = "—";
    }
}
