using System.Net;
using System.Text;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LeadIntel.Tests;

/// <summary>Analytics, enrichment and Telegram formatting, without a database or network.</summary>
public class ServiceTests
{
    [Fact]
    public void AnalyticsWinRatesChannelsAndSuggestions()
    {
        var leads = new List<(int, string, List<string>)>();
        // with CRASH: 5 won, 1 lost; without: 1 won, 5 lost; plus 2 untouched leads
        foreach (var st in new[] { "QUALIFIED", "CONVERTED", "QUALIFIED", "QUALIFIED", "CONVERTED", "DECLINED" }) leads.Add((95, st, ["CRASH", "HOS_VIOLATIONS"]));
        foreach (var st in new[] { "QUALIFIED", "DECLINED", "DISQUALIFIED", "DECLINED", "DECLINED", "DISQUALIFIED" }) leads.Add((45, st, ["HOS_VIOLATIONS"]));
        leads.Add((30, "NEW", ["CRASH"]));
        leads.Add((30, "NO_ANSWER", []));
        var events = new List<Dictionary<string, object?>>
        {
            new() { ["from"] = "NEW", ["to"] = "NO_ANSWER", ["channel"] = "phone" },
            new() { ["from"] = "NO_ANSWER", ["to"] = "CONTACTED", ["channel"] = "email" },
            new() { ["from"] = "NEW", ["to"] = "REVIEWED" },  // no channel
        };
        var rules = LeadIntel.Scoring.ScoringEngine.DefaultRuleRows().Where(r => r.Kind == "signal_type" && r.Enabled).ToList();

        var a = LeadService.Compute(leads, events, rules);

        Assert.Equal(new Rates(14, 13, 6, 6, 0.5), a.Totals);
        var crash = a.BySignal.Single(r => r.Type == "CRASH");
        Assert.Equal((7, 5, 1, 5 / 6.0), (crash.Leads, crash.Won, crash.Lost, crash.WinRate!.Value));
        Assert.Equal(1.5, crash.Lift);  // smoothed: (5+1)/(6+2) vs (6+1)/(12+2)
        Assert.Equal(5, a.ByScoreBand.Single(b => b.Band == "90-100").Won);
        Assert.Equal(5, a.ByScoreBand.Single(b => b.Band == "40-59").Lost);
        Assert.Equal(new Dictionary<string, (int, int)> { ["phone"] = (1, 1), ["email"] = (1, 0) },
                     a.ByChannel.ToDictionary(c => c.Channel, c => (c.Attempts, c.NoAnswer)));
        var s = a.Suggestions.Single(x => x.Key == "sig_crash");
        Assert.Equal((10, 15), (s.Current, s.Suggested));
        Assert.Contains("5/6 won", s.Reason);
        Assert.DoesNotContain(a.Suggestions, x => x.Key == "sig_inactive");  // negative weights are never auto-suggested
    }

    [Fact]
    public void NoSuggestionsWithoutEnoughOutcomes()
    {
        var a = LeadService.Compute([(90, "QUALIFIED", ["CRASH"])], [], LeadIntel.Scoring.ScoringEngine.DefaultRuleRows());
        Assert.Empty(a.Suggestions);
        Assert.Equal(1.0, a.Totals.WinRate);
    }

    const string Home = """
        <html><body><h1>Acme Freight</h1>
        <a href="mailto:Info@AcmeFreight.com?subject=hi">Email us</a>
        <a href="tel:+1 (214) 555-0100">Call</a>
        <a href="https://www.facebook.com/acmefreight/">fb</a>
        <a href="https://www.facebook.com/sharer/sharer.php?u=x">share</a>
        <p>Sales: Sales@AcmeFreight.com · (214) 555-0177 · <img src="logo@2x.png"> hero@2x.png</p>
        <a href="https://wa.me/12145550188">WhatsApp</a>
        <p>Now hiring CDL-A drivers — great home time!</p>
        <a href="/contact">Contact</a></body></html>
        """;
    const string Contact = """<p>Dispatch: <a href="tel:214.555.0199">214.555.0199</a></p><form><textarea name="m"></textarea></form>""";

    /// <summary>Serves pages by host+path; records every request.</summary>
    class FakeWeb(Dictionary<string, (int Status, string Body)> pages, List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var key = req.RequestUri!.Host + req.RequestUri.AbsolutePath;
            seen.Add(key);
            if (req.RequestUri.Host == "api.search.brave.com")
            {
                var urls = pages["_search"].Body.Split(' ').Select(u => $"{{\"url\":\"{u}\"}}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"web\":{{\"results\":[{string.Join(",", urls)}]}}}}", Encoding.UTF8, "application/json"),
                });
            }
            var (status, body) = pages.GetValueOrDefault(key, (404, ""));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                RequestMessage = req, Content = new StringContent(body, Encoding.UTF8, "text/html"),
            });
        }
    }

    static Enricher MakeEnricher(Dictionary<string, (int, string)> pages, List<string> seen, string? searchKey = null) =>
        new(new HttpClient(new FakeWeb(pages, seen)), new Settings { BraveApiKey = searchKey }, NullLogger<Enricher>.Instance);

    static Company Acme() => new() { Name = "ACME FREIGHT LLC", DotNumber = "123", City = "DALLAS", State = "TX" };

    [Fact]
    public async Task WebsiteFromRegisteredEmailDomain()
    {
        var seen = new List<string>();
        var enricher = MakeEnricher(new() { ["acmefreight.com/"] = (200, Home), ["acmefreight.com/contact"] = (200, Contact) }, seen);
        var (site, contacts, hiring) = await enricher.EnrichAsync(Acme(), ["ops@acmefreight.com"], []);
        Assert.Equal("https://acmefreight.com/", site);
        Assert.Equal([("https://acmefreight.com/", "Now hiring CDL-A drivers")], hiring);
        Assert.Equal(new HashSet<(string, string, string, string?)>
        {
            ("website", "https://acmefreight.com/", "email_domain", "https://acmefreight.com/"),
            ("email", "info@acmefreight.com", "website", "https://acmefreight.com/"),
            ("phone", "2145550100", "website", "https://acmefreight.com/"),
            ("social", "https://www.facebook.com/acmefreight", "website", "https://acmefreight.com/"),
            ("phone", "2145550199", "website", "https://acmefreight.com/contact"),
            ("email", "sales@acmefreight.com", "website", "https://acmefreight.com/"),  // plain text, not a link
            ("phone", "2145550177", "website", "https://acmefreight.com/"),
            ("social", "https://wa.me/12145550188", "website", "https://acmefreight.com/"),
            ("form", "https://acmefreight.com/contact", "website", "https://acmefreight.com/contact"),
        }, contacts.Select(c => (c.Type, c.Value, c.Source, c.SourceUrl)).ToHashSet());
    }

    [Fact]
    public async Task FreeMailIsSkippedAndRobotsDisallowIsHonored()
    {
        var seen = new List<string>();
        Assert.Null((await MakeEnricher([], seen).EnrichAsync(Acme(), ["acme@gmail.com"], [])).Website);
        Assert.Empty(seen);  // no search key, free-mail domain: nothing fetched

        var enricher = MakeEnricher(new() { ["acmefreight.com/robots.txt"] = (200, "User-agent: *\nDisallow: /"), ["acmefreight.com/"] = (200, Home) }, seen);
        var (site, contacts, _) = await enricher.EnrichAsync(Acme(), ["ops@acmefreight.com"], []);
        Assert.Equal("https://acmefreight.com/", site);
        Assert.Equal(["website"], contacts.Select(c => c.Type));
        Assert.DoesNotContain("acmefreight.com/", seen);  // site exists (robots answered) but pages were not read
    }

    [Fact]
    public async Task SearchResultNeedsDotOrPhoneOnPage()
    {
        var seen = new List<string>();
        var enricher = MakeEnricher(new()
        {
            ["_search"] = (200, "https://www.yelp.com/biz/acme https://other-acme.com/ https://acme-trucking.com/"),
            ["other-acme.com/"] = (200, "<p>Acme Freight, Ohio. USDOT 99999</p>"),
            ["acme-trucking.com/"] = (200, "<p>Acme Freight LLC · USDOT #123 · (214) 555-0100</p>"),
        }, seen, searchKey: "key");
        var (site, contacts, _) = await enricher.EnrichAsync(Acme(), ["acme@gmail.com"], ["2145550100"]);
        Assert.Equal("https://acme-trucking.com/", site);
        Assert.Contains(contacts, c => c is { Type: "website", Source: "web_search" });
        Assert.DoesNotContain(seen, s => s.Contains("yelp.com"));  // directories are skipped without fetching
    }

    [Fact]
    public void PrivateAddressesAreRefused()
    {
        foreach (var ip in new[] { "127.0.0.1", "10.1.2.3", "192.168.1.1", "172.20.0.1", "169.254.169.254", "::1", "fd00::1" })
            Assert.False(Web.IsPublic(IPAddress.Parse(ip)), ip);
        Assert.True(Web.IsPublic(IPAddress.Parse("93.184.216.34")));
    }

    [Fact]
    public void TelegramAlertEscapesHtml()
    {
        var lead = new Lead
        {
            Id = 7, Score = 88, Status = "NEW",
            ScoreBreakdown = [new("sig_crash", "Crash <on record>", 10, "1 crash")],
            ServiceLines = ["insurance"],
            Company = new Company
            {
                Name = "A&B <Trucking>", DotNumber = "123", City = "DALLAS", State = "TX", FleetSize = 12,
                Contacts = [new Contact { Id = 1, Type = "phone", Value = "2145550100" }],
            },
        };
        var text = LeadMessages.FormatLead(lead, "http://localhost:8000/");
        Assert.Contains("<b>A&amp;B &lt;Trucking&gt;</b> — score <b>88</b>", text);
        Assert.Contains("Crash &lt;on record&gt;", text);
        Assert.Contains("Dallas, TX", text);
        Assert.Contains("(214) 555-0100", text);
        Assert.Contains("href=\"http://localhost:8000/leads/7\"", text);
    }
}
