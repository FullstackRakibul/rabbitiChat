namespace RabbitIC.Api.Models;

public class Conversation
{
    public int Id { get; set; }
    public string Title { get; set; } = "New chat";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

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
