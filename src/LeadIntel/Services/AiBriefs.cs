using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Scoring;
using LeadIntel.Signals;
using Microsoft.EntityFrameworkCore;

namespace LeadIntel.Services;

/// <summary>
/// An AI pre-call brief per lead, written by Claude from the lead's stored facts.
/// Interpretation only: stored on the lead (leads.ai_summary), never mixed into facts or signals, and labeled as AI
/// everywhere it is shown. Only business facts are sent - no phone numbers or emails. A lead is re-summarized only
/// when the facts it would be given change, so repeat runs cost nothing.
/// </summary>
public partial class AiBriefs(AppDbContext db, Settings settings, ILogger<AiBriefs> log)
{
    public const string Model = "claude-opus-5-5";
    const int MaxSignals = 25;

    const string System = """
        You write short pre-call briefs for a sales rep who sells services to US trucking companies: compliance and audits, driver files and drug testing, ELD and hours of service, safety tech and training, maintenance, insurance, registration and permits, new-carrier setup, and back office.

        You are given facts about one company from public FMCSA records and the company's own website, plus the services our rules matched to those facts. Use only these facts. Never invent numbers, names, dates, problems or needs, and don't speculate beyond what a fact supports. Refer to facts by their dates.

        The opener must be friendly and helpful, never accusatory: don't lead with their violations or crashes as a criticism. If a contact person is named, you may address them by first name.
        """;

    static readonly Dictionary<string, JsonElement> Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
        {
          "type": "object",
          "properties": {
            "summary": {"type": "string", "description": "2-3 sentences: why this company is worth contacting now"},
            "talking_points": {"type": "array", "items": {"type": "string"}, "description": "2-4 specific points, each tied to a dated fact"},
            "opener": {"type": "string", "description": "one friendly first sentence for a call or email"}
          },
          "required": ["summary", "talking_points", "opener"],
          "additionalProperties": false
        }
        """)!;

    public bool Enabled => !string.IsNullOrEmpty(settings.AnthropicApiKey);
    AnthropicClient? client;
    AnthropicClient Client => client ??= new AnthropicClient { ApiKey = settings.AnthropicApiKey };

    [GeneratedRegex(@" [(]\d+d ago[)]")] private static partial Regex DaysAgo();

    /// <summary>Deterministic (sorted, absolute dates) so its hash only changes when the facts do.
    /// Needs Company with Signals and Contacts loaded.</summary>
    public static string FactsPrompt(Lead lead)
    {
        var c = lead.Company!;
        var person = c.Contacts.OrderBy(x => x.Id).FirstOrDefault(x => x.Type == "person")?.Value;
        var signals = c.Signals.OrderBy(s => Severity.Rank(s.Severity)).ThenByDescending(s => s.ObservedAt ?? DateOnly.MinValue).ThenBy(s => s.Id).ToList();
        string D(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "None";
        var lines = new List<string>
        {
            $"Company: {c.Name}" + (c.DbaName is null ? "" : $" (DBA {c.DbaName})"),
            $"Location: {c.City ?? "?"}, {c.State ?? "?"} · fleet: {c.FleetSize?.ToString() ?? "None"} power units, {c.Drivers?.ToString() ?? "None"} drivers",
            $"USDOT status: {(c.OperatingStatus == "A" ? "active" : c.OperatingStatus ?? "None")} · registered: {D(c.AddedAt)}",
            $"Contact person on registration: {person ?? "unknown"}",
            $"Website: {c.Website ?? "none found"}",
            $"Lead score: {lead.Score}/100 · our status: {lead.Status}",
            "", "Score reasons:",
        };
        // "(4d ago)" changes daily; dropping it keeps the hash (and the bill) stable while facts don't change
        lines.AddRange(lead.ScoreBreakdown.Select(b => $"- {b.Label}: {DaysAgo().Replace(b.Reason, "")}"));
        lines.AddRange(["", "Services our rules matched (strongest first): " +
            string.Join(", ", lead.ServiceLines.Where(ServicesCatalog.Lines.ContainsKey).Select(k => ServicesCatalog.Lines[k].Label))]);
        lines.AddRange(["", $"Facts ({Math.Min(signals.Count, MaxSignals)} of {signals.Count}, most severe first):"]);
        lines.AddRange(signals.Take(MaxSignals).Select(s => $"- [{s.Severity}] {s.Type} on {D(s.ObservedAt)}: {s.Description}"));
        return string.Join("\n", lines);
    }

    /// <summary>(brief or null if refused/truncated, model that answered, input tokens, output tokens).</summary>
    async Task<(AiBrief? Brief, string Model, long In, long Out)> AskAsync(string prompt)
    {
        var r = await Client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 16000,
            System = System,
            Messages = [new() { Role = Role.User, Content = prompt }],
            // medium effort: a short brief from given facts doesn't need deep reasoning; raise it if briefs get thin
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium, Format = new BetaJsonOutputFormat { Schema = Schema } },
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),  // a false-positive refusal retries on the recommended model
        });
        var (tokensIn, tokensOut) = (r.Usage.InputTokens, r.Usage.OutputTokens);
        if (r.StopReason != "end_turn")  // refusal (even after fallback) or max_tokens: nothing trustworthy to store
        {
            log.LogWarning("brief not stored: stop_reason={Reason}", r.StopReason);
            return (null, r.Model, tokensIn, tokensOut);
        }
        var text = r.Content.Select(b => b.Value).OfType<BetaTextBlock>().First().Text;
        var data = JsonSerializer.Deserialize<AiBrief>(text, Json.Options)!;
        return (data, r.Model, tokensIn, tokensOut);
    }

    public static Dictionary<string, long> NewStats() =>
        new() { ["summarized"] = 0, ["skipped"] = 0, ["failed"] = 0, ["input_tokens"] = 0, ["output_tokens"] = 0 };

    public async Task SummarizeLeadAsync(Lead lead, Dictionary<string, long> stats, bool force = false)
    {
        var prompt = FactsPrompt(lead);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Model}\n{System}\n{prompt}")));
        if (!force && lead.AiSummary?.InputHash == digest) return;
        var (brief, model, tokensIn, tokensOut) = await AskAsync(prompt);
        stats["input_tokens"] += tokensIn;
        stats["output_tokens"] += tokensOut;
        if (brief is null)
        {
            stats["skipped"]++;
            return;
        }
        lead.AiSummary = brief with { Model = model, InputHash = digest };
        lead.AiSummaryAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        stats["summarized"]++;
    }

    /// <summary>Brief leads at/above AI_MIN_SCORE whose facts changed, best first, at most AI_LIMIT API calls.
    /// Null when ANTHROPIC_API_KEY isn't set.</summary>
    public async Task<Dictionary<string, long>?> SummarizeLeadsAsync(int? minScore = null, int? limit = null)
    {
        if (!Enabled) return null;
        var max = limit ?? settings.AiLimit;
        var min = minScore ?? settings.AiMinScore;
        var leads = await db.Leads.Include(l => l.Company).ThenInclude(c => c!.Signals)
            .Include(l => l.Company).ThenInclude(c => c!.Contacts)
            .Where(l => l.Score >= min).OrderByDescending(l => l.Score).ThenBy(l => l.Id).AsSplitQuery().ToListAsync();
        var stats = NewStats();
        foreach (var lead in leads)
        {
            if (stats["summarized"] + stats["skipped"] + stats["failed"] >= max) break;
            try
            {
                await SummarizeLeadAsync(lead, stats);
            }
            catch (AnthropicUnauthorizedException)
            {
                log.LogError("Anthropic API key rejected; stopping AI briefs");
                stats["failed"]++;
                break;
            }
            catch (AnthropicApiException e)  // rate limits / 5xx were already retried by the SDK
            {
                log.LogWarning("lead {Id} brief failed: {Error}", lead.Id, e.GetType().Name);
                stats["failed"]++;
            }
        }
        log.LogInformation("AI briefs: {Stats}", Json.Serialize(stats));
        return stats;
    }
}
