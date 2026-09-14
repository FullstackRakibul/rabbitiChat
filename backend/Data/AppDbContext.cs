using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Models;
using RabbitIC.Api.Models.TodoRabbit;

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

    /// <summary>Connected sources and buyer profiles — one table, split by Kind.</summary>
    public DbSet<ContextSource> ContextSources => Set<ContextSource>();

    /// <summary>Queries the user has run. Never holds result payloads.</summary>
    public DbSet<SearchHistory> SearchHistories => Set<SearchHistory>();

    // todorabbit — Kanban boards (a creation of Mostisko, the AI).
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardColumn> Columns => Set<BoardColumn>();
    public DbSet<Card> Cards => Set<Card>();

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
            .OnDelete(DeleteBehavior.Cascade);

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

        // The history list is always "my active rows, newest run first" — this
        // index covers the filter and the sort in one.
        b.Entity<SearchHistory>()
            .HasIndex(h => new { h.OwnerUserId, h.IsDeleted, h.LastRunAt });

        b.Entity<BuyerProfile>().HasIndex(p => p.Name).IsUnique();
        b.Entity<ExtractionRun>().HasIndex(r => r.DocumentId);
    }
}
