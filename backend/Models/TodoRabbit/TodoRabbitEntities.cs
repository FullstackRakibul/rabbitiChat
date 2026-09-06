// todorabbit — a creation of Mostisko, the AI.
//
// Kanban board entities. A board is a tree: Board → Columns → Cards, and the
// whole tree is owned by exactly one user. Columns are user-defined at runtime,
// so a card's status is the column it lives in — never an enum.

namespace RabbitIC.Api.Models.TodoRabbit;

/// <summary>A Kanban board. Scoped to its owner exactly like Conversations.</summary>
public class Board
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }              // owner — every query is scoped to this
    public string Name { get; set; } = "New board";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<BoardColumn> Columns { get; set; } = new();
}

/// <summary>
/// A column on a board. Named BoardColumn rather than Column because "Column"
/// collides with EF Core / System.ComponentModel attribute names.
/// </summary>
public class BoardColumn
{
    public int Id { get; set; }
    public int BoardId { get; set; }
    public string Title { get; set; } = "New column";
    /// <summary>Position within the board. Normalised to 0,1,2,… after every reorder.</summary>
    public int SortOrder { get; set; }
    /// <summary>Palette key the UI maps to a dot colour (e.g. "blue"). Never a raw CSS value.</summary>
    public string ColorKey { get; set; } = "slate";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Board? Board { get; set; }
    public List<Card> Cards { get; set; } = new();
}

/// <summary>A card inside a column.</summary>
public class Card
{
    public int Id { get; set; }
    public int ColumnId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Long, multi-line body. Blank lines are significant and preserved verbatim.</summary>
    public string Content { get; set; } = "";
    /// <summary>"low" | "medium" | "high", or null for no badge.</summary>
    public string? Priority { get; set; }
    /// <summary>Position within its column. Normalised to 0,1,2,… after every move.</summary>
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public BoardColumn? Column { get; set; }
}
