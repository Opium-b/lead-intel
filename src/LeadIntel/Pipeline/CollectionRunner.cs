using LeadIntel.Collectors;
using LeadIntel.Config;
using LeadIntel.Data;
using LeadIntel.Services;
using Microsoft.EntityFrameworkCore;

namespace LeadIntel.Pipeline;

/// <summary>One collection run: fetch -> ingest -> signals/scores -> enrichment -> AI briefs -> Telegram alerts,
/// recorded in collector_runs. Later steps are best-effort: collection already succeeded.</summary>
public class CollectionRunner(AppDbContext db, FmcsaCollector fmcsa, LeadPipeline pipeline, EnrichmentJob enrichment,
                              AiBriefs ai, LeadNotifications notifications, Settings settings, ILogger<CollectionRunner> log)
{
    public const string Source = FmcsaCollector.Name;

    /// <summary>A run still marked running and started within 2 hours (older ones are treated as crashed).</summary>
    public Task<bool> BusyAsync() =>
        db.CollectorRuns.AnyAsync(r => r.Status == "running" && r.StartedAt > DateTime.UtcNow.AddHours(-2));

    public async Task<CollectorRun> RunAsync(DateOnly? since = null, int limit = 500)
    {
        if (since is null)
        {
            var last = await db.CollectorRuns
                .Where(r => r.Source == Source && r.Status == "success" && r.Cursor != null)
                .OrderByDescending(r => r.Id).Select(r => r.Cursor).FirstOrDefaultAsync();
            since = last is { } c ? c.AddDays(-settings.CollectOverlapDays)
                : DateOnly.FromDateTime(DateTime.Today).AddDays(-settings.CollectInitialLookbackDays);
        }
        var from = since.Value;
        return await RunAsync(new() { ["since"] = from.ToString("yyyy-MM-dd"), ["limit"] = limit }, from,
                              async () => FmcsaCollector.Normalize(await fmcsa.FetchAsync(from, limit)));
    }

    /// <summary>Re-fetch full history for every company already tracked (new inspections, insurance, etc.).</summary>
    public async Task<CollectorRun> RefreshAsync()
    {
        var dots = await db.Companies.Where(c => c.DotNumber != null).Select(c => c.DotNumber!).ToListAsync();
        return await RunAsync(new() { ["refresh"] = dots.Count }, null,
                              async () => FmcsaCollector.Normalize(await fmcsa.FetchCompaniesAsync(dots, applyTarget: false)));
    }

    /// <summary>since: the discovery window start; null for refreshes, which don't move the cursor.</summary>
    async Task<CollectorRun> RunAsync(Dictionary<string, object?> stats, DateOnly? since, Func<Task<NormalizedBatch>> collect)
    {
        var run = new CollectorRun { Source = Source, Stats = stats };
        db.CollectorRuns.Add(run);
        await db.SaveChangesAsync();
        var runId = run.Id;
        log.LogInformation("collector run {Id}: {Stats}", runId, Json.Serialize(stats));
        try
        {
            var batch = await collect();
            var ids = await pipeline.IngestAsync(batch);
            var result = await pipeline.ProcessCompaniesAsync(ids);
            result["enrichment"] = await BestEffort("enrichment", runId, () => enrichment.EnrichLeadsAsync());
            // after enrichment (fresh facts), before notifications (alerts can carry the brief)
            result["ai"] = await BestEffort("AI briefs", runId, () => ai.SummarizeLeadsAsync());
            // after enrichment, so messages include the contacts just found
            result["notifications"] = await BestEffort("notifications", runId, () => notifications.NotifyQualifiedLeadsAsync());
            run = await db.CollectorRuns.FirstAsync(r => r.Id == runId);  // the pipeline clears tracked entities
            run.Status = "success";
            run.Cursor = since is null ? null : batch.Cursor ?? since;
            run.Stats = stats.Concat(batch.Stats).Concat(result).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        }
        catch (Exception e)
        {
            db.ChangeTracker.Clear();
            run = await db.CollectorRuns.FirstAsync(r => r.Id == runId);
            run.Status = "failed";
            run.Error = DailyReport.Truncate($"{e.GetType().Name}: {e.Message}", 2000);
            log.LogError(e, "collector run {Id} failed", runId);
            throw;
        }
        finally
        {
            run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        log.LogInformation("collector run {Id} done: {Stats}", runId, Json.Serialize(run.Stats));
        return run;
    }

    async Task<object?> BestEffort<T>(string step, int runId, Func<Task<T>> fn)
    {
        try
        {
            return await fn();
        }
        catch (Exception e)
        {
            db.ChangeTracker.Clear();
            log.LogError(e, "collector run {Id}: {Step} failed", runId, step);
            return null;
        }
    }
}
