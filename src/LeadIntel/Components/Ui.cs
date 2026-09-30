using LeadIntel.Data;
using LeadIntel.Services;

namespace LeadIntel.Components;

/// <summary>Labels and formatting shared by the dashboard pages.</summary>
public static class Ui
{
    public static readonly Dictionary<string, string> SignalLabels = new()
    {
        ["NEW_CARRIER"] = "New carrier", ["NEW_AUTHORITY"] = "New operating authority", ["INSURANCE_CANCELLATION"] = "Insurance cancellation",
        ["INSURANCE_RENEWAL"] = "Insurance renewal window", ["AUTHORITY_REVOKED"] = "Authority revoked",
        ["OUT_OF_SERVICE"] = "Out-of-service order", ["HIGH_OOS_RATE"] = "High out-of-service rate", ["REPEATED_VIOLATIONS"] = "Repeated violations",
        ["CRASH"] = "Crash", ["HOS_VIOLATIONS"] = "Hours-of-service violations", ["MAINTENANCE_VIOLATIONS"] = "Maintenance violations",
        ["UNSAFE_DRIVING"] = "Unsafe driving", ["DRIVER_FITNESS"] = "Driver fitness violations", ["DRUG_ALCOHOL"] = "Drug & alcohol violations",
        ["HAZMAT_VIOLATIONS"] = "Hazmat violations", ["HAZMAT_CARRIER"] = "Hazmat carrier", ["STALE_MCS150"] = "Overdue MCS-150 update",
        ["INSPECTION_VIOLATION"] = "Inspection violation", ["INACTIVE_STATUS"] = "Inactive USDOT status",
        ["FLEET_GROWTH"] = "Growing fleet", ["FLEET_SHRINK"] = "Shrinking fleet", ["DRIVER_GROWTH"] = "Adding drivers",
        ["HIRING_DRIVERS"] = "Hiring drivers", ["REACTIVATED"] = "Reactivated carrier",
        ["INSURANCE_SUSPENDED"] = "Suspended: no insurance", ["SUSPENSION_NOTICE"] = "Suspension notice",
        ["INSURANCE_NEEDED"] = "Pending: no insurance filed", ["REINSTATED_AUTHORITY"] = "Authority reinstated",
        ["REGISTER_PUBLISHED"] = "Published in FMCSA Register", ["VOLUNTARY_SUSPENSION"] = "Voluntary suspension",
        ["NAME_CHANGE"] = "Company name change",
    };

    public static string Signal(string t) => SignalLabels.TryGetValue(t, out var l) ? l
        : t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant().Replace('_', ' ');
    public static string Status(string? s) => LeadMessages.Status(s);
    public static IEnumerable<string> Statuses => LeadStatus.All;
    public static IReadOnlyDictionary<string, string> Channels => LeadMessages.ChannelLabels;
    public static string Phone(string p) => LeadMessages.Phone(p);
    public static string ScoreClass(int s) => s >= 70 ? "score-high" : s >= 40 ? "score-mid" : "score-low";
    public static string Date(DateOnly? d) => d?.ToString("MMM d, yyyy") ?? "—";
    public static string Date(DateTime d) => d.ToLocalTime().ToString("MMM d, yyyy");
    public static string DateTime(DateTime? d) => d?.ToLocalTime().ToString("MMM d, yyyy h:mm tt") ?? "—";
    public static string Pct(double? r) => r is null ? "—" : $"{Math.Round(r.Value * 100)}%";
    public static string M(Dictionary<string, object?> meta, string key) => Json.Text(meta.GetValueOrDefault(key)) ?? "";

    public static string EvidenceValue(object? v) => v switch
    {
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } a => string.Join(", ", a.EnumerateArray().Select(x => Json.AsString(x))),
        _ => Json.Text(v) ?? "",
    };
}
