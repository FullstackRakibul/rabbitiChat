// todorabbit — a creation of Mostisko, the AI.
//
// Wire contracts for the board module. Entities never cross the API boundary:
// OwnerUserId in particular stays server-side so ownership isn't inspectable.

namespace RabbitIC.Api.DTOs.TodoRabbit;

// ---- Output ----

/// <summary>A board in list view — no columns or cards, just the summary row.</summary>
public record BoardSummaryDto(int Id, string Name, DateTime CreatedAt, int ColumnCount, int CardCount);

/// <summary>A board in detail view — the full nested tree the Kanban UI renders.</summary>
public record BoardDto(int Id, string Name, DateTime CreatedAt, List<ColumnDto> Columns);

public record ColumnDto(int Id, int BoardId, string Title, int SortOrder, string ColorKey,
                        DateTime CreatedAt, List<CardDto> Cards);

public record CardDto(int Id, int ColumnId, string Title, string Content, string? Priority,
                      int SortOrder, DateTime CreatedAt, DateTime UpdatedAt);

// ---- Input ----
// Every field is nullable so a PUT can carry a partial update; the service
// decides which nulls mean "leave alone" and which mean "clear".

public record BoardInput(string? Name);

public record ColumnInput(string? Title, string? ColorKey);

/// <summary>Create/update a card. Priority is tri-state: absent = unchanged, "" = cleared.</summary>
public record CardInput(string? Title, string? Content, string? Priority);

/// <summary>Columns in their new left-to-right order. Ids not on the board are rejected.</summary>
public record ReorderInput(List<int>? ColumnIds);

/// <summary>Destination of a card drag: target column and 0-based slot within it.</summary>
public record MoveCardInput(int? ToColumnId, int? ToIndex);
