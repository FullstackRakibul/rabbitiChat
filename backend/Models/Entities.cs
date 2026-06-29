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

public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string Role { get; set; } = "user";      // "user" | "assistant" | "system"
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Conversation? Conversation { get; set; }
}
