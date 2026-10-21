// ম্যাট্রিক্স — what the model writes: the summary (docs/MATRIX_PROMPT.md §3.8).
//
// The summary is written from the report card alone — aggregates and labels, never a
// row, so no name, ID or salary the caller may not see reaches the model — then
// checked by code: the exact number check, a label on every point, no label the card
// does not have. It is saved per dataset and language; a card made again later makes
// it stale, and the page writes it again.

using System.Text;
using System.Text.Json;
using RabbitIC.Api.DTOs.Matrix;
using RabbitIC.Api.Hr;
using RabbitIC.Api.Matrix;
using RabbitIC.Api.MinimalApis;
using RabbitIC.Api.ModelSources;
using RabbitIC.Api.Models;
using RabbitIC.Api.Rbac;

namespace RabbitIC.Api.Services;

public partial class MatrixService
{
    public async Task<MxResult<MxSummaryDto>> SummaryAsync(User user, int datasetId, string language, CancellationToken ct = default)
    {
        if (!MatrixSummary.Languages.Contains(language)) return MxResult<MxSummaryDto>.Fail(MxOutcome.Invalid, "lang is bn or en.");
        if (await _repo.GetDatasetAsync(datasetId, ct) is null) return MxResult<MxSummaryDto>.Fail(MxOutcome.NotFound, "No such dataset.");
        var saved = await _repo.GetSummaryAsync(datasetId, language, ct);
        if (saved is null || JsonSerializer.Deserialize<MxSummaryDto>(saved.MetaJson, Json) is not { } dto)
            return MxResult<MxSummaryDto>.Fail(MxOutcome.NotFound, "No summary has been written yet.");
        var card = await _repo.GetCardAsync(datasetId, ct);
        return MxResult<MxSummaryDto>.Success(dto with { Stale = card is not null && card.CreatedAtUtc > dto.CardCreatedAtUtc.AddSeconds(1) });
    }

    public async Task<MxResult<MxSummaryDto>> WriteSummaryAsync(User user, int datasetId, MxSummaryInput input, CancellationToken ct = default)
    {
        var language = input.Language ?? "bn";
        if (!MatrixSummary.Languages.Contains(language)) return MxResult<MxSummaryDto>.Fail(MxOutcome.Invalid, "language is bn or en.");
        var cardResult = await CardAsync(user, datasetId, ct);
        if (cardResult.Outcome != MxOutcome.Ok) return MxResult<MxSummaryDto>.Fail(cardResult.Outcome, cardResult.Error!);
        var card = cardResult.Value!;
        var (model, refused) = await _models.ResolveAsync(user, input.Model, ct);
        if (model is null) return MxResult<MxSummaryDto>.Fail(MxOutcome.Forbidden, refused!);

        var cardText = ReportCardBuilder.Render(card);
        var labels = ReportCardBuilder.Labels(card);
        var system = MatrixSummary.System(language);
        var capabilities = await ChatEndpoint.ModelCapabilitiesAsync(_chat, ModelPolicy.LocalSource, model, ct);
        bool? think = capabilities.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase) || c.Equals("reasoning", StringComparison.OrdinalIgnoreCase)) ? false : null;
        var args = new ChatArgs(model, new List<ChatTurn> { new("system", system), new("user", MatrixSummary.User(cardText, language)) },
            Tools: null, Think: think, NumCtx: _models.NumCtx, KeepAlive: null, Temperature: 0);

        var answer = new StringBuilder();
        var formulas = new HrFormulaStream(MatrixSummary.Number);
        try
        {
            await foreach (var e in _chat.StreamAsync(ModelPolicy.LocalSource, args, ct))
                if (e.Kind == ChatEventKind.Content)
                    foreach (var piece in formulas.Push(e.Text)) answer.Append(piece.Text);
            foreach (var piece in formulas.Finish()) answer.Append(piece.Text);
        }
        catch (ChatSourceException ex)
        {
            return MxResult<MxSummaryDto>.Fail(MxOutcome.Invalid, $"The model could not write the summary: {ex.Message}");
        }

        var text = answer.ToString().Trim();
        var calculated = formulas.Formulas.Where(f => f.Ok).Select(f => f.Value!.Value);
        var unverified = HrNumberCheck.UnverifiedExact(MatrixSummary.WithoutLabels(text), new[] { cardText }, calculated).ToList();
        var (points, uncited, unknown) = MatrixSummary.Check(text, labels.Select(l => l.Label).ToList());
        var stored = await _repo.GetCardAsync(datasetId, ct);
        var dto = new MxSummaryDto(datasetId, language, text, model, points.Count,
            formulas.Formulas.Select(f => new MxSummaryFormula(f.Offset, f.Display.Length, f.Expression, f.Display, f.Ok, f.Error)).ToList(),
            unverified, uncited, unknown, labels, stored?.CreatedAtUtc ?? DateTime.UtcNow, false, DateTime.UtcNow);
        await _repo.SaveSummaryAsync(new MxSummary
        {
            DatasetId = datasetId, Language = language, Text = text, MetaJson = JsonSerializer.Serialize(dto, Json), CreatedAtUtc = dto.CreatedAtUtc,
        }, ct);
        _log.LogInformation("Matrix summary: dataset {Dataset} ({Language}) by {Model}: {Points} points, {Unverified} unverified numbers, {Uncited} uncited points.",
            datasetId, language, model, points.Count, unverified.Count, uncited.Count);
        return MxResult<MxSummaryDto>.Success(dto);
    }
}
