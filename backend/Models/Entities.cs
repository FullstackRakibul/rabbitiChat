namespace RabbitIC.Api.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";        // unique
    public string SecretHash { get; set; } = "";      // PBKDF2 hash (base64)
    public string SecretSalt { get; set; } = "";      // per-user salt (base64)
    public string Token { get; set; } = "";           // current bearer token (base64url)

    /// <summary>
    /// "General" | "System" | "SystemAdmin" — see <see cref="UserRoles"/>. Decides
    /// the baseline tab set; UserTabPermission rows add to it. Rows written before
    /// RBAC existed read as General, so an upgrade grants nobody new access.
    /// </summary>
    public string Role { get; set; } = UserRoles.General;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Conversation> Conversations { get; set; } = new();
    public List<UserTabPermission> TabPermissions { get; set; } = new();
}

public class Conversation
{
    public int Id { get; set; }
    public int UserId { get; set; }                   // owner — every query is scoped to this
    public string Title { get; set; } = "New chat";

    /// <summary>
    /// Comma-separated ContextSource ids that were active for this conversation,
    /// so reopening it restores the same context set. A list rather than a join
    /// table: it is read and written whole, never queried across.
    /// Empty string = no sources; null = never set (pre-feature conversations).
    /// </summary>
    public string? ActiveSourceIds { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
    public List<Message> Messages { get; set; } = new();
}

public class Document
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }              // owner — every query is scoped to this
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string? Buyer { get; set; }                // e.g. "Kohls" — set at upload time
    public string Status { get; set; } = "processing"; // "processing" | "ready" | "failed"
    public string? Error { get; set; }                // populated when Status == "failed"

    /// <summary>
    /// Where the original upload is stored on disk (data/uploads/{id}{ext}).
    /// Null for documents uploaded before files were persisted — those can be
    /// chatted with (chunks exist) but not re-parsed or viewed as a source PDF.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>Last successful extraction, stored verbatim so the UI can re-display without re-running the model.</summary>
    public string? ExtractedJson { get; set; }
    public DateTime? ExtractedAt { get; set; }
    /// <summary>Workspace thread this document belongs to — extractions are appended here.</summary>
    public int? ConversationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<EmbeddingChunk> Chunks { get; set; } = new();
}

/// <summary>One embedded text chunk of a Document. Vector is a raw float32 LE blob.</summary>
public class EmbeddingChunk
{
    public int Id { get; set; }

    public int DocumentId { get; set; }
    /// <summary>Owner. Null = shared team knowledge base.</summary>
    public int? OwnerUserId { get; set; }

    // Filterable metadata — indexed in AppDbContext.
    public string? Buyer { get; set; }
    public string? StyleNumber { get; set; }
    public string? SourceRef { get; set; }            // e.g. "15901248.pdf p2" — used for citations

    public string Text { get; set; } = "";

    /// <summary>L2-normalised float32 vector, raw little-endian bytes.</summary>
    public byte[] Vector { get; set; } = Array.Empty<byte>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Document? Document { get; set; }
}

public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string Role { get; set; } = "user";      // "user" | "assistant" | "system"
    public string Content { get; set; } = "";
    /// <summary>True when this reply was grounded on live web results, so history can show the badge.</summary>
    public bool WebSearchUsed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Conversation? Conversation { get; set; }
}
