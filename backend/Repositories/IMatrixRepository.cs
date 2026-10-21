// ম্যাট্রিক্স — data access (docs/MATRIX_PROMPT.md §3.1, §3.2). The repository knows
// nothing about who may do what: permissions and masking are MatrixService's.

using RabbitIC.Api.Matrix;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Repositories;

public interface IMatrixRepository
{
    Task<MxUpload> AddUploadAsync(MxUpload upload, CancellationToken ct = default);
    Task<MxUpload?> GetUploadAsync(int id, CancellationToken ct = default);
    Task<List<MxUpload>> ListUploadsAsync(int take, CancellationToken ct = default);

    Task<List<MxTemplate>> ListTemplatesAsync(CancellationToken ct = default);
    Task<MxTemplate?> GetTemplateAsync(int id, CancellationToken ct = default);
    Task<MxTemplate> AddTemplateAsync(MxTemplate template, CancellationToken ct = default);

    Task<List<MxDataset>> ListDatasetsAsync(CancellationToken ct = default);
    Task<MxDataset?> GetDatasetAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Adds the dataset row, then creates mx_ds_&lt;id&gt; with one typed column per
    /// <paramref name="columns"/> entry plus _row and _sheet, fills it, and indexes
    /// its dimension and date columns — in one transaction.
    /// </summary>
    Task<MxDataset> CreateDatasetAsync(MxDataset dataset, IReadOnlyList<MxDsColumn> columns,
        IReadOnlyList<object?[]> rows, IReadOnlyList<int> sourceRows, CancellationToken ct = default);

    /// <summary>Marks this dataset the current snapshot of its kind and template, and every older one not.</summary>
    Task SetCurrentAsync(MxDataset dataset, CancellationToken ct = default);

    /// <summary>
    /// Rows of a dataset (alias b), paged; columns from <paramref name="keys"/> (already validated) plus _row.
    /// <paramref name="where"/> is a condition MatrixQuery.WhereFor built, with its parameters.
    /// </summary>
    Task<(int Total, List<Dictionary<string, object?>> Rows)> RowsAsync(MxDataset dataset, IReadOnlyList<string> keys,
        string where, IReadOnlyList<object> whereArgs, IReadOnlyList<int>? onlyRows, int offset, int limit, CancellationToken ct = default);

    /// <summary>Runs SQL that MatrixQuery compiled (names from code, every value a $p parameter).</summary>
    Task<List<Dictionary<string, object?>>> QueryAsync(string sql, IReadOnlyList<object> args, CancellationToken ct = default);

    Task<long> ScalarAsync(string sql, IReadOnlyList<object> args, CancellationToken ct = default);

    Task<List<MxRelation>> ListRelationsAsync(CancellationToken ct = default);
    Task<List<MxKpiDefinition>> ListKpiDefinitionsAsync(CancellationToken ct = default);
    Task<MxKpiDefinition?> GetKpiDefinitionAsync(string key, CancellationToken ct = default);
    Task SaveCardAsync(int datasetId, string json, CancellationToken ct = default);

    Task<List<MxDashboard>> ListDashboardsAsync(CancellationToken ct = default);
    Task<MxDashboard?> GetDashboardAsync(int id, CancellationToken ct = default);
    Task<MxDashboard> AddDashboardAsync(MxDashboard dashboard, CancellationToken ct = default);
    Task DeleteDashboardAsync(MxDashboard dashboard, CancellationToken ct = default);

    Task<MxReportCard?> GetCardAsync(int datasetId, CancellationToken ct = default);

    // ---- Ask: conversations of Kind "matrix", always the asker's own ----
    Task<Conversation?> GetConversationAsync(int id, int userId, CancellationToken ct = default);
    Task<Conversation> AddConversationAsync(Conversation conversation, CancellationToken ct = default);
    Task<List<Conversation>> ListConversationsAsync(int userId, string? scope, CancellationToken ct = default);
    Task DeleteConversationAsync(Conversation conversation, CancellationToken ct = default);
    Task<List<Message>> MessagesAsync(int conversationId, CancellationToken ct = default);
    Task AddMessageAsync(Message message, CancellationToken ct = default);

    Task<MxSummary?> GetSummaryAsync(int datasetId, string language, CancellationToken ct = default);

    /// <summary>Keeps one summary per dataset and language: the new one replaces the old.</summary>
    Task SaveSummaryAsync(MxSummary summary, CancellationToken ct = default);

    /// <summary>Distinct trimmed, lower-cased values of a column (the designation lookup).</summary>
    Task<HashSet<string>> DistinctAsync(MxDataset dataset, string key, CancellationToken ct = default);

    Task AddIssuesAsync(IEnumerable<MxQualityIssue> issues, CancellationToken ct = default);
    Task<List<MxQualityIssue>> ListIssuesAsync(int datasetId, CancellationToken ct = default);
    Task<MxQualityIssue?> GetIssueAsync(int id, CancellationToken ct = default);

    /// <summary>Replaces a dataset's issues of one check with <paramref name="issue"/> (none when null) — a re-check's result.</summary>
    Task ReplaceIssuesAsync(int datasetId, string check, MxQualityIssue? issue, CancellationToken ct = default);

    /// <summary>
    /// Applies a fix to one column: copies the raw values into _raw_&lt;key&gt; first
    /// (once), then corrects the column. Returns how many rows changed.
    /// </summary>
    Task<int> ApplyFixAsync(MxDataset dataset, string key, string kind, CancellationToken ct = default);

    Task AddFixLogAsync(MxFixLog log, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
