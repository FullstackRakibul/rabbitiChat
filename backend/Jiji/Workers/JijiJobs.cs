// Jiji — what a job handler is to the worker. A handler does the work and throws
// JijiJobException to say how a failure should be treated; the worker alone writes
// job and content state, through conditional updates fenced by the lease token.

using RabbitIC.Api.Jiji.Models;

namespace RabbitIC.Api.Jiji.Workers;

/// <summary>A claimed job. <see cref="LeaseToken"/> is this claim's; it is what every later write matches on.</summary>
public sealed record JijiClaimedJob(int Id, int ContentId, int ContentRevision, string Type, int Attempts, string LeaseToken);

public interface IJijiJobHandler
{
    /// <summary>One of <see cref="JijiJobType"/>.</summary>
    string Type { get; }

    /// <summary>
    /// Runs the job. <paramref name="ct"/> is cancelled on shutdown and when the lease is
    /// lost. Returning means done; a superseded revision is reported by throwing
    /// <see cref="JijiSupersededException"/>.
    /// </summary>
    Task RunAsync(JijiClaimedJob job, CancellationToken ct);
}

/// <summary>
/// A failure the handler understands. Transient ones (a timeout, a 5xx, a 429) are retried
/// after 1, 5 and 30 minutes; permanent ones (bad configuration, a refused request, a
/// malformed answer) fail the job at once, for a person to fix and retry.
/// </summary>
public sealed class JijiJobException(string message, bool transient, Exception? inner = null) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}

/// <summary>The content moved on (rejected, re-approved) since this job was queued. The job ends without effect.</summary>
public sealed class JijiSupersededException(string message) : Exception(message);
