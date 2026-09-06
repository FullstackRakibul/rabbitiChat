namespace RabbitIC.Api.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";        // unique
    public string SecretHash { get; set; } = "";      // PBKDF2 hash (base64)
    public string SecretSalt { get; set; } = "";      // per-user salt (base64)
    public string Token { get; set; } = "";           // current bearer token (base64url)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Conversation> Conversations { get; set; } = new();
}

public class Conversation
{
    public int Id { get; set; }
    public int UserId { get; set; }                   // owner — every query is scoped to this
    public string Title { get; set; } = "New chat";
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
