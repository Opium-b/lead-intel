using LeadIntel.Config;
using LeadIntel.Pipeline;

namespace LeadIntel.Services;

/// <summary>Fire-and-forget work after a request (Telegram updates, reprocessing), each in its own DI scope.</summary>
public class Jobs(IServiceScopeFactory scopes, ILogger<Jobs> log)
{
    // ponytail: in-process, lost on restart; a durable queue if jobs ever must survive a crash
    public void Start(string name, Func<IServiceProvider, Task> work) => _ = Task.Run(async () =>
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            await work(scope.ServiceProvider);
        }
        catch (Exception e)
        {
            log.LogError(e, "background job failed: {Name}", name);
        }
    });
}

/// <summary>COLLECT_EVERY_HOURS > 0: this process also collects and sends the daily decisions report on a timer
/// (the one-server setup; otherwise run the CLI from cron/launchd).</summary>
public class Scheduler(IServiceScopeFactory scopes, Settings settings, ILogger<Scheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (settings.CollectEveryHours <= 0) return;
        await Task.Delay(TimeSpan.FromMinutes(1), stop);  // let the app finish starting
        using var timer = new PeriodicTimer(TimeSpan.FromHours(settings.CollectEveryHours));
        do
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            try
            {
                var runner = sp.GetRequiredService<CollectionRunner>();
                if (!await runner.BusyAsync()) await runner.RunAsync();
            }
            catch (Exception e) { log.LogError(e, "scheduled collection failed"); }
            try { await sp.GetRequiredService<DailyReport>().SendAsync(once: true); }
            catch (Exception e) { log.LogError(e, "scheduled daily report failed"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
