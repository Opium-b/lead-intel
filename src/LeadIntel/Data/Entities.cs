using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LeadIntel.Data;

// Entities map 1:1 onto the existing PostgreSQL tables (snake_case columns via EFCore.NamingConventions).
// JSONB columns are plain .NET types serialized with Json.Options (snake_case keys).

public static class LeadStatus
{
    public const string New = "NEW", Reviewed = "REVIEWED", Contacted = "CONTACTED", NoAnswer = "NO_ANSWER",
        Qualified = "QUALIFIED", Declined = "DECLINED", Disqualified = "DISQUALIFIED", Converted = "CONVERTED";

    public static readonly string[] All = [New, Reviewed, Contacted, NoAnswer, Qualified, Declined, Disqualified, Converted];
}

public class Company
{
    public int Id { get; set; }
    public string? DotNumber { get; set; }
    public string? McNumber { get; set; }
    public string Name { get; set; } = "";
    public string? DbaName { get; set; }
    public string? State { get; set; }
    public string? City { get; set; }
    public string? Location { get; set; }
    public int? FleetSize { get; set; }
    public int? Drivers { get; set; }
    public string? OperatingStatus { get; set; }
    public string? Website { get; set; }
    public DateOnly? AddedAt { get; set; }  // carrier registration date at source
    public DateTime? EnrichedAt { get; set; }  // last website/contact lookup
    [Column(TypeName = "jsonb")] public Dictionary<string, string?> Attributes { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<Signal> Signals { get; set; } = [];
    public List<Contact> Contacts { get; set; } = [];
    public Lead? Lead { get; set; }
}

/// <summary>Raw, unmodified fact as fetched from a source. Signals point here as evidence.</summary>
public class SourceRecord
{
    public long Id { get; set; }
    public string Source { get; set; } = "";
    public string RecordType { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public int? CompanyId { get; set; }
    public DateOnly? ObservedAt { get; set; }
    public string? SourceUrl { get; set; }
    [Column(TypeName = "jsonb")] public Dictionary<string, JsonElement> Payload { get; set; } = [];
    public string PayloadHash { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}

public class Signal
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Source { get; set; } = "";
    public string? SourceUrl { get; set; }
    public DateOnly? ObservedAt { get; set; }
    public string DetectedBy { get; set; } = "";
    public string Origin { get; set; } = "rule";  // 'rule' = fact-derived, 'ai' = interpretation
    [Column(TypeName = "jsonb")] public Dictionary<string, object?> Evidence { get; set; } = [];
    public long? SourceRecordId { get; set; }
    public string DedupeKey { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public Company? Company { get; set; }
}

public class Contact
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Type { get; set; } = "";  // phone | email | website | social | person | fax | address | form
    public string Value { get; set; } = "";
    public string? Label { get; set; }
    public string Source { get; set; } = "";
    public string? SourceUrl { get; set; }
    public DateTime CreatedAt { get; set; }

    public Company? Company { get; set; }
}

public record BreakdownItem(string Key, string Label, int Points, string Reason);

/// <summary>AI interpretation, kept apart from facts.</summary>
public record AiBrief(string Summary, List<string> TalkingPoints, string Opener, string? Model, string? InputHash);

public class Lead
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int Score { get; set; }
    [Column(TypeName = "jsonb")] public List<BreakdownItem> ScoreBreakdown { get; set; } = [];
    [Column(TypeName = "jsonb")] public List<string> ServiceLines { get; set; } = [];  // ranked keys, see ServicesCatalog
    public string Status { get; set; } = LeadStatus.New;
    public DateTime? ScoredAt { get; set; }
    public DateTime? NotifiedAt { get; set; }
    [Column(TypeName = "jsonb")] public AiBrief? AiSummary { get; set; }
    public DateTime? AiSummaryAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Company? Company { get; set; }
    public List<LeadEvent> Events { get; set; } = [];
}

public class LeadEvent
{
    public long Id { get; set; }
    public int LeadId { get; set; }
    public string EventType { get; set; } = "";
    [Column("metadata", TypeName = "jsonb")] public Dictionary<string, object?> Meta { get; set; } = [];
    public DateTime CreatedAt { get; set; }

    public Lead? Lead { get; set; }
}

/// <summary>Parameters of a scoring rule; which ones apply depends on the rule's kind.</summary>
public record RuleParams
{
    public string? Type { get; init; }
    public List<string>? Severity { get; init; }
    public int? MinCount { get; init; }
    public int? Days { get; init; }
    public List<string>? Types { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }
}

public class ScoringRule
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Weight { get; set; }
    [Column(TypeName = "jsonb")] public RuleParams Params { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
}

public class CollectorRun
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = "running";  // running | success | failed | built
    public DateOnly? Cursor { get; set; }  // data watermark reached by this run
    [Column(TypeName = "jsonb")] public Dictionary<string, object?> Stats { get; set; } = [];
    public string? Error { get; set; }
}
