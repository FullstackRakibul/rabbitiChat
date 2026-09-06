// todorabbit — a creation of Mostisko, the AI.
//
// Business logic for the board module: ownership, validation, and SortOrder
// normalisation. Every method takes the resolved owner id and returns null when
// the row is not theirs, so the endpoints answer 404 for both "gone" and "not
// yours" — ownership stays unprobeable.

using RabbitIC.Api.DTOs.TodoRabbit;
using RabbitIC.Api.Models.TodoRabbit;
using RabbitIC.Api.Repositories;

namespace RabbitIC.Api.Services;

public class TodoRabbitService
{
    private readonly ITodoRabbitRepository _repo;

    /// <summary>Columns every new board starts with, in order.</summary>
    private static readonly (string Title, string ColorKey)[] DefaultColumns =
    {
        ("To Do",       "slate"),
        ("In Progress", "blue"),
        ("Done",        "green"),
    };

    /// <summary>Palette keys the UI knows how to render. Anything else falls back to slate.</summary>
    private static readonly HashSet<string> ColorKeys = new(StringComparer.OrdinalIgnoreCase)
    { "slate", "blue", "green", "amber", "red", "purple", "pink", "teal" };

    private static readonly HashSet<string> Priorities = new(StringComparer.OrdinalIgnoreCase)
    { "low", "medium", "high" };

    public TodoRabbitService(ITodoRabbitRepository repo) => _repo = repo;

    // ---- Boards ----

    public async Task<List<BoardSummaryDto>> ListBoardsAsync(int userId, CancellationToken ct = default)
    {
        var boards = await _repo.ListBoardsAsync(userId, ct);
        var counts = await _repo.GetBoardCountsAsync(boards.Select(b => b.Id).ToList(), ct);

        return boards
            .Select(b =>
            {
                var c = counts.TryGetValue(b.Id, out var v) ? v : (Columns: 0, Cards: 0);
                return new BoardSummaryDto(b.Id, b.Name, b.CreatedAt, c.Columns, c.Cards);
            })
            .ToList();
    }

    /// <summary>Creates a board seeded with the three default columns.</summary>
    public async Task<BoardDto> CreateBoardAsync(int userId, BoardInput input, CancellationToken ct = default)
    {
        var board = new Board
        {
            OwnerUserId = userId,
            Name = Clean(input.Name) ?? "My board",
        };

        for (var i = 0; i < DefaultColumns.Length; i++)
        {
            board.Columns.Add(new BoardColumn
            {
                Title = DefaultColumns[i].Title,
                ColorKey = DefaultColumns[i].ColorKey,
                SortOrder = i,
            });
        }

        await _repo.AddBoardAsync(board, ct);
        await _repo.SaveChangesAsync(ct);

        return ToDto(board);
    }

    public async Task<BoardDto?> GetBoardAsync(int boardId, int userId, CancellationToken ct = default)
    {
        var board = await _repo.GetBoardTreeAsync(boardId, userId, ct);
        return board is null ? null : ToDto(board);
    }

    public async Task<BoardSummaryDto?> RenameBoardAsync(
        int boardId, int userId, BoardInput input, CancellationToken ct = default)
    {
        var board = await _repo.GetBoardAsync(boardId, userId, ct);
        if (board is null) return null;

        var name = Clean(input.Name);
        if (name is not null) board.Name = name;

        await _repo.SaveChangesAsync(ct);
        return new BoardSummaryDto(board.Id, board.Name, board.CreatedAt, 0, 0);
    }

    public async Task<bool> DeleteBoardAsync(int boardId, int userId, CancellationToken ct = default)
    {
        var board = await _repo.GetBoardAsync(boardId, userId, ct);
        if (board is null) return false;

        await _repo.RemoveBoardAsync(board, ct);
        await _repo.SaveChangesAsync(ct);
        return true;
    }

    // ---- Columns ----

    public async Task<ColumnDto?> AddColumnAsync(
        int boardId, int userId, ColumnInput input, CancellationToken ct = default)
    {
        var board = await _repo.GetBoardAsync(boardId, userId, ct);
        if (board is null) return null;

        var column = new BoardColumn
        {
            BoardId = boardId,
            Title = Clean(input.Title) ?? "New column",
            ColorKey = NormaliseColor(input.ColorKey),
            SortOrder = await _repo.NextColumnSortOrderAsync(boardId, ct),
        };

        await _repo.AddColumnAsync(column, ct);
        await _repo.SaveChangesAsync(ct);

        return ToDto(column, new List<Card>());
    }

    public async Task<ColumnDto?> UpdateColumnAsync(
        int columnId, int userId, ColumnInput input, CancellationToken ct = default)
    {
        var column = await _repo.GetColumnAsync(columnId, userId, ct);
        if (column is null) return null;

        var title = Clean(input.Title);
        if (title is not null) column.Title = title;
        if (input.ColorKey is not null) column.ColorKey = NormaliseColor(input.ColorKey);

        await _repo.SaveChangesAsync(ct);

        var cards = await _repo.ListCardsAsync(columnId, ct);
        return ToDto(column, cards);
    }

    public async Task<bool> DeleteColumnAsync(int columnId, int userId, CancellationToken ct = default)
    {
        var column = await _repo.GetColumnAsync(columnId, userId, ct);
        if (column is null) return false;

        var boardId = column.BoardId;
        await _repo.RemoveColumnAsync(column, ct);
        await _repo.SaveChangesAsync(ct);

        // Close the gap the removed column left behind.
        await NormaliseColumnsAsync(boardId, ct);
        return true;
    }

    /// <summary>
    /// Applies a new left-to-right column order. The id list must be exactly the
    /// board's own columns — a foreign or missing id rejects the whole request,
    /// so a partial reorder can never half-apply.
    /// </summary>
    public async Task<List<ColumnDto>?> ReorderColumnsAsync(
        int userId, ReorderInput input, CancellationToken ct = default)
    {
        var ids = input.ColumnIds;
        if (ids is null || ids.Count == 0) return null;
        if (ids.Distinct().Count() != ids.Count) return null;

        // The first column anchors the board; every other id must belong to it.
        var first = await _repo.GetColumnAsync(ids[0], userId, ct);
        if (first is null) return null;

        var columns = await _repo.ListColumnsAsync(first.BoardId, ct);
        if (columns.Count != ids.Count) return null;
        if (!ids.All(id => columns.Any(c => c.Id == id))) return null;

        for (var i = 0; i < ids.Count; i++)
            columns.First(c => c.Id == ids[i]).SortOrder = i;

        await _repo.SaveChangesAsync(ct);

        return columns.OrderBy(c => c.SortOrder)
                      .Select(c => ToDto(c, new List<Card>()))
                      .ToList();
    }

    // ---- Cards ----

    public async Task<CardDto?> AddCardAsync(
        int columnId, int userId, CardInput input, CancellationToken ct = default)
    {
        var column = await _repo.GetColumnAsync(columnId, userId, ct);
        if (column is null) return null;

        var card = new Card
        {
            ColumnId = columnId,
            Title = Clean(input.Title) ?? "Untitled",
            Content = input.Content ?? "",          // kept verbatim — blank lines matter
            Priority = NormalisePriority(input.Priority),
            SortOrder = await _repo.NextCardSortOrderAsync(columnId, ct),
        };

        await _repo.AddCardAsync(card, ct);
        await _repo.SaveChangesAsync(ct);

        return ToDto(card);
    }

    public async Task<CardDto?> UpdateCardAsync(
        int cardId, int userId, CardInput input, CancellationToken ct = default)
    {
        var card = await _repo.GetCardAsync(cardId, userId, ct);
        if (card is null) return null;

        var title = Clean(input.Title);
        if (title is not null) card.Title = title;
        if (input.Content is not null) card.Content = input.Content;
        // Tri-state: absent leaves it, "" clears the badge, a name sets it.
        if (input.Priority is not null)
            card.Priority = input.Priority.Length == 0 ? null : NormalisePriority(input.Priority);

        card.UpdatedAt = DateTime.UtcNow;
        await _repo.SaveChangesAsync(ct);

        return ToDto(card);
    }

    public async Task<bool> DeleteCardAsync(int cardId, int userId, CancellationToken ct = default)
    {
        var card = await _repo.GetCardAsync(cardId, userId, ct);
        if (card is null) return false;

        var columnId = card.ColumnId;
        await _repo.RemoveCardAsync(card, ct);
        await _repo.SaveChangesAsync(ct);

        await NormaliseCardsAsync(columnId, ct);
        return true;
    }

    /// <summary>
    /// Moves a card to a slot in a (possibly different) column, then reindexes
    /// both the source and destination columns to a clean 0,1,2,....
    /// </summary>
    public async Task<CardDto?> MoveCardAsync(
        int cardId, int userId, MoveCardInput input, CancellationToken ct = default)
    {
        if (input.ToColumnId is not int toColumnId) return null;

        var card = await _repo.GetCardAsync(cardId, userId, ct);
        if (card is null) return null;

        // The destination must be the caller's too, and on the same board —
        // otherwise a card could be dragged into someone else's column.
        var target = await _repo.GetColumnAsync(toColumnId, userId, ct);
        if (target is null) return null;
        if (card.Column is null || card.Column.BoardId != target.BoardId) return null;

        var fromColumnId = card.ColumnId;

        var destination = (await _repo.ListCardsAsync(toColumnId, ct))
            .Where(c => c.Id != cardId)
            .ToList();

        var index = Math.Clamp(input.ToIndex ?? destination.Count, 0, destination.Count);
        destination.Insert(index, card);

        card.ColumnId = toColumnId;
        card.UpdatedAt = DateTime.UtcNow;

        for (var i = 0; i < destination.Count; i++)
            destination[i].SortOrder = i;

        await _repo.SaveChangesAsync(ct);

        // The source column now has a hole where the card used to be.
        if (fromColumnId != toColumnId)
            await NormaliseCardsAsync(fromColumnId, ct);

        return ToDto(card);
    }

    // ---- Normalisation ----

    /// <summary>Reindexes a column's cards to 0,1,2,... preserving their current order.</summary>
    private async Task NormaliseCardsAsync(int columnId, CancellationToken ct)
    {
        var cards = await _repo.ListCardsAsync(columnId, ct);
        var dirty = false;
        for (var i = 0; i < cards.Count; i++)
        {
            if (cards[i].SortOrder == i) continue;
            cards[i].SortOrder = i;
            dirty = true;
        }
        if (dirty) await _repo.SaveChangesAsync(ct);
    }

    /// <summary>Reindexes a board's columns to 0,1,2,... preserving their current order.</summary>
    private async Task NormaliseColumnsAsync(int boardId, CancellationToken ct)
    {
        var columns = await _repo.ListColumnsAsync(boardId, ct);
        var dirty = false;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].SortOrder == i) continue;
            columns[i].SortOrder = i;
            dirty = true;
        }
        if (dirty) await _repo.SaveChangesAsync(ct);
    }

    // ---- Mapping / validation ----

    private static BoardDto ToDto(Board board) => new(
        board.Id, board.Name, board.CreatedAt,
        board.Columns.OrderBy(c => c.SortOrder)
             .Select(c => ToDto(c, c.Cards))
             .ToList());

    private static ColumnDto ToDto(BoardColumn column, IEnumerable<Card> cards) => new(
        column.Id, column.BoardId, column.Title, column.SortOrder, column.ColorKey, column.CreatedAt,
        cards.OrderBy(c => c.SortOrder).Select(ToDto).ToList());

    private static CardDto ToDto(Card card) => new(
        card.Id, card.ColumnId, card.Title, card.Content, card.Priority,
        card.SortOrder, card.CreatedAt, card.UpdatedAt);

    /// <summary>Trims a caller-supplied string; null/blank means "not provided".</summary>
    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormaliseColor(string? key) =>
        key is not null && ColorKeys.Contains(key.Trim()) ? key.Trim().ToLowerInvariant() : "slate";

    private static string? NormalisePriority(string? value)
    {
        var v = Clean(value);
        return v is not null && Priorities.Contains(v) ? v.ToLowerInvariant() : null;
    }
}
