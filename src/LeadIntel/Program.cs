using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Anthropic.Exceptions;
using LeadIntel;
using LeadIntel.Collectors;
using LeadIntel.Components;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Pipeline;
using LeadIntel.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// One process, three faces: `dotnet run` serves the dashboard (Blazor) + REST API; `dotnet run -- <command>` runs a
// pipeline command once (see Cli.cs); COLLECT_EVERY_HOURS > 0 adds an in-process scheduler.
var settings = Settings.FromEnvironment();
var isCli = Cli.IsCommand(args);
var builder = WebApplication.CreateBuilder(isCli ? [] : args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning).AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddLeadIntel(settings);

if (isCli)
{
    await using var cli = builder.Build();
    return await Cli.RunAsync(cli.Services, args);
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.LoginPath = "/login";
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx =>  // the API answers 401 instead of redirecting to the sign-in page
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.DictionaryKeyPolicy = null;
    o.SerializerOptions.Encoder = Json.Options.Encoder;
});
builder.Services.AddHostedService<Scheduler>();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())  // empty database: create the tables
    await Schema.EnsureCreatedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/logout", async ctx =>
{
    await ctx.SignOutAsync();
    ctx.Response.Redirect("/login");
});
app.MapApi(settings);
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
return 0;

public partial class Program;  // visible to the integration tests (WebApplicationFactory)

namespace LeadIntel
{
    public static class Setup
    {
        public static IServiceCollection AddLeadIntel(this IServiceCollection s, Settings settings)
        {
            s.AddSingleton(settings);
            var dataSource = AppDbContext.BuildDataSource(settings.DatabaseUrl);
            s.AddSingleton(dataSource);
            s.AddDbContextFactory<AppDbContext>(o => AppDbContext.Configure(o, dataSource));
            s.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

            s.AddHttpClient<FmcsaCollector>(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(120);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("leadintel/0.1 (lead research; public FMCSA data)");
                if (settings.SocrataAppToken is { } token) c.DefaultRequestHeaders.Add("X-App-Token", token);
            });
            s.AddHttpClient<TelegramNotifier>(c => c.Timeout = TimeSpan.FromSeconds(120));
            s.AddHttpClient<Enricher>(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(10);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(Web.UserAgent);
            }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectCallback = Web.PublicOnlyConnect, MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.All,
            });

            s.AddScoped<LeadPipeline>();
            s.AddScoped<CollectionRunner>();
            s.AddScoped<EnrichmentJob>();
            s.AddScoped<AiBriefs>();
            s.AddScoped<LeadNotifications>();
            s.AddScoped<DailyReport>();
            s.AddScoped<LeadService>();
            s.AddSingleton<Jobs>();
            return s;
        }
    }

    /// <summary>The REST API (same data the dashboard shows): a cookie from the sign-in page or
    /// <c>Authorization: Bearer API_SECRET</c>.</summary>
    public static class Api
    {
        public static bool SecretMatches(string given, Settings settings) =>
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(settings.ApiSecret));

        public static void MapApi(this WebApplication app, Settings settings)
        {
            var api = app.MapGroup("/api");
            api.MapGet("/health", () => new { ok = true });

            // ponytail: single shared secret; add users + roles when there's more than one operator
            var p = api.MapGroup("").AddEndpointFilter(async (ctx, next) =>
            {
                var http = ctx.HttpContext;
                var header = http.Request.Headers.Authorization.ToString();
                var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && SecretMatches(header[7..].Trim(), settings);
                if (!bearer && http.User.Identity?.IsAuthenticated != true)
                    return Results.Problem("Invalid or missing token", statusCode: 401);
                try
                {
                    return await next(ctx);
                }
                catch (BadRequestException e) { return Results.Problem(e.Message, statusCode: 422); }
                catch (NotFoundException e) { return Results.Problem(e.Message, statusCode: 404); }
                catch (AnthropicApiException e) { return Results.Problem($"Claude API error: {e.GetType().Name}", statusCode: 502); }
                catch (InvalidOperationException e) when (e.Message.StartsWith("Claude")) { return Results.Problem(e.Message, statusCode: 502); }
            });

            p.MapGet("/service-lines", LeadService.ServiceLineList);
            p.MapGet("/leads", (LeadService s, string? q, string? state, string? status, [FromQuery(Name = "min_score")] int? minScore,
                                [FromQuery(Name = "signal_type")] string? signalType, [FromQuery(Name = "service_line")] string? serviceLine,
                                string? sort, int? page, [FromQuery(Name = "page_size")] int? pageSize) =>
                s.ListAsync(new LeadFilter
                {
                    Q = q, State = state, Status = status, MinScore = minScore ?? 0, SignalType = signalType, ServiceLine = serviceLine,
                    Sort = sort ?? "-score", Page = page ?? 1, PageSize = pageSize ?? 25,
                }));
            p.MapGet("/leads/{id:int}", (LeadService s, int id) => s.GetAsync(id));
            p.MapPatch("/leads/{id:int}", (LeadService s, int id, LeadUpdate body) => s.UpdateAsync(id, body));
            p.MapPost("/leads/{id:int}/summary", (LeadService s, int id) => s.RegenerateBriefAsync(id));
            p.MapPost("/leads/{id:int}/notes", async (LeadService s, int id, NoteIn body) =>
                Results.Created($"/api/leads/{id}", await s.AddNoteAsync(id, body.Text)));

            p.MapGet("/stats/overview", (LeadService s) => s.OverviewAsync());
            p.MapGet("/stats/analytics", (LeadService s) => s.AnalyticsAsync());

            p.MapGet("/scoring-rules", (LeadService s) => s.RulesAsync());
            p.MapPut("/scoring-rules/{key}", (LeadService s, string key, ScoringRuleUpdate body) => s.UpdateRuleAsync(key, body));
            p.MapGet("/admin/runs", (LeadService s) => s.RunsAsync());
            p.MapPost("/admin/collect", async (CollectionRunner runner, Jobs jobs, CollectIn? body) =>
            {
                body ??= new();
                if (body.Limit is < 1 or > 5000) throw new BadRequestException("limit: 1-5000");
                if (await runner.BusyAsync()) return Results.Problem("A collection run is already in progress", statusCode: 409);
                jobs.Start("collect", sp => sp.GetRequiredService<CollectionRunner>().RunAsync(body.Since, body.Limit));
                return Results.Accepted(value: new { accepted = true });
            });
            p.MapPost("/admin/reprocess", async (CollectionRunner runner, Jobs jobs) =>
            {
                if (await runner.BusyAsync()) return Results.Problem("A collection run is already in progress", statusCode: 409);
                jobs.Start("reprocess", sp => sp.GetRequiredService<LeadPipeline>().ProcessCompaniesAsync());
                return Results.Accepted(value: new { accepted = true });
            });
        }

        public record CollectIn(DateOnly? Since = null, int Limit = 500);

        public static ClaimsPrincipal Operator() =>
            new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
