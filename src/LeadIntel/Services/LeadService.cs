using System.Text.RegularExpressions;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Scoring;
using LeadIntel.Signals;
using Microsoft.EntityFrameworkCore;

namespace LeadIntel.Services;

// Read/write models shared by the REST API and the Blazor dashboard (JSON: snake_case via Json.Options).

public record LeadFilter
{
    public string? Q { get; init; }
    public string? State { get; init; }
    public string? Status { get; init; }
    public int MinScore { get; init; }
    public string? SignalType { get; init; }
    public string? ServiceLine { get; init; }
    public string Sort { get; init; } = "-score";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public record LeadRow(int Id, int CompanyId, string Name, string? DotNumber, string? McNumber, string? State, string? City,
                      int? FleetSize, int? Drivers, DateOnly? AddedAt, int Score, string Status, int SignalCount,
                      List<string> SignalTypes, List<string> ServiceLines, string? Phone, string? Email, string? Person, DateTime UpdatedAt);
public record LeadPage(List<LeadRow> Items, int Total, int Page, int PageSize);
public record LeadDetail(int Id, int Score, List<BreakdownItem> ScoreBreakdown, List<Pitch> Pitch, string Status, DateTime? ScoredAt,
                         DateTime? NotifiedAt, AiBrief? AiSummary, DateTime? AiSummaryAt, DateTime CreatedAt, DateTime UpdatedAt,
                         CompanyOut Company, List<SignalOut> Signals, List<ContactOut> Contacts, List<EventOut> Events);
public record CompanyOut(int Id, string Name, string? DbaName, string? DotNumber, string? McNumber, string? State, string? City,
                         string? Location, int? FleetSize, int? Drivers, string? OperatingStatus, string? Website, DateOnly? AddedAt,
                         Dictionary<string, string?> Attributes);
public record SignalOut(long Id, string Type, string Description, string Severity, string Source, string? SourceUrl, DateOnly? ObservedAt,
                        string DetectedBy, string Origin, Dictionary<string, object?> Evidence, DateTime CreatedAt);
public record ContactOut(int Id, string Type, string Value, string? Label, string Source, string? SourceUrl);
public record EventOut(long Id, string EventType, Dictionary<string, object?> Meta, DateTime CreatedAt);
public record LeadUpdate(string Status, string? Channel = null, string? Note = null);
public record NoteIn(string Text);
public record ScoringRuleOut(string Key, string Label, string Kind, int Weight, RuleParams Params, bool Enabled);
public record ScoringRuleUpdate(int? Weight = null, bool? Enabled = null, string? Label = null, RuleParams? Params = null);

public record ActivityOut(long Id, int LeadId, string Company, int Score, string EventType, Dictionary<string, object?> Meta, DateTime CreatedAt);
public record RunOut(int Id, string Source, string Status, DateTime StartedAt, DateTime? FinishedAt, DateOnly? Cursor,
                     Dictionary<string, object?> Stats, string? Error);
public record Overview(int Companies, int NewSignals7d, int Leads, int QualifiedLeads, int HighScoreLeads, int HighScoreThreshold,
                       int LeadsToday, Dictionary<string, int> SignalsByType, List<ActivityOut> RecentActivity, RunOut? LastRun);

public record Rates(int Leads, int Worked, int Won, int Lost, double? WinRate);
public record SignalRates(string Type, int Leads, int Worked, int Won, int Lost, double? WinRate, double Lift);
public record BandRates(string Band, int Leads, int Worked, int Won, int Lost, double? WinRate);
public record ChannelStats(string Channel, int Attempts, int NoAnswer, int Won, int Lost);
public record WeightSuggestion(string Key, string Label, string Type, int Current, int Suggested, string Reason);
public record Thresholds(int MinDecidedWithSignal, int MinDecidedTotal);
public record Analytics(Dictionary<string, int> Funnel, Rates Totals, List<SignalRates> BySignal, List<BandRates> ByScoreBand,
                        List<ChannelStats> ByChannel, List<WeightSuggestion> Suggestions, Thresholds Thresholds);

public class BadRequestException(string message) : Exception(message);
public class NotFoundException(string message) : Exception(message);

public static partial class Validate
{
    public static readonly string[] Channels = ["phone", "email", "sms", "whatsapp", "in_person", "other"];
    public static readonly string[] Sorts = ["score", "updated_at", "name", "state", "signals", "added_at"];
    [GeneratedRegex("^[A-Za-z]{2}$")] public static partial Regex State();
    [GeneratedRegex("^[A-Z_]{1,64}$")] public static partial Regex SignalType();
    [GeneratedRegex("^[a-z_]{1,32}$")] public static partial Regex ServiceLine();

    public static void Filter(LeadFilter f)
    {
        if (f.Q is { Length: > 100 }) throw new BadRequestException("q: at most 100 characters");
        if (f.State is { Length: > 0 } s && !State().IsMatch(s)) throw new BadRequestException("state: two letters");
        if (f.Status is { Length: > 0 } st && !LeadStatus.All.Contains(st)) throw new BadRequestException("status: unknown");
        if (f.MinScore is < 0 or > 100) throw new BadRequestException("min_score: 0-100");
        if (f.SignalType is { Length: > 0 } t && !SignalType().IsMatch(t)) throw new BadRequestException("signal_type: invalid");
        if (f.ServiceLine is { Length: > 0 } l && !ServiceLine().IsMatch(l)) throw new BadRequestException("service_line: invalid");
        if (!Sorts.Contains(f.Sort.TrimStart('-'))) throw new BadRequestException("sort: unknown column");
        if (f.Page < 1 || f.PageSize is < 1 or > 100) throw new BadRequestException("page >= 1, page_size 1-100");
    }

    public static void Update(LeadUpdate u)
    {
        if (!LeadStatus.All.Contains(u.Status)) throw new BadRequestException("status: unknown");
        if (u.Channel is not null && !Channels.Contains(u.Channel)) throw new BadRequestException("channel: unknown");
        if (u.Note is { Length: > 2000 }) throw new BadRequestException("note: at most 2000 characters");
    }
}

/// <summary>Leads, stats and analytics for the dashboard and the API. One DbContext per call, so Blazor components
/// (long-lived circuits) never share a context between concurrent operations.</summary>
public class LeadService(IDbContextFactory<AppDbContext> dbs, IServiceScopeFactory scopes, Settings settings, Jobs jobs)
{
    public static List<object> ServiceLineList() =>
        ServicesCatalog.Lines.Select(kv => (object)new { key = kv.Key, label = kv.Value.Label, services = kv.Value.Services }).ToList();

    public async Task<LeadPage> ListAsync(LeadFilter f)
    {
        Validate.Filter(f);
        await using var db = await dbs.CreateDbContextAsync();
        var q = db.Leads.Where(l => l.Score >= f.MinScore);
        if (f.Q?.Trim() is { Length: > 0 } text)
        {
            var like = $"%{text}%";
            q = q.Where(l => EF.Functions.ILike(l.Company!.Name, like) || EF.Functions.ILike(l.Company.DbaName!, like)
                             || l.Company.DotNumber == text || l.Company.McNumber == text);
        }
        if (f.State is { Length: > 0 }) q = q.Where(l => l.Company!.State == f.State.ToUpperInvariant());
        if (f.Status is { Length: > 0 }) q = q.Where(l => l.Status == f.Status);
        if (f.ServiceLine is { Length: > 0 }) q = q.Where(l => EF.Functions.JsonContains(l.ServiceLines, $"[\"{f.ServiceLine}\"]"));
        if (f.SignalType is { Length: > 0 }) q = q.Where(l => l.Company!.Signals.Any(s => s.Type == f.SignalType));

        var total = await q.CountAsync();
        var desc = f.Sort.StartsWith('-');
        // nulls last in both directions, then id for a stable page order
        var sorted = f.Sort.TrimStart('-') switch
        {
            "name" => desc ? q.OrderByDescending(l => l.Company!.Name) : q.OrderBy(l => l.Company!.Name),
            "state" => desc ? q.OrderBy(l => l.Company!.State == null).ThenByDescending(l => l.Company!.State)
                            : q.OrderBy(l => l.Company!.State == null).ThenBy(l => l.Company!.State),
            "added_at" => desc ? q.OrderBy(l => l.Company!.AddedAt == null).ThenByDescending(l => l.Company!.AddedAt)
                               : q.OrderBy(l => l.Company!.AddedAt == null).ThenBy(l => l.Company!.AddedAt),
            "signals" => desc ? q.OrderByDescending(l => l.Company!.Signals.Count) : q.OrderBy(l => l.Company!.Signals.Count),
            "updated_at" => desc ? q.OrderByDescending(l => l.UpdatedAt) : q.OrderBy(l => l.UpdatedAt),
            _ => desc ? q.OrderByDescending(l => l.Score) : q.OrderBy(l => l.Score),
        };
        var rows = await sorted.ThenBy(l => l.Id).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize)
            .Select(l => new
            {
                Lead = l, l.Company,
                SignalCount = l.Company!.Signals.Count,
                SignalTypes = l.Company.Signals.Select(s => s.Type).Distinct().ToList(),
                Phone = l.Company.Contacts.Where(c => c.Type == "phone").OrderBy(c => c.Id).Select(c => c.Value).FirstOrDefault(),
                Email = l.Company.Contacts.Where(c => c.Type == "email").OrderBy(c => c.Id).Select(c => c.Value).FirstOrDefault(),
                Person = l.Company.Contacts.Where(c => c.Type == "person").OrderBy(c => c.Id).Select(c => c.Value).FirstOrDefault(),
            }).AsNoTracking().ToListAsync();
        var items = rows.Select(r => new LeadRow(r.Lead.Id, r.Company!.Id, r.Company.Name, r.Company.DotNumber, r.Company.McNumber,
            r.Company.State, r.Company.City, r.Company.FleetSize, r.Company.Drivers, r.Company.AddedAt, r.Lead.Score, r.Lead.Status,
            r.SignalCount, r.SignalTypes.Order(StringComparer.Ordinal).ToList(), r.Lead.ServiceLines, r.Phone, r.Email, r.Person, r.Lead.UpdatedAt)).ToList();
        return new(items, total, f.Page, f.PageSize);
    }

    static async Task<Lead> LoadAsync(AppDbContext db, int id, bool track = false)
    {
        var q = db.Leads.Include(l => l.Company).ThenInclude(c => c!.Signals).Include(l => l.Company).ThenInclude(c => c!.Contacts)
                  .Include(l => l.Events).AsSplitQuery();
        return await (track ? q : q.AsNoTracking()).FirstOrDefaultAsync(l => l.Id == id) ?? throw new NotFoundException("Lead not found");
    }

    public static LeadDetail ToDetail(Lead lead)
    {
        var c = lead.Company!;
        var signals = c.Signals.OrderBy(s => Severity.Rank(s.Severity)).ThenByDescending(s => s.ObservedAt ?? DateOnly.MinValue).ToList();
        var pitch = ServicesCatalog.Rank(signals.Select(s => (s.Type, s.Severity, (IReadOnlyDictionary<string, object?>)s.Evidence)));
        return new(lead.Id, lead.Score, lead.ScoreBreakdown, pitch, lead.Status, lead.ScoredAt, lead.NotifiedAt, lead.AiSummary, lead.AiSummaryAt,
            lead.CreatedAt, lead.UpdatedAt,
            new(c.Id, c.Name, c.DbaName, c.DotNumber, c.McNumber, c.State, c.City, c.Location, c.FleetSize, c.Drivers, c.OperatingStatus,
                c.Website, c.AddedAt, c.Attributes),
            signals.Select(s => new SignalOut(s.Id, s.Type, s.Description, s.Severity, s.Source, s.SourceUrl, s.ObservedAt, s.DetectedBy,
                s.Origin, s.Evidence, s.CreatedAt)).ToList(),
            c.Contacts.OrderBy(x => x.Id).Select(x => new ContactOut(x.Id, x.Type, x.Value, x.Label, x.Source, x.SourceUrl)).ToList(),
            lead.Events.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Select(e => new EventOut(e.Id, e.EventType, e.Meta, e.CreatedAt)).ToList());
    }

    public async Task<LeadDetail> GetAsync(int id)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return ToDetail(await LoadAsync(db, id));
    }

    /// <summary>Log a contact: a status change (with optional channel/note) or just a note. Sent to Telegram afterwards.</summary>
    public async Task<LeadDetail> UpdateAsync(int id, LeadUpdate body)
    {
        Validate.Update(body);
        await using var db = await dbs.CreateDbContextAsync();
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id) ?? throw new NotFoundException("Lead not found");
        var note = (body.Note ?? "").Trim();
        var meta = new Dictionary<string, object?>();
        if (lead.Status != body.Status)
        {
            meta["from"] = lead.Status;
            meta["to"] = body.Status;
            if (body.Channel is not null) meta["channel"] = body.Channel;
            if (note.Length > 0) meta["note"] = note;
            db.LeadEvents.Add(new LeadEvent { LeadId = id, EventType = "STATUS_CHANGED", Meta = meta });
            lead.Status = body.Status;
        }
        else if (note.Length > 0)
        {
            meta["text"] = note;
            if (body.Channel is not null) meta["channel"] = body.Channel;
            db.LeadEvents.Add(new LeadEvent { LeadId = id, EventType = "NOTE", Meta = meta });
        }
        else return await GetAsync(id);
        await db.SaveChangesAsync();
        TelegramUpdate(id);
        return await GetAsync(id);
    }

    public async Task<EventOut> AddNoteAsync(int id, string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > 5000) throw new BadRequestException("text: 1-5000 characters");
        await using var db = await dbs.CreateDbContextAsync();
        if (!await db.Leads.AnyAsync(l => l.Id == id)) throw new NotFoundException("Lead not found");
        var ev = new LeadEvent { LeadId = id, EventType = "NOTE", Meta = new() { ["text"] = text } };
        db.LeadEvents.Add(ev);
        await db.SaveChangesAsync();
        await db.Entry(ev).ReloadAsync();  // created_at is set by the database
        TelegramUpdate(id);
        return new(ev.Id, ev.EventType, ev.Meta, ev.CreatedAt);
    }

    /// <summary>Runs after the response: a slow or failing Telegram never delays or breaks the dashboard.</summary>
    void TelegramUpdate(int id) => jobs.Start($"telegram update for lead {id}", sp => sp.GetRequiredService<LeadNotifications>().SendLeadUpdateAsync(id));

    /// <summary>Re-derive signals and scores for every company in the background (after a weight change).</summary>
    public void StartReprocess() => jobs.Start("reprocess", sp => sp.GetRequiredService<LeadIntel.Pipeline.LeadPipeline>().ProcessCompaniesAsync());

    /// <summary>(Re)write the AI brief for one lead now. Takes a few seconds; costs one API call.</summary>
    public async Task<LeadDetail> RegenerateBriefAsync(int id)
    {
        await using var scope = scopes.CreateAsyncScope();
        var ai = scope.ServiceProvider.GetRequiredService<AiBriefs>();
        if (!ai.Enabled) throw new BadRequestException("AI briefs are off: set ANTHROPIC_API_KEY in .env");
        var stats = AiBriefs.NewStats();
        var lead = await LoadAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), id, track: true);
        await ai.SummarizeLeadAsync(lead, stats, force: true);
        if (stats["summarized"] == 0) throw new InvalidOperationException("Claude declined or didn't finish this brief; try again later");
        return await GetAsync(id);
    }

    public async Task<Overview> OverviewAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var today = DateTime.Today.ToUniversalTime();
        var high = settings.NotifyMinScore;
        var activity = await db.LeadEvents.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(20)
            .Select(e => new ActivityOut(e.Id, e.LeadId, e.Lead!.Company!.Name, e.Lead.Score, e.EventType, e.Meta, e.CreatedAt)).ToListAsync();
        var last = await db.CollectorRuns.OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        return new(
            await db.Companies.CountAsync(),
            await db.Signals.CountAsync(s => s.CreatedAt >= weekAgo),
            await db.Leads.CountAsync(),
            await db.Leads.CountAsync(l => l.Status == LeadStatus.Qualified),
            await db.Leads.CountAsync(l => l.Score >= high),
            high,
            await db.Leads.CountAsync(l => l.CreatedAt >= today),
            await db.Signals.GroupBy(s => s.Type).ToDictionaryAsync(g => g.Key, g => g.Count()),
            activity,
            last is null ? null : ToRunOut(last));
    }

    public static RunOut ToRunOut(CollectorRun r) => new(r.Id, r.Source, r.Status, r.StartedAt, r.FinishedAt, r.Cursor, r.Stats, r.Error);

    public async Task<List<RunOut>> RunsAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return (await db.CollectorRuns.OrderByDescending(r => r.Id).Take(50).ToListAsync()).Select(ToRunOut).ToList();
    }

    // ---------- scoring rules ----------

    public async Task<List<ScoringRuleOut>> RulesAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        await ScoringEngine.SeedRulesAsync(db);
        return await db.ScoringRules.OrderByDescending(r => r.Weight).ThenBy(r => r.Id)
            .Select(r => new ScoringRuleOut(r.Key, r.Label, r.Kind, r.Weight, r.Params, r.Enabled)).ToListAsync();
    }

    public async Task<ScoringRuleOut> UpdateRuleAsync(string key, ScoringRuleUpdate body)
    {
        if (body.Weight is < -100 or > 100) throw new BadRequestException("weight: -100 to 100");
        if (body.Label is { Length: 0 or > 128 }) throw new BadRequestException("label: 1-128 characters");
        await using var db = await dbs.CreateDbContextAsync();
        var r = await db.ScoringRules.FirstOrDefaultAsync(x => x.Key == key) ?? throw new NotFoundException("Rule not found");
        if (body.Weight is { } w) r.Weight = w;
        if (body.Enabled is { } e) r.Enabled = e;
        if (body.Label is { } l) r.Label = l;
        if (body.Params is { } p) r.Params = p;
        await db.SaveChangesAsync();
        return new(r.Key, r.Label, r.Kind, r.Weight, r.Params, r.Enabled);
    }

    // ---------- analytics ----------
    // Which signals, scores and contact channels actually lead to deals, from the contact log.
    // Won = QUALIFIED/CONVERTED, lost = DECLINED/DISQUALIFIED. Weight suggestions are advice; nothing here changes a weight.

    static readonly HashSet<string> Won = [LeadStatus.Qualified, LeadStatus.Converted];
    static readonly HashSet<string> Lost = [LeadStatus.Declined, LeadStatus.Disqualified];
    static readonly HashSet<string> Unworked = [LeadStatus.New, LeadStatus.Reviewed];
    public const int MinDecidedWith = 5;  // decided leads carrying a signal before its weight is judged
    public const int MinDecidedTotal = 10;
    static readonly (int Lo, int Hi)[] Bands = [(90, 100), (70, 89), (60, 69), (40, 59), (0, 39)];

    public static Rates RatesOf(IReadOnlyCollection<string> statuses)
    {
        int won = statuses.Count(Won.Contains), lost = statuses.Count(Lost.Contains);
        return new(statuses.Count, statuses.Count(s => !Unworked.Contains(s)), won, lost, won + lost > 0 ? (double)won / (won + lost) : null);
    }

    /// <summary>Win rate pulled toward 50% for small samples (Laplace), so 1/1 doesn't read as a sure thing.</summary>
    static double Smoothed(Rates r) => (r.Won + 1.0) / (r.Won + r.Lost + 2);

    public async Task<Analytics> AnalyticsAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var leads = await db.Leads.Select(l => new { l.CompanyId, l.Score, l.Status }).ToListAsync();
        var types = (await db.Signals.Select(s => new { s.CompanyId, s.Type }).Distinct().ToListAsync())
            .GroupBy(x => x.CompanyId).ToDictionary(g => g.Key, g => g.Select(x => x.Type).ToList());
        var channelEvents = await db.LeadEvents.Where(e => e.EventType == "STATUS_CHANGED").Select(e => e.Meta).ToListAsync();
        var rules = await db.ScoringRules.Where(r => r.Kind == "signal_type" && r.Enabled).OrderBy(r => r.Id).ToListAsync();
        return Compute(leads.Select(l => (l.Score, l.Status, types.GetValueOrDefault(l.CompanyId) ?? [])).ToList(), channelEvents, rules);
    }

    /// <summary>Pure part of the analytics, separate so it can be tested without a database.</summary>
    public static Analytics Compute(List<(int Score, string Status, List<string> SignalTypes)> leads, List<Dictionary<string, object?>> statusEvents,
                                    List<ScoringRule> signalRules)
    {
        var totals = RatesOf(leads.Select(l => l.Status).ToList());
        var bySignal = leads.SelectMany(l => l.SignalTypes.Select(t => (Type: t, l.Status))).GroupBy(x => x.Type)
            .Select(g =>
            {
                var r = RatesOf(g.Select(x => x.Status).ToList());
                return new SignalRates(g.Key, r.Leads, r.Worked, r.Won, r.Lost, r.WinRate, Math.Round(Smoothed(r) / Smoothed(totals), 2));
            })
            .OrderByDescending(r => r.Leads).ThenBy(r => r.Type, StringComparer.Ordinal).ToList();
        var byBand = Bands.Select(b =>
        {
            var r = RatesOf(leads.Where(l => b.Lo <= l.Score && l.Score <= b.Hi).Select(l => l.Status).ToList());
            return new BandRates($"{b.Lo}-{b.Hi}", r.Leads, r.Worked, r.Won, r.Lost, r.WinRate);
        }).ToList();

        var channels = new Dictionary<string, int[]>();  // attempts, no answer, won, lost
        foreach (var meta in statusEvents)
        {
            if (Json.Text(meta.GetValueOrDefault("channel")) is not { Length: > 0 } ch) continue;
            var to = Json.Text(meta.GetValueOrDefault("to")) ?? "";
            var c = channels.TryGetValue(ch, out var x) ? x : channels[ch] = new int[4];
            c[0]++;
            if (to == LeadStatus.NoAnswer) c[1]++;
            if (Won.Contains(to)) c[2]++;
            if (Lost.Contains(to)) c[3]++;
        }
        var byChannel = channels.Select(kv => new ChannelStats(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]))
            .OrderByDescending(c => c.Attempts).ToList();

        var decidedTotal = totals.Won + totals.Lost;
        var stats = bySignal.ToDictionary(r => r.Type);
        var suggestions = new List<WeightSuggestion>();
        foreach (var rule in signalRules)
        {
            if (rule.Params.Type is not { } t || !stats.TryGetValue(t, out var r) || rule.Weight <= 0
                || decidedTotal < MinDecidedTotal || r.Won + r.Lost < MinDecidedWith) continue;
            var suggested = Math.Clamp((int)Math.Round(rule.Weight * r.Lift / 5, MidpointRounding.ToEven) * 5, -100, 100);
            if (suggested != rule.Weight)
                suggestions.Add(new(rule.Key, rule.Label, r.Type, rule.Weight, suggested,
                    $"{r.Won}/{r.Won + r.Lost} won with this signal vs {totals.Won}/{decidedTotal} overall (lift {r.Lift})"));
        }
        return new(leads.GroupBy(l => l.Status).ToDictionary(g => g.Key, g => g.Count()), totals, bySignal, byBand, byChannel,
                   suggestions.OrderByDescending(s => Math.Abs(s.Suggested - s.Current)).ToList(), new(MinDecidedWith, MinDecidedTotal));
    }
}
