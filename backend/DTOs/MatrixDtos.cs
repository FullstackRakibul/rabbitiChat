// ম্যাট্রিক্স — wire contracts (docs/MATRIX_PROMPT.md §3.10).
//
// The profile is what WorkbookProfiler produces and what /uploads returns: the
// shape of a workbook — sheets, tables, header texts, types, quirks, mapping
// suggestions — and never a cell value. Rows reach the client only through the
// rows route, which masks what the caller may not see.

namespace RabbitIC.Api.DTOs.Matrix;

// ---- Profile ----

public sealed class MxProfile
{
    public string FileName { get; set; } = "";
    public string? ReportDate { get; set; }
    public List<MxSheetProfile> Sheets { get; set; } = new();
}

public sealed class MxSheetProfile
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string? UsedRange { get; set; }
    public int Rows { get; set; }
    public int Cols { get; set; }
    public List<MxTableProfile> Tables { get; set; } = new();
}

public sealed class MxTableProfile
{
    /// <summary>"&lt;sheet index&gt;.&lt;table index&gt;" — what an import names the table by.</summary>
    public string Id { get; set; } = "";
    public string Sheet { get; set; } = "";
    public string Range { get; set; } = "";
    public List<int> HeaderRows { get; set; } = new();
    public int FirstDataRow { get; set; }
    public int LastDataRow { get; set; }
    public int DataRows { get; set; }

    /// <summary>Title and other lines above the header (the report's own name, its date range).</summary>
    public List<string> Details { get; set; } = new();
    public string? ReportDate { get; set; }
    public string Kind { get; set; } = "custom";
    public string Fingerprint { get; set; } = "";
    public List<string> Quirks { get; set; } = new();
    public MxCopyInfo? CopyOf { get; set; }

    /// <summary>Imported by default: every table that is not a copy of another.</summary>
    public bool Selected { get; set; } = true;
    public MxTemplateRef? Template { get; set; }
    public List<MxColumnProfile> Columns { get; set; } = new();
}

public sealed class MxCopyInfo
{
    public string Sheet { get; set; } = "";
    public double OverlapPct { get; set; }

    /// <summary>A near-copy: the same people, different columns.</summary>
    public bool Near { get; set; }
    public int ExtraColumns { get; set; }
}

public sealed record MxTemplateRef(int Id, string Name, int Version);

public sealed class MxColumnProfile
{
    /// <summary>0-based within the table.</summary>
    public int Index { get; set; }
    public string Letter { get; set; } = "";
    public string Header { get; set; } = "";
    public string Type { get; set; } = "text";
    public string? Unit { get; set; }
    public int Nulls { get; set; }
    public int NullText { get; set; }
    public int Distinct { get; set; }
    public string? Formula { get; set; }
    public int FormulaCount { get; set; }
    public List<string> Quirks { get; set; } = new();
    public string Sensitivity { get; set; } = "personal";
    public string? SensitiveCategory { get; set; }
    public MxSuggestion? Suggestion { get; set; }

    /// <summary>The field the header's spelling alone names (the catalog's synonyms), before any template or model.</summary>
    public string? Synonym { get; set; }
}

/// <summary>Source: synonym, model, template or none. A model's suggestion waits for a person to confirm it.</summary>
public sealed class MxSuggestion
{
    public string? Field { get; set; }
    public string Source { get; set; } = "none";
    public double Confidence { get; set; }
    public bool Confirmed { get; set; }
}

// ---- Uploads, templates, datasets ----

public sealed record MxUploadDto(
    int Id, string FileName, long Size, string Sha256, string Status, string? ReportDate, DateTime UploadedAtUtc, MxProfile? Profile);

public sealed record MxUploadsDto(List<MxUploadDto> Uploads);

public sealed record MxTemplateDto(
    int Id, string Name, string ReportKind, string SheetName, int Version, int Columns, DateTime UpdatedAtUtc);

public sealed record MxTemplateInput(string? Name, string? ReportKind);

public sealed record MxColumnDto(
    string Key, string Header, string? Field, string Type, string Sensitivity, string? Unit, bool Derived, string? Bn, string? En);

public sealed record MxDroppedDto(string Header, string Category);

public sealed record MxDatasetDto(
    int Id, string Name, string Kind, string Sheet, string Range, int RowCount, string? ReportDate, bool IsCurrent,
    int UploadId, string FileName, int? TemplateId, DateTime CreatedAtUtc, List<MxColumnDto> Columns, List<MxDroppedDto> Dropped,
    int OpenIssues);

// ---- Import ----

/// <param name="Field">A catalog key; null leaves the column out; "keep" imports it unmapped (personal until classified).</param>
public sealed record MxMappingInput(int Index, string? Field, string? Sensitivity);

/// <param name="Confirm">"suggestions": every suggestion shown is confirmed as it stands (the UI's "Confirm all").</param>
/// <param name="AllowSensitive">Sensitive columns an administrator keeps for this template, each with a note.</param>
public sealed record MxImportTable(
    string Table, string? Kind, string? Name, string? ReportDate, List<MxMappingInput>? Mapping, string? Confirm,
    List<MxAllowSensitive>? AllowSensitive);

public sealed record MxAllowSensitive(int Index, string Note);

public sealed record MxImportInput(List<MxImportTable>? Tables);

public sealed record MxImportResult(List<MxDatasetDto> Datasets, List<MxTemplateDto> Templates, string Message);

// ---- Rows (the viewer) ----

public sealed record MxRowsColumn(string Key, string Header, string Type, string Sensitivity, bool Masked, string? Bn, string? En);

/// <summary>Rows and cells a citation or quality issue points at: the viewer outlines the rows and fills the cells.</summary>
public sealed record MxHighlight(List<int> Rows, List<string> Cells);

public sealed record MxRowsDto(
    int DatasetId, int Total, int Offset, List<MxRowsColumn> Columns, List<Dictionary<string, object?>> Rows, MxHighlight? Highlight);

// ---- Quality ----

public sealed record MxIssueDto(
    int Id, string Check, string Severity, int Count, string? Field, List<string> Samples, List<int> HighlightRows,
    string Detail, string SuggestedFix, string? FixKind, string State, string Highlight);

public sealed record MxIssuesDto(int DatasetId, List<MxIssueDto> Issues);

public sealed record MxFixInput(string? Note);

public sealed record MxFixResult(int IssueId, string Field, string RawColumn, int RowsChanged, string Kind);

// ---- Catalog ----

public sealed record MxFieldDto(string Key, string Group, string Role, string Type, string Sensitivity, string En, string Bn, bool Derived);

// ---- Queries (§3.6) ----

/// <param name="Explain">Also return the SQL and its parameters (the query's own words are always returned).</param>
public sealed record MxQueryRequest(System.Text.Json.JsonElement Query, bool? Explain);

public sealed record MxQueryColumn(string Key, string Label, string Role, string Type);

public sealed record MxQueryDataset(int Id, string Name, string Kind, string? ReportDate, int RowCount);

public sealed record MxQueryJoin(int DatasetId, string Kind, string On, long Unmatched);

/// <summary>Where a result came from: its query in words, the datasets and their dates, how many rows matched, and the joins' misses.</summary>
public sealed record MxProvenance(
    string Plain, List<MxQueryDataset> Datasets, long MatchedRows, List<MxQueryJoin> Joins, string? Sql, List<object>? Params);

/// <summary>Rows: each group's values and measures, a "_cite" token for the rows behind it, and "_cites" per count_if measure.</summary>
public sealed record MxQueryResult(List<MxQueryColumn> Columns, List<Dictionary<string, object?>> Rows, MxProvenance Provenance);

// ---- KPIs (§3.7) ----

public sealed record MxKpiDto(string Key, string Area, string NameEn, string NameBn, string Words, string Unit, string Shape,
    string? Query, System.Text.Json.JsonElement? Thresholds, string[]? Kinds);

public sealed record MxKpiInput(string? NameEn, string? NameBn, string? Words, System.Text.Json.JsonElement? Thresholds);

/// <param name="Previous">The snapshot before Dataset (snapshot KPIs); default: the one before it of the same layout.</param>
/// <param name="Against">The manpower or master dataset attendance % is measured against; default: the current master.</param>
public sealed record MxKpiRunInput(System.Text.Json.JsonElement? Dataset, System.Text.Json.JsonElement? Previous,
    System.Text.Json.JsonElement? Against, Dictionary<string, System.Text.Json.JsonElement>? Params);

public sealed record MxKpiSummary(double? Min, double? Median, double? Max, int Count);

public sealed record MxKpiResult(string Key, string NameEn, string NameBn, string Unit, double? Value, string? Text,
    string? GroupBy, List<Dictionary<string, object?>> Rows, MxKpiSummary? Summary, string? Detail, string? Cite, MxProvenance? Provenance);

// ---- Report cards (§3.8) ----

public sealed record MxCardItem(string Key, string Label, string LabelBn, double? Value, string? Text, string Unit, string? Cite);

public sealed record MxCardEntry(string Label, double? Value, string Unit, string? Cite);

public sealed record MxCardBreakdown(string Key, string Label, string LabelBn, List<MxCardEntry> Top, List<MxCardEntry> Bottom);

public sealed record MxCardChange(string Key, string Label, double? Before, double? After, double? Change);

public sealed record MxCardComparison(int PreviousId, string? PreviousDate, List<MxCardChange> Items);

public sealed record MxCardIssue(string Check, string Severity, int Count);

public sealed record MxReportCardDto(int DatasetId, string Kind, string Name, string? ReportDate, List<string> Scope,
    List<MxCardItem> Items, List<MxCardBreakdown> Breakdowns, MxCardComparison? Comparison, List<MxCardIssue> Quality,
    int Tokens, DateTime CreatedAtUtc);
