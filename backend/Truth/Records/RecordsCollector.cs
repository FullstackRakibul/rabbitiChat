// The Records Department's one loop (docs/TRUTH_PROMPT_P5.md §3.1). Every 15 seconds it asks which
// sources are due — each has its own schedule (every N minutes, never under 5, ±20 %, backing off
// after errors) — and runs at most 4 fetches at a time. Off when Truth:Records:Enabled is false,
// which every eval suite but the truth eval sets, so no eval reaches the live network.

namespace RabbitIC.Api.Truth.Records;

public sealed class RecordsCollector(IServiceScopeFactory scopes, IConfiguration config, ILogger<RecordsCollector> log) : BackgroundService
{
    public const int Parallel = 4;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private readonly HashSet<string> running = new();
    private readonly SemaphoreSlim slots = new(Parallel);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!(config.GetValue<bool?>("Truth:Records:Enabled") ?? true)) return;
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await RunDueAsync(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Records: the collector's tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stop));
    }

    private async Task RunDueAsync(CancellationToken stop)
    {
        List<WireSource> due;
        using (var scope = scopes.CreateScope())
            due = await scope.ServiceProvider.GetRequiredService<RecordsService>().DueAsync(DateTime.UtcNow, stop);
        foreach (var source in due)
        {
            lock (running) if (!running.Add(source.Id)) continue;
            await slots.WaitAsync(stop);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var report = await scope.ServiceProvider.GetRequiredService<RecordsService>().RunAsync(source.Id, save: true, stop);
                    if (!report.Ok) log.LogInformation("Records: {Source} failed: {Error}", source.Id, report.Error);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Records: {Source} threw.", source.Id); }
                finally
                {
                    slots.Release();
                    lock (running) running.Remove(source.Id);
                }
            }, stop);
        }
    }
}
