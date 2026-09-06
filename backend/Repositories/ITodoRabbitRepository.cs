// todorabbit — a creation of Mostisko, the AI.
//
// Data access for the board module. Every read takes an ownerUserId and filters
// on it in the query itself, so an unowned row is indistinguishable from a
// missing one — the service turns both into 404.

using RabbitIC.Api.Models.TodoRabbit;

namespace RabbitIC.Api.Repositories;

public interface ITodoRabbitRepository
{
    // ---- Boards ----
    Task<List<Board>> ListBoardsAsync(int ownerUserId, CancellationToken ct = default);
    /// <summary>The board alone — no columns loaded. Null when it isn't the caller's.</summary>
    Task<Board?> GetBoardAsync(int boardId, int ownerUserId, CancellationToken ct = default);
    /// <summary>The board with columns and cards loaded, each already sorted.</summary>
    Task<Board?> GetBoardTreeAsync(int boardId, int ownerUserId, CancellationToken ct = default);
    Task AddBoardAsync(Board board, CancellationToken ct = default);
    Task RemoveBoardAsync(Board board, CancellationToken ct = default);
    /// <summary>Column/card counts for a set of boards, keyed by board id — avoids loading the trees.</summary>
    Task<Dictionary<int, (int Columns, int Cards)>> GetBoardCountsAsync(
        List<int> boardIds, CancellationToken ct = default);

    // ---- Columns ----
    /// <summary>One column, verified against the owner by joining through its board.</summary>
    Task<BoardColumn?> GetColumnAsync(int columnId, int ownerUserId, CancellationToken ct = default);
    Task<List<BoardColumn>> ListColumnsAsync(int boardId, CancellationToken ct = default);
    Task AddColumnAsync(BoardColumn column, CancellationToken ct = default);
    Task RemoveColumnAsync(BoardColumn column, CancellationToken ct = default);
    Task<int> NextColumnSortOrderAsync(int boardId, CancellationToken ct = default);

    // ---- Cards ----
    Task<Card?> GetCardAsync(int cardId, int ownerUserId, CancellationToken ct = default);
    Task<List<Card>> ListCardsAsync(int columnId, CancellationToken ct = default);
    Task AddCardAsync(Card card, CancellationToken ct = default);
    Task RemoveCardAsync(Card card, CancellationToken ct = default);
    Task<int> NextCardSortOrderAsync(int columnId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
