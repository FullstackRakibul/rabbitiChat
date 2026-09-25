using System;
using System.Text.Json.Serialization;

namespace RabbitIC.Api.Models;

public class Note
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }
    
    // TEXT, Markdown — the FIRST LINE is the title
    public string Content { get; set; } = string.Empty;
    
    // derived from the first line on save, max 120 chars, for list display/search
    public string Title { get; set; } = string.Empty;
    
    public bool IsPinned { get; set; }
    public string? FolderName { get; set; }
    
    public bool IsShared { get; set; }
    public string? ShareToken { get; set; }
    public DateTime? SharedAt { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    [JsonIgnore]
    public User? Owner { get; set; }
}
