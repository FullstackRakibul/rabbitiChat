// Usage rows and provider status changes, written in the background: a model call never
// waits on SQLite to say what it cost. A full queue drops the oldest entry, not the call.

using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;

namespace RabbitIC.Api.ModelProviders;

public sealed class ModelUsageRecorder(IServiceScopeFactory scopes, ILogger<ModelUsageRecorder> log) : BackgroundService
{
    private abstract record Item;
    private sealed record UsageItem(ModelProviderUsage Row) : Item;
    private sealed record StatusItem(string ProviderKey, string Status, string? Message) : Item;

    private readonly Channel<Item> _queue = Channel.CreateBounded<Item>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Record(ModelProviderUsage row) => _queue.Writer.TryWrite(new UsageItem(row));

    /// <summary>A real call's outcome changed what Settings should show (key rejected, credit used up, ok again).</summary>
    public void RecordStatus(string providerKey, string status, string? message) =>
        _queue.Writer.TryWrite(new StatusItem(providerKey, status, message));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                await DrainAsync(stoppingToken);
        }
        catch (OperationCanceledException) { }
        // What is still queued at shutdown is written once more, without the stopping token.
        await DrainAsync(CancellationToken.None);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        var batch = new List<Item>();
        while (batch.Count < 200 && _queue.Reader.TryRead(out var item)) batch.Add(item);
        if (batch.Count == 0) return;

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ModelProviderUsages.AddRange(batch.OfType<UsageItem>().Select(u => u.Row));
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);

            foreach (var s in batch.OfType<StatusItem>())
            {
                var now = DateTime.UtcNow;
                await db.ModelProviders.Where(p => p.Key == s.ProviderKey)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(p => p.LastTestStatus, s.Status)
                        .SetProperty(p => p.LastTestMessage, s.Message)
                        .SetProperty(p => p.LastTestAt, now), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Model provider usage: {Count} entries not written: {Message}", batch.Count, ex.Message);
        }
    }
}
