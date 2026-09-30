using ClosedXML.Excel;
using LeadIntel.Collectors;
using LeadIntel.Data;
using Microsoft.EntityFrameworkCore;
using static LeadIntel.Collectors.FmcsaCollector;

namespace LeadIntel.Services;

/// <summary>
/// Everything FMCSA's "Daily Registration Decisions" page used to publish, as one Excel file.
/// That page stopped listing documents on 2026-05-20 and its PDFs are no longer served; Motus open data carries the
/// same decisions. Each row is enriched with the Motus carrier record (name, phone, address) and the census (email,
/// trucks, officer). Sent to Telegram once per day (collector_runs rows with source 'daily_report').
/// </summary>
public class DailyReport(AppDbContext db, FmcsaCollector fmcsa, TelegramNotifier telegram, ILogger<DailyReport> log)
{
    public const string Source = "daily_report";
    const string OutDir = "reports";
    const int ReadyHour = 10;  // by mid-morning US Eastern the previous day's daily-difference sets are published
    static readonly TimeZoneInfo FmcsaTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    public const string AdminFix = "Admin correction (MOTUS Issue #220)";

    static readonly string[] Cols = ["Date", "Event", "Authority type", "Docket", "USDOT", "Legal name", "DBA", "Phone", "Email", "Officer",
        "Street", "City", "State", "Zip", "Trucks", "Drivers", "Authority status", "BIPD on file ($)", "Insurer", "Suspension effective"];
    static readonly Dictionary<string, string> Notes = new()
    {
        ["4 Regular Routes (passenger)"] = "Motus doesn't mark regular-route authority; this is every passenger-carrier grant.",
        ["5 Name Changes"] = "Motus publishes no name-change decisions; these are insurance filings re-issued under a new name.",
        ["6 Reinstatements"] = "'Admin correction (MOTUS Issue #220)' rows are FMCSA fixing its own data, not real reinstatements.",
        ["7 Transfers"] = "Motus open data publishes no authority transfers.",
        ["8 Broker-FF Financial Security"] = "Broker/freight-forwarder suspension notices, plus surety bond / trust fund cancellations taking effect up to 30 days after the period.",
    };

    /// <summary>A decision row: the source row plus the event text and the decision date (YYYYMMDD).</summary>
    public record Decision(Row Row, string? Event, string? Date, string? AuthType = null)
    {
        public string? this[string key] => Row.Str(key);
    }

    /// <summary>Split Motus rows into the old page's 8 categories. lo/hi: YYYYMMDD.</summary>
    public static Dictionary<string, List<Decision>> Categorize(List<Row> auth, List<Row> orders, List<Row> ins, string lo, string hi)
    {
        static string Reason(Row r) => r.Str("reason") ?? "";
        static string Type(Row r) => r.Str("op_auth_type") ?? "";
        var granted = auth.Where(r => Reason(r).Equals("granted", StringComparison.OrdinalIgnoreCase))
                          .Select(r => new Decision(r, "Granted", r.Str("status_change_date"))).ToList();
        return new()
        {
            ["1 Daily Register"] = auth.Where(r => Reason(r) is "Published to FMCSA Register" or "Revoked" or "Out of Service" || Reason(r).StartsWith("Involuntary Suspension"))
                .Select(r => new Decision(r, Reason(r), r.Str("status_change_date")))
                .Concat(orders.Select(r => new Decision(r, r.Str("order1_type_desc"), r.Str("order1_serve_date")))).ToList(),
            ["2 Certificates of Authority"] = granted.Where(d => !Type(d.Row).StartsWith("Mexico") && !Type(d.Row).Contains("Passengers")).ToList(),
            ["3 OP-2 MX Commercial Zone"] = granted.Where(d => Type(d.Row).StartsWith("Mexico")).ToList(),
            ["4 Regular Routes (passenger)"] = granted.Where(d => Type(d.Row).Contains("Passengers")).ToList(),
            ["5 Name Changes"] = ins.Where(r => r.Str("filing_status_reason") == "NAMECHG"
                    && string.CompareOrdinal(lo, r.Str("cancl_effective_date") ?? "") <= 0 && string.CompareOrdinal(r.Str("cancl_effective_date") ?? "", hi) <= 0)
                .Select(r => new Decision(r, "Insurance filing re-issued under new name (NAMECHG)", r.Str("cancl_effective_date"))).ToList(),
            ["6 Reinstatements"] = auth.Where(r => Reason(r).StartsWith("Reinstated"))
                .Select(r => new Decision(r, Reason(r).Contains("#220") ? AdminFix : Reason(r), r.Str("status_change_date"))).ToList(),
            ["7 Transfers"] = [],
            ["8 Broker-FF Financial Security"] = orders
                .Where(r => (Type(r).StartsWith("Broker") || Type(r).StartsWith("Freight Forwarder")) && (r.Str("order1_type_desc") ?? "").Contains("Involuntary"))
                .Select(r => new Decision(r, r.Str("order1_type_desc"), r.Str("order1_serve_date")))
                .Concat(ins.Where(r => r.Str("ins_type_desc") is "SURETY" or "TRUST FUND" && r.Str("filing_status_reason") == "CANCEL")
                    .Select(r => new Decision(r, $"{r.Str("ins_type_desc")} cancellation effective {r.Str("cancl_effective_date")}",
                                              r.Str("cancl_effective_date"), "(bond / trust fund)"))).ToList(),
        };
    }

    /// <summary>Fetch the decisions for lo..hi, write the workbook, return rows per category.</summary>
    public async Task<Dictionary<string, int>> BuildAsync(DateOnly lo, DateOnly hi, string path)
    {
        string L = lo.ToString("yyyyMMdd"), H = hi.ToString("yyyyMMdd");

        // history + daily-difference set (newest day), one row per decision
        async Task<List<Row>> Both(string ds, string daily, string where, string[] keys)
        {
            var rows = new Dictionary<string, Row>();
            foreach (var r in (await fmcsa.GetAsync(ds, where)).Concat(await fmcsa.GetAsync(daily, where)))
                rows[string.Join("|", keys.Select(k => DocketId(r.Str(k))))] = r;
            return rows.Values.ToList();
        }

        var auth = await Both(MotusAuthority, MotusAuthorityDaily, $"status_change_date between '{L}' and '{H}'",
                              ["usdot_number", "docket_number", "op_auth_type", "reason", "status_change_date"]);
        var orders = await Both(MotusOrders, MotusOrdersDaily, $"order1_serve_date between '{L}' and '{H}'",
                                ["usdot_number", "docket_number", "order1_type_desc", "order1_serve_date"]);
        var ins = await fmcsa.GetAsync(MotusInsuranceHistory, $"cancl_effective_date between '{L}' and '{hi.AddDays(30):yyyyMMdd}'");
        var sheets = Categorize(auth, orders, ins, L, H);
        var all = sheets.Values.SelectMany(x => x).ToList();

        var dockets = all.Select(d => DocketId(d["docket_number"])).Where(k => k != "").Distinct().Order().ToList();
        var dots = all.Select(d => d["usdot_number"]).Where(v => v is { Length: > 0 } && v.All(char.IsAsciiDigit)).Distinct().Order().ToList();
        var carrier = new Dictionary<string, Row>();
        var census = new Dictionary<string, Row>();
        foreach (var part in dockets.Chunk(Chunk))
            foreach (var ds in new[] { MotusCarrier, MotusCarrierDaily })  // daily last, so it wins
                foreach (var r in await fmcsa.GetAsync(ds, $"docket_number in ({In(part)})"))
                    carrier[r.Str("docket_number")!] = r;
        foreach (var part in dots.Chunk(Chunk))
            foreach (var r in await fmcsa.GetAsync(Census, $"dot_number in ({In(part!)})"))
                census[r.Str("dot_number")!] = r;

        object?[] RowOf(Decision d)
        {
            var k = DocketId(d["docket_number"]);
            var dot = d["usdot_number"] ?? "";
            var m = carrier.GetValueOrDefault(k) ?? new Row();
            var s = census.GetValueOrDefault(dot) ?? new Row();
            var date = d.Date ?? "";
            string? Or(string? a, string? b) => string.IsNullOrEmpty(a) ? b : a;
            var bipd = m.Str("bipd_file");
            return [date.Length >= 8 ? $"{date[..4]}-{date[4..6]}-{date[6..8]}" : date, d.Event, d.AuthType ?? Or(d["op_auth_type"], m.Str("op_auth_type")),
                k, dot, Or(m.Str("legal_name"), s.Str("legal_name")), s.Str("dba_name"), Or(m.Str("bus_telno"), s.Str("phone")),
                string.IsNullOrEmpty(s.Str("email_address")) ? null : s.Str("email_address")!.ToLowerInvariant(), s.Str("company_officer_1"),
                Or(m.Str("bus_street_po"), s.Str("phy_street")), Or(m.Str("bus_city"), s.Str("phy_city")),
                Or(m.Str("bus_state_code"), s.Str("phy_state")), Or(m.Str("bus_zip_code"), s.Str("phy_zip")),
                s.Str("power_units"), s.Str("total_drivers"), m.Str("op_auth_status"),
                double.TryParse(bipd, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : null,
                d["insurance_company_name"], d["order1_effective_date"]];
        }

        using var wb = new XLWorkbook();
        var summary = wb.AddWorksheet("Summary");
        summary.Cell(1, 1).Value = $"FMCSA daily decisions {lo:yyyy-MM-dd} to {hi:yyyy-MM-dd}, from Motus open data (data.transportation.gov)";
        summary.Cell(2, 1).InsertData(new[] { new object[] { "Category", "Records", "Note" } });
        var line = 3;
        foreach (var (name, rows) in sheets)
        {
            var ws = wb.AddWorksheet(name.Length > 31 ? name[..31] : name);
            ws.Cell(1, 1).InsertData(new[] { Cols });
            ws.Row(1).Style.Font.Bold = true;
            var data = rows.OrderByDescending(r => r.Date ?? "", StringComparer.Ordinal).Select(RowOf).ToList();
            if (data.Count > 0) ws.Cell(2, 1).InsertData(data);
            ws.SheetView.FreezeRows(1);
            ws.Range(1, 1, Math.Max(1, data.Count + 1), Cols.Length).SetAutoFilter();
            summary.Cell(line++, 1).InsertData(new[] { new object[] { name, rows.Count, Notes.GetValueOrDefault(name, "") } });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        wb.SaveAs(path);

        var real = sheets.ToDictionary(kv => kv.Key, kv => kv.Value.Where(d => d.Event != AdminFix).ToList());
        var counts = real.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        counts["companies"] = real.Values.SelectMany(x => x).Select(d => d["usdot_number"]).Where(v => !string.IsNullOrEmpty(v)).Distinct().Count();
        return counts;
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Build the report for <paramref name="day"/> (default yesterday) and send it to Telegram.
    /// once: skip a day already sent.</summary>
    public async Task<Dictionary<string, object?>?> SendAsync(DateOnly? day = null, bool once = false)
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, FmcsaTz);
        if (day is null)
        {
            day = DateOnly.FromDateTime(now.Date).AddDays(-1);
            if (once && now.Hour < ReadyHour)
            {
                log.LogInformation("daily report for {Day}: FMCSA data not complete before {Hour}:00 US Eastern", day, ReadyHour);
                return null;
            }
        }
        if (once && await db.CollectorRuns.AnyAsync(r => r.Source == Source && r.Status == "success" && r.Cursor == day))
        {
            log.LogInformation("daily report for {Day} already sent", day);
            return null;
        }
        var run = new CollectorRun { Source = Source, Cursor = day };
        db.CollectorRuns.Add(run);
        await db.SaveChangesAsync();
        try
        {
            var path = Path.Combine(OutDir, $"FMCSA-decisions-{day:yyyy-MM-dd}.xlsx");
            var counts = await BuildAsync(day.Value, day.Value, path);
            if (telegram.Configured)
            {
                var caption = string.Join("\n", new[] { $"📋 <b>FMCSA daily decisions — {day:ddd dd MMM yyyy}</b>", $"{counts["companies"]} companies", "" }
                    .Concat(counts.Where(kv => kv.Value > 0 && kv.Key != "companies").Select(kv => $"• {kv.Key[2..]}: {kv.Value}")));
                await telegram.SendDocumentAsync(path, caption);
            }
            // not 'success' unless sent, so `report --once` retries after Telegram is configured
            run.Status = telegram.Configured ? "success" : "built";
            run.Stats = new() { ["counts"] = counts, ["file"] = path, ["sent"] = telegram.Configured };
        }
        catch (Exception e)
        {
            run.Status = "failed";
            run.Error = Truncate($"{e.GetType().Name}: {e.Message}", 2000);
            log.LogError(e, "daily report for {Day} failed", day);
        }
        finally
        {
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        log.LogInformation("daily report {Day}: {Status} {Stats}", day, run.Status, Json.Serialize(run.Stats));
        return run.Stats;
    }
}
