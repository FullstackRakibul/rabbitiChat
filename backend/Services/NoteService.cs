using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Services;

public record NoteDto(
    int Id,
    string Title,
    string Content,
    bool IsPinned,
    string? FolderName,
    bool IsShared,
    string? ShareToken,
    DateTime UpdatedAt,
    DateTime CreatedAt,
    bool IsDeleted,
    DateTime? DeletedAt);

public record NoteSummaryDto(
    int Id,
    string Title,
    bool IsPinned,
    string? FolderName,
    bool IsShared,
    DateTime UpdatedAt,
    bool IsDeleted,
    DateTime? DeletedAt);

public record PublicNoteDto(
    string Title,
    string Content,
    DateTime UpdatedAt);

public interface INoteService
{
    Task<NoteDto> CreateAsync(int userId, string content, bool isPinned, string? folderName);
    Task<NoteDto?> GetAsync(int userId, int id);
    Task<List<NoteSummaryDto>> ListAsync(int userId, string? search, string? folderName, bool includeDeleted);
    Task<NoteDto?> UpdateAsync(int userId, int id, string content, bool isPinned, string? folderName, DateTime expectedUpdatedAt);
    Task<bool> SoftDeleteAsync(int userId, int id);
    Task<bool> RestoreAsync(int userId, int id);
    Task<bool> PermanentDeleteAsync(int userId, int id);
    Task<string?> ShareAsync(int userId, int id);
    Task<bool> UnshareAsync(int userId, int id);
    Task PurgeOldDeletedNotesAsync();
    Task<PublicNoteDto?> GetPublicNoteAsync(string token);
}

public class NoteService : INoteService
{
    private readonly AppDbContext _db;

    public NoteService(AppDbContext db)
    {
        _db = db;
    }
    
    private static string ExtractTitle(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "Untitled";
        var firstLine = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine)) return "Untitled";
        var title = firstLine.Trim();
        // Strip common markdown headings
        while (title.StartsWith("#")) title = title.Substring(1).Trim();
        if (title.Length > 120) title = title.Substring(0, 120);
        return title;
    }
    
    private static NoteDto ToDto(Note n) => new NoteDto(
        n.Id, n.Title, n.Content, n.IsPinned, n.FolderName, n.IsShared, n.ShareToken, n.UpdatedAt, n.CreatedAt, n.IsDeleted, n.DeletedAt);

    public async Task<NoteDto> CreateAsync(int userId, string content, bool isPinned, string? folderName)
    {
        var now = DateTime.UtcNow;
        var note = new Note
        {
            OwnerUserId = userId,
            Content = content,
            Title = ExtractTitle(content),
            IsPinned = isPinned,
            FolderName = folderName,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.Notes.Add(note);
        await _db.SaveChangesAsync();
        return ToDto(note);
    }

    public async Task<NoteDto?> GetAsync(int userId, int id)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.OwnerUserId == userId && n.Id == id);
        return note == null ? null : ToDto(note);
    }

    public async Task<List<NoteSummaryDto>> ListAsync(int userId, string? search, string? folderName, bool includeDeleted)
    {
        var q = _db.Notes.Where(n => n.OwnerUserId == userId && n.IsDeleted == includeDeleted);
        
        if (!string.IsNullOrWhiteSpace(folderName))
            q = q.Where(n => n.FolderName == folderName);
            
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            q = q.Where(n => n.Title.ToLower().Contains(s) || n.Content.ToLower().Contains(s));
        }

        var list = await q.OrderByDescending(n => n.IsPinned)
                          .ThenByDescending(n => n.UpdatedAt)
                          .Select(n => new NoteSummaryDto(n.Id, n.Title, n.IsPinned, n.FolderName, n.IsShared, n.UpdatedAt, n.IsDeleted, n.DeletedAt))
                          .ToListAsync();
        return list;
    }

    public async Task<NoteDto?> UpdateAsync(int userId, int id, string content, bool isPinned, string? folderName, DateTime expectedUpdatedAt)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.OwnerUserId == userId && n.Id == id);
        if (note == null || note.IsDeleted) return null;

        // Using precise equality for SQLite since datetime is stored as string in UTC ISO8601
        // We compare ticks just to be sure, or simply format them identically.
        // Actually, expectedUpdatedAt comes from the client, so we will do a difference check within 1 ms.
        if (Math.Abs((note.UpdatedAt - expectedUpdatedAt).TotalMilliseconds) > 1)
        {
            throw new InvalidOperationException("Conflict"); // We will catch this in the endpoint
        }

        note.Content = content;
        note.Title = ExtractTitle(content);
        note.IsPinned = isPinned;
        note.FolderName = folderName;
        note.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return ToDto(note);
    }

    public async Task<bool> SoftDeleteAsync(int userId, int id)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.OwnerUserId == userId && n.Id == id);
        if (note == null || note.IsDeleted) return false;

        note.IsDeleted = true;
        note.DeletedAt = DateTime.UtcNow;
        note.IsShared = false;
        note.ShareToken = null;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RestoreAsync(int userId, int id)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.OwnerUserId == userId && n.Id == id);
        if (note == null || !note.IsDeleted) return false;

        note.IsDeleted = false;
        note.DeletedAt = null;
        note.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> PermanentDeleteAsync(int userId, int id)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.OwnerUserId == userId && n.Id == id);
        if (note == null || !note.IsDeleted) return false;

        _db.Notes.Remove(note);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<string?> ShareAsync(int userId, int id)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.OwnerUserId == userId && n.Id == id);
        if (note == null || note.IsDeleted) return null;

        var tokenBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(tokenBytes);
        }
        
        var token = Convert.ToBase64String(tokenBytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        note.IsShared = true;
        note.ShareToken = token;
        note.SharedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return token;
    }

    public async Task<bool> UnshareAsync(int userId, int id)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.OwnerUserId == userId && n.Id == id);
        if (note == null || note.IsDeleted) return false;

        note.IsShared = false;
        note.ShareToken = null;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task PurgeOldDeletedNotesAsync()
    {
        var threshold = DateTime.UtcNow.AddDays(-30);
        var oldNotes = await _db.Notes.Where(n => n.IsDeleted && n.DeletedAt < threshold).ToListAsync();
        if (oldNotes.Any())
        {
            _db.Notes.RemoveRange(oldNotes);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<PublicNoteDto?> GetPublicNoteAsync(string token)
    {
        var note = await _db.Notes.FirstOrDefaultAsync(n => n.ShareToken == token && n.IsShared && !n.IsDeleted);
        if (note == null) return null;
        return new PublicNoteDto(note.Title, note.Content, note.UpdatedAt);
    }
}
