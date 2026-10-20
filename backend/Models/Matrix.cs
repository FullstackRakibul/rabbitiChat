namespace RabbitIC.Api.Models;

// ম্যাট্রিক্স · Matrix — docs/MATRIX_PROMPT.md §3.2.
//
// An upload is profiled, then imported into one dataset per chosen table. Each
// dataset's rows live in a table of their own, mx_ds_<id>, made and dropped only
// by MatrixRepository; the rows here describe those tables, the templates that
// map a layout's columns to catalog fields, the quality issues found at import,
// and the fixes applied to them. The original file is never stored: an upload
// keeps only its name, size, hash and profile.

/// <summary>One uploaded workbook. The file itself is never kept — only what is needed to say what it was.</summary>
public class MxUpload
{
    public const string StatusProfiled = "profiled";
    public const string StatusImported = "imported";
    public const string StatusExpired = "expired";

    public int Id { get; set; }
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public int UploadedByUserId { get; set; }
    public DateTime UploadedAtUtc { get; set; }

    /// <summary>ISO date (yyyy-MM-dd): detected from a date-range line, or entered at import.</summary>
    public string? ReportDate { get; set; }

    /// <summary><see cref="StatusProfiled"/>, <see cref="StatusImported"/> or <see cref="StatusExpired"/> (the bytes are gone; re-upload to import).</summary>
    public string Status { get; set; } = StatusProfiled;

    /// <summary>The profile (MatrixProfile as JSON): sheets, tables, header texts, types, quirks. Never a cell value.</summary>
    public string ProfileJson { get; set; } = "";
}

/// <summary>How one layout's columns map to catalog fields. Matched by fingerprint; a change makes a new version.</summary>
public class MxTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>manpower, master, attendance-daily, designation-lookup, separation-list or custom.</summary>
    public string ReportKind { get; set; } = "custom";

    /// <summary>SHA-256 of the sheet name and the normalised header texts.</summary>
    public string Fingerprint { get; set; } = "";
    public string SheetName { get; set; } = "";

    /// <summary>[{ index, header, field, sensitivity }] — field null means "not imported".</summary>
    public string MappingJson { get; set; } = "[]";

    /// <summary>Sensitive columns an administrator allowed for this template: [{ index, header, note, byUserId, atUtc }].</summary>
    public string AllowedSensitiveJson { get; set; } = "[]";

    public int Version { get; set; } = 1;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int UpdatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One imported table. Its rows are in <see cref="TableName"/>.</summary>
public class MxDataset
{
    public int Id { get; set; }
    public int UploadId { get; set; }
    public int? TemplateId { get; set; }
    public string Name { get; set; } = "";
    public string ReportKind { get; set; } = "custom";
    public string Sheet { get; set; } = "";

    /// <summary>The table's range in the sheet, e.g. "A3:K679".</summary>
    public string TableRange { get; set; } = "";
    public int RowCount { get; set; }

    /// <summary>mx_ds_&lt;id&gt;, set by the repository after the row exists — never from user text.</summary>
    public string TableName { get; set; } = "";

    /// <summary>[{ key, header, field, type, sensitivity, unit, derived, source }] — the dataset's columns, in order.</summary>
    public string ColumnsJson { get; set; } = "[]";

    /// <summary>Columns found sensitive and not imported: [{ header, category }].</summary>
    public string DroppedJson { get; set; } = "[]";

    public string? ReportDate { get; set; }

    /// <summary>The latest snapshot of its kind and layout (template).</summary>
    public bool IsCurrent { get; set; }

    public int CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One quality finding in a dataset (§3.5).</summary>
public class MxQualityIssue
{
    public const string Open = "open";
    public const string Fixed = "fixed";
    public const string Ignored = "ignored";

    public int Id { get; set; }
    public int DatasetId { get; set; }

    /// <summary>The check's key, e.g. "dup_emp_id" (QualityChecks).</summary>
    public string Check { get; set; } = "";

    /// <summary>error, warning or info.</summary>
    public string Severity { get; set; } = "warning";
    public int Count { get; set; }

    /// <summary>The dataset column it is about, when there is one.</summary>
    public string? Field { get; set; }

    /// <summary>Up to 20 sample cell references, e.g. ["HRM!A8514", …].</summary>
    public string SampleRefsJson { get; set; } = "[]";

    /// <summary>The source rows (_row) of the samples, for the viewer's highlight.</summary>
    public string SampleRowsJson { get; set; } = "[]";

    public string Detail { get; set; } = "";
    public string SuggestedFix { get; set; } = "";

    /// <summary>The fix this issue can take (trim, unify, pad-ids), or null when it needs a person.</summary>
    public string? FixKind { get; set; }

    public string State { get; set; } = Open;
}

/// <summary>A fix applied to a dataset column: the raw values are kept in _raw_&lt;column&gt;.</summary>
public class MxFixLog
{
    public int Id { get; set; }
    public int DatasetId { get; set; }
    public int? IssueId { get; set; }
    public string Field { get; set; } = "";
    public string Kind { get; set; } = "";
    public int RowsChanged { get; set; }
    public int ByUserId { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Note { get; set; }
}

/// <summary>How two kinds of dataset join (§3.2): left kind.field ↔ right kind.field, through a normaliser.</summary>
public class MxRelation
{
    public int Id { get; set; }
    public string LeftKind { get; set; } = "";
    public string LeftField { get; set; } = "";
    public string RightKind { get; set; } = "";
    public string RightField { get; set; } = "";

    /// <summary>A normaliser name from MatrixNormalisers, e.g. "emp-id-6" (pad to six digits).</summary>
    public string Normaliser { get; set; } = "";
}

// The rows below are filled from Phase M2 on (queries, KPIs, cards) and M3–M4
// (dashboards, summaries); M1 creates their tables so the data contract is whole.

public class MxDashboard
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? ReportKind { get; set; }
    public int? DatasetId { get; set; }
    public string LayoutJson { get; set; } = "{}";
    public int OwnerUserId { get; set; }
    public bool Shared { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class MxReportCard
{
    public int Id { get; set; }
    public int DatasetId { get; set; }
    public string Json { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
}

public class MxSummary
{
    public int Id { get; set; }
    public int DatasetId { get; set; }
    public string Language { get; set; } = "en";
    public string Text { get; set; } = "";
    public string MetaJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
}

public class MxKpiDefinition
{
    public string Key { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string NameBn { get; set; } = "";
    public string Description { get; set; } = "";
    public string FormulaWords { get; set; } = "";
    public string QueryJson { get; set; } = "{}";
    public string Unit { get; set; } = "";
    public string ThresholdsJson { get; set; } = "{}";
    public DateTime UpdatedAtUtc { get; set; }
}
