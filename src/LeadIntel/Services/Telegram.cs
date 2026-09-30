using System.Net.Http.Headers;
using System.Text.Json;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Scoring;
using Microsoft.EntityFrameworkCore;
using static System.Net.WebUtility;

namespace LeadIntel.Services;

/// <summary>
/// One Telegram message per qualifying lead, at most once (leads.notified_at), plus status/note updates from the
/// dashboard. Failures are logged and swallowed: lead generation never depends on Telegram.
/// </summary>
public class TelegramNotifier(HttpClient http, Settings settings)
{
    public bool Configured => settings.TelegramConfigured;

    /// <summary>Throws InvalidOperationException without the token in its message (the token is part of the URL).</summary>
    public async Task SendAsync(string text, CancellationToken ct = default) =>
        await PostAsync("sendMessage", JsonContent.Create(new Dictionary<string, object>
        {
            ["chat_id"] = settings.TelegramChatId!, ["text"] = text, ["parse_mode"] = "HTML", ["disable_web_page_preview"] = true,
        }), ct);

    /// <summary>Upload a file (max 50 MB) with an HTML caption.</summary>
    public async Task SendDocumentAsync(string path, string caption, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(settings.TelegramChatId!), "chat_id" },
            { new StringContent(caption.Length > 1024 ? caption[..1024] : caption), "caption" },
            { new StringContent("HTML"), "parse_mode" },
        };
        var doc = new StreamContent(file);
        doc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(doc, "document", Path.GetFileName(path));
        await PostAsync("sendDocument", form, ct);
    }

    async Task PostAsync(string method, HttpContent content, CancellationToken ct)
    {
        JsonElement body;
        int status;
        try
        {
            using var r = await http.PostAsync($"https://api.telegram.org/bot{settings.TelegramBotToken}/{method}", content, ct);
            status = (int)r.StatusCode;
            body = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new InvalidOperationException($"telegram unreachable: {e.GetType().Name}");
        }
        if (!body.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException($"telegram rejected {method}: {status} " +
                (body.TryGetProperty("description", out var d) ? d.GetString() : ""));
    }
}

public static class LeadMessages
{
    public static readonly Dictionary<string, string> StatusLabels = new()
    {
        ["NEW"] = "Not contacted yet", ["REVIEWED"] = "Reviewed", ["CONTACTED"] = "Contacted", ["NO_ANSWER"] = "No answer",
        ["QUALIFIED"] = "Qualified", ["DECLINED"] = "Declined", ["DISQUALIFIED"] = "Disqualified", ["CONVERTED"] = "Converted",
    };
    public static readonly Dictionary<string, string> ChannelLabels = new()
    {
        ["phone"] = "Phone", ["email"] = "Email", ["sms"] = "SMS", ["whatsapp"] = "WhatsApp", ["in_person"] = "In person", ["other"] = "Other",
    };
    const int History = 6;  // timeline entries shown in a status update

    public static string Status(string? s) => s is not null && StatusLabels.TryGetValue(s, out var l) ? l : s ?? "";
    public static string Phone(string v) => v.Length == 10 ? $"({v[..3]}) {v[3..6]}-{v[6..]}" : v;
    static string? M(LeadEvent e, string key) => Json.Text(e.Meta.GetValueOrDefault(key));
    static string Via(LeadEvent e) => M(e, "channel") is { Length: > 0 } ch ? $" via {ChannelLabels.GetValueOrDefault(ch, ch)}" : "";
    static string Local(DateTime utc, string fmt) => utc.ToLocalTime().ToString(fmt);

    static string? HistoryLine(LeadEvent e)
    {
        var text = e.EventType switch
        {
            "STATUS_CHANGED" => Status(M(e, "to")) + Via(e) + (M(e, "note") is { Length: > 0 } n ? $" — {n}" : ""),
            "NOTE" => $"Note{Via(e)}: {M(e, "text")}",
            "NOTIFIED" => "Alert sent to Telegram",
            "CREATED" => $"Lead created (score {M(e, "score")})",
            _ => null,
        };
        return text is null ? null : $"• {Local(e.CreatedAt, "dd MMM HH:mm")} — {HtmlEncode(text)}";
    }

    static string Link(string dashboard, int leadId) => $"<a href=\"{HtmlEncode(dashboard.TrimEnd('/'))}/leads/{leadId}\">Open in dashboard</a>";

    /// <summary>New-lead alert. Needs Company with Contacts loaded.</summary>
    public static string FormatLead(Lead lead, string dashboard)
    {
        var c = lead.Company!;
        string? First(string kind) => c.Contacts.OrderBy(x => x.Id).FirstOrDefault(x => x.Type == kind)?.Value;
        var ids = new[]
        {
            c.DotNumber is null ? null : $"USDOT {c.DotNumber}", c.McNumber,
            c.City is not null && c.State is not null ? $"{System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c.City.ToLowerInvariant())}, {c.State}" : null,
            c.FleetSize is null ? null : $"{c.FleetSize} trucks",
        };
        var lines = new List<string>
        {
            $"🔥 <b>{HtmlEncode(c.Name)}</b> — score <b>{lead.Score}</b>/100",
            HtmlEncode(string.Join(" · ", ids.Where(x => x is not null))),
            $"Status: <b>{HtmlEncode(Status(lead.Status))}</b>",
            "", "<b>Why:</b>",
        };
        lines.AddRange(lead.ScoreBreakdown.Take(4).Select(b => $"• {HtmlEncode(b.Label)} — {HtmlEncode(b.Reason)}"));
        if (lead.AiSummary is { } ai)
            lines.AddRange(["", $"🤖 <i>AI brief (check the facts):</i> {HtmlEncode(ai.Summary)}"]);
        if (lead.ServiceLines.Count > 0)
            lines.AddRange(["", "<b>Pitch:</b> " + HtmlEncode(string.Join(", ",
                lead.ServiceLines.Take(3).Where(ServicesCatalog.Lines.ContainsKey).Select(k => ServicesCatalog.Lines[k].Label)))]);
        var phone = First("phone");
        var contact = new[] { First("person"), phone is null ? null : Phone(phone), First("email"), c.Website }.Where(x => x is not null).ToList();
        if (contact.Count > 0)
            lines.AddRange(["", "<b>Contact:</b> " + HtmlEncode(string.Join(" · ", contact))]);
        lines.AddRange(["", Link(dashboard, lead.Id)]);
        return string.Join("\n", lines);
    }

    /// <summary>A status change or note from the dashboard, with the lead's recent history. Needs Company and Events.</summary>
    public static string FormatUpdate(Lead lead, LeadEvent ev, string dashboard)
    {
        var name = HtmlEncode(lead.Company!.Name);
        var via = Via(ev).TrimStart();
        var channel = via.Length > 0 ? $" ({HtmlEncode(via)})" : "";
        string head, note;
        if (ev.EventType == "STATUS_CHANGED")
            (head, note) = ($"📝 <b>{name}</b>\n{HtmlEncode(Status(M(ev, "from")))} → {HtmlEncode(Status(M(ev, "to")))}{channel}", M(ev, "note") ?? "");
        else
            (head, note) = ($"💬 <b>{name}</b>\nNote added{channel}", M(ev, "text") ?? "");
        var lines = new List<string> { head, $"🕒 {Local(ev.CreatedAt, "dd MMM yyyy HH:mm")}" };
        if (note.Length > 0) lines.Add($"🗒 {HtmlEncode(note)}");
        lines.AddRange(["", $"Status: <b>{HtmlEncode(Status(lead.Status))}</b> · score {lead.Score}"]);
        var history = lead.Events.OrderByDescending(e => e.Id).Select(HistoryLine).OfType<string>().Take(History).ToList();
        if (history.Count > 0) lines.AddRange(["", "<b>History:</b>", .. history]);
        lines.AddRange(["", Link(dashboard, lead.Id)]);
        return string.Join("\n", lines);
    }
}

public class LeadNotifications(AppDbContext db, TelegramNotifier telegram, Settings settings, ILogger<LeadNotifications> log)
{
    static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(1.1);  // Telegram allows ~1 message/second per chat

    /// <summary>Send leads with score >= NOTIFY_MIN_SCORE not yet notified, best first. Null when not configured.</summary>
    public async Task<Dictionary<string, int>?> NotifyQualifiedLeadsAsync(int? minScore = null, int? limit = null)
    {
        if (!telegram.Configured) return null;
        var min = minScore ?? settings.NotifyMinScore;
        var leads = await db.Leads.Include(l => l.Company).ThenInclude(c => c!.Contacts)
            .Where(l => l.NotifiedAt == null && l.Score >= min)
            .OrderByDescending(l => l.Score).ThenBy(l => l.Id).Take(limit ?? settings.NotifyLimit).ToListAsync();
        var stats = new Dictionary<string, int> { ["sent"] = 0, ["failed"] = 0 };
        for (var i = 0; i < leads.Count; i++)
        {
            var lead = leads[i];
            if (i > 0) await Task.Delay(SendInterval);
            try
            {
                await telegram.SendAsync(LeadMessages.FormatLead(lead, settings.FrontendOrigin));
            }
            catch (InvalidOperationException e)
            {
                log.LogWarning("lead {Id} not notified: {Error}", lead.Id, e.Message);  // stays unmarked: retried next run
                stats["failed"]++;
                continue;
            }
            lead.NotifiedAt = DateTime.UtcNow;
            db.LeadEvents.Add(new LeadEvent { LeadId = lead.Id, EventType = "NOTIFIED", Meta = new() { ["channel"] = "telegram", ["score"] = lead.Score } });
            await db.SaveChangesAsync();
            stats["sent"]++;
        }
        log.LogInformation("notifications: sent {Sent}, failed {Failed}", stats["sent"], stats["failed"]);
        return stats;
    }

    /// <summary>Send the lead's latest status change or note. Null when Telegram isn't configured; never throws.</summary>
    public async Task<bool?> SendLeadUpdateAsync(int leadId)
    {
        if (!telegram.Configured) return null;
        var lead = await db.Leads.AsNoTracking().Include(l => l.Company).Include(l => l.Events).FirstOrDefaultAsync(l => l.Id == leadId);
        var ev = lead?.Events.Where(e => e.EventType is "STATUS_CHANGED" or "NOTE").MaxBy(e => e.Id);
        if (ev is null) return false;
        try
        {
            await telegram.SendAsync(LeadMessages.FormatUpdate(lead!, ev, settings.FrontendOrigin));
            return true;
        }
        catch (InvalidOperationException e)
        {
            log.LogWarning("lead {Id} update not sent: {Error}", leadId, e.Message);
            return false;
        }
    }
}
