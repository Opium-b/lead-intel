using System.Globalization;
using static LeadIntel.Collectors.FmcsaCollector;

namespace LeadIntel.Signals;

// Deterministic signal rules. To add one: write a class implementing ISignalRule and add it to SignalEngine.Rules.
// Shared helpers live in RuleHelpers at the bottom of this file.

public class OutOfServiceRule : ISignalRule
{
    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.Recent("inspection", today))
            if (f.Num("oos_total") > 0)
                yield return f.Draft(nameof(OutOfServiceRule), "OUT_OF_SERVICE",
                    $"Out-of-service order at {f.ObservedAt:yyyy-MM-dd} inspection in {f["report_state"]}: " +
                    $"{f.Num("driver_oos_total")} driver / {f.Num("vehicle_oos_total")} vehicle OOS, {f.Num("viol_total")} violation(s)",
                    Severity.High, f.Pick(RuleHelpers.InspectionFields));
    }
}

public class InspectionViolationRule : ISignalRule
{
    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.Recent("inspection", today))
            if (f.Num("viol_total") > 0 && f.Num("oos_total") == 0)
                yield return f.Draft(nameof(InspectionViolationRule), "INSPECTION_VIOLATION",
                    $"{f.Num("viol_total")} violation(s) at {f.ObservedAt:yyyy-MM-dd} inspection in {f["report_state"]}",
                    Severity.Medium, f.Pick(RuleHelpers.InspectionFields));
    }
}

public class RepeatedViolationRule : ISignalRule
{
    const int MinCount = 3;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var inspections = facts.Recent("inspection", today).ToList();
        var bad = inspections.Where(f => f.Num("viol_total") > 0).OrderByDescending(f => f.ObservedAt).ToList();
        if (bad.Count < MinCount) yield break;
        var latest = bad[0];
        yield return new SignalDraft
        {
            Type = "REPEATED_VIOLATIONS", Severity = Severity.High, Source = latest.Source, SourceUrl = latest.SourceUrl,
            Description = $"{bad.Count} of {inspections.Count} inspections in the last 12 months had violations",
            ObservedAt = latest.ObservedAt, DetectedBy = nameof(RepeatedViolationRule), DedupeKey = "REPEATED_VIOLATIONS",
            SourceRecordId = latest.Id,
            Evidence = new()
            {
                ["violating_inspections"] = bad.Count, ["total_inspections"] = inspections.Count,
                ["inspection_ids"] = bad.Take(20).Select(f => f.ExternalId).ToList(), ["latest"] = latest.ObservedAt?.ToString("yyyy-MM-dd"),
            },
        };
    }
}

/// <summary>Share of inspections ending in an OOS order. Normalizes for fleet size / inspection volume.</summary>
public class HighOutOfServiceRateRule : ISignalRule
{
    const int MinInspections = 5;
    const double MinRate = 0.30;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var insp = facts.Recent("inspection", today).ToList();
        var oos = insp.Where(f => f.Num("oos_total") > 0).OrderByDescending(f => f.ObservedAt).ToList();
        if (insp.Count < MinInspections || oos.Count == 0 || (double)oos.Count / insp.Count < MinRate) yield break;
        var rate = (double)oos.Count / insp.Count;
        yield return new SignalDraft
        {
            Type = "HIGH_OOS_RATE", Severity = Severity.High, Source = oos[0].Source, SourceUrl = oos[0].SourceUrl,
            Description = $"{oos.Count} of {insp.Count} inspections ({rate.Pct()}) in the last 12 months ended in an " +
                          $"out-of-service order (threshold {MinRate.Pct()})",
            ObservedAt = oos[0].ObservedAt, DetectedBy = nameof(HighOutOfServiceRateRule), DedupeKey = "HIGH_OOS_RATE",
            SourceRecordId = oos[0].Id,
            Evidence = new()
            {
                ["oos_inspections"] = oos.Count, ["total_inspections"] = insp.Count, ["rate"] = Math.Round(rate, 3),
                ["threshold"] = MinRate, ["inspection_ids"] = oos.Take(20).Select(f => f.ExternalId).ToList(),
            },
        };
    }
}

public class CrashRule : ISignalRule
{
    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.Recent("crash", today))
        {
            int fatal = f.Num("fatalities"), injured = f.Num("injuries");
            var severity = fatal > 0 ? Severity.Critical : injured > 0 ? Severity.High : Severity.Medium;
            var parts = new List<string>();
            if (fatal > 0) parts.Add($"{fatal} fatality(ies)");
            if (injured > 0) parts.Add($"{injured} injury(ies)");
            if (f["tow_away"] == "Y") parts.Add("tow-away");
            var desc = $"Reportable crash on {f.ObservedAt:yyyy-MM-dd} in {f["report_state"]}" + (parts.Count > 0 ? $": {string.Join(", ", parts)}" : "");
            yield return f.Draft(nameof(CrashRule), "CRASH", desc, severity, f.Pick("crash_id", "report_date", "report_state", "city",
                "fatalities", "injuries", "tow_away", "hazmat_released", "vehicles_in_accident"));
        }
    }
}

public class NewCarrierRule : ISignalRule
{
    const int Days = 180;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var c = facts.Census();
        var added = ParseDate(c?["add_date"]);
        if (c is null || added is null || today.DayNumber - added.Value.DayNumber > Days) yield break;
        var s = c.Draft(nameof(NewCarrierRule), "NEW_CARRIER", $"New carrier: USDOT registered on {added:yyyy-MM-dd}", Severity.Low,
                        c.Pick("dot_number", "add_date", "status_code", "power_units"));
        (s.ObservedAt, s.DedupeKey) = (added, "NEW_CARRIER");
        yield return s;
    }
}

/// <summary>MCS-150 must be updated every 24 months; overdue updates can lead to deactivation.</summary>
public class StaleRegistrationRule : ISignalRule
{
    const int Days = 730;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var c = facts.Census();
        var updated = ParseDate(c?["mcs150_date"]);
        if (c is null || updated is null || c["status_code"] != "A" || today.DayNumber - updated.Value.DayNumber <= Days) yield break;
        var age = today.DayNumber - updated.Value.DayNumber;
        var s = c.Draft(nameof(StaleRegistrationRule), "STALE_MCS150",
            $"MCS-150 registration last updated {updated:yyyy-MM-dd} ({age / 30} months ago; biennial update required)", Severity.Medium,
            c.Pick("dot_number", "mcs150_date", "status_code"));
        (s.ObservedAt, s.DedupeKey) = (updated, $"STALE_MCS150:{updated:yyyy-MM-dd}");  // date of the fact, not of our fetch
        yield return s;
    }
}

public class InactiveStatusRule : ISignalRule
{
    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var c = facts.Census();
        var status = c?["status_code"];
        if (c is null || status is null or "A") yield break;
        var s = c.Draft(nameof(InactiveStatusRule), "INACTIVE_STATUS", $"USDOT status is '{status}' (not active)", Severity.Medium,
                        c.Pick("dot_number", "status_code"));
        s.DedupeKey = $"INACTIVE_STATUS:{status}";
        yield return s;
    }
}

/// <summary>One signal per official FMCSA BASIC category, aggregating the company's violations in the last 12 months.</summary>
public class ViolationCategoryRule : ISignalRule
{
    // BASIC category prefix -> (signal type, label, severity, violations needed in 12 months)
    static readonly (string Prefix, string Type, string Label, string Severity, int MinCount)[] Basics =
    [
        ("Hours-of-Service", "HOS_VIOLATIONS", "hours-of-service", Severity.Medium, 1),
        ("Vehicle Maintenance", "MAINTENANCE_VIOLATIONS", "vehicle maintenance", Severity.Medium, 3),
        ("Unsafe Driving", "UNSAFE_DRIVING", "unsafe driving", Severity.Medium, 1),
        ("Driver Fitness", "DRIVER_FITNESS", "driver fitness", Severity.Medium, 1),
        ("Controlled Substances", "DRUG_ALCOHOL", "controlled substances/alcohol", Severity.High, 1),
        ("Hazardous Materials", "HAZMAT_VIOLATIONS", "hazardous materials", Severity.Medium, 1),
    ];

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var violations = facts.Recent("violation", today).ToList();
        foreach (var (prefix, type, label, severity, minCount) in Basics)
        {
            var vs = violations.Where(f => (f["basic_desc"] ?? "").StartsWith(prefix, StringComparison.Ordinal))
                               .OrderByDescending(f => f.ObservedAt).ToList();
            if (vs.Count < minCount || vs.Count == 0) continue;
            var oos = vs.Count(f => f["oos_indicator"] == "true");
            var top = vs.Select(f => $"{f["viol_code"]} {f["section_desc"] ?? ""}".Trim())
                        .GroupBy(k => k).Select(g => (Key: g.Key, N: g.Count(), First: vs.FindIndex(f => $"{f["viol_code"]} {f["section_desc"] ?? ""}".Trim() == g.Key)))
                        .OrderByDescending(g => g.N).ThenBy(g => g.First).Take(5).ToList();
            yield return new SignalDraft
            {
                Type = type, Severity = oos > 0 ? Severity.High : severity, Source = vs[0].Source, SourceUrl = vs[0].SourceUrl,
                Description = $"{vs.Count} {label} violation(s) in the last 12 months" + (oos > 0 ? $", {oos} out-of-service" : "")
                              + $"; most common: {top[0].Key}",
                ObservedAt = vs[0].ObservedAt, DetectedBy = nameof(ViolationCategoryRule), DedupeKey = type, SourceRecordId = vs[0].Id,
                Evidence = new()
                {
                    ["basic"] = vs[0]["basic_desc"], ["violations"] = vs.Count, ["oos_violations"] = oos,
                    ["latest"] = vs[0].ObservedAt?.ToString("yyyy-MM-dd"), ["top_violations"] = top.Select(t => $"{t.N}× {t.Key}").ToList(),
                    ["inspection_ids"] = vs.Select(f => f["unique_id"]).Distinct().Take(20).ToList(),
                },
            };
        }
    }
}

public class AuthorityRevokedRule : ISignalRule
{
    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var seen = new HashSet<string>();
        foreach (var f in facts)
        {
            if (f.RecordType != "authority" || !(f["disp_action_desc"] ?? "").Contains("REVOK")) continue;
            var served = ParseDate(f["disp_served_date"]) ?? ParseDate(f["disp_decided_date"]);
            if (served is null || today.DayNumber - served.Value.DayNumber > 365) continue;
            var s = f.Draft(nameof(AuthorityRevokedRule), "AUTHORITY_REVOKED",
                $"Operating authority {f["docket_number"]} ({(f["mod_col_1"] ?? "").ToLowerInvariant()}) {f["disp_action_desc"]!.ToLowerInvariant()} on {served:yyyy-MM-dd}",
                Severity.High, f.Pick(RuleHelpers.AuthorityFields));
            s.ObservedAt = served;
            seen.Add(DocketId(f["docket_number"]));
            yield return s;
        }
        foreach (var f in facts.MotusLatest().Values)  // Motus revocations, unless already reported above
        {
            var d = ParseDate(f["status_change_date"]);
            if (f["reason"] != "Revoked" || d is null || today.DayNumber - d.Value.DayNumber > 365 || seen.Contains(f.Docket())) continue;
            var s = f.Draft(nameof(AuthorityRevokedRule), "AUTHORITY_REVOKED",
                $"Operating authority {f.Docket()} ({(f["op_auth_type"] ?? "").ToLowerInvariant()}) revoked on {d:yyyy-MM-dd}",
                Severity.High, f.Pick(RuleHelpers.MotusStatusFields));
            s.ObservedAt = d;
            yield return s;
        }
    }
}

public class NewAuthorityRule : ISignalRule
{
    const int Days = 180;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.Where(f => f.RecordType == "authority"))
        {
            var granted = ParseDate(f["orig_served_date"]);
            if (granted is null || f["original_action_desc"] != "GRANTED" || !string.IsNullOrEmpty(f["disp_action_desc"])
                || today.DayNumber - granted.Value.DayNumber > Days) continue;
            var s = f.Draft(nameof(NewAuthorityRule), "NEW_AUTHORITY",
                $"New operating authority {f["docket_number"]} ({(f["mod_col_1"] ?? "").ToLowerInvariant()}) granted {granted:yyyy-MM-dd}",
                Severity.Low, f.Pick(RuleHelpers.AuthorityFields));
            s.ObservedAt = granted;
            yield return s;
        }
        // Motus: a grant counts while that authority is still active
        var latest = facts.MotusLatest();
        foreach (var f in facts.Where(f => f.RecordType == "authority_status"))
        {
            var granted = ParseDate(f["status_change_date"]);
            if (granted is null || !string.Equals(f["reason"], "granted", StringComparison.OrdinalIgnoreCase)
                || today.DayNumber - granted.Value.DayNumber > Days || latest[f.AuthKey()]["op_auth_status"] != "Active") continue;
            var s = f.Draft(nameof(NewAuthorityRule), "NEW_AUTHORITY",
                $"New operating authority {f.Docket()} ({(f["op_auth_type"] ?? "").ToLowerInvariant()}) granted {granted:yyyy-MM-dd}",
                Severity.Low, f.Pick(RuleHelpers.MotusStatusFields));
            s.ObservedAt = granted;
            yield return s;
        }
    }
}

/// <summary>Authority currently suspended because insurance lapsed. It needs a new filing to be reinstated.</summary>
public class InsuranceSuspendedRule : ISignalRule
{
    const int Days = 365;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var latest = facts.MotusLatest();
        foreach (var f in latest.Values)
        {
            var reason = f["reason"] ?? "";
            var d = ParseDate(f["status_change_date"]);
            if (f["op_auth_status"] != "Inactive" || !reason.StartsWith("Involuntary Suspension") || !reason.Contains("insurance")
                || d is null || today.DayNumber - d.Value.DayNumber > Days || f.OtherActive(facts, latest)) continue;
            var age = today.DayNumber - d.Value.DayNumber;
            var s = f.Draft(nameof(InsuranceSuspendedRule), "INSURANCE_SUSPENDED",
                $"Authority {f.Docket()} ({(f["op_auth_type"] ?? "").ToLowerInvariant()}) suspended {d:yyyy-MM-dd}: {reason.AfterFirst(" - ")}",
                age <= 30 ? Severity.Critical : Severity.High, f.Pick(RuleHelpers.MotusStatusFields));
            s.ObservedAt = d;
            yield return s;
        }
    }
}

/// <summary>FMCSA served notice that the authority will be suspended on a date unless filings (in practice almost always
/// insurance, sometimes BOC-3) are back on file. Resolved once a newer insurance filing shows up.</summary>
public class SuspensionNoticeRule : ISignalRule
{
    const int FutureDays = 60;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var latest = facts.MotusLatest();
        foreach (var f in facts)
        {
            if (f.RecordType != "authority_order" || !(f["order1_type_desc"] ?? "").Contains("Involuntary")) continue;
            DateOnly? served = ParseDate(f["order1_serve_date"]), effective = ParseDate(f["order1_effective_date"]);
            if (served is null || effective is null) continue;
            var inDays = effective.Value.DayNumber - today.DayNumber;
            if (inDays < 0 || inDays > FutureDays) continue;  // once effective, InsuranceSuspendedRule reports the actual suspension
            if ((latest.TryGetValue(f.AuthKey(), out var now) && now["op_auth_status"] != "Active") || f.OtherActive(facts, latest)) continue;
            if (facts.Any(i => i.RecordType == "insurance" && i.Docket() == f.Docket()
                               && (ParseDate(i["effective_date"]) ?? DateOnly.MinValue) >= served)) continue;
            var s = f.Draft(nameof(SuspensionNoticeRule), "SUSPENSION_NOTICE",
                $"FMCSA served a suspension notice on {served:yyyy-MM-dd}: authority {f.Docket()} will be suspended {effective:yyyy-MM-dd} " +
                $"(in {inDays} days) unless required filings are on file", Severity.Critical,
                f.Pick("docket_number", "op_auth_type", "order1_type_desc", "order1_serve_date", "order1_effective_date"));
            s.ObservedAt = served;
            yield return s;
        }
    }
}

/// <summary>Authority reinstated, usually because insurance is back on file: a carrier restarting operations.
/// FMCSA's own 'MOTUS Issue #220' data fix is not a reinstatement.</summary>
public class ReinstatedRule : ISignalRule
{
    const int Days = 30;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.MotusLatest().Values)
        {
            var reason = f["reason"] ?? "";
            var d = ParseDate(f["status_change_date"]);
            if (!reason.StartsWith("Reinstated") || reason.Contains("#220") || d is null || today.DayNumber - d.Value.DayNumber > Days) continue;
            var s = f.Draft(nameof(ReinstatedRule), "REINSTATED_AUTHORITY",
                $"Authority {f.Docket()} ({(f["op_auth_type"] ?? "").ToLowerInvariant()}) reinstated {d:yyyy-MM-dd}"
                + (reason.Contains(" - ") ? $": {reason.AfterFirst(" - ")}" : ""), Severity.Medium, f.Pick(RuleHelpers.MotusStatusFields));
            s.ObservedAt = d;
            yield return s;
        }
    }
}

/// <summary>Application published in the FMCSA Register: a new carrier/broker weeks away from its grant.</summary>
public class RegisterPublishedRule : ISignalRule
{
    const int Days = 60;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.MotusLatest().Values)
        {
            var d = ParseDate(f["status_change_date"]);
            if (f["reason"] != "Published to FMCSA Register" || f["op_auth_status"] != "Pending" || d is null
                || today.DayNumber - d.Value.DayNumber > Days) continue;
            var s = f.Draft(nameof(RegisterPublishedRule), "REGISTER_PUBLISHED",
                $"Application for {f.Docket()} ({(f["op_auth_type"] ?? "").ToLowerInvariant()}) published in the FMCSA Register {d:yyyy-MM-dd}; " +
                "authority not granted yet", Severity.Medium, f.Pick(RuleHelpers.MotusStatusFields));
            s.ObservedAt = d;
            yield return s;
        }
    }
}

public class VoluntarySuspensionRule : ISignalRule
{
    const int Days = 30;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.Where(f => f.RecordType == "authority_order"))
        {
            var kind = f["order1_type_desc"] ?? "";
            var served = ParseDate(f["order1_serve_date"]);
            if (served is null || !kind.Contains("Voluntary") || kind.Contains("Involuntary") || today.DayNumber - served.Value.DayNumber > Days) continue;
            var s = f.Draft(nameof(VoluntarySuspensionRule), "VOLUNTARY_SUSPENSION",
                $"Carrier asked to suspend authority {f.Docket()}: notice served {served:yyyy-MM-dd}, effective " +
                (ParseDate(f["order1_effective_date"])?.ToString("yyyy-MM-dd") ?? "None"), Severity.Low,
                f.Pick("docket_number", "op_auth_type", "order1_type_desc", "order1_serve_date", "order1_effective_date"));
            s.ObservedAt = served;
            yield return s;
        }
    }
}

/// <summary>Insurance filing re-issued under a new company name (Motus publishes no name-change decisions).</summary>
public class NameChangeRule : ISignalRule
{
    const int Days = 60;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.Where(f => f.RecordType == "insurance_history"))
        {
            var d = ParseDate(f["cancl_effective_date"]);
            if (d is null || f["filing_status_reason"] != "NAMECHG") continue;
            var age = today.DayNumber - d.Value.DayNumber;
            if (age < 0 || age > Days) continue;
            var s = f.Draft(nameof(NameChangeRule), "NAME_CHANGE", $"Company name changed on its authority {f.Docket()} ({d:yyyy-MM-dd})",
                            Severity.Low, f.Pick(RuleHelpers.InsuranceFields));
            (s.ObservedAt, s.DedupeKey) = (d, $"NAME_CHANGE:{f.Docket()}:{d:yyyy-MM-dd}");
            yield return s;
        }
    }
}

/// <summary>Authority application pending, liability (BIPD) insurance required but none filed: FMCSA won't grant it until an
/// insurer files. Applications idle for months are usually abandoned, so only recent ones count.</summary>
public class InsuranceNeededRule : ISignalRule
{
    const int Days = 180;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var latest = facts.MotusLatest();
        foreach (var f in facts)
        {
            if (f.RecordType != "authority_filings" || f["op_auth_status"] != "Pending"
                || RuleHelpers.Money(f["min_cov_amount"]) <= 0 || RuleHelpers.Money(f["bipd_file"]) > 0) continue;
            var since = latest.TryGetValue(f.AuthKey(), out var last) ? ParseDate(last["status_change_date"]) : null;
            if (since is null || today.DayNumber - since.Value.DayNumber > Days) continue;
            var s = f.Draft(nameof(InsuranceNeededRule), "INSURANCE_NEEDED",
                $"Authority {f.Docket()} ({(f["op_auth_type"] ?? "").ToLowerInvariant()}) pending since {since:yyyy-MM-dd}: " +
                $"${RuleHelpers.Money(f["min_cov_amount"]).ToString("N0", CultureInfo.InvariantCulture)} liability insurance required, none filed yet",
                Severity.High, f.Pick("docket_number", "op_auth_type", "op_auth_status", "min_cov_amount", "bipd_file", "cargo_req",
                                      "cargo_file", "bond_req", "bond_file"));
            s.ObservedAt = since;
            yield return s;
        }
    }
}

public class InsuranceCancellationRule : ISignalRule
{
    const int PastDays = 30, FutureDays = 90;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts)
        {
            string kind;
            if (f.RecordType == "insurance")  // legacy active/pending filing with a cancellation date
                kind = f["mod_col_1"] ?? "Insurance";
            else if (f.RecordType == "insurance_history")  // Motus: TERM/REPL (replaced) and NAMECHG aren't problems
            {
                var reason = f["filing_status_reason"];
                kind = f["ins_type_desc"] is { Length: > 0 } t ? t : "Insurance";
                if (!(reason == "CANCEL" || (string.IsNullOrEmpty(reason) && kind.Contains("CANCELLATION")))) continue;
            }
            else continue;
            var cancel = ParseDate(f["cancl_effective_date"]);
            if (cancel is null) continue;
            var inDays = cancel.Value.DayNumber - today.DayNumber;
            if (inDays < -PastDays || inDays > FutureDays || f.Replaced(facts)) continue;
            var when = inDays >= 0 ? $"in {inDays} days" : $"{-inDays} days ago";
            var s = f.Draft(nameof(InsuranceCancellationRule), "INSURANCE_CANCELLATION",
                $"{kind} policy with {f.Insurer()} has a cancellation effective {cancel:yyyy-MM-dd} ({when})",
                inDays >= 0 ? Severity.Critical : Severity.High, f.Pick(RuleHelpers.InsuranceFields));
            s.ObservedAt = cancel;
            yield return s;
        }
    }
}

/// <summary>Estimate: BIPD liability policies are typically annual, so the next anniversary is a renewal window.</summary>
public class InsuranceRenewalRule : ISignalRule
{
    const int WindowDays = 60;

    public static DateOnly NextAnniversary(DateOnly start, DateOnly today)
    {
        foreach (var year in new[] { today.Year, today.Year + 1 })
        {
            var d = start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(year)
                ? new DateOnly(year, 2, 28)  // Feb 29 in a non-leap year
                : new DateOnly(year, start.Month, start.Day);
            if (d >= today) return d;
        }
        throw new InvalidOperationException("unreachable");
    }

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts)
        {
            var bipd = (f["mod_col_1"] ?? "").Contains("BIPD") || f["ins_type_code"] == "1";
            if (f.RecordType != "insurance" || !bipd) continue;
            var start = ParseDate(f["effective_date"]);
            if (start is null || start > today || !string.IsNullOrEmpty(f["cancl_effective_date"])) continue;
            var next = NextAnniversary(start.Value, today);
            var inDays = next.DayNumber - today.DayNumber;
            if (inDays > WindowDays) continue;
            var evidence = f.Pick(RuleHelpers.InsuranceFields);
            evidence["estimated_renewal"] = next.ToString("yyyy-MM-dd");
            evidence["assumption"] = "annual policy term";
            var s = f.Draft(nameof(InsuranceRenewalRule), "INSURANCE_RENEWAL",
                $"Liability policy with {f.Insurer()} effective {start:yyyy-MM-dd}; estimated annual renewal {next:yyyy-MM-dd} (in {inDays} days)",
                Severity.Low, evidence);
            (s.ObservedAt, s.DedupeKey) = (start, $"INSURANCE_RENEWAL:{f.ExternalId}:{next:yyyy-MM-dd}");
            yield return s;
        }
    }
}

public class HazmatCarrierRule : ISignalRule
{
    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        var c = facts.Census();
        if (c is null || c["hm_ind"] != "Y") yield break;
        var s = c.Draft(nameof(HazmatCarrierRule), "HAZMAT_CARRIER", "Registered as a hazardous-materials carrier", Severity.Low,
                        c.Pick("dot_number", "hm_ind"));
        s.DedupeKey = "HAZMAT_CARRIER";
        yield return s;
    }
}

/// <summary>Changes between two census snapshots (see Pipeline.CensusChanges). Small count moves are noise.</summary>
public class CensusChangeRule : ISignalRule
{
    const int MinDelta = 2;
    const double MinRatio = 0.10;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today)
    {
        foreach (var f in facts.Recent("census_change", today))
        {
            string? field = f["field"], old = f["from"], now = f["to"];
            var evidence = f.Pick("field", "from", "to");
            if (field == "status_code")
            {
                if (now == "A" && old != "A")
                    yield return f.Draft(nameof(CensusChangeRule), "REACTIVATED", $"USDOT status changed '{old}' → active", Severity.Medium, evidence);
                continue;
            }
            int? before = ParseInt(old), after = ParseInt(now);
            if (before is null || after is null || Math.Abs(after.Value - before.Value) < Math.Max(MinDelta, before.Value * MinRatio)) continue;
            var what = field == "power_units" ? "power units" : "drivers";
            var desc = $"{char.ToUpperInvariant(what[0])}{what[1..]} {(after > before ? "grew" : "dropped")} {before} → {after} (FMCSA registration)";
            if (field == "power_units")
                yield return f.Draft(nameof(CensusChangeRule), after > before ? "FLEET_GROWTH" : "FLEET_SHRINK", desc,
                                     after > before ? Severity.Medium : Severity.Low, evidence);
            else if (field == "total_drivers" && after > before)
                yield return f.Draft(nameof(CensusChangeRule), "DRIVER_GROWTH", desc, Severity.Medium, evidence);
        }
    }
}

/// <summary>Hiring language on the company's own website (recorded during enrichment).</summary>
public class HiringRule : ISignalRule
{
    const int Days = 90;

    public IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today) =>
        facts.Where(f => f.RecordType == "hiring" && f.ObservedAt is not null && today.DayNumber - f.ObservedAt.Value.DayNumber <= Days)
             .Select(f => f.Draft(nameof(HiringRule), "HIRING_DRIVERS", $"Website says “{f["phrase"]}”", Severity.Medium, f.Pick("phrase", "page")));
}

public static class SignalEngine
{
    public static readonly ISignalRule[] Rules =
    [
        new OutOfServiceRule(), new InspectionViolationRule(), new RepeatedViolationRule(), new HighOutOfServiceRateRule(),
        new CrashRule(), new NewCarrierRule(), new StaleRegistrationRule(), new InactiveStatusRule(), new ViolationCategoryRule(),
        new AuthorityRevokedRule(), new NewAuthorityRule(), new InsuranceSuspendedRule(), new SuspensionNoticeRule(),
        new InsuranceNeededRule(), new InsuranceCancellationRule(), new InsuranceRenewalRule(), new ReinstatedRule(),
        new RegisterPublishedRule(), new VoluntarySuspensionRule(), new NameChangeRule(),
        new HazmatCarrierRule(), new CensusChangeRule(), new HiringRule(),
    ];

    public static List<SignalDraft> Detect(IReadOnlyList<Fact> facts, DateOnly today) =>
        Rules.SelectMany(r => r.Evaluate(facts, today)).ToList();
}

public static class RuleHelpers
{
    public static readonly string[] InspectionFields = ["inspection_id", "insp_date", "report_state", "insp_level_id", "viol_total",
        "oos_total", "driver_oos_total", "vehicle_oos_total", "hazmat_oos_total"];
    public static readonly string[] AuthorityFields = ["docket_number", "sub_number", "mod_col_1", "original_action_desc",
        "orig_served_date", "disp_action_desc", "disp_decided_date", "disp_served_date"];
    public static readonly string[] MotusStatusFields = ["docket_number", "usdot_number", "op_auth_type", "op_auth_status", "reason",
        "status_change_date"];
    public static readonly string[] InsuranceFields = ["docket_number", "mod_col_1", "name_company", "insurance_company_name",
        "ins_type_desc", "ins_type_code", "filing_status_reason", "policy_no", "ins_form_code", "effective_date", "cancl_effective_date",
        "max_cov_amount"];

    static readonly int Window = 365;

    public static IEnumerable<Fact> Recent(this IEnumerable<Fact> facts, string recordType, DateOnly today) =>
        facts.Where(f => f.RecordType == recordType && f.ObservedAt is not null && today.DayNumber - f.ObservedAt.Value.DayNumber <= Window);

    public static int Num(this Fact f, string key) => ParseInt(f[key]) ?? 0;

    /// <summary>The listed payload fields that are present, as evidence.</summary>
    public static Dictionary<string, object?> Pick(this Fact f, params string[] keys) =>
        keys.Where(f.Has).ToDictionary(k => k, k => (object?)f[k]);

    public static SignalDraft Draft(this Fact f, string rule, string type, string description, string severity,
                                    Dictionary<string, object?> evidence) => new()
    {
        Type = type, Description = description, Severity = severity, Source = f.Source, SourceUrl = f.SourceUrl,
        ObservedAt = f.ObservedAt, DetectedBy = rule, DedupeKey = $"{type}:{f.RecordType}:{f.ExternalId}", Evidence = evidence,
        SourceRecordId = f.Id,
    };

    public static Fact? Census(this IEnumerable<Fact> facts) => facts.FirstOrDefault(f => f.RecordType == "census");

    public static string Docket(this Fact f) => DocketId(f["docket_number"]);

    public static (string, string) AuthKey(this Fact f) => (f.Docket(), f["op_auth_type"] ?? "");

    /// <summary>Newest Motus status row per authority (docket + authority type).</summary>
    public static Dictionary<(string, string), Fact> MotusLatest(this IEnumerable<Fact> facts)
    {
        var latest = new Dictionary<(string, string), Fact>();
        foreach (var f in facts.Where(f => f.RecordType == "authority_status"))
        {
            var k = f.AuthKey();
            if (!latest.TryGetValue(k, out var cur) || string.CompareOrdinal(f["status_change_date"] ?? "", cur["status_change_date"] ?? "") >= 0)
                latest[k] = f;
        }
        return latest;
    }

    /// <summary>Carrier still runs the same kind of authority under another docket (e.g. only an old second MC lapsed).</summary>
    public static bool OtherActive(this Fact f, IEnumerable<Fact> facts, Dictionary<(string, string), Fact> latest)
    {
        var kind = f["op_auth_type"];
        return latest.Values.Concat(facts.Where(x => x.RecordType == "authority_filings"))
            .Any(o => o["op_auth_type"] == kind && o.Docket() != f.Docket() && o["op_auth_status"] == "Active");
    }

    public static double Money(string? v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;

    /// <summary>Coverage kind, comparable within one system: Motus type code, or the legacy 'BIPD/Primary'-style label.</summary>
    static (string, string) Coverage(Fact f) =>
        f.Has("ins_type_code") ? ("motus", f["ins_type_code"] ?? "") : ("legacy", (f["mod_col_1"] ?? "").Split('/')[0]);

    public static string Insurer(this Fact f) =>
        f["name_company"] is { Length: > 0 } a ? a : f["insurance_company_name"] is { Length: > 0 } b ? b : "unknown insurer";

    /// <summary>A newer, uncancelled policy of the same coverage on the same docket means the carrier already switched.</summary>
    public static bool Replaced(this Fact f, IEnumerable<Fact> facts)
    {
        var start = ParseDate(f["effective_date"]) ?? DateOnly.MinValue;
        return facts.Any(o => o.RecordType == "insurance" && !ReferenceEquals(o, f) && string.IsNullOrEmpty(o["cancl_effective_date"])
                              && o.Docket() == f.Docket() && Coverage(o) == Coverage(f) && o["policy_no"] != f["policy_no"]
                              && (ParseDate(o["effective_date"]) ?? DateOnly.MinValue) > start);
    }

    public static string AfterFirst(this string s, string sep)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        return i < 0 ? s : s[(i + sep.Length)..];
    }

    /// <summary>Python's "{:.0%}": 0.6 -> "60%".</summary>
    public static string Pct(this double v) => $"{Math.Round(v * 100, MidpointRounding.ToEven).ToString(CultureInfo.InvariantCulture)}%";
}
