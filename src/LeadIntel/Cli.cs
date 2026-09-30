using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Pipeline;
using LeadIntel.Scoring;
using LeadIntel.Services;
using Microsoft.EntityFrameworkCore;

namespace LeadIntel;

/// <summary>
/// dotnet run -- run [--since YYYY-MM-DD] [--limit 500]   collect + process (continues from the last cursor)
/// dotnet run -- refresh                                  re-fetch tracked companies
/// dotnet run -- enrich [--limit N]                       websites + contacts for due leads
/// dotnet run -- notify [--test]                          send pending Telegram alerts
/// dotnet run -- summarize [--limit N]                    AI briefs for leads whose facts changed
/// dotnet run -- report [--date YYYY-MM-DD] [--once]      FMCSA daily decisions Excel -> Telegram
/// dotnet run -- reprocess                                re-derive signals/scores after changing rules/weights
/// dotnet run -- reset-scoring                            restore default weights
/// </summary>
public static class Cli
{
    static readonly string[] Commands = ["run", "refresh", "enrich", "notify", "summarize", "report", "reprocess", "reset-scoring"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    static string? Opt(string[] args, string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    static int? IntOpt(string[] args, string name) => Opt(args, name) is { } v ? int.Parse(v) : null;
    static DateOnly? DateOpt(string[] args, string name) => Opt(args, name) is { } v ? DateOnly.ParseExact(v, "yyyy-MM-dd") : null;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("cli");
        var settings = sp.GetRequiredService<Settings>();
        var db = sp.GetRequiredService<AppDbContext>();
        await Schema.EnsureCreatedAsync(db);
        switch (args[0])
        {
            case "run":
                await sp.GetRequiredService<CollectionRunner>().RunAsync(DateOpt(args, "--since"), IntOpt(args, "--limit") ?? 500);
                break;
            case "refresh":
                await sp.GetRequiredService<CollectionRunner>().RefreshAsync();
                break;
            case "enrich":
                await sp.GetRequiredService<EnrichmentJob>().EnrichLeadsAsync(IntOpt(args, "--limit"));
                break;
            case "notify":
                if (!settings.TelegramConfigured) return Fail("Set TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID in .env first");
                if (args.Contains("--test"))
                {
                    await sp.GetRequiredService<TelegramNotifier>().SendAsync("✅ Lead Intel is connected.");
                    log.LogInformation("test message sent");
                }
                else await sp.GetRequiredService<LeadNotifications>().NotifyQualifiedLeadsAsync();
                break;
            case "summarize":
                if (await sp.GetRequiredService<AiBriefs>().SummarizeLeadsAsync(limit: IntOpt(args, "--limit")) is null)
                    return Fail("Set ANTHROPIC_API_KEY in .env first");
                break;
            case "report":
                await sp.GetRequiredService<DailyReport>().SendAsync(DateOpt(args, "--date"), args.Contains("--once"));
                break;
            case "reset-scoring":
                await ScoringEngine.ResetRulesAsync(db);
                log.LogInformation("scoring rules reset to defaults; run 'reprocess' to apply");
                break;
            case "reprocess":
                log.LogInformation("reprocess: {Result}", Json.Serialize(await sp.GetRequiredService<LeadPipeline>().ProcessCompaniesAsync()));
                break;
        }
        return 0;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
