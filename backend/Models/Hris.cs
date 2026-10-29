// এইচআর ওয়ার্কস্পেস · HRIS Workspace (docs/HRIS_WORKSPACE_PROMPT.md §3.2).
//
// A pin is a saved reference, not a picture: a widget (a KPI or a MatrixQuery, the
// shape of an MxDashboard widget), a dataset kind whose latest card or summary it
// shows, or a frozen chat answer. Everything it shows is computed again by Matrix
// when the board is opened, with the board's filters.

namespace RabbitIC.Api.Models;

public static class HrisBoard
{
    /// <summary>The board HR curates (action:hris.pin) and everyone opens.</summary>
    public const string Shared = "shared";

    /// <summary>One user's own pins.</summary>
    public const string Personal = "personal";

    public static bool IsKnown(string? board) => board is Shared or Personal;
}

public static class HrisPinKind
{
    public const string Chart = "chart";
    public const string Kpi = "kpi";
    public const string Card = "card";
    public const string Summary = "summary";
    public const string Answer = "answer";

    public static readonly string[] All = { Chart, Kpi, Card, Summary, Answer };
}

public class HrisPin
{
    public int Id { get; set; }

    /// <summary><see cref="HrisBoard.Shared"/> or <see cref="HrisBoard.Personal"/>.</summary>
    public string Board { get; set; } = HrisBoard.Shared;

    /// <summary>The owner of a personal pin; null on the shared board.</summary>
    public int? OwnerUserId { get; set; }

    public string Kind { get; set; } = HrisPinKind.Chart;
    public string TitleEn { get; set; } = "";
    public string TitleBn { get; set; } = "";

    /// <summary>What the kind needs: { widgets: [...] } for chart and kpi, { dataset, language? } for card and summary, the answer for answer.</summary>
    public string PayloadJson { get; set; } = "{}";

    public int Order { get; set; }

    /// <summary>S, M or L.</summary>
    public string Size { get; set; } = "M";

    /// <summary>The starter pin this row was seeded as, e.g. "headcount"; null for a pin someone added.</summary>
    public string? SeedKey { get; set; }

    public int? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>That a board was given its starter pins once, so a restart (or HR removing them all) never adds them again.</summary>
public class HrisBoardSeed
{
    public string Board { get; set; } = "";
    public DateTime SeededAtUtc { get; set; } = DateTime.UtcNow;
}
