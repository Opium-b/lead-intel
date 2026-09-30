using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using LeadIntel.Collectors;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Pipeline;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LeadIntel.Services;

/// <summary>
/// Find a lead's website and the contacts published on it.
/// No guessing. A website is accepted only when it is the company's own registered email domain, or when a search
/// result's site shows the company's USDOT number or registered phone. Every contact records the page it was seen on,
/// and robots.txt is honored for every page read.
/// </summary>
public static partial class Web
{
    public const string UserAgent = "leadintel/0.1 (lead research; honors robots.txt)";

    public static readonly HashSet<string> FreeMail =
    [
        "gmail.com", "googlemail.com", "yahoo.com", "ymail.com", "rocketmail.com", "hotmail.com", "outlook.com",
        "live.com", "msn.com", "aol.com", "icloud.com", "me.com", "mac.com", "protonmail.com", "proton.me", "gmx.com",
        "mail.com", "zoho.com", "yandex.com", "comcast.net", "att.net", "sbcglobal.net", "bellsouth.net", "verizon.net",
        "charter.net", "cox.net", "earthlink.net", "frontier.com", "windstream.net", "centurylink.net", "optonline.net",
        "roadrunner.com", "twc.com", "spectrum.net", "juno.com", "netzero.net",
    ];
    // Search results on these hosts are never the company's own site, so they're skipped without fetching.
    public static readonly string[] Directories =
    [
        "facebook.com", "linkedin.com", "instagram.com", "x.com", "twitter.com", "youtube.com", "tiktok.com",
        "yelp.com", "bbb.org", "yellowpages.com", "mapquest.com", "manta.com", "bizapedia.com", "opencorporates.com",
        "dnb.com", "zoominfo.com", "indeed.com", "glassdoor.com", "google.com", "carriersource.io", "wikipedia.org",
    ];
    public static readonly Dictionary<string, string> Social = new()
    {
        ["facebook.com"] = "Facebook", ["linkedin.com"] = "LinkedIn", ["instagram.com"] = "Instagram", ["x.com"] = "X", ["twitter.com"] = "X",
    };
    static readonly string[] NotEmail = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", "example.com", "domain.com", "sentry.io", "wixpress.com"];

    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)+[a-z]{2,}$")] public static partial Regex Domain();
    [GeneratedRegex(@"^[a-z0-9._%+-]+@(?:[a-z0-9-]+\.)+[a-z]{2,}$")] private static partial Regex Email();
    [GeneratedRegex(@"(?<![\w.%+-])[\w.%+-]+@(?:[a-z0-9-]+\.)+[a-z]{2,}\b", RegexOptions.IgnoreCase)] private static partial Regex TextEmail();
    // separators required: bare digit runs are ids
    [GeneratedRegex(@"(?<!\d)\(?\d{3}\)?[\s.-]\d{3}[.-]\d{4}(?!\d)")] private static partial Regex PhoneText();
    // Driver-hiring language only: "now hiring" alone could be an office job.
    [GeneratedRegex(@"\b(?:now hiring|we(?:'|’)?re hiring|we are hiring|hiring)(?:[\s-]+(?:cdl(?:-a)?|class[\s-]a|otr|local|regional|experienced|company|truck|team))*[\s-]+drivers?\b|\bhiring[\s-]+owner[\s-]operators?\b|\bdrivers?\s+wanted\b|\b(?:cdl(?:-a)?|class[\s-]a|truck)\s+drivers?\s+needed\b|\bdrive\s+for\s+us\b", RegexOptions.IgnoreCase)]
    private static partial Regex Hiring();

    public static string? Phone(string raw)
    {
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        return digits.Length == 10 ? digits : null;
    }

    /// <summary>The entry of <paramref name="domains"/> that host is, or is a subdomain of.</summary>
    public static string? On(string host, IEnumerable<string> domains) =>
        domains.FirstOrDefault(d => host == d || host.EndsWith("." + d));

    public record Page(string Url, List<ContactRecord> Contacts, List<string> Subpages, string Text, string? HiringPhrase)
    {
        public static Page Parse(string url, string html)
        {
            var doc = new HtmlParser().ParseDocument(html);
            var text = string.Join(" ", doc.DocumentElement.Descendants<IText>().Select(t => t.Data));
            var contacts = new List<ContactRecord>();
            var subpages = new List<string>();
            void Add(string type, string value, string label) => contacts.Add(new(type, value, "website", url, label));
            var baseUri = new Uri(url);
            foreach (var href in doc.QuerySelectorAll("a[href]").Select(a => a.GetAttribute("href")!.Trim()))
            {
                var low = href.ToLowerInvariant();
                if (low.StartsWith("mailto:"))
                {
                    var email = Uri.UnescapeDataString(href[7..]).Split('?')[0].Trim().ToLowerInvariant();
                    if (Email().IsMatch(email)) Add("email", email, "Email on website");
                }
                else if (low.StartsWith("tel:"))
                {
                    if (Phone(href[4..]) is { } p) Add("phone", p, "Phone on website");
                }
                else if (Uri.TryCreate(baseUri, href, out var full) && full.Scheme is "http" or "https")
                {
                    var fullUrl = full.GetLeftPart(UriPartial.Query);  // without #fragment
                    var host = full.Host.ToLowerInvariant();
                    var path = full.AbsolutePath.TrimEnd('/');
                    if (host is "wa.me" or "api.whatsapp.com" or "wa.link")
                    {
                        var number = host == "wa.me" ? path.Trim('/')
                            : System.Web.HttpUtility.ParseQueryString(full.Query)["phone"] ?? "";
                        if (number.Length > 0 && number.All(char.IsAsciiDigit)) Add("social", $"https://wa.me/{number}", "WhatsApp");
                    }
                    else if (On(host, Social.Keys) is { } net)
                    {
                        if (path.Length > 0 && !new[] { "share", "intent", "dialog" }.Any(path.ToLowerInvariant().Contains))
                            Add("social", $"https://{host}{path}", $"{Social[net]} page");
                    }
                    else if (host == baseUri.Host.ToLowerInvariant() && path.Contains("contact", StringComparison.OrdinalIgnoreCase) && !subpages.Contains(fullUrl))
                        subpages.Add(fullUrl);
                }
            }
            foreach (Match m in TextEmail().Matches(text))
            {
                var email = m.Value.ToLowerInvariant();
                if (!NotEmail.Any(email.EndsWith)) Add("email", email, "Email on website");
            }
            foreach (Match m in PhoneText().Matches(text))
                Add("phone", Phone(m.Value)!, "Phone on website");
            if (doc.QuerySelector("textarea") is not null)  // a message box: a contact form, not a search or login form
                Add("form", url, "Contact form");
            var hiring = Hiring().Match(text);
            return new(url, contacts, subpages, text, hiring.Success ? string.Join(" ", hiring.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null);
        }

        /// <summary>What on this page proves it belongs to the company, if anything.</summary>
        public string? Shows(string? dot, ISet<string> phones)
        {
            if (dot is not null && Regex.IsMatch(Text, $@"(?<!\d){Regex.Escape(dot)}(?!\d)")) return $"USDOT {dot}";
            var seen = PhoneText().Matches(Text).Select(m => Phone(m.Value)).OfType<string>()
                .Concat(Contacts.Where(c => c.Type == "phone").Select(c => c.Value));
            return seen.FirstOrDefault(phones.Contains) is { } hit ? $"registered phone {hit}" : null;
        }
    }

    /// <summary>Minimal robots.txt: the group for our agent (or *), longest matching Allow/Disallow prefix wins.</summary>
    public class Robots
    {
        public bool DisallowAll, AllowAll;
        readonly List<(bool Allow, string Path)> rules = [];

        public static Robots Parse(string text)
        {
            var groups = new Dictionary<string, List<(bool, string)>>();
            List<string> agents = [];
            var lastWasAgent = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Split('#')[0].Trim();
                var i = line.IndexOf(':');
                if (i < 0) continue;
                var (key, value) = (line[..i].Trim().ToLowerInvariant(), line[(i + 1)..].Trim());
                if (key == "user-agent")
                {
                    if (!lastWasAgent) agents = [];
                    agents.Add(value.ToLowerInvariant());
                    foreach (var a in agents) groups.TryAdd(a, []);
                    lastWasAgent = true;
                    continue;
                }
                lastWasAgent = false;
                if (key is "allow" or "disallow" && value.Length > 0)
                    foreach (var a in agents) groups[a].Add((key == "allow", value));
            }
            var r = new Robots();
            var mine = groups.FirstOrDefault(g => g.Key != "*" && "leadintel".StartsWith(g.Key.Split('/')[0])).Value ?? groups.GetValueOrDefault("*");
            if (mine is not null) r.rules.AddRange(mine);
            return r;
        }

        public bool CanFetch(string url)
        {
            if (DisallowAll) return false;
            if (AllowAll) return true;
            var path = new Uri(url).PathAndQuery;
            var best = rules.Where(x => path.StartsWith(x.Path)).OrderByDescending(x => x.Path.Length).ThenByDescending(x => x.Allow).FirstOrDefault();
            return best.Path is null || best.Allow;
        }
    }

    /// <summary>Refuse private/loopback targets at connect time: domains come from public registrations anyone can edit.
    /// Checking the address actually connected to also covers DNS rebinding.</summary>
    public static async ValueTask<Stream> PublicOnlyConnect(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || !addresses.All(IsPublic))
            throw new HttpRequestException("refusing non-public address");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return !ip.Equals(IPAddress.IPv6Any);
        var b = ip.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31)
                 || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }
}

public class Enricher(HttpClient http, Settings settings, ILogger<Enricher> log)
{
    const string SearchUrl = "https://api.search.brave.com/res/v1/web/search";
    const int MaxBytes = 2_000_000;  // per page
    const int MaxSubpages = 2;  // contact pages linked from the homepage
    const int MaxCandidates = 5;  // search results checked per company

    static List<(string Page, string Phrase)> HiringOf(List<Web.Page> pages) =>
        pages.Where(p => p.HiringPhrase is not null).Select(p => (p.Url, p.HiringPhrase!)).Take(1).ToList();  // one per site

    /// <summary>(website, contacts, [(page, hiring phrase)]). Network errors on a site mean nothing was found.</summary>
    public async Task<(string? Website, List<ContactRecord> Contacts, List<(string Page, string Phrase)> Hiring)> EnrichAsync(
        Company company, IEnumerable<string> emails, IEnumerable<string> phones)
    {
        foreach (var domain in emails.Select(e => e[(e.LastIndexOf('@') + 1)..].ToLowerInvariant()).Distinct())
        {
            if (Web.FreeMail.Contains(domain) || !Web.Domain().IsMatch(domain)) continue;
            if (await ReadSiteAsync($"https://{domain}/") is { } site)
                return (site.Url, Collect(new("website", site.Url, "email_domain", site.Url, $"Domain of registered email @{domain}"), site.Pages), HiringOf(site.Pages));
        }
        if (settings.BraveApiKey is not null)
        {
            var phoneSet = phones.ToHashSet();
            foreach (var root in await SearchAsync(company))
            {
                if (await ReadSiteAsync(root) is not { } site) continue;
                var proof = site.Pages.Select(p => (p.Url, Why: p.Shows(company.DotNumber, phoneSet))).FirstOrDefault(x => x.Why is not null);
                if (proof.Why is not null)
                    return (site.Url, Collect(new("website", site.Url, "web_search", proof.Url, $"Found by web search; page shows {proof.Why}"), site.Pages), HiringOf(site.Pages));
            }
        }
        return (null, [], []);
    }

    static List<ContactRecord> Collect(ContactRecord website, List<Web.Page> pages)
    {
        var found = new Dictionary<(string, string), ContactRecord> { [(website.Type, website.Value)] = website };
        foreach (var c in pages.SelectMany(p => p.Contacts).Where(c => c.Value.Length <= 512))
            found.TryAdd((c.Type, c.Value), c);
        return found.Values.ToList();
    }

    async Task<List<string>> SearchAsync(Company company)
    {
        var q = string.Join(" ", new[] { $"\"{company.DbaName ?? company.Name}\"", company.City, company.State }.Where(x => x is not null));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{SearchUrl}?q={Uri.EscapeDataString(q)}&count=10");
        req.Headers.Add("X-Subscription-Token", settings.BraveApiKey);
        req.Headers.Add("Accept", "application/json");
        using var r = await http.SendAsync(req);
        r.EnsureSuccessStatusCode();  // quota/auth problems surface as a failed company, retried next run
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var roots = new List<string>();
        if (body.TryGetProperty("web", out var web) && web.TryGetProperty("results", out var results))
            foreach (var result in results.EnumerateArray())
                if (Uri.TryCreate(result.GetProperty("url").GetString(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                    && Web.On(u.Host, Web.Directories) is null && !u.Host.EndsWith(".gov"))
                    roots.Add($"{u.Scheme}://{u.Host}/");
        return roots.Distinct().Take(MaxCandidates).ToList();
    }

    /// <summary>(site url, pages read) if the site answers; pages is empty when robots.txt forbids reading them.</summary>
    async Task<(string Url, List<Web.Page> Pages)?> ReadSiteAsync(string root)
    {
        Web.Robots robots;
        try
        {
            using var r = await http.GetAsync(new Uri(new Uri(root), "/robots.txt"));
            robots = (int)r.StatusCode >= 500 ? new Web.Robots { DisallowAll = true }
                : (int)r.StatusCode >= 400 ? new Web.Robots { AllowAll = true }
                : Web.Robots.Parse(await r.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
        // ponytail: a parked domain also answers here; add a parked-page check if those show up as websites
        if (!robots.CanFetch(root)) return (root, []);
        if (await PageAsync(root) is not { } home) return null;
        var pages = new List<Web.Page> { home };
        foreach (var sub in home.Subpages.Take(MaxSubpages))
            if (robots.CanFetch(sub) && await PageAsync(sub) is { } page)
                pages.Add(page);
        return (home.Url, pages);
    }

    async Task<Web.Page?> PageAsync(string url)
    {
        try
        {
            using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (r.StatusCode != HttpStatusCode.OK || r.Content.Headers.ContentType?.MediaType?.Contains("html") != true) return null;
            await using var stream = await r.Content.ReadAsStreamAsync();
            var buffer = new byte[MaxBytes];
            int total = 0, n;
            while (total < MaxBytes && (n = await stream.ReadAsync(buffer.AsMemory(total, MaxBytes - total))) > 0) total += n;
            var charset = r.Content.Headers.ContentType?.CharSet;
            Encoding enc;
            try { enc = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); } catch (ArgumentException) { enc = Encoding.UTF8; }
            return Web.Page.Parse(r.RequestMessage!.RequestUri!.ToString(), enc.GetString(buffer, 0, total));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            log.LogDebug("page {Url} not read: {Error}", url, e.GetType().Name);
            return null;
        }
    }
}

public class EnrichmentJob(AppDbContext db, Enricher enricher, LeadPipeline pipeline, Settings settings, ILogger<EnrichmentJob> log)
{
    /// <summary>Enrich leads at or above ENRICH_MIN_SCORE not enriched within ENRICH_TTL_DAYS, best score first.</summary>
    public async Task<Dictionary<string, int>> EnrichLeadsAsync(int? limit = null)
    {
        var now = DateTime.UtcNow;
        var stale = now.AddDays(-settings.EnrichTtlDays);
        var due = await db.Companies.Include(c => c.Lead)
            .Where(c => c.Lead != null && c.Lead.Score >= settings.EnrichMinScore && (c.EnrichedAt == null || c.EnrichedAt < stale))
            .OrderByDescending(c => c.Lead!.Score).Take(limit ?? settings.EnrichLimit).ToListAsync();

        var stats = new Dictionary<string, int> { ["enriched"] = 0, ["websites"] = 0, ["contacts"] = 0, ["failed"] = 0 };
        var changed = new List<int>();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        foreach (var company in due)
        {
            var known = await db.Contacts.Where(c => c.CompanyId == company.Id).Select(c => new { c.Type, c.Value }).ToListAsync();
            (string? Website, List<ContactRecord> Contacts, List<(string Page, string Phrase)> Hiring) found;
            try
            {
                found = await enricher.EnrichAsync(company, known.Where(k => k.Type == "email").Select(k => k.Value),
                                                   known.Where(k => k.Type == "phone").Select(k => k.Value));
            }
            catch (Exception e)
            {
                log.LogError(e, "enrichment failed for company {Id}", company.Id);  // not marked enriched: retried next run
                stats["failed"]++;
                continue;
            }
            var added = found.Contacts.Count > 0 ? await LeadPipeline.InsertContactsAsync(conn, null, company.Id, found.Contacts) : [];
            foreach (var (page, phrase) in found.Hiring)  // a fact from their own site; the HiringRule turns it into a signal
            {
                var payload = new Dictionary<string, JsonElement>
                {
                    ["phrase"] = JsonSerializer.SerializeToElement(phrase), ["page"] = JsonSerializer.SerializeToElement(page),
                    ["seen_on"] = JsonSerializer.SerializeToElement(DateOnly.FromDateTime(now).ToString("yyyy-MM-dd")),
                };
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO source_records (source, record_type, external_id, company_id, observed_at, source_url, payload, payload_hash)
                    VALUES ('website', 'hiring', @ext, @company, @observed, @url, @payload, @hash)
                    ON CONFLICT (source, record_type, external_id) DO UPDATE SET company_id = EXCLUDED.company_id,
                        observed_at = EXCLUDED.observed_at, payload = EXCLUDED.payload, payload_hash = EXCLUDED.payload_hash
                    """, conn);
                cmd.Parameters.AddWithValue("ext", page.Length > 128 ? page[..128] : page);
                cmd.Parameters.AddWithValue("company", company.Id);
                cmd.Parameters.AddWithValue("observed", DateOnly.FromDateTime(now));
                cmd.Parameters.AddWithValue("url", page);
                cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(payload) });
                cmd.Parameters.AddWithValue("hash", PayloadHash.Of(payload));
                await cmd.ExecuteNonQueryAsync();
            }
            company.Website = found.Website ?? company.Website;
            company.EnrichedAt = now;
            if (added.Count > 0)
                db.LeadEvents.Add(new LeadEvent
                {
                    LeadId = company.Lead!.Id, EventType = "ENRICHED",
                    Meta = new() { ["website"] = found.Website, ["contacts"] = added.Count, ["types"] = added.Distinct().Order().ToList() },
                });
            if (added.Count > 0 || found.Hiring.Count > 0) changed.Add(company.Id);
            await db.SaveChangesAsync();
            stats["enriched"]++;
            if (found.Website is not null) stats["websites"]++;
            stats["contacts"] += added.Count;
        }
        if (changed.Count > 0)
            await pipeline.ProcessCompaniesAsync(changed);  // contacts feed the has_contact scoring rule
        log.LogInformation("enrichment: {Stats}", Json.Serialize(stats));
        return stats;
    }
}
