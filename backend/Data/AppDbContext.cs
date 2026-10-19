using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Models;
using RabbitIC.Api.Models.TodoRabbit;
using RabbitIC.Api.Observability;

namespace RabbitIC.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<EmbeddingChunk> EmbeddingChunks => Set<EmbeddingChunk>();
    public DbSet<BuyerProfile> BuyerProfiles => Set<BuyerProfile>();

    /// <summary>RBAC — explicit per-tab grants that add to a user's role baseline.</summary>
    public DbSet<UserTabPermission> UserTabPermissions => Set<UserTabPermission>();

    public DbSet<ExtractionRun> ExtractionRuns => Set<ExtractionRun>();

    /// <summary>Which models each role may run — see Rbac/ModelPolicy.cs.</summary>
    public DbSet<RoleModelPolicy> RoleModelPolicies => Set<RoleModelPolicy>();

    /// <summary>Connected sources and buyer profiles — one table, split by Kind.</summary>
    public DbSet<ContextSource> ContextSources => Set<ContextSource>();

    /// <summary>মানব সম্পদ · নিয়মাবলি: HR's rule pages and their append-only history.</summary>
    public DbSet<HrRulePage> HrRulePages => Set<HrRulePage>();
    public DbSet<HrRulePageVersion> HrRulePageVersions => Set<HrRulePageVersion>();

    /// <summary>মানব সম্পদ · প্রসিডিউর: uploaded procedures, their append-only versions, the lookup registry and review questions.</summary>
    public DbSet<HrProcedure> HrProcedures => Set<HrProcedure>();
    public DbSet<HrProcedureVersion> HrProcedureVersions => Set<HrProcedureVersion>();
    public DbSet<HrLookup> HrLookups => Set<HrLookup>();
    public DbSet<HrLookupLog> HrLookupLog => Set<HrLookupLog>();
    public DbSet<HrReviewQuestion> HrReviewQuestions => Set<HrReviewQuestion>();
    public DbSet<HrQuestionOccurrence> HrQuestionOccurrences => Set<HrQuestionOccurrence>();

    /// <summary>Queries the user has run. Never holds result payloads.</summary>
    public DbSet<SearchHistory> SearchHistories => Set<SearchHistory>();

    // IELTS — the mistake spine plus the Reading engine.
    public DbSet<Mistake> Mistakes => Set<Mistake>();
    public DbSet<ReadingTest> ReadingTests => Set<ReadingTest>();
    public DbSet<ReadingQuestion> ReadingQuestions => Set<ReadingQuestion>();
    public DbSet<ReadingAttempt> ReadingAttempts => Set<ReadingAttempt>();
    public DbSet<ReadingAttemptAnswer> ReadingAttemptAnswers => Set<ReadingAttemptAnswer>();

    // todorabbit — Kanban boards (a creation of Mostisko, the AI).
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardColumn> Columns => Set<BoardColumn>();
    public DbSet<Card> Cards => Set<Card>();

    public DbSet<Note> Notes => Set<Note>();

    // ---- Observability ----
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<AuditLog>    AuditLogs    => Set<AuditLog>();

    // ---- Oddity gateway ----
    public DbSet<OddityKey>      OddityKeys     => Set<OddityKey>();
    public DbSet<OdditySettings> OdditySettings => Set<OdditySettings>();
    public DbSet<OddityRequest>  OddityRequests => Set<OddityRequest>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // Add the audit interceptor when it is available in DI.
        // The interceptor is resolved lazily so it does not circularly depend on the context.
        base.OnConfiguring(options);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        b.Entity<User>()
            .HasIndex(u => u.Token);

        // RBAC — one row per granted tab. The unique index is the guarantee that a
        // grant is a set membership, not a count: granting the same tab twice is
        // impossible rather than merely harmless.
        b.Entity<UserTabPermission>()
            .HasOne(p => p.User)
            .WithMany(u => u.TabPermissions)
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<UserTabPermission>()
            .HasIndex(p => new { p.UserId, p.TabKey })
            .IsUnique();

        b.Entity<Conversation>()
            .HasOne(c => c.User)
            .WithMany(u => u.Conversations)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Message>()
            .HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Document>()
            .HasIndex(d => d.OwnerUserId);

        b.Entity<EmbeddingChunk>()
            .HasOne(c => c.Document)
            .WithMany(d => d.Chunks)
            .HasForeignKey(c => c.DocumentId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        // An HR library chunk belongs to its library item instead of a document;
        // deleting the item takes its chunks with it (docs/HR_MODULE_PROMPT.md §3.2).
        b.Entity<EmbeddingChunk>()
            .HasOne(c => c.ContextSource)
            .WithMany()
            .HasForeignKey(c => c.ContextSourceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<EmbeddingChunk>().HasIndex(c => c.ContextSourceId);
        b.Entity<EmbeddingChunk>().HasIndex(c => c.Kind);

        // Filterable metadata — keep these indexed (Search gates on them before similarity).
        b.Entity<EmbeddingChunk>().HasIndex(c => c.DocumentId);
        b.Entity<EmbeddingChunk>().HasIndex(c => c.OwnerUserId);
        b.Entity<EmbeddingChunk>().HasIndex(c => c.Buyer);
        b.Entity<EmbeddingChunk>().HasIndex(c => c.StyleNumber);

        // todorabbit — a board is a cascade tree owned by one user.
        b.Entity<Board>().HasIndex(x => x.OwnerUserId);

        b.Entity<BoardColumn>()
            .HasOne(c => c.Board)
            .WithMany(x => x.Columns)
            .HasForeignKey(c => c.BoardId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<BoardColumn>().HasIndex(c => c.BoardId);

        b.Entity<Card>()
            .HasOne(c => c.Column)
            .WithMany(x => x.Cards)
            .HasForeignKey(c => c.ColumnId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<Card>().HasIndex(c => c.ColumnId);

        // Every context-source query filters on owner first, then kind.
        b.Entity<ContextSource>().HasIndex(s => new { s.OwnerUserId, s.Kind });

        // The Rulebook (docs/HR_RULEBOOK_PROMPT.md §3.1). A title is unique among
        // pages that are not retired, ignoring case. Versions are keyed by page and
        // number, and a page can never take its versions with it (Restrict); the
        // append-only triggers are made in SchemaUpgrade, for every database.
        b.Entity<HrRulePage>().Property(p => p.Title).UseCollation("NOCASE");
        b.Entity<HrRulePage>().HasIndex(p => p.Title).IsUnique().HasFilter("\"Status\" <> 'retired'");
        b.Entity<HrRulePage>().HasIndex(p => p.LibraryItemId);
        b.Entity<HrRulePageVersion>().HasKey(v => new { v.PageId, v.Version });

        // The Procedure Analyzer (docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.1, §3.3).
        // Versions and the registry log are append-only (triggers in SchemaUpgrade);
        // the registry is one row per (Column, Value), compared ignoring case.
        b.Entity<HrProcedure>().Property(p => p.ObjectName).UseCollation("NOCASE");
        b.Entity<HrProcedure>().HasIndex(p => p.ObjectName).IsUnique();
        b.Entity<HrProcedureVersion>().HasKey(v => new { v.ProcedureId, v.Version });
        b.Entity<HrProcedureVersion>().HasOne<HrProcedure>().WithMany().HasForeignKey(v => v.ProcedureId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<HrLookup>().HasKey(l => new { l.Column, l.Value });
        b.Entity<HrLookup>().Property(l => l.Column).UseCollation("NOCASE");
        b.Entity<HrLookup>().Property(l => l.Value).UseCollation("NOCASE");
        b.Entity<HrReviewQuestion>().HasIndex(q => q.Key).IsUnique();
        b.Entity<HrQuestionOccurrence>().HasIndex(o => new { o.QuestionId, o.ProcedureId, o.Version, o.Line }).IsUnique();
        b.Entity<HrRulePageVersion>()
            .HasOne<HrRulePage>()
            .WithMany()
            .HasForeignKey(v => v.PageId)
            .OnDelete(DeleteBehavior.Restrict);

        // The history list is always "my active rows, newest run first" — this
        // index covers the filter and the sort in one.
        b.Entity<SearchHistory>()
            .HasIndex(h => new { h.OwnerUserId, h.IsDeleted, h.LastRunAt });

        // IELTS — the review queue is always "my mistakes, this module, due
        // first", so one composite index covers the filter and the ordering.
        b.Entity<Mistake>().HasIndex(m => new { m.OwnerUserId, m.Module, m.NextReviewAt });

        b.Entity<ReadingTest>().HasIndex(t => t.OwnerUserId);

        // Unique only where a SeedKey exists: user-authored tests all leave it
        // null, and SQLite treats each NULL as distinct, so they never collide.
        // This index is what makes re-seeding an update rather than a duplicate.
        b.Entity<ReadingTest>()
            .HasIndex(t => t.SeedKey)
            .IsUnique()
            .HasFilter("\"SeedKey\" IS NOT NULL");

        // A test is a cascade tree: deleting it takes its questions with it.
        b.Entity<ReadingQuestion>()
            .HasOne(q => q.Test)
            .WithMany(t => t.Questions)
            .HasForeignKey(q => q.ReadingTestId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ReadingQuestion>().HasIndex(q => q.ReadingTestId);

        b.Entity<ReadingAttempt>().HasIndex(a => new { a.OwnerUserId, a.StartedAt });

        b.Entity<ReadingAttemptAnswer>()
            .HasOne(a => a.Attempt)
            .WithMany(x => x.Answers)
            .HasForeignKey(a => a.ReadingAttemptId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ReadingAttemptAnswer>().HasIndex(a => a.ReadingAttemptId);

        // One row per role, keyed by the role name itself: there is nothing else
        // to identify it by, and a surrogate key would allow two rows for one role.
        b.Entity<RoleModelPolicy>().HasKey(p => p.Role);

        b.Entity<BuyerProfile>().HasIndex(p => p.Name).IsUnique();
        b.Entity<ExtractionRun>().HasIndex(r => r.DocumentId);

        b.Entity<Note>()
            .HasOne(n => n.Owner)
            .WithMany()
            .HasForeignKey(n => n.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);
            
        b.Entity<Note>().HasIndex(n => new { n.OwnerUserId, n.IsDeleted, n.IsPinned, n.UpdatedAt });
        
        b.Entity<Note>()
            .HasIndex(n => n.ShareToken)
            .IsUnique()
            .HasFilter("\"ShareToken\" IS NOT NULL");

        // ---- Observability indexes ----
        b.Entity<ActivityLog>().HasIndex(a => a.TimestampUtc);
        b.Entity<ActivityLog>().HasIndex(a => new { a.Category, a.TimestampUtc });
        b.Entity<ActivityLog>().HasIndex(a => a.CorrelationId);
        b.Entity<ActivityLog>().HasIndex(a => new { a.UserId, a.TimestampUtc });
        b.Entity<ActivityLog>().HasIndex(a => new { a.StatusCode, a.TimestampUtc });

        b.Entity<AuditLog>().HasIndex(a => a.TimestampUtc);
        b.Entity<AuditLog>().HasIndex(a => new { a.EntityName, a.EntityKey });
        b.Entity<AuditLog>().HasIndex(a => a.CorrelationId);
        b.Entity<AuditLog>().HasIndex(a => a.UserId);

        // ---- Oddity ----
        // No relationships on purpose: a request row must outlive its key.
        b.Entity<OddityKey>().HasIndex(k => k.Prefix).IsUnique();
        b.Entity<OddityKey>().HasIndex(k => k.OwnerUserId);
        b.Entity<OddityRequest>().HasIndex(r => new { r.OwnerUserId, r.TimestampUtc });
        b.Entity<OddityRequest>().HasIndex(r => new { r.KeyId, r.TimestampUtc });

        // SQLite keeps no DateTimeKind; mark Oddity's *Utc columns as UTC on the way
        // out so the JSON carries a "Z" and the boards never read them as local time.
        var utc = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcOrNull = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
            v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        b.Entity<OddityKey>().Property(k => k.CreatedAtUtc).HasConversion(utc);
        b.Entity<OddityKey>().Property(k => k.ExpiresAtUtc).HasConversion(utcOrNull);
        b.Entity<OdditySettings>().Property(s => s.UpdatedAtUtc).HasConversion(utc);
        b.Entity<OddityRequest>().Property(r => r.TimestampUtc).HasConversion(utc);
    }
}
