// মানব সম্পদ — background indexing (docs/HR_MODULE_PROMPT.md §3.2, "Large libraries").
//
// Saving an item splits it and writes its keyword rows at once, so it can be
// found by keyword immediately; the embeddings are made here, one item at a
// time, so a save — or an upload of 200 files — never waits on Ollama. The item
// shows "indexing… n of m" meanwhile. When embedding fails the item is marked
// failed with the reason and waits for Retry; nothing retries in a loop on its own.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Hr;

public sealed class HrIndexer(IServiceScopeFactory scopes, ILogger<HrIndexer> log) : BackgroundService
{
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, byte> _queued = new();

    /// <summary>Queue an item for embedding. An item already waiting is not queued twice.</summary>
    public void Enqueue(int itemId)
    {
        if (_queued.TryAdd(itemId, 0)) _queue.Writer.TryWrite(itemId);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Whatever was left un-embedded by the last run (or by a seed) goes first.
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = await db.EmbeddingChunks.AsNoTracking()
                .Where(c => c.Kind == ContextSource.KindHr && c.ContextSourceId != null && c.Vector.Length == 0)
                .Select(c => c.ContextSourceId!.Value).Distinct().ToListAsync(stop);
            var failed = await db.ContextSources.AsNoTracking()
                .Where(s => s.Kind == ContextSource.KindHr && s.IndexError != null).Select(s => s.Id).ToListAsync(stop);
            foreach (var id in pending.Except(failed)) Enqueue(id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "HR indexer: could not list pending items at start-up.");
        }

        await foreach (var itemId in _queue.Reader.ReadAllAsync(stop))
        {
            _queued.TryRemove(itemId, out _);
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<HrLibrary>().EmbedItemAsync(itemId, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogWarning(e, "HR indexer: item {Item} failed outside embedding.", itemId);
            }
        }
    }
}
