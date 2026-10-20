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

/// <summary>A file as received: its name and bytes. The bytes never reach the disk.</summary>
public sealed record MxIncomingFile(string FileName, byte[] Bytes);

public interface IMatrixService
{
    public const long MaxUploadBytes = 20L * 1024 * 1024;   // the limit Documents and the HR library use

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
}
