// Oddity — fixed-window rate limit per gateway key, the same shape as
// SearchRateLimiter. In memory and single-instance by design: this app runs as
// one process (SQLite is single-writer), so there is nothing to share it with.

using System.Collections.Concurrent;

namespace RabbitIC.Api.Oddity;

internal static class OddityRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<int, Counter> Buckets = new();

    private sealed class Counter
    {
        public DateTime WindowStart;
        public int Count;
    }

    /// <summary>Consumes one call for the key; false once this minute's budget is spent. 0 means no limit.</summary>
    public static bool TryTake(int keyId, int perMinute)
    {
        if (perMinute <= 0) return true;

        var now = DateTime.UtcNow;
        var bucket = Buckets.GetOrAdd(keyId, _ => new Counter { WindowStart = now });

        lock (bucket)
        {
            if (now - bucket.WindowStart >= Window)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }

            if (bucket.Count >= perMinute) return false;

            bucket.Count++;
            return true;
        }
    }
}
