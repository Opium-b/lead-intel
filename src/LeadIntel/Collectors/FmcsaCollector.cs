using System.Globalization;
using System.Net;
using System.Text.Json;
using LeadIntel.Config;

namespace LeadIntel.Collectors;

/// <summary>
/// FMCSA open data (data.transportation.gov, Socrata). Public, no auth required.
/// Discovery is problem-first: recent inspections with violations, newly registered carriers and every FMCSA
/// registration decision (Motus) -> census profile -> 12 months of inspections, crashes, violations by category,
/// operating-authority history and insurance.
/// </summary>
public class FmcsaCollector(HttpClient http, Settings settings, ILogger<FmcsaCollector> log)
{
    public const string Name = "fmcsa";
    public const string Base = "https://data.transportation.gov/resource";
    public const string Census = "az4n-8mr2";
    public const string Inspections = "fx4q-ay7w";
    public const string Crashes = "aayw-vxb3";
    public const string Violations = "8mt8-2mdr";  // SMS input: violations with their official BASIC category
    public const string Authority = "9mw4-x3tu";  // AuthHist: MC dockets, grants, revocations (DOT zero-padded to 8)
    public const string Insurance = "qh9u-swkp";  // ActPendInsur: active/pending insurance filings (DOT padded)
    // FMCSA's registration system (Motus). New carriers only appear here. DOT not padded, dates YYYYMMDD.
    public const string MotusAuthority = "yu5v-wbh6";  // authority status changes (pending, granted, suspended, revoked)
    public const string MotusOrders = "wb4f-neki";  // revocation / suspension notices with effective dates
    public const string MotusInsurance = "c5y8-a4uz";  // current insurance filings
    public const string MotusInsuranceHistory = "3uet-3z4i";  // past filings incl. cancellations
    public const string MotusCarrier = "inys-ebih";  // one row per authority: status + which filings are on file
    // "All With History" sets lag a few days; these daily-difference sets carry the latest day's changes.
    public const string MotusAuthorityDaily = "dm5j-zc6c";
    public const string MotusOrdersDaily = "e67p-xyd5";
    public const string MotusCarrierDaily = "nakq-58th";
    public const int HistoryDays = 365;
    public const int NewCarrierDays = 180;
    public const int Chunk = 100;  // DOT numbers per IN (...) query
    const int Page = 10_000;

    readonly List<string> states = settings.CollectStates
        .Select(s => s.ToUpperInvariant()).Where(s => s.Length == 2 && s.All(char.IsLetter)).ToList();

    // ---------- small helpers shared with rules and reports ----------

    static readonly string[] DateFormats = ["yyyyMMdd", "MM/dd/yyyy", "dd-MMM-yy"];

    /// <summary>FMCSA uses 'YYYYMMDD[ HHMM]', 'MM/DD/YYYY' and 'DD-MON-YY' depending on the dataset.</summary>
    public static DateOnly? ParseDate(string? v)
    {
        v = (v ?? "").Trim();
        foreach (var (fmt, n) in DateFormats.Zip([8, 10, 9]))
            if (v.Length >= n && DateOnly.TryParseExact(v[..n], fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        return null;
    }

    public static int? ParseInt(string? v) => int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) ? i : null;

    public static string RowUrl(string dataset, string key, string value) => $"{Base}/{dataset}.json?{key}={value}";

    /// <summary>Values are digit strings or FMCSA docket ids, never user input.</summary>
    public static string In(IEnumerable<string> values) => string.Join(",", values.Select(v => $"'{v}'"));

    /// <summary>Motus writes dockets both as 'MC-123' and 'MC123'.</summary>
    public static string DocketId(string? v) => (v ?? "").Replace("-", "");

    static string Pad(string dot) => dot.PadLeft(8, '0');
    static string Unpad(string? dot) => dot is { Length: > 0 } && dot.All(char.IsDigit) ? long.Parse(dot).ToString() : "";

    // ---------- HTTP ----------

    /// <summary>All matching rows (paged), or at most <paramref name="limit"/>. Retries throttling and 5xx.</summary>
    public async Task<List<Row>> GetAsync(string dataset, string where, int? limit = null, string order = ":id",
                                          string? select = null, CancellationToken ct = default)
    {
        var rows = new List<Row>();
        while (true)
        {
            var size = limit is null ? Page : Math.Min(Page, limit.Value - rows.Count);
            var query = new Dictionary<string, string>
            {
                ["$where"] = where, ["$limit"] = size.ToString(), ["$offset"] = rows.Count.ToString(), ["$order"] = order,
            };
            if (select is not null) query["$select"] = select;
            var url = $"{Base}/{dataset}.json?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            List<Row> page = [];
            for (var attempt = 0; ; attempt++)
            {
                using var r = await http.GetAsync(url, ct);
                if ((r.StatusCode is HttpStatusCode.TooManyRequests || (int)r.StatusCode >= 500) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2), ct);
                    continue;
                }
                r.EnsureSuccessStatusCode();
                page = await r.Content.ReadFromJsonAsync<List<Row>>(ct) ?? [];
                break;
            }
            rows.AddRange(page);
            if (page.Count < size || (limit is not null && rows.Count >= limit)) return rows;
        }
    }

    // ---------- target profile ----------

    /// <summary>decision: named in an FMCSA registration/insurance decision. Every one of those is a lead (brokers
    /// with 0 trucks, suspended or inactive carriers included); only the fleet ceiling applies.</summary>
    public bool InTarget(Row r, bool decision = false)
    {
        var units = ParseInt(r.Str("power_units")) ?? 0;
        if (decision) return units <= settings.TargetMaxFleet;
        if (settings.TargetActiveOnly && r.Str("status_code") != "A") return false;
        var added = ParseDate(r.Str("add_date"));
        var isNew = added is not null && DateOnly.FromDateTime(DateTime.Today).DayNumber - added.Value.DayNumber <= NewCarrierDays;
        return (isNew ? settings.TargetNewCarrierMinFleet : settings.TargetMinFleet) <= units && units <= settings.TargetMaxFleet;
    }

    string StateFilter(string column) => states.Count > 0 ? $" AND {column} in ({In(states)})" : "";

    // ---------- discovery ----------

    public async Task<RawFmcsa> FetchAsync(DateOnly since, int limit, CancellationToken ct = default)
    {
        var s = since.ToString("yyyyMMdd");
        var today = DateOnly.FromDateTime(DateTime.Today);
        // ponytail: newest-first with a hard cap; nationwide volume exceeds one run's cap, so older rows in a busy
        // window are skipped. Raise --limit or set COLLECT_STATES for coverage.
        var flagged = await GetAsync(Inspections, $"insp_date >= '{s}' AND viol_total != '0' AND dot_number != '0'"
            + StateFilter("insp_carrier_state"), limit, "insp_date DESC", "dot_number", ct);
        var newCarriers = await GetAsync(Census, $"add_date >= '{s}' AND status_code = 'A'" + StateFilter("phy_state"),
            Math.Max(limit / 4, 1), "add_date DESC", "dot_number", ct);

        async Task<List<string>> Motus(string where, int? n = null, string[]? datasets = null, string order = "status_change_date DESC")
        {
            var dots = new List<string>();
            foreach (var ds in datasets ?? [MotusAuthority, MotusAuthorityDaily])
                dots.AddRange((await GetAsync(ds, where, n, order, "usdot_number", ct)).Select(r => r.Str("usdot_number") ?? ""));
            return dots;
        }

        // Everything FMCSA's "Daily Registration Decisions" page used to publish (it stopped 2026-05-20; Motus
        // carries the same decisions). Uncapped: the date window bounds it (~1,000 DOTs a day nationwide).
        var granted = await Motus($"reason in ('Granted', 'GRANTED') AND status_change_date >= '{s}'");
        var pending = await Motus($"op_auth_status = 'Pending' AND status_change_date >= '{s}'");
        var reinstated = await Motus($"reason like 'Reinstated%' AND reason not like '%#220%' AND status_change_date >= '{s}'");
        var suspended = await Motus("(reason like 'Involuntary Suspension%' OR reason in ('Revoked', 'Out of Service')) AND "
                                    + $"status_change_date >= '{s}'");
        // suspension notices, voluntary and involuntary, carriers and brokers / freight forwarders
        var noticed = await Motus($"order1_serve_date >= '{s}'", null, [MotusOrders, MotusOrdersDaily], "order1_serve_date DESC");
        // broker / freight forwarder financial security (bond, trust fund) cancellations and name changes
        var bonds = await Motus($"ins_type_desc in ('SURETY', 'TRUST FUND') AND filing_status_reason = 'CANCEL' AND "
                                + $"cancl_effective_date between '{s}' and '{today.AddDays(30):yyyyMMdd}'",
                                null, [MotusInsuranceHistory], "cancl_effective_date");
        var renamed = await Motus($"filing_status_reason = 'NAMECHG' AND cancl_effective_date >= '{s}'",
                                  null, [MotusInsuranceHistory], "cancl_effective_date");
        var (lo, hi) = (today.AddDays(-7), today.AddDays(45));
        // ponytail: no filing date in these sets, so each run re-reads the upcoming window (capped); tracked companies
        // just get refreshed. A cursor needs a filing date FMCSA doesn't publish.
        var cancelling = await Motus($"filing_status_reason = 'CANCEL' AND cancl_effective_date between '{lo:yyyyMMdd}' and '{hi:yyyyMMdd}'",
                                     Math.Max(limit / 2, 1), [MotusInsuranceHistory], "cancl_effective_date");
        var months = new[] { lo, today, hi, today.AddDays(30) }.Select(d => $"{d:MM}/%/{d:yyyy}").Distinct().Order();
        cancelling.AddRange((await GetAsync(Insurance, string.Join(" OR ", months.Select(m => $"cancl_effective_date like '{m}'")),
                                            Math.Max(limit / 2, 1), select: "dot_number", ct: ct)).Select(r => Unpad(r.Str("dot_number"))));

        var decided = granted.Concat(pending).Concat(reinstated).Concat(suspended).Concat(noticed).Concat(bonds).Concat(renamed).ToList();
        var dots = flagged.Concat(newCarriers).Select(r => r.Str("dot_number") ?? "").Concat(decided).Concat(cancelling)
            .Where(d => d.Length > 0 && d.All(char.IsDigit)).Distinct().ToList();
        log.LogInformation("fmcsa discovery: {Flagged} flagged inspections, {New} new carriers, {Granted} granted, {Pending} pending, "
            + "{Reinstated} reinstated, {Suspended} suspended/revoked, {Noticed} suspension notices, {Bonds} bond cancellations, "
            + "{Renamed} name changes, {Cancelling} cancelling insurance, {Dots} unique DOTs", flagged.Count, newCarriers.Count,
            granted.Count, pending.Count, reinstated.Count, suspended.Count, noticed.Count, bonds.Count, renamed.Count, cancelling.Count, dots.Count);
        return await FetchCompaniesAsync(dots, decided: decided.ToHashSet(), ct: ct);
    }

    /// <summary>Profile + full recent history for specific DOT numbers. Discovery applies the target profile; refresh
    /// doesn't, so tracked companies that change (e.g. go inactive) are still updated.</summary>
    public async Task<RawFmcsa> FetchCompaniesAsync(List<string> dots, bool applyTarget = true, ISet<string>? decided = null,
                                                    CancellationToken ct = default)
    {
        var raw = new RawFmcsa();
        foreach (var chunk in dots.Chunk(Chunk))
            raw.Census.AddRange((await GetAsync(Census, $"dot_number in ({In(chunk)})", ct: ct))
                .Where(r => !applyTarget || InTarget(r, decided?.Contains(r.Str("dot_number") ?? "") == true)));
        var targets = raw.Census.Select(r => r.Str("dot_number")!).ToList();
        log.LogInformation("fmcsa profiles: kept {Kept} of {Total} DOTs", targets.Count, dots.Count);

        var since = DateOnly.FromDateTime(DateTime.Today).AddDays(-HistoryDays).ToString("yyyyMMdd");
        foreach (var chunk in targets.Chunk(Chunk))
        {
            string plain = In(chunk), padded = In(chunk.Select(Pad));
            raw.Inspections.AddRange(await GetAsync(Inspections, $"dot_number in ({plain}) AND insp_date >= '{since}'", ct: ct));
            raw.Crashes.AddRange(await GetAsync(Crashes, $"dot_number in ({plain}) AND report_date >= '{since}'", ct: ct));
            raw.Violations.AddRange(await GetAsync(Violations, $"dot_number in ({plain})", ct: ct));
            raw.Authority.AddRange(await GetAsync(Authority, $"dot_number in ({padded})", ct: ct));
            raw.Insurance.AddRange(await GetAsync(Insurance, $"dot_number in ({padded})", ct: ct));
            // history first, then the daily set, so the newest copy of a row wins on ingest
            foreach (var ds in new[] { MotusAuthority, MotusAuthorityDaily })
                raw.MotusAuthority.AddRange(await GetAsync(ds, $"usdot_number in ({plain})", ct: ct));
            foreach (var ds in new[] { MotusOrders, MotusOrdersDaily })
                raw.MotusOrders.AddRange(await GetAsync(ds, $"usdot_number in ({plain})", ct: ct));
            foreach (var ds in new[] { MotusCarrier, MotusCarrierDaily })
                raw.MotusCarrier.AddRange(await GetAsync(ds, $"usdot_number in ({plain})", ct: ct));
            raw.MotusInsurance.AddRange(await GetAsync(MotusInsurance, $"usdot_number in ({plain})", ct: ct));
            raw.MotusInsuranceHistory.AddRange(await GetAsync(MotusInsuranceHistory,
                $"usdot_number in ({plain}) AND cancl_effective_date >= '{since}'", ct: ct));
        }
        return raw;
    }

    // ---------- normalize ----------

    public static NormalizedBatch Normalize(RawFmcsa raw)
    {
        var companies = new List<CompanyRecord>();
        var events = new List<EventRecord>();
        var known = new HashSet<string>();
        var mc = new Dictionary<string, string>();
        var today = DateOnly.FromDateTime(DateTime.Today);

        foreach (var r in raw.Authority)  // prefer an MC docket without a disposition (still active)
        {
            string dot = Unpad(r.Str("dot_number")), docket = r.Str("docket_number") ?? "";
            if (docket.StartsWith("MC") && (!mc.ContainsKey(dot) || string.IsNullOrEmpty(r.Str("disp_action_desc"))))
                mc[dot] = docket;
        }
        foreach (var r in raw.MotusAuthority.OrderBy(r => r.Str("status_change_date") ?? "", StringComparer.Ordinal))
        {
            string dot = r.Str("usdot_number") ?? "", docket = DocketId(r.Str("docket_number"));
            if (docket.StartsWith("MC") && r.Str("op_auth_status") is "Active" or "Pending" && !mc.ContainsKey(dot))
                mc[dot] = docket;
        }

        foreach (var r in raw.Census)
        {
            var dot = r.Str("dot_number") ?? "";
            if (dot.Length == 0 || !dot.All(char.IsDigit) || string.IsNullOrEmpty(r.Str("legal_name"))) continue;
            known.Add(dot);
            companies.Add(ToCompany(r, mc.GetValueOrDefault(dot)));
            events.Add(new(Name, "census", dot, dot, today, RowUrl(Census, "dot_number", dot), r));
        }

        DateOnly? newest = null;
        foreach (var r in raw.Inspections)
        {
            var d = ParseDate(r.Str("insp_date"));
            if (!known.Contains(r.Str("dot_number") ?? "") || string.IsNullOrEmpty(r.Str("inspection_id")) || d is null) continue;
            newest = newest is null || d > newest ? d : newest;
            var id = r.Str("inspection_id")!;
            events.Add(new(Name, "inspection", id, r.Str("dot_number")!, d, RowUrl(Inspections, "inspection_id", id), r));
        }
        foreach (var r in raw.Crashes)
        {
            var d = ParseDate(r.Str("report_date"));
            if (!known.Contains(r.Str("dot_number") ?? "") || string.IsNullOrEmpty(r.Str("crash_id")) || d is null) continue;
            var id = r.Str("crash_id")!;
            events.Add(new(Name, "crash", id, r.Str("dot_number")!, d, RowUrl(Crashes, "crash_id", id), r));
        }
        foreach (var r in raw.Violations)
        {
            var d = ParseDate(r.Str("insp_date"));
            if (!known.Contains(r.Str("dot_number") ?? "") || string.IsNullOrEmpty(r.Str("unique_id")) || d is null) continue;
            var uid = r.Str("unique_id")!;
            events.Add(new(Name, "violation", $"{uid}:{r.Str("viol_code")}:{r.Str("viol_unit")}", r.Str("dot_number")!, d,
                           RowUrl(Violations, "unique_id", uid), r));
        }
        foreach (var r in raw.Authority)
        {
            var dot = Unpad(r.Str("dot_number"));
            var docket = r.Str("docket_number");
            if (!known.Contains(dot) || string.IsNullOrEmpty(docket)) continue;
            var d = ParseDate(r.Str("disp_served_date")) ?? ParseDate(r.Str("orig_served_date"));
            events.Add(new(Name, "authority", $"{docket}:{r.Str("sub_number") ?? "0"}", dot, d, RowUrl(Authority, "docket_number", docket), r));
        }
        foreach (var r in raw.Insurance)
        {
            var dot = Unpad(r.Str("dot_number"));
            if (!known.Contains(dot) || string.IsNullOrEmpty(r.Str("policy_no"))) continue;
            var ext = $"{r.Str("docket_number")}:{r.Str("policy_no")}:{r.Str("ins_form_code")}:{r.Str("effective_date")}";
            events.Add(new(Name, "insurance", ext, dot, ParseDate(r.Str("effective_date")), RowUrl(Insurance, "dot_number", Pad(dot)), r));
        }

        (string Type, List<Row> Rows, string Dataset, string DateKey, string[] IdKeys)[] motus =
        [
            ("authority_status", raw.MotusAuthority, MotusAuthority, "status_change_date",
             ["docket_number", "op_auth_type", "op_auth_status", "status_change_date"]),
            ("authority_order", raw.MotusOrders, MotusOrders, "order1_serve_date", ["docket_number", "order1_type_desc", "order1_serve_date"]),
            ("insurance", raw.MotusInsurance, MotusInsurance, "effective_date", ["docket_number", "policy_no", "ins_form_code", "effective_date"]),
            ("insurance_history", raw.MotusInsuranceHistory, MotusInsuranceHistory, "cancl_effective_date",
             ["docket_number", "policy_no", "ins_form_code", "effective_date", "cancl_effective_date"]),
            ("authority_filings", raw.MotusCarrier, MotusCarrier, "", ["docket_number", "op_auth_type"]),
        ];
        foreach (var (type, rows, dataset, dateKey, idKeys) in motus)
            foreach (var r in rows)
            {
                var dot = r.Str("usdot_number") ?? "";
                if (!known.Contains(dot)) continue;
                // the DOT is part of the id: some rows (voluntary suspension notices) have no docket, and without it
                // every company served the same day would share one id and overwrite each other
                var ext = $"motus:{dot}:" + string.Join(":", idKeys.Select(k => r.Str(k) ?? ""));
                events.Add(new(Name, type, ext, dot, ParseDate(r.Str(dateKey)), RowUrl(dataset, "usdot_number", dot), r));
            }

        return new NormalizedBatch
        {
            Companies = companies, Events = events, Cursor = newest,
            Stats = new() { ["companies"] = companies.Count, ["events"] = events.Count },
        };
    }

    static CompanyRecord ToCompany(Row r, string? mcNumber)
    {
        var dot = r.Str("dot_number")!;
        var url = RowUrl(Census, "dot_number", dot);
        var contacts = new List<ContactRecord>();
        string Digits(string k) => new((r.Str(k) ?? "").Where(char.IsDigit).ToArray());
        foreach (var (key, kind, label) in new[] { ("phone", "phone", "Registered phone"), ("cell_phone", "phone", "Registered cell phone"),
                                                    ("fax", "fax", "Registered fax") })
            if (Digits(key) is { Length: >= 10 } number)
                contacts.Add(new(kind, number, "fmcsa_census", url, label));
        if ((r.Str("email_address") ?? "").Trim().ToLowerInvariant() is { } email && email.Contains('@'))
            contacts.Add(new("email", email, "fmcsa_census", url, "Registered email"));
        foreach (var key in new[] { "company_officer_1", "company_officer_2" })
            if (TitleCase(string.Join(' ', (r.Str(key) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))) is { Length: > 0 } name)
                contacts.Add(new("person", name, "fmcsa_census", url, "Company officer"));
        string? street = r.Str("phy_street"), city = r.Str("phy_city"), st = r.Str("phy_state"), zip = r.Str("phy_zip");
        string? mStreet = r.Str("carrier_mailing_street"), mCity = r.Str("carrier_mailing_city"),
                mSt = r.Str("carrier_mailing_state"), mZip = r.Str("carrier_mailing_zip");
        if (!string.IsNullOrEmpty(mStreet) && mStreet.Trim() != (street ?? "").Trim())  // same as the yard address adds nothing
        {
            var mailing = string.Join(", ", new[] { mStreet.Trim(), mCity, $"{mSt} {mZip}".Trim() }.Where(p => !string.IsNullOrEmpty(p)));
            contacts.Add(new("address", mailing, "fmcsa_census", url, "Mailing address"));
        }
        var location = string.Join(", ", new[] { street, city, $"{st} {zip}".Trim() }.Where(p => !string.IsNullOrEmpty(p)));
        string[] attributeKeys = ["mcs150_date", "carrier_operation", "classdef", "hm_ind", "business_org_desc", "fleetsize", "mcs150_mileage"];
        return new CompanyRecord
        {
            DotNumber = dot,
            Name = r.Str("legal_name")!.Trim(),
            DbaName = (r.Str("dba_name") ?? "").Trim() is { Length: > 0 } dba ? dba : null,
            McNumber = mcNumber,
            State = string.IsNullOrEmpty(st) ? null : st[..Math.Min(2, st.Length)],
            City = city,
            Location = location.Length > 0 ? location : null,
            FleetSize = ParseInt(r.Str("power_units")),
            Drivers = ParseInt(r.Str("total_drivers")),
            OperatingStatus = r.Str("status_code"),
            AddedAt = ParseDate(r.Str("add_date")),
            Attributes = attributeKeys.Where(r.ContainsKey).ToDictionary(k => k, k => r.Str(k)),
            Contacts = contacts,
        };
    }

    /// <summary>Python's str.title(): first letter of every alphabetic run upper-case, the rest lower.</summary>
    public static string TitleCase(string s)
    {
        var chars = s.ToCharArray();
        var prevLetter = false;
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = prevLetter ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
            prevLetter = char.IsLetter(chars[i]);
        }
        return new string(chars);
    }
}

/// <summary>One Socrata row: column name -> JSON value (almost always a string).</summary>
public class Row : Dictionary<string, JsonElement>
{
    public Row() { }
    public Row(IDictionary<string, JsonElement> d) : base(d) { }
}

public static class RowExtensions
{
    public static string? Str(this Row r, string key) => LeadIntel.Data.Json.Str(r, key);

    public static Row With(this Row r, params (string Key, string? Value)[] values)
    {
        var copy = new Row(r);
        foreach (var (k, v) in values) copy[k] = JsonSerializer.SerializeToElement(v);
        return copy;
    }
}

public class RawFmcsa
{
    public List<Row> Census { get; init; } = [];
    public List<Row> Inspections { get; init; } = [];
    public List<Row> Crashes { get; init; } = [];
    public List<Row> Violations { get; init; } = [];
    public List<Row> Authority { get; init; } = [];
    public List<Row> Insurance { get; init; } = [];
    public List<Row> MotusAuthority { get; init; } = [];
    public List<Row> MotusOrders { get; init; } = [];
    public List<Row> MotusInsurance { get; init; } = [];
    public List<Row> MotusInsuranceHistory { get; init; } = [];
    public List<Row> MotusCarrier { get; init; } = [];
}
