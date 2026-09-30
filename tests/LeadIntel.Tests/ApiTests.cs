using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadIntel.Tests;

/// <summary>The whole app against the leadintel_test database (emptied first): collect-free pipeline + REST API.</summary>
public class ApiTests : IClassFixture<ApiTests.App>
{
    const string Secret = "test-secret-test-secret";

    public class App : WebApplicationFactory<Program>
    {
        public App()
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", Environment.GetEnvironmentVariable("TEST_DATABASE_URL") ?? "Host=localhost;Database=leadintel_test");
            Environment.SetEnvironmentVariable("API_SECRET", Secret);
            Environment.SetEnvironmentVariable("COLLECT_EVERY_HOURS", "0");
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureTestServices(s =>  // never reach Telegram, Claude or Brave from tests
                s.AddSingleton(Settings.FromEnvironment() with { TelegramBotToken = null, TelegramChatId = null, AnthropicApiKey = null, BraveApiKey = null }));
    }

    readonly App app;
    public ApiTests(App app) => this.app = app;

    HttpClient Client(bool auth = true)
    {
        var c = app.CreateClient();
        if (auth) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        return c;
    }

    async Task<int> SeedLeadAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE companies, source_records, signals, contacts, leads, lead_events, scoring_rules, collector_runs RESTART IDENTITY CASCADE");
        var pipeline = scope.ServiceProvider.GetRequiredService<LeadPipeline>();
        var batch = LeadIntel.Collectors.FmcsaCollector.Normalize(CoreTests.Raw());
        for (var i = 0; i < 2; i++)  // idempotent: a second pass changes nothing
            await pipeline.ProcessCompaniesAsync(await pipeline.IngestAsync(batch), CoreTests.Today);
        Assert.Equal(1, await db.Leads.CountAsync());
        Assert.Equal(1, await db.LeadEvents.CountAsync(e => e.EventType == "CREATED"));
        return await db.Leads.Select(l => l.Id).SingleAsync();
    }

    [Fact]
    public async Task ApiNeedsTheSecret()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client(auth: false).GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(auth: false).GetAsync("/api/leads")).StatusCode);
        var wrong = Client(auth: false);
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/stats/overview")).StatusCode);
    }

    [Fact]
    public async Task LeadFlow()
    {
        var id = await SeedLeadAsync();
        var http = Client();

        var page = await http.GetFromJsonAsync<JsonElement>("/api/leads?min_score=1&sort=-signals");
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        var row = page.GetProperty("items")[0];
        Assert.Equal("123", row.GetProperty("dot_number").GetString());
        Assert.Equal("2145550100", row.GetProperty("phone").GetString());
        Assert.Contains("CRASH", row.GetProperty("signal_types").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonElement>("/api/leads?service_line=nope")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await http.GetFromJsonAsync<JsonElement>("/api/leads?signal_type=CRASH&state=tx")).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.GetAsync("/api/leads?state=texas")).StatusCode);

        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/leads/{id}");
        Assert.NotEmpty(detail.GetProperty("pitch").EnumerateArray());
        Assert.Equal("critical", detail.GetProperty("signals")[0].GetProperty("severity").GetString());  // most severe first

        var updated = await (await http.PatchAsJsonAsync($"/api/leads/{id}", new { status = "CONTACTED", channel = "phone", note = "call back Friday" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONTACTED", updated.GetProperty("status").GetString());
        var ev = updated.GetProperty("events")[0];
        Assert.Equal("STATUS_CHANGED", ev.GetProperty("event_type").GetString());
        Assert.Equal("call back Friday", ev.GetProperty("meta").GetProperty("note").GetString());

        Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync($"/api/leads/{id}/notes", new { text = "left voicemail" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/leads/999999")).StatusCode);

        var overview = await http.GetFromJsonAsync<JsonElement>("/api/stats/overview");
        Assert.Equal(1, overview.GetProperty("leads").GetInt32());
        Assert.Equal("NOTE", overview.GetProperty("recent_activity")[0].GetProperty("event_type").GetString());
        var analytics = await http.GetFromJsonAsync<JsonElement>("/api/stats/analytics");
        Assert.Equal(1, analytics.GetProperty("totals").GetProperty("worked").GetInt32());
    }
}
