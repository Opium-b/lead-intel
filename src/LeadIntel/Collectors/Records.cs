using System.Text.Json;

namespace LeadIntel.Collectors;

// Source-agnostic contract: the pipeline only ever sees these types.

public record ContactRecord(string Type, string Value, string Source, string? SourceUrl = null, string? Label = null);

public record CompanyRecord
{
    public required string DotNumber { get; init; }
    public required string Name { get; init; }
    public string? DbaName { get; init; }
    public string? McNumber { get; init; }
    public string? State { get; init; }
    public string? City { get; init; }
    public string? Location { get; init; }
    public int? FleetSize { get; init; }
    public int? Drivers { get; init; }
    public string? OperatingStatus { get; init; }
    public DateOnly? AddedAt { get; init; }
    public Dictionary<string, string?> Attributes { get; init; } = [];
    public List<ContactRecord> Contacts { get; init; } = [];
}

/// <summary>One raw fact (inspection, crash, registration snapshot...) tied to a company.</summary>
public record EventRecord(string Source, string RecordType, string ExternalId, string CompanyDot, DateOnly? ObservedAt,
                          string SourceUrl, Dictionary<string, JsonElement> Payload);

public class NormalizedBatch
{
    public List<CompanyRecord> Companies { get; init; } = [];
    public List<EventRecord> Events { get; set; } = [];
    public DateOnly? Cursor { get; init; }  // newest data date seen; next run starts from here
    public Dictionary<string, object?> Stats { get; init; } = [];
}
