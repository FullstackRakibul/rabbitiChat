// Jiji — the one worker (docs/JIJI_PLAN.md, Worker and files). Concurrency 1, polls every
// 5 seconds, runs Tts, then Render, then Upload. Every rule is one conditional UPDATE:
//
//   claim      Pending → Running WHERE Status='Pending' AND AvailableAtUtc <= now; sets LeasedBy
//              (this process), a new LeaseToken, LeaseUntilUtc = now + lease. Go on only if 1 row changed.
//   heartbeat  extend LeaseUntilUtc WHERE Id AND LeaseToken; 0 rows → the lease is lost and the run is cancelled.
//   complete   WHERE Id AND LeaseToken AND Running; 0 rows → reclaimed, the result is thrown away.
//              The content moves on and the next job is inserted in the same transaction; the unique
//              (ContentId, Type, ContentRevision) index makes that insert idempotent.
//   fail       transient: back to Pending after 1, 5, 30 minutes; then, or when permanent, Failed.
//   expired    Running with LeaseUntilUtc < now → Pending (Failed when its attempts are spent), at
//              startup and on every poll. Upload is left to its session check (day 6).
//
// Only job types with a handler are claimed: a Render job waits for the renderer (day 4).
// Raw SQL on purpose: these updates must be single statements, so they skip the EF audit
// interceptor; the job row itself is the record (attempts, times, last error).

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitIC.Api.Data;
using RabbitIC.Api.Jiji.Models;

namespace RabbitIC.Api.Jiji.Workers;

public sealed class JijiWorker(IServiceScopeFactory scopes, IOptions<JijiOptions> options, ILogger<JijiWorker> log) : BackgroundService
{
    /// <summary>1 run + 3 retries.</summary>
    public const int MaxAttempts = 4;
    private static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30) };

    /// <summary>Made at app start; who holds a lease.</summary>
    public static readonly string WorkerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>What a finished job moves its content from and to, and the job that follows.</summary>
    private static readonly Dictionary<string, (string From, string To, string? Next)> Transitions = new()
    {
        [JijiJobType.Tts] = (JijiStatus.ScriptApproved, JijiStatus.Voiced, JijiJobType.Render),
        [JijiJobType.Render] = (JijiStatus.Voiced, JijiStatus.Rendered, null),
    };

    private JijiOptions.WorkerOptions Opt => options.Value.Worker;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!Opt.Enabled) { log.LogInformation("Jiji worker: disabled (Jiji:Worker:Enabled)."); return; }
        string[] types;
        using (var scope = scopes.CreateScope())
            types = scope.ServiceProvider.GetServices<IJijiJobHandler>().Select(h => h.Type).ToArray();
        log.LogInformation("Jiji worker {Worker}: handles {Types}.", WorkerId, string.Join(", ", types));

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RecoverExpiredAsync(stop);
                while (!stop.IsCancellationRequested && await ClaimAsync(types, stop) is { } job)
                    await RunAsync(job, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "Jiji worker: poll failed."); }

            try { await Task.Delay(TimeSpan.FromSeconds(Opt.PollSeconds), stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ---- claim ----

    private async Task<JijiClaimedJob?> ClaimAsync(string[] types, CancellationToken ct)
    {
        if (types.Length == 0) return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var candidate = await db.JijiJobs.AsNoTracking()
            .Where(j => j.Status == JijiJobStatus.Pending && j.AvailableAtUtc <= now && types.Contains(j.Type))
            .OrderBy(j => j.AvailableAtUtc).ThenBy(j => j.Id)
            .Select(j => (int?)j.Id)
            .FirstOrDefaultAsync(ct);
        if (candidate is not { } id) return null;

        var token = Guid.NewGuid().ToString("N");
        var lease = now.AddSeconds(Opt.LeaseSeconds);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiJobs" SET "Status" = 'Running', "LeasedBy" = {WorkerId}, "LeaseToken" = {token},
                "LeaseUntilUtc" = {lease}, "StartedAtUtc" = {now}, "Attempts" = "Attempts" + 1
            WHERE "Id" = {id} AND "Status" = 'Pending' AND "AvailableAtUtc" <= {now}
            """, ct);
        if (changed != 1) return null;   // someone else took it

        var j = await db.JijiJobs.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        return new JijiClaimedJob(j.Id, j.ContentId, j.ContentRevision, j.Type, j.Attempts, token);
    }

    // ---- run, heartbeat ----

    private async Task RunAsync(JijiClaimedJob job, CancellationToken stop)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var leaseLost = false;
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Opt.HeartbeatSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(run.Token))
                {
                    if (await ExtendLeaseAsync(job, run.Token)) continue;
                    leaseLost = true;
                    log.LogWarning("Jiji worker: lost the lease on job {Job}; its result will be dropped.", job.Id);
                    await run.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { log.LogWarning(e, "Jiji worker: heartbeat for job {Job} failed.", job.Id); }
        });

        log.LogInformation("Jiji worker: job {Job} ({Type}) for item {Content} r{Revision}, attempt {Attempt}.",
            job.Id, job.Type, job.ContentId, job.ContentRevision, job.Attempts);
        Exception? failure = null;
        string? superseded = null;
        var finished = false;
        try
        {
            using var scope = scopes.CreateScope();
            var handler = scope.ServiceProvider.GetServices<IJijiJobHandler>().First(h => h.Type == job.Type);
            await handler.RunAsync(job, run.Token);
            finished = true;
        }
        catch (JijiSupersededException e) { superseded = e.Message; }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { /* lease lost or shutting down */ }
        catch (Exception e) { failure = e; }
        finally
        {
            await run.CancelAsync();
            await heartbeat;
        }

        // Writes below use a fresh token: a shutdown must still record what happened.
        if (leaseLost) return;
        if (superseded is not null) await CompleteAsync(job, superseded);
        else if (finished) await CompleteAsync(job, null);
        else if (failure is not null && !stop.IsCancellationRequested) await FailAsync(job, failure);
        else await ReleaseAsync(job);   // shutdown mid-run
    }

    private async Task<bool> ExtendLeaseAsync(JijiClaimedJob job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lease = DateTime.UtcNow.AddSeconds(Opt.LeaseSeconds);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiJobs" SET "LeaseUntilUtc" = {lease}
            WHERE "Id" = {job.Id} AND "LeaseToken" = {job.LeaseToken} AND "Status" = 'Running'
            """, ct) == 1;
    }

    // ---- complete, fail, release ----

    /// <param name="supersededReason">Set when the content moved on: the job ends Done with no effect.</param>
    private async Task CompleteAsync(JijiClaimedJob job, string? supersededReason)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync();

        var done = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiJobs" SET "Status" = 'Done', "CompletedAtUtc" = {now}, "LeaseUntilUtc" = NULL,
                "LastError" = {(supersededReason is null ? null : "Superseded: " + supersededReason)}
            WHERE "Id" = {job.Id} AND "LeaseToken" = {job.LeaseToken} AND "Status" = 'Running'
            """);
        if (done != 1)
        {
            log.LogWarning("Jiji worker: job {Job} was reclaimed before it finished; result dropped.", job.Id);
            return;   // rolled back on dispose
        }

        if (supersededReason is null && Transitions.TryGetValue(job.Type, out var t))
        {
            var moved = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "JijiContents" SET "Status" = {t.To}, "LastError" = NULL, "UpdatedAt" = {now}
                WHERE "Id" = {job.ContentId} AND "Revision" = {job.ContentRevision} AND "Status" = {t.From}
                """);
            if (moved == 1 && t.Next is { } next)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT OR IGNORE INTO "JijiJobs" ("ContentId", "ContentRevision", "Type", "Status", "Attempts", "AvailableAtUtc")
                    VALUES ({job.ContentId}, {job.ContentRevision}, {next}, 'Pending', 0, {now})
                    """);
        }
        await tx.CommitAsync();
        log.LogInformation("Jiji worker: job {Job} ({Type}) done{Note}.", job.Id, job.Type,
            supersededReason is null ? "" : " (superseded)");
    }

    private async Task FailAsync(JijiClaimedJob job, Exception e)
    {
        var transient = e is not JijiJobException j || j.Transient;   // unknown errors get the bounded retries
        var message = e is JijiJobException ? e.Message : $"{e.GetType().Name}: {e.Message}";
        if (message.Length > 1000) message = message[..1000];
        var now = DateTime.UtcNow;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (transient && job.Attempts < MaxAttempts)
        {
            var at = now + Backoff[Math.Clamp(job.Attempts - 1, 0, Backoff.Length - 1)];
            var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "JijiJobs" SET "Status" = 'Pending', "AvailableAtUtc" = {at}, "LeaseToken" = NULL, "LeasedBy" = NULL,
                    "LeaseUntilUtc" = NULL, "LastError" = {message}
                WHERE "Id" = {job.Id} AND "LeaseToken" = {job.LeaseToken} AND "Status" = 'Running'
                """);
            if (n == 1) log.LogWarning("Jiji worker: job {Job} failed (attempt {Attempt}), retry at {At:u}: {Error}", job.Id, job.Attempts, at, message);
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiJobs" SET "Status" = 'Failed', "CompletedAtUtc" = {now}, "LeaseUntilUtc" = NULL, "LastError" = {message}
            WHERE "Id" = {job.Id} AND "LeaseToken" = {job.LeaseToken} AND "Status" = 'Running'
            """);
        if (failed != 1) return;
        // A failure keeps the last good status; it only says what went wrong.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiContents" SET "LastError" = {job.Type + ": " + message}, "UpdatedAt" = {now}
            WHERE "Id" = {job.ContentId} AND "Revision" = {job.ContentRevision}
            """);
        await tx.CommitAsync();
        log.LogWarning("Jiji worker: job {Job} failed{Kind}: {Error}", job.Id, transient ? " after its retries" : "", message);
    }

    /// <summary>Shutdown mid-run: back to Pending at once, and the attempt does not count.</summary>
    private async Task ReleaseAsync(JijiClaimedJob job)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiJobs" SET "Status" = 'Pending', "Attempts" = MAX("Attempts" - 1, 0), "LeaseToken" = NULL,
                "LeasedBy" = NULL, "LeaseUntilUtc" = NULL, "AvailableAtUtc" = {DateTime.UtcNow}
            WHERE "Id" = {job.Id} AND "LeaseToken" = {job.LeaseToken} AND "Status" = 'Running'
            """);
        log.LogInformation("Jiji worker: released job {Job} on shutdown.", job.Id);
    }

    // ---- expired leases ----

    private async Task RecoverExpiredAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var n = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "JijiJobs" SET
                "Status" = CASE WHEN "Attempts" >= {MaxAttempts} THEN 'Failed' ELSE 'Pending' END,
                "CompletedAtUtc" = CASE WHEN "Attempts" >= {MaxAttempts} THEN {now} ELSE NULL END,
                "AvailableAtUtc" = {now}, "LeaseToken" = NULL, "LeasedBy" = NULL, "LeaseUntilUtc" = NULL,
                "LastError" = 'The worker stopped while running it (lease expired).'
            WHERE "Status" = 'Running' AND "LeaseUntilUtc" < {now} AND "Type" <> 'Upload'
            """, ct);
        if (n > 0) log.LogWarning("Jiji worker: {Count} job(s) had an expired lease and were requeued.", n);
    }
}
