// ম্যাট্রিক্স — the module's business rules (docs/MATRIX_PROMPT.md §3.4, §3.5, §3.10).

using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Services;

/// <summary>Why a Matrix operation was refused. <see cref="Ok"/> is the only success.</summary>
public enum MxOutcome { Ok, NotFound, Forbidden, Invalid, Gone }

public sealed record MxResult<T>(MxOutcome Outcome, T? Value = default, string? Error = null)
{
    public static MxResult<T> Success(T value) => new(MxOutcome.Ok, value);
    public static MxResult<T> Fail(MxOutcome outcome, string error) => new(outcome, default, error);
}

/// <summary>Where an Ask answer streams to: the endpoint's NDJSON writer. Start comes once the question is accepted.</summary>
public interface IMxAskSink
{
    Task StartAsync(int conversationId);
    Task StatusAsync(string text);
    Task FrameAsync(object frame);
    Task ContentAsync(string text);
}

/// <summary>A file as received: its name and bytes. The bytes never reach the disk.</summary>
public sealed record MxIncomingFile(string FileName, byte[] Bytes);

public interface IMatrixService
{
    public const long MaxUploadBytes = 50L * 1024 * 1024;   // the limit Documents and the HR library use

    Task<MxResult<MxUploadsDto>> UploadAsync(User user, IReadOnlyList<MxIncomingFile> files, CancellationToken ct = default);
    Task<List<MxUploadDto>> ListUploadsAsync(CancellationToken ct = default);
    Task<MxResult<MxProfile>> GetProfileAsync(int uploadId, CancellationToken ct = default);
    Task<MxResult<MxImportResult>> ImportAsync(User user, int uploadId, MxImportInput input, CancellationToken ct = default);

    Task<List<MxTemplateDto>> ListTemplatesAsync(CancellationToken ct = default);
    Task<MxResult<MxTemplateDto>> UpdateTemplateAsync(int id, MxTemplateInput input, User user, CancellationToken ct = default);

    Task<List<MxDatasetDto>> ListDatasetsAsync(CancellationToken ct = default);
    Task<MxResult<MxDatasetDto>> GetDatasetAsync(int id, CancellationToken ct = default);

    /// <summary>The viewer's rows: filtered, paged, masked for what <paramref name="user"/> may not see.</summary>
    Task<MxResult<MxRowsDto>> RowsAsync(User user, int datasetId, int offset, int limit, string? filter, string? highlight,
        bool onlyHighlighted, CancellationToken ct = default);

    Task<MxResult<MxIssuesDto>> IssuesAsync(int datasetId, CancellationToken ct = default);
    Task<MxResult<MxFixResult>> FixAsync(User user, int issueId, MxFixInput input, CancellationToken ct = default);

    IReadOnlyList<MxFieldDto> Catalog();

    /// <summary>Runs a MatrixQuery (§3.6) for the caller: validated, permission-checked, parameterised, with provenance and citations.</summary>
    Task<MxResult<MxQueryResult>> QueryAsync(User user, System.Text.Json.JsonElement query, bool explain, CancellationToken ct = default);

    Task<List<MxKpiDto>> ListKpisAsync(CancellationToken ct = default);
    Task<MxResult<MxKpiDto>> UpdateKpiAsync(string key, MxKpiInput input, CancellationToken ct = default);
    Task<MxResult<MxKpiResult>> RunKpiAsync(User user, string key, MxKpiRunInput input, CancellationToken ct = default);

    /// <summary>The dataset's report card (§3.8): the one made at import, or made now.</summary>
    Task<MxResult<MxReportCardDto>> CardAsync(User user, int datasetId, CancellationToken ct = default);

    /// <summary>The dashboards the caller may see (shared ones and their own); the automatic ones are made first if missing.</summary>
    Task<List<MxDashboardSummaryDto>> ListDashboardsAsync(User user, CancellationToken ct = default);
    Task<MxResult<MxDashboardDto>> GetDashboardAsync(User user, int id, CancellationToken ct = default);
    Task<MxResult<MxDashboardDto>> CreateDashboardAsync(User user, MxDashboardInput input, CancellationToken ct = default);
    Task<MxResult<MxDashboardDto>> UpdateDashboardAsync(User user, int id, MxDashboardInput input, CancellationToken ct = default);
    Task<MxResult<bool>> DeleteDashboardAsync(User user, int id, CancellationToken ct = default);

    /// <summary>Every widget's numbers for the caller, with the dashboard's filters, and what ran to make them.</summary>
    Task<MxResult<MxDashboardRun>> RunDashboardAsync(User user, int id, MxDashboardRunInput input, CancellationToken ct = default);

    /// <summary>The saved summary of a dataset in a language ("bn" or "en"); NotFound when none has been written.</summary>
    Task<MxResult<MxSummaryDto>> SummaryAsync(User user, int datasetId, string language, CancellationToken ct = default);

    /// <summary>Has the model write the summary from the report card, checks it, saves it.</summary>
    Task<MxResult<MxSummaryDto>> WriteSummaryAsync(User user, int datasetId, MxSummaryInput input, CancellationToken ct = default);

    /// <summary>
    /// Ask (§3.9): plan → queries → a streamed, checked answer, saved in a "matrix" conversation.
    /// Fails before anything streams (bad question, no dataset, no model); after Start every
    /// outcome — a refusal, "I couldn't form a query" — is an answer.
    /// </summary>
    Task<MxResult<int>> AskAsync(User user, MxAskInput input, IMxAskSink sink, CancellationToken ct = default);

    Task<List<MxConversationDto>> ListConversationsAsync(User user, int? datasetId, CancellationToken ct = default);
    Task<MxResult<List<MxMessageDto>>> ConversationAsync(User user, int id, CancellationToken ct = default);
    Task<MxResult<bool>> DeleteConversationAsync(User user, int id, CancellationToken ct = default);

    /// <summary>A dataset's Report page charts: its kind's automatic charts, run on this dataset (tiles come from the card).</summary>
    Task<MxResult<MxDashboardRun>> RunReportAsync(User user, int datasetId, MxDashboardRunInput input, CancellationToken ct = default);

    /// <summary>A dataset as xlsx or csv (no sensitive columns; personal and salary only when permitted), or a dashboard layout as json.</summary>
    Task<MxResult<MxExportFile>> ExportAsync(User user, string kind, int id, CancellationToken ct = default);
}
