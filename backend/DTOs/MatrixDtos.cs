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
    System.Text.Json.JsonElement? Against, Dictionary<string, System.Text.Json.JsonElement>? Params,
    List<RabbitIC.Api.Matrix.MxScopeFilter>? Scope = null);

public sealed record MxKpiSummary(double? Min, double? Median, double? Max, int Count);

public sealed record MxKpiResult(string Key, string NameEn, string NameBn, string Unit, double? Value, string? Text,
    string? GroupBy, List<Dictionary<string, object?>> Rows, MxKpiSummary? Summary, string? Detail, string? Cite, MxProvenance? Provenance)
{
    /// <summary>The dashboard filters (Scope keys) this run could apply; the others do not fit its dataset.</summary>
    public List<string> ScopeApplied { get; init; } = new();
}

// ---- Report cards (§3.8) ----

/// <param name="Detail">More of the item in words, such as a median's lowest and highest line.</param>
public sealed record MxCardItem(string Key, string Label, string LabelBn, double? Value, string? Text, string Unit, string? Cite, string? Detail = null);

public sealed record MxCardEntry(string Label, double? Value, string Unit, string? Cite);

public sealed record MxCardBreakdown(string Key, string Label, string LabelBn, List<MxCardEntry> Top, List<MxCardEntry> Bottom);

public sealed record MxCardChange(string Key, string Label, double? Before, double? After, double? Change);

public sealed record MxCardComparison(int PreviousId, string? PreviousDate, List<MxCardChange> Items);

public sealed record MxCardIssue(string Check, string Severity, int Count);

public sealed record MxReportCardDto(int DatasetId, string Kind, string Name, string? ReportDate, List<string> Scope,
    List<MxCardItem> Items, List<MxCardBreakdown> Breakdowns, MxCardComparison? Comparison, List<MxCardIssue> Quality,
    int Tokens, DateTime CreatedAtUtc, int Version = 0);

// ---- Dashboards (§3.8) ----

public sealed record MxDashboardSummaryDto(int Id, string Name, string? NameBn, string? ReportKind, string? Auto, bool Shared,
    int OwnerUserId, bool CanEdit, int Widgets, DateTime UpdatedAtUtc);

public sealed record MxDashboardDto(int Id, string Name, string? NameBn, string? ReportKind, string? Auto, bool Shared,
    int OwnerUserId, bool CanEdit, System.Text.Json.JsonElement Layout, DateTime UpdatedAtUtc);

/// <summary>POST creates (Layout omitted: an empty dashboard; CopyOf: a copy of that one); PUT renames, re-lays out, shares.</summary>
public sealed record MxDashboardInput(string? Name, System.Text.Json.JsonElement? Layout, bool? Shared, string? ReportKind, int? CopyOf);

/// <summary>The filters of §3.8, applied to every widget whose dataset has the field. From/To: ISO dates.</summary>
public sealed record MxDashboardFilters(List<string>? Unit, List<string>? Section, List<int>? Line, List<string>? EmpType,
    List<string>? Status, List<string>? Shift, string? From, string? To);

public sealed record MxDashboardRunInput(MxDashboardFilters? Filters, bool Compare = false);

public sealed record MxTrendPoint(int DatasetId, string? ReportDate, double? Value, string? Cite);

/// <summary>The same widget on the previous snapshot: a value's change, or the previous rows.</summary>
public sealed record MxWidgetCompare(int PreviousId, string? PreviousDate, double? Before, double? After, double? Change,
    List<Dictionary<string, object?>>? Rows);

/// <summary>
/// One widget's numbers, with what produced them: its KPI and run input, or the query that
/// ran (dashboard filters included) — running either again gives the same numbers.
/// </summary>
public sealed record MxWidgetResult(
    string Id, string Type, string NameEn, string NameBn, string? Kpi, System.Text.Json.JsonElement? Query, MxKpiRunInput? Input,
    string? Unit, double? Value, string? Text, List<string> GroupBy, List<string> Measures, List<Dictionary<string, object?>> Rows,
    MxKpiSummary? Summary, string? Detail, string? Cite, string? State, System.Text.Json.JsonElement? Thresholds,
    MxWidgetCompare? Compare, List<MxTrendPoint>? Trend, List<string> FiltersApplied, List<string> FiltersIgnored,
    int? DatasetId, string? ReportDate, string? Error);

public sealed record MxDashboardRun(int Id, string Name, List<MxWidgetResult> Widgets,
    Dictionary<string, List<object>> FilterOptions, List<MxQueryDataset> Datasets)
{
    /// <summary>The widgets that ran (their place on the grid): the Report page has no saved layout of its own.</summary>
    public List<System.Text.Json.JsonElement> Layout { get; init; } = new();
}

/// <summary>A file to send: its name, media type and bytes.</summary>
public sealed record MxExportFile(string FileName, string ContentType, byte[] Bytes, List<string> Headers);
