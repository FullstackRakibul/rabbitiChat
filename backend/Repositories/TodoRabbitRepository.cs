// todorabbit — a creation of Mostisko, the AI.
//
// EF Core / SQLite implementation. LINQ only — the sole raw SQL in this module
// is the CREATE TABLE DDL in SchemaUpgrade, which EF can't express.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models.TodoRabbit;

namespace RabbitIC.Api.Repositories;

public class TodoRabbitRepository : ITodoRabbitRepository
{
    private readonly AppDbContext _db;

    public TodoRabbitRepository(AppDbContext db) => _db = db;

    // ---- Boards ----

    public Task<List<Board>> ListBoardsAsync(int ownerUserId, CancellationToken ct = default) =>
        _db.Boards
            .Where(b => b.OwnerUserId == ownerUserId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

    public Task<Board?> GetBoardAsync(int boardId, int ownerUserId, CancellationToken ct = default) =>
        _db.Boards
            .FirstOrDefaultAsync(b => b.Id == boardId && b.OwnerUserId == ownerUserId, ct);

    public async Task<Board?> GetBoardTreeAsync(int boardId, int ownerUserId, CancellationToken ct = default)
    {
        var board = await _db.Boards
            .Include(b => b.Columns.OrderBy(c => c.SortOrder))
                .ThenInclude(c => c.Cards.OrderBy(card => card.SortOrder))
            .FirstOrDefaultAsync(b => b.Id == boardId && b.OwnerUserId == ownerUserId, ct);

        return board;
    }

    public async Task AddBoardAsync(Board board, CancellationToken ct = default)
    {
        await _db.Boards.AddAsync(board, ct);
    }

    public Task RemoveBoardAsync(Board board, CancellationToken ct = default)
    {
        _db.Boards.Remove(board);   // columns and cards go with it (cascade)
        return Task.CompletedTask;
    }

    public async Task<Dictionary<int, (int Columns, int Cards)>> GetBoardCountsAsync(
        List<int> boardIds, CancellationToken ct = default)
    {
        if (boardIds.Count == 0) return new Dictionary<int, (int, int)>();

        // One grouped pass per level rather than loading the trees themselves.
        var columnCounts = await _db.Columns
            .Where(c => boardIds.Contains(c.BoardId))
            .GroupBy(c => c.BoardId)
            .Select(g => new { BoardId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var cardCounts = await _db.Cards
            .Where(card => card.Column != null && boardIds.Contains(card.Column.BoardId))
            .GroupBy(card => card.Column!.BoardId)
            .Select(g => new { BoardId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return boardIds.ToDictionary(
            id => id,
            id => (columnCounts.FirstOrDefault(c => c.BoardId == id)?.Count ?? 0,
                   cardCounts.FirstOrDefault(c => c.BoardId == id)?.Count ?? 0));
    }

    // ---- Columns ----

    public Task<BoardColumn?> GetColumnAsync(int columnId, int ownerUserId, CancellationToken ct = default) =>
        _db.Columns
            .Include(c => c.Board)
            .FirstOrDefaultAsync(c => c.Id == columnId && c.Board != null &&
                                      c.Board.OwnerUserId == ownerUserId, ct);

    public Task<List<BoardColumn>> ListColumnsAsync(int boardId, CancellationToken ct = default) =>
        _db.Columns
            .Where(c => c.BoardId == boardId)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

    public async Task AddColumnAsync(BoardColumn column, CancellationToken ct = default)
    {
        await _db.Columns.AddAsync(column, ct);
    }

    public Task RemoveColumnAsync(BoardColumn column, CancellationToken ct = default)
    {
        _db.Columns.Remove(column);  // cards cascade
        return Task.CompletedTask;
    }

    public async Task<int> NextColumnSortOrderAsync(int boardId, CancellationToken ct = default)
    {
        // Max()+1 on an empty set would throw on a non-nullable int, so project to int?.
        var max = await _db.Columns
            .Where(c => c.BoardId == boardId)
            .MaxAsync(c => (int?)c.SortOrder, ct);
        return (max ?? -1) + 1;
    }

    // ---- Cards ----

    public Task<Card?> GetCardAsync(int cardId, int ownerUserId, CancellationToken ct = default) =>
        _db.Cards
            .Include(card => card.Column)!
                .ThenInclude(c => c!.Board)
            .FirstOrDefaultAsync(card => card.Id == cardId && card.Column != null &&
                                         card.Column.Board != null &&
                                         card.Column.Board.OwnerUserId == ownerUserId, ct);

    public Task<List<Card>> ListCardsAsync(int columnId, CancellationToken ct = default) =>
        _db.Cards
            .Where(c => c.ColumnId == columnId)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

    public async Task AddCardAsync(Card card, CancellationToken ct = default)
    {
        await _db.Cards.AddAsync(card, ct);
    }

    public Task RemoveCardAsync(Card card, CancellationToken ct = default)
    {
        _db.Cards.Remove(card);
        return Task.CompletedTask;
    }

    public async Task<int> NextCardSortOrderAsync(int columnId, CancellationToken ct = default)
    {
        var max = await _db.Cards
            .Where(c => c.ColumnId == columnId)
            .MaxAsync(c => (int?)c.SortOrder, ct);
        return (max ?? -1) + 1;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
