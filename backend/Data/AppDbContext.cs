using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<EmbeddingChunk> EmbeddingChunks => Set<EmbeddingChunk>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        b.Entity<User>()
            .HasIndex(u => u.Token);

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
    }
}
