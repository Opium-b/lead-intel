using System.Text.Json;
using LeadIntel.Data;

namespace LeadIntel.Signals;

/// <summary>Read-only view of a stored source record handed to rules.</summary>
public record Fact(long? Id, string Source, string RecordType, string ExternalId, DateOnly? ObservedAt, string? SourceUrl,
                   IReadOnlyDictionary<string, JsonElement> Payload)
{
    public string? this[string key] => Payload.Str(key);
    public bool Has(string key) => Payload.ContainsKey(key);
}

public class SignalDraft
{
    public required string Type { get; set; }
    public required string Description { get; set; }
    public required string Severity { get; set; }
    public required string Source { get; set; }
    public string? SourceUrl { get; set; }
    public DateOnly? ObservedAt { get; set; }
    public required string DedupeKey { get; set; }
    public required string DetectedBy { get; set; }
    public Dictionary<string, object?> Evidence { get; set; } = [];
    public long? SourceRecordId { get; set; }
}

/// <summary>A deterministic rule: reads stored facts, emits evidence-backed signals.</summary>
public interface ISignalRule
{
    IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today);
}

public static class Severity
{
    public const string Low = "low", Medium = "medium", High = "high", Critical = "critical";

    public static readonly Dictionary<string, int> Order = new() { [Critical] = 0, [High] = 1, [Medium] = 2, [Low] = 3 };
    public static int Rank(string s) => Order.GetValueOrDefault(s, 9);
}
