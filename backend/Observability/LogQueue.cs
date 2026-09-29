// Observability — bounded async log queue.
//
// The queue is the ONLY thing written to on the hot path (request / EF interceptor /
// ILogger provider). DB work happens exclusively in LogWriterBackgroundService on a
// background thread. A full queue drops the oldest entry (DropOldest), counts drops,
// and emits one "system" warning per minute.

using System.Threading.Channels;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Observability;

public interface ILogQueue
{
    /// <summary>
    /// Enqueues an entry. Never throws, never blocks. If the queue is full,
    /// the oldest entry is silently dropped and the drop counter increments.
    /// </summary>
    void Enqueue(ActivityLog entry);

    /// <summary>
    /// Enqueues an audit entry. Never throws, never blocks.
    /// </summary>
    void EnqueueAudit(AuditLog entry);

    /// <summary>
    /// Tries to drain up to <paramref name="maxCount"/> activity entries
    /// without waiting. Returns how many were actually dequeued.
    /// </summary>
    int TryDrainActivity(List<ActivityLog> target, int maxCount);

    /// <summary>
    /// Tries to drain up to <paramref name="maxCount"/> audit entries.
    /// </summary>
    int TryDrainAudit(List<AuditLog> target, int maxCount);

    long DroppedCount { get; }
    void ResetDroppedCount();
    DateTime? LastWriteTime { get; set; }
    int ActivityQueueDepth { get; }
}

public sealed class BoundedLogQueue : ILogQueue
{
    private const int Capacity = 10_000;

    private readonly Channel<ActivityLog> _activity =
        Channel.CreateBounded<ActivityLog>(new BoundedChannelOptions(Capacity)
        {
            FullMode       = BoundedChannelFullMode.DropOldest,
            SingleReader   = true,
            SingleWriter   = false,
        });

    private readonly Channel<AuditLog> _audit =
        Channel.CreateBounded<AuditLog>(new BoundedChannelOptions(Capacity / 10)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private long _dropped;
    public long DroppedCount => Interlocked.Read(ref _dropped);
    public void ResetDroppedCount() => Interlocked.Exchange(ref _dropped, 0);
    public DateTime? LastWriteTime { get; set; }

    public int ActivityQueueDepth => _activity.Reader.Count;

    public void Enqueue(ActivityLog entry)
    {
        // TryWrite returns false ONLY when the bounded channel is full and full-mode
        // is something other than DropOldest. With DropOldest, it always succeeds
        // (an old item is dropped instead). Track the drop ourselves.
        if (!_activity.Writer.TryWrite(entry))
            Interlocked.Increment(ref _dropped);
    }

    public void EnqueueAudit(AuditLog entry)
    {
        _audit.Writer.TryWrite(entry); // best-effort
    }

    public int TryDrainActivity(List<ActivityLog> target, int maxCount)
    {
        var count = 0;
        while (count < maxCount && _activity.Reader.TryRead(out var item))
        {
            target.Add(item);
            count++;
        }
        return count;
    }

    public int TryDrainAudit(List<AuditLog> target, int maxCount)
    {
        var count = 0;
        while (count < maxCount && _audit.Reader.TryRead(out var item))
        {
            target.Add(item);
            count++;
        }
        return count;
    }
}
