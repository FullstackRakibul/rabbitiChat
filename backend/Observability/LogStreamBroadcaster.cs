// Observability — SSE live-stream broadcaster.
//
// LogStreamBroadcaster is a singleton that keeps a list of active SSE clients.
// LogWriterBackgroundService calls Broadcast() after each bulk DB write.
// Each client subscribes with optional filters (category, min level).

using System.Collections.Concurrent;
using System.Text.Json;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Observability;

public sealed class LogStreamBroadcaster
{
    private readonly ConcurrentDictionary<string, SseClient> _clients = new();

    public void AddClient(string id, SseClient client) => _clients[id] = client;
    public void RemoveClient(string id) => _clients.TryRemove(id, out _);
    public int ConnectedClients => _clients.Count;

    public async Task BroadcastAsync(IReadOnlyList<ActivityLog> entries)
    {
        if (_clients.IsEmpty || entries.Count == 0) return;

        foreach (var (id, client) in _clients)
        {
            foreach (var entry in entries)
            {
                if (!client.Matches(entry)) continue;
                try
                {
                    await client.SendAsync(entry);
                }
                catch
                {
                    // Client disconnected mid-stream — remove it.
                    _clients.TryRemove(id, out _);
                    break;
                }
            }
        }
    }
}

public sealed class SseClient
{
    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Filters
    public string? CategoryFilter  { get; init; }
    public string? MinLevelFilter  { get; init; }

    private static readonly string[] LevelOrder =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    public SseClient(HttpResponse response) => _response = response;

    public bool Matches(ActivityLog entry)
    {
        if (CategoryFilter is not null &&
            !string.Equals(entry.Category, CategoryFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (MinLevelFilter is not null)
        {
            var minIdx = Array.FindIndex(LevelOrder, l =>
                string.Equals(l, MinLevelFilter, StringComparison.OrdinalIgnoreCase));
            var entryIdx = Array.FindIndex(LevelOrder, l =>
                string.Equals(l, entry.Level, StringComparison.OrdinalIgnoreCase));
            if (entryIdx < minIdx) return false;
        }

        return true;
    }

    public async Task SendAsync(ActivityLog entry)
    {
        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(entry, SseJsonOptions.Default);
            await _response.WriteAsync($"data: {json}\n\n");
            await _response.Body.FlushAsync();
        }
        finally
        {
            _lock.Release();
        }
    }
}

internal static class SseJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
