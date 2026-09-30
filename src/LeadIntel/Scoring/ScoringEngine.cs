using LeadIntel.Data;
using Microsoft.EntityFrameworkCore;

namespace LeadIntel.Scoring;

public record SignalView(string Type, DateOnly? ObservedAt, string Severity = "medium");

public record CompanyView(string? OperatingStatus, int? FleetSize, List<SignalView> Signals, ISet<string> ContactTypes,
                          List<string>? ServiceLines = null);

/// <summary>
/// Explainable scoring. Weights live in the scoring_rules table; this class only knows *kinds* of factors.
/// score = clamp(sum of weights of matched rules, 0, 100); every matched rule adds a breakdown line.
/// </summary>
public static class ScoringEngine
{
    // Tuned for a full-service trucking provider. Rows are seeded once; the DB is the source of truth after that.
    static readonly List<string> Urgent = ["OUT_OF_SERVICE", "CRASH", "INSURANCE_CANCELLATION", "AUTHORITY_REVOKED",
        "INSURANCE_SUSPENDED", "SUSPENSION_NOTICE", "INSURANCE_NEEDED"];

    static RuleParams T(string type) => new() { Type = type };

    public static readonly (string Key, string Label, string Kind, int Weight, RuleParams Params, bool Enabled)[] DefaultRules =
    [
        ("sig_new_carrier", "New carrier", "signal_type", 25, T("NEW_CARRIER"), true),
        ("sig_insurance_cancel", "Insurance cancellation", "signal_type", 25, T("INSURANCE_CANCELLATION"), true),
        ("sig_insurance_suspended", "Suspended for no insurance", "signal_type", 30, T("INSURANCE_SUSPENDED"), true),
        ("sig_suspension_notice", "Suspension notice served", "signal_type", 30, T("SUSPENSION_NOTICE"), true),
        ("sig_insurance_needed", "Pending authority, no insurance filed", "signal_type", 25, T("INSURANCE_NEEDED"), true),
        ("sig_high_oos_rate", "High out-of-service rate", "signal_type", 20, T("HIGH_OOS_RATE"), true),
        ("sig_authority_revoked", "Authority revoked", "signal_type", 20, T("AUTHORITY_REVOKED"), true),
        ("sig_new_authority", "New operating authority", "signal_type", 15, T("NEW_AUTHORITY"), true),
        ("sig_stale_mcs150", "Overdue MCS-150 update", "signal_type", 15, T("STALE_MCS150"), true),
        ("sig_drug_alcohol", "Drug & alcohol violations", "signal_type", 15, T("DRUG_ALCOHOL"), true),
        ("sig_crash", "Crash on record", "signal_type", 10, T("CRASH"), true),
        ("sig_crash_injury", "Injury or fatal crash", "signal_type", 10, T("CRASH") with { Severity = ["high", "critical"] }, true),
        ("sig_crash_fatal", "Fatal crash", "signal_type", 10, T("CRASH") with { Severity = ["critical"] }, true),
        ("sig_oos_pattern", "3+ out-of-service orders", "signal_type", 10, T("OUT_OF_SERVICE") with { MinCount = 3 }, true),
        ("sig_repeated", "Repeated violations", "signal_type", 10, T("REPEATED_VIOLATIONS"), true),
        ("sig_hos", "Hours-of-service violations", "signal_type", 10, T("HOS_VIOLATIONS"), true),
        ("sig_maintenance", "Maintenance violations", "signal_type", 10, T("MAINTENANCE_VIOLATIONS"), true),
        ("sig_unsafe_driving", "Unsafe driving violations", "signal_type", 10, T("UNSAFE_DRIVING"), true),
        ("sig_driver_fitness", "Driver fitness violations", "signal_type", 10, T("DRIVER_FITNESS"), true),
        ("sig_insurance_renewal", "Insurance renewal window", "signal_type", 10, T("INSURANCE_RENEWAL"), true),
        ("sig_reactivated", "Reactivated carrier", "signal_type", 15, T("REACTIVATED"), true),
        ("sig_reinstated", "Authority reinstated", "signal_type", 20, T("REINSTATED_AUTHORITY"), true),
        ("sig_register_published", "Published in FMCSA Register", "signal_type", 20, T("REGISTER_PUBLISHED"), true),
        ("sig_name_change", "Company name change", "signal_type", 10, T("NAME_CHANGE"), true),
        ("sig_voluntary_suspension", "Voluntary suspension", "signal_type", 5, T("VOLUNTARY_SUSPENSION"), true),
        ("sig_fleet_growth", "Growing fleet", "signal_type", 10, T("FLEET_GROWTH"), true),
        ("sig_driver_growth", "Adding drivers", "signal_type", 10, T("DRIVER_GROWTH"), true),
        ("sig_hiring", "Hiring drivers", "signal_type", 10, T("HIRING_DRIVERS"), true),
        ("sig_fleet_shrink", "Shrinking fleet", "signal_type", 5, T("FLEET_SHRINK"), true),
        ("hot_now", "Hot right now", "recent_signal", 10, new() { Days = 14, Types = Urgent }, true),
        ("multi_service", "Needs 3+ service lines", "service_lines", 10, new() { Min = 3 }, true),
        ("sig_out_of_service", "Out-of-service order", "signal_type", 5, T("OUT_OF_SERVICE"), true),
        ("sig_hazmat_violations", "Hazmat violations", "signal_type", 5, T("HAZMAT_VIOLATIONS"), true),
        ("sig_hazmat_carrier", "Hazmat carrier", "signal_type", 5, T("HAZMAT_CARRIER"), true),
        ("sig_inactive", "Inactive carrier", "signal_type", -40, T("INACTIVE_STATUS"), true),
        // Off by default: every collected company already matches these (they're collection filters).
        ("sig_violation", "Inspection violation", "signal_type", 5, T("INSPECTION_VIOLATION"), false),
        ("recent_activity", "Recent activity", "recent_signal", 10, new() { Days = 30 }, false),
        ("active_company", "Active carrier", "active_company", 5, new(), false),
        ("target_fleet", "Target fleet size", "fleet_size", 5, new() { Min = 5, Max = 500 }, false),
        ("has_contact", "Contact information", "has_contact", 5, new() { Types = ["phone", "email"] }, false),
    ];

    delegate string? Kind(CompanyView c, RuleParams p, DateOnly today);

    static readonly Dictionary<string, Kind> Kinds = new()
    {
        ["signal_type"] = (c, p, today) =>
        {
            var hits = c.Signals.Where(s => s.Type == p.Type && (p.Severity is not { Count: > 0 } || p.Severity.Contains(s.Severity))).ToList();
            if (hits.Count < (p.MinCount ?? 1)) return null;
            var latest = hits.Where(s => s.ObservedAt is not null).Select(s => s.ObservedAt).Max();
            return $"{hits.Count} signal(s)" + (latest is not null ? $", latest {latest:yyyy-MM-dd}" : "");
        },
        ["recent_signal"] = (c, p, today) =>
        {
            var latest = c.Signals.Where(s => s.ObservedAt is not null && (p.Types is not { Count: > 0 } || p.Types.Contains(s.Type)))
                                  .MaxBy(s => s.ObservedAt);
            if (latest is null) return null;
            var age = today.DayNumber - latest.ObservedAt!.Value.DayNumber;
            return age >= 0 && age <= p.Days ? $"{latest.Type.ToLowerInvariant().Replace('_', ' ')} on {latest.ObservedAt:yyyy-MM-dd} ({age}d ago)" : null;
        },
        ["service_lines"] = (c, p, today) =>
            (c.ServiceLines?.Count ?? 0) >= p.Min ? $"{c.ServiceLines!.Count} service lines: {string.Join(", ", c.ServiceLines)}" : null,
        ["active_company"] = (c, p, today) => c.OperatingStatus == "A" ? "USDOT status active" : null,
        ["fleet_size"] = (c, p, today) =>
            c.FleetSize is { } n && (p.Min ?? 0) <= n && n <= (p.Max ?? int.MaxValue) ? $"{n} power units" : null,
        ["has_contact"] = (c, p, today) =>
            c.ContactTypes.Intersect(p.Types ?? []).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } found
                ? string.Join(", ", found) + " on file" : null,
    };

    public static (int Points, List<BreakdownItem> Breakdown) Score(CompanyView company, IEnumerable<ScoringRule> rules, DateOnly today)
    {
        var breakdown = new List<BreakdownItem>();
        foreach (var r in rules)
            if (r.Enabled && Kinds.TryGetValue(r.Kind, out var fn) && fn(company, r.Params, today) is { } reason)
                breakdown.Add(new(r.Key, r.Label, r.Weight, reason));
        breakdown = breakdown.OrderByDescending(b => b.Points).ToList();  // stable, like Python's sort
        return (Math.Clamp(breakdown.Sum(b => b.Points), 0, 100), breakdown);
    }

    public static List<ScoringRule> DefaultRuleRows() => DefaultRules.Select(r => new ScoringRule
    {
        Key = r.Key, Label = r.Label, Kind = r.Kind, Weight = r.Weight, Params = r.Params, Enabled = r.Enabled,
    }).ToList();

    /// <summary>Insert default rules that don't exist yet. Never overwrites tuned weights.</summary>
    public static async Task SeedRulesAsync(AppDbContext db)
    {
        // ON CONFLICT: the API and a collection run may seed at the same moment
        foreach (var r in DefaultRuleRows())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO scoring_rules (key, label, kind, weight, params, enabled)
                VALUES ({r.Key}, {r.Label}, {r.Kind}, {r.Weight}, {Json.Serialize(r.Params)}::jsonb, {r.Enabled})
                ON CONFLICT (key) DO NOTHING
                """);
    }

    /// <summary>Enabled rules in insertion order (the Python version relied on the table's natural order).</summary>
    public static Task<List<ScoringRule>> LoadRulesAsync(AppDbContext db) =>
        db.ScoringRules.AsNoTracking().Where(r => r.Enabled).OrderBy(r => r.Id).ToListAsync();

    /// <summary>Replace all rules with the defaults (discards tuned weights).</summary>
    public static async Task ResetRulesAsync(AppDbContext db)
    {
        await db.ScoringRules.ExecuteDeleteAsync();
        await SeedRulesAsync(db);
    }
}
