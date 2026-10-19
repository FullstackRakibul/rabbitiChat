namespace RabbitIC.Api.Models;

// মানব সম্পদ · প্রসিডিউর বিশ্লেষণ (Procedure Analyzer) — docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.1, §3.3.
//
// An uploaded stored procedure or function and its versions (append-only, as
// the Rulebook's are), the lookup registry that says what a code value means,
// and the review questions the scan opens when it does not know.

public class HrProcedure
{
    public int Id { get; set; }

    /// <summary>The object's name as CREATE/ALTER names it, without schema or brackets.</summary>
    public string ObjectName { get; set; } = "";

    /// <summary>"procedure" or "function".</summary>
    public string Kind { get; set; } = "procedure";

    public int CurrentVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One uploaded version. Insert-only: SQLite triggers refuse UPDATE and DELETE.</summary>
public class HrProcedureVersion
{
    public int ProcedureId { get; set; }
    public int Version { get; set; }

    /// <summary>The file as text, one line ending (\n), so line numbers mean the same everywhere.</summary>
    public string Sql { get; set; } = "";

    /// <summary>Of <see cref="Sql"/>: an identical upload is "no change".</summary>
    public string Sha256 { get; set; } = "";

    public int UploadedByUserId { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Note { get; set; }

    /// <summary>The deterministic scan of this version (HrProcFacts as JSON), made once, at upload.</summary>
    public string FactsJson { get; set; } = "";
}

/// <summary>What a code value means: (Column, Value) → Label. Seeded from docs/hr/hr-lookups.csv; HR and DevOps add to it.</summary>
public class HrLookup
{
    public string Column { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Label { get; set; }
    public string? Note { get; set; }
    public string? Source { get; set; }
    public int? UpdatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Every change to the registry after its seed. Insert-only.</summary>
public class HrLookupLog
{
    public int Id { get; set; }
    public string Column { get; set; } = "";
    public string Value { get; set; } = "";
    public string? OldLabel { get; set; }
    public string? NewLabel { get; set; }
    public string? OldNote { get; set; }
    public string? NewNote { get; set; }

    /// <summary>Why: "edit", or "answer to question #n".</summary>
    public string Reason { get; set; } = "edit";
    public int ChangedByUserId { get; set; }
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// A question the scan could not answer itself. One per distinct item (Key),
/// whichever procedures it occurs in; a closed question stays closed.
/// </summary>
public class HrReviewQuestion
{
    public int Id { get; set; }

    /// <summary>lookup, employee, rundate or table.</summary>
    public string Kind { get; set; } = "lookup";

    /// <summary>What makes it one question: "lookup:attstatus:EXL", "employee:015405", "rundate:ProcessFinalSettelment:Left…:1".</summary>
    public string Key { get; set; } = "";

    public string? Column { get; set; }
    public string? Value { get; set; }
    public string Text { get; set; } = "";

    /// <summary>open, answered or not-business.</summary>
    public string Status { get; set; } = StatusOpen;
    public string? AnswerLabel { get; set; }
    public string? AnswerNote { get; set; }
    public int? AnsweredByUserId { get; set; }
    public DateTime? AnsweredAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public const string StatusOpen = "open";
    public const string StatusAnswered = "answered";
    public const string StatusNotBusiness = "not-business";
}

/// <summary>Where a question's item occurs: one procedure version, one line.</summary>
public class HrQuestionOccurrence
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public int ProcedureId { get; set; }
    public int Version { get; set; }
    public string ObjectName { get; set; } = "";
    public string? Section { get; set; }
    public int Line { get; set; }
    public string Snippet { get; set; } = "";
}
