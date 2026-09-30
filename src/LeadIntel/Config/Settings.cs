using System.Text.Json;

namespace LeadIntel.Config;

/// <summary>
/// Every setting, read from environment variables (names match the Python version's .env, e.g. API_SECRET,
/// TARGET_MIN_FLEET). A .env file next to the app or in ../backend is loaded first; real env vars win.
/// </summary>
public sealed record Settings
{
    public string DatabaseUrl { get; init; } = "Host=localhost;Database=leadintel";
    public string ApiSecret { get; init; } = "";
    public string FrontendOrigin { get; init; } = "http://localhost:5173";

    public string? SocrataAppToken { get; init; }
    public List<string> CollectStates { get; init; } = [];  // empty = nationwide
    public int CollectOverlapDays { get; init; } = 3;
    public int CollectInitialLookbackDays { get; init; } = 14;
    public int CollectEveryHours { get; init; }  // > 0: the web process also runs collection + daily report

    // Target profile: who we collect history for and turn into leads
    public int TargetMinFleet { get; init; } = 5;
    public int TargetMaxFleet { get; init; } = 500;
    public int TargetNewCarrierMinFleet { get; init; } = 1;  // carriers registered in the last 180 days may be smaller
    public bool TargetActiveOnly { get; init; } = true;

    public int LeadMinScore { get; init; } = 1;

    // Enrichment: website + contacts for leads at/above this score, re-checked every TTL days
    public int EnrichMinScore { get; init; } = 30;
    public int EnrichTtlDays { get; init; } = 30;
    public int EnrichLimit { get; init; } = 100;
    public string? BraveApiKey { get; init; }

    // AI briefs (Claude). Off unless a key is set.
    public string? AnthropicApiKey { get; init; }
    public int AiMinScore { get; init; } = 60;
    public int AiLimit { get; init; } = 25;

    public string? TelegramBotToken { get; init; }
    public string? TelegramChatId { get; init; }
    public int NotifyMinScore { get; init; } = 70;
    public int NotifyLimit { get; init; } = 20;

    public bool TelegramConfigured => !string.IsNullOrEmpty(TelegramBotToken) && !string.IsNullOrEmpty(TelegramChatId);

    public static Settings FromEnvironment()
    {
        DotEnv.Load();
        string? S(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;
        int I(string key, int fallback) => int.TryParse(S(key), out var v) ? v : fallback;
        bool B(string key, bool fallback) => S(key)?.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
        var d = new Settings();
        var secret = S("API_SECRET") ?? "";
        if (secret.Length < 16)
            throw new InvalidOperationException("API_SECRET must be set to at least 16 characters (src/LeadIntel/.env)");
        return d with
        {
            DatabaseUrl = ToNpgsql(S("DATABASE_URL")) ?? d.DatabaseUrl,
            ApiSecret = secret,
            FrontendOrigin = S("FRONTEND_ORIGIN") ?? d.FrontendOrigin,
            SocrataAppToken = S("SOCRATA_APP_TOKEN"),
            CollectStates = ParseList(S("COLLECT_STATES")),
            CollectOverlapDays = I("COLLECT_OVERLAP_DAYS", d.CollectOverlapDays),
            CollectInitialLookbackDays = I("COLLECT_INITIAL_LOOKBACK_DAYS", d.CollectInitialLookbackDays),
            CollectEveryHours = I("COLLECT_EVERY_HOURS", d.CollectEveryHours),
            TargetMinFleet = I("TARGET_MIN_FLEET", d.TargetMinFleet),
            TargetMaxFleet = I("TARGET_MAX_FLEET", d.TargetMaxFleet),
            TargetNewCarrierMinFleet = I("TARGET_NEW_CARRIER_MIN_FLEET", d.TargetNewCarrierMinFleet),
            TargetActiveOnly = B("TARGET_ACTIVE_ONLY", d.TargetActiveOnly),
            LeadMinScore = I("LEAD_MIN_SCORE", d.LeadMinScore),
            EnrichMinScore = I("ENRICH_MIN_SCORE", d.EnrichMinScore),
            EnrichTtlDays = I("ENRICH_TTL_DAYS", d.EnrichTtlDays),
            EnrichLimit = I("ENRICH_LIMIT", d.EnrichLimit),
            BraveApiKey = S("BRAVE_API_KEY"),
            AnthropicApiKey = S("ANTHROPIC_API_KEY"),
            AiMinScore = I("AI_MIN_SCORE", d.AiMinScore),
            AiLimit = I("AI_LIMIT", d.AiLimit),
            TelegramBotToken = S("TELEGRAM_BOT_TOKEN"),
            TelegramChatId = S("TELEGRAM_CHAT_ID"),
            NotifyMinScore = I("NOTIFY_MIN_SCORE", d.NotifyMinScore),
            NotifyLimit = I("NOTIFY_LIMIT", d.NotifyLimit),
        };
    }

    /// <summary>COLLECT_STATES as JSON (["TX","OK"], the Python format) or comma-separated (TX,OK).</summary>
    static List<string> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        if (raw.TrimStart().StartsWith('['))
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Accepts the Python-style URL (postgresql+psycopg://user:pw@host:port/db) or an Npgsql string.</summary>
    public static string? ToNpgsql(string? url)
    {
        if (url is null || !url.Contains("://")) return url;
        var uri = new Uri(url.Replace("postgresql+psycopg://", "postgresql://"));
        var parts = new List<string> { $"Host={(uri.Host is "" ? "localhost" : uri.Host)}", $"Database={uri.AbsolutePath.TrimStart('/')}" };
        if (uri.Port > 0) parts.Add($"Port={uri.Port}");
        if (uri.UserInfo is { Length: > 0 } info)
        {
            var (user, _, pw) = info.Partition(':');
            parts.Add($"Username={Uri.UnescapeDataString(user)}");
            if (pw.Length > 0) parts.Add($"Password={Uri.UnescapeDataString(pw)}");
        }
        return string.Join(';', parts);
    }
}

static class StringPartition
{
    public static (string, string, string) Partition(this string s, char sep)
    {
        var i = s.IndexOf(sep);
        return i < 0 ? (s, "", "") : (s[..i], sep.ToString(), s[(i + 1)..]);
    }
}

/// <summary>Minimal .env loader: KEY=value lines, # comments, optional quotes. Never overrides real env vars.</summary>
public static class DotEnv
{
    public static void Load()
    {
        var candidates = new[] { ".env", Path.Combine("src", "LeadIntel", ".env") };  // project folder or repo root
        var path = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (path is null) return;
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || !t.Contains('=')) continue;
            var (key, _, value) = t.Partition('=');
            key = key.Trim();
            value = value.Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
