// ম্যাট্রিক্স — Ask (docs/MATRIX_PROMPT.md §3.9): plan → MatrixQuery → answer, cited and
// checked, saved as a "matrix" conversation scoped to its dataset. MatrixChat.cs holds the
// prompts and the plan's shape; this runs them.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
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
    private static string ScopeOf(int datasetId) => $"dataset:{datasetId}";

    public async Task<MxResult<int>> AskAsync(User user, MxAskInput input, IMxAskSink sink, CancellationToken ct = default)
    {
        var question = (input.Message ?? "").Trim();
        if (question.Length == 0) return MxResult<int>.Fail(MxOutcome.Invalid, "message is required.");
        if (question.Length > 2000) return MxResult<int>.Fail(MxOutcome.Invalid, "A question is at most 2,000 characters.");

        Conversation? convo = null;
        if (input.ConversationId is int cid && (convo = await _repo.GetConversationAsync(cid, user.Id, ct)) is null)
            return MxResult<int>.Fail(MxOutcome.NotFound, "No such conversation.");
        var datasetId = input.DatasetId
            ?? (convo?.Scope is { } s && s.StartsWith("dataset:") && int.TryParse(s[8..], out var sid) ? sid : (int?)null);
        if (datasetId is null) return MxResult<int>.Fail(MxOutcome.Invalid, "Ask about a dataset: open its Report page.");
        var scope = await _repo.GetDatasetAsync(datasetId.Value, ct);
        if (scope is null) return MxResult<int>.Fail(MxOutcome.NotFound, "No such dataset.");
        var (model, refused) = await _models.ResolveAsync(user, input.Model, ct);
        if (model is null) return MxResult<int>.Fail(MxOutcome.Forbidden, refused!);

        // ---- the conversation (this person's, about this dataset) ----
        convo ??= await _repo.AddConversationAsync(new Conversation
        {
            UserId = user.Id, Kind = MatrixChat.ConversationKind, Scope = ScopeOf(scope.Id),
            Title = question.Length > 40 ? question[..40] + "…" : question,
        }, ct);
        var earlier = await _repo.MessagesAsync(convo.Id, ct);
        var history = earlier.Zip(earlier.Skip(1)).Where(p => p.First.Role == "user" && p.Second.Role == "assistant")
            .Select(p => (p.First.Content, p.Second.Content)).TakeLast(2).ToList();
        await _repo.AddMessageAsync(new Message { ConversationId = convo.Id, Role = "user", Content = question }, ct);

        await sink.StartAsync(convo.Id);
        var clock = Stopwatch.StartNew();
        var bengali = MatrixChat.IsBengali(question);
        await sink.StatusAsync(bengali ? "খুঁজছি… প্রশ্নটি বুঝে নিচ্ছি" : "Looking… working out what to compute");

        var card = (await CardAsync(user, scope.Id, ct)).Value!;
        var cardText = ReportCardBuilder.Render(card);
        var cardLabels = ReportCardBuilder.Labels(card);
        var ctx = await QueryContextAsync(user, ct);
        var kpis = (await ListKpisAsync(ct)).ToDictionary(k => k.Key);

        // The dataset in scope, then the current dataset of each other kind it can be joined with.
        var inScope = new List<(MxDataset, List<MxDsColumn>)> { (scope, Columns(scope)) };
        foreach (var other in ctx.Datasets.Where(d => d.IsCurrent && d.ReportKind != scope.ReportKind && d.ReportKind != MxReportKind.Custom)
                     .GroupBy(d => d.ReportKind).Select(g => g.OrderBy(d => d.ReportDate ?? "").ThenBy(d => d.Id).Last()))
            inScope.Add((other, Columns(other)));

        // ---- 1. plan ----
        var messages = new List<SchemaJsonMessage>
        {
            new("system", MatrixChat.PlanSystem(scope, inScope, KpiLibrary.All, ctx, await SampleValuesAsync(ctx, inScope, ct))),
            new("user", MatrixChat.PlanUser(cardText, history, question)),
        };
        var meta = new Dictionary<string, object?> { ["model"] = model, ["datasetId"] = scope.Id, ["cardLabels"] = cardLabels };
        var first = await SchemaJsonChat.CallAsync(_http, model, MatrixChat.PlanSchema, messages, _models.NumCtx, ct);
        if (first.Error is not null)
            return await FinishAsync(convo, sink, $"The model could not plan an answer: {first.Error}", meta, clock, ct);

        var plan = MatrixChat.ParsePlan(first.Content);
        var (errors, forbidden) = Validate(plan, ctx);
        var repaired = false;
        if (forbidden is null && errors.Count > 0)
        {
            // One repair round, with the validator's own words.
            repaired = true;
            messages.Add(new("assistant", first.Content ?? ""));
            messages.Add(new("user", MatrixChat.RepairMessage(errors)));
            var second = await SchemaJsonChat.CallAsync(_http, model, MatrixChat.PlanSchema, messages, _models.NumCtx, ct);
            plan = second.Error is null ? MatrixChat.ParsePlan(second.Content) : null;
            (errors, forbidden) = second.Error is null ? Validate(plan, ctx) : (new List<string> { second.Error }, null);
        }
        meta["plan"] = plan is null ? null : new { plan.Kind, plan.Queries, plan.Definition, plan.Need };
        meta["repaired"] = repaired;
        if (forbidden is not null)
        {
            meta["refusal"] = forbidden;
            return await FinishAsync(convo, sink, MatrixChat.Refusal(forbidden, bengali), meta, clock, ct);
        }
        if (plan is null || errors.Count > 0)
        {
            var reason = errors.Count > 0 ? string.Join(" ", errors) : "the plan was not one of the four asked for.";
            meta["couldNotPlan"] = reason;
            return await FinishAsync(convo, sink, $"{MatrixChat.CouldNotPlan}: {reason}", meta, clock, ct);
        }
        if (plan.Kind == "need")
        {
            meta["need"] = plan.Need;
            return await FinishAsync(convo, sink,
                (bengali ? "এই ডেটায় এর উত্তর নেই। " : "This data cannot answer that. ") + (plan.Need ?? ""), meta, clock, ct);
        }

        // ---- 2. run ----
        var results = new List<MxAskResult>();
        string? definition = null;
        if (plan.Kind == "queries")
        {
            await sink.StatusAsync(bengali ? $"খুঁজছি… {MatrixFormat(plan.Queries.Count, true)}টি প্রশ্ন চালানো হচ্ছে" : $"Looking… running {plan.Queries.Count} quer{(plan.Queries.Count == 1 ? "y" : "ies")}");
            var label = 0;
            var room = MatrixChat.MaxRowsToModel;
            foreach (var q in plan.Queries)
            {
                try
                {
                    var r = await RunQueryAsync(ctx, q, false, ct);
                    var rows = r.Rows.Select(row => new MxAskRow($"R{++label}",
                        row.Where(kv => !kv.Key.StartsWith('_')).ToDictionary(kv => kv.Key, kv => kv.Value),
                        row.GetValueOrDefault("_cite") as string)).ToList();
                    var shown = Math.Min(rows.Count, room);
                    room -= shown;
                    label -= rows.Count - shown;           // labels only for the rows the model sees
                    rows = rows.Take(shown).ToList();
                    results.Add(new MxAskResult(q, r.Provenance.Plain, r.Provenance.Datasets, r.Provenance.MatchedRows,
                        r.Columns.Select(c => c.Key).ToList(), r.Columns.Where(c => c.Role == "group").Select(c => c.Key).ToList(),
                        r.Columns.Where(c => c.Role == "measure").Select(c => c.Key).ToList(), rows, shown, null));
                }
                catch (MxQueryError e)
                {
                    results.Add(new MxAskResult(q, "", new(), 0, new(), new(), new(), new(), 0, e.Message));
                }
            }
        }
        else if (plan.Kind == "definition" && plan.Definition is { } key && kpis.TryGetValue(key.Trim(), out var kpi))
        {
            meta["kpi"] = kpi.Key;
            definition = $"[KPI: {kpi.NameEn}] {kpi.NameEn} ({kpi.NameBn}): {kpi.Words} Unit: {kpi.Unit}."
                + (kpi.Query is null ? "" : $" Computed by the query {kpi.Query}");
        }
        meta["results"] = results;
        await sink.FrameAsync(new
        {
            type = "context", model, kind = plan.Kind, repaired, kpi = meta.GetValueOrDefault("kpi"), datasetId = scope.Id,
            results = results.Select(r => new
            {
                plain = r.Plain, datasets = r.Datasets, matched = r.Matched, columns = r.Columns, groupBy = r.GroupBy, measures = r.Measures,
                rows = r.Rows.Select(x => new { label = x.Label, values = x.Values, cite = x.Cite }), shown = r.Shown, error = r.Error, query = r.Query,
            }),
            cardLabels,
        });

        // ---- 3. answer ----
        await sink.StatusAsync(bengali ? "উত্তর লিখছি…" : "Writing the answer…");
        var resultsText = MatrixChat.ResultsText(results, definition);
        var capabilities = await ChatEndpoint.ModelCapabilitiesAsync(_chat, ModelPolicy.LocalSource, model, ct);
        bool? think = capabilities.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase) || c.Equals("reasoning", StringComparison.OrdinalIgnoreCase)) ? false : null;
        var args = new ChatArgs(model, new List<ChatTurn>
        {
            new("system", MatrixChat.AnswerSystem(bengali)),
            new("user", MatrixChat.AnswerUser(cardText, resultsText, question)),
        }, Tools: null, Think: think, NumCtx: _models.NumCtx, KeepAlive: null, Temperature: 0);

        var answer = new StringBuilder();
        var formulas = new HrFormulaStream(MatrixSummary.Number);
        long? firstWord = null;
        async Task SendAsync(IEnumerable<HrFormulaPiece> pieces)
        {
            foreach (var piece in pieces)
            {
                if (piece.Text.Length == 0) continue;
                firstWord ??= clock.ElapsedMilliseconds;
                answer.Append(piece.Text);
                await sink.ContentAsync(piece.Text);
                if (piece.Formula is { } f)
                    await sink.FrameAsync(new { type = "formula", offset = f.Offset, length = f.Display.Length, expression = f.Expression, display = f.Display, ok = f.Ok, error = f.Error });
            }
        }
        try
        {
            await foreach (var e in _chat.StreamAsync(ModelPolicy.LocalSource, args, ct))
                if (e.Kind == ChatEventKind.Content) await SendAsync(formulas.Push(e.Text));
            await SendAsync(formulas.Finish());
        }
        catch (ChatSourceException ex)
        {
            await SendAsync(formulas.Finish());
            await sink.FrameAsync(new { type = "error", message = ex.Message });
            answer.Append($"\n\n_{ex.Message}_");
        }

        // ---- 4. the checks ----
        var text = answer.ToString();
        var calculated = formulas.Formulas.Where(f => f.Ok).Select(f => f.Value!.Value);
        var unverified = HrNumberCheck.UnverifiedExact(MatrixSummary.WithoutLabels(text), new[] { resultsText, cardText, question }, calculated).ToList();
        var unknown = MatrixChat.UnknownCitations(text,
            results.SelectMany(r => r.Rows.Select(x => x.Label)).ToHashSet(), cardLabels.Select(l => l.Label).ToHashSet(), KpiLibrary.All);
        var failed = formulas.Formulas.Where(f => !f.Ok).Select(f => f.Expression).ToList();
        await sink.FrameAsync(new { type = "unverified", amounts = unverified, formulas = failed, citations = unknown });

        meta["formulas"] = formulas.Formulas.Select(f => new MxSummaryFormula(f.Offset, f.Display.Length, f.Expression, f.Display, f.Ok, f.Error)).ToList();
        meta["unverified"] = unverified;
        meta["unknownCitations"] = unknown;
        meta["firstWordMs"] = firstWord;
        meta["totalMs"] = clock.ElapsedMilliseconds;
        await _repo.AddMessageAsync(new Message { ConversationId = convo.Id, Role = "assistant", Content = text, MetaJson = JsonSerializer.Serialize(meta, Json) }, ct);
        await sink.FrameAsync(new { type = "done", firstWordMs = firstWord, totalMs = clock.ElapsedMilliseconds });
        _log.LogInformation("Matrix ask: dataset {Dataset}, plan {Kind}{Repaired}, {Queries} queries, {Unverified} unverified, {Unknown} unknown citations, first word {FirstWord} ms.",
            scope.Id, plan.Kind, repaired ? " (repaired)" : "", results.Count, unverified.Count, unknown.Count, firstWord);
        return MxResult<int>.Success(convo.Id);
    }

    /// <summary>
    /// The commonest values of each public dimension field (units, sections, grades, statuses …),
    /// up to eight, for the planner to match the question's words to a field and a value.
    /// Personal and salary fields never; kept for ten minutes, so the plan prompt stays the same.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> SampleValuesAsync(MxQueryContext ctx,
        IReadOnlyList<(MxDataset Dataset, List<MxDsColumn> Columns)> sets, CancellationToken ct)
    {
        var all = new Dictionary<string, List<string>>();
        foreach (var (d, cols) in sets)
        {
            var key = $"matrix:samples:{d.Id}";
            if (!_cache.TryGetValue(key, out Dictionary<string, List<string>>? mine) || mine is null)
            {
                mine = new();
                foreach (var c in cols.Where(c => c.Sensitivity == MxSensitivity.Public && c.Type == MxType.Text
                             && FieldCatalog.Find(c.Field)?.Role == MxRole.Dimension))
                {
                    try
                    {
                        var r = await RunQueryAsync(ctx, JsonSerializer.SerializeToElement(new
                        {
                            dataset = d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), filter = new object[] { new object[] { c.Key, "not_null" } },
                            group_by = new[] { c.Key }, measures = new object[] { new object[] { "count", "*", "n" } }, sort = new object[] { new object[] { "n", "desc" } }, limit = 8,
                        }), false, ct);
                        mine[$"{d.Id}:{c.Key}"] = r.Rows.Select(x => x[c.Key]?.ToString()?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct().ToList();
                    }
                    catch (MxQueryError) { /* a field the person may not read: no samples */ }
                }
                _cache.Set(key, mine, TimeSpan.FromMinutes(10));
            }
            foreach (var kv in mine) all[kv.Key] = kv.Value;
        }
        return all;
    }

    /// <summary>Validation errors of a plan's queries, and the first permission refusal (403), which no repair can fix.</summary>
    private static (List<string> Errors, string? Forbidden) Validate(MxPlan? plan, MxQueryContext ctx)
    {
        if (plan is null) return (new List<string> { "The answer was not a plan of the asked JSON shape." }, null);
        var errors = new List<string>();
        if (plan.Kind == "queries" && plan.Queries.Count == 0) errors.Add("A plan of kind queries needs at least one query.");
        if (plan.Kind == "definition" && KpiLibrary.Find(plan.Definition?.Trim()) is null) errors.Add($"There is no KPI \"{plan.Definition}\".");
        if (plan.Kind != "queries") return (errors, null);
        foreach (var (q, i) in plan.Queries.Select((q, i) => (q, i)))
        {
            try { MatrixQuery.Compile(q, ctx); }
            catch (MxQueryError e) when (e.Status == 403) { return (errors, e.Message); }
            catch (MxQueryError e) { errors.Add($"Query {i + 1}: {e.Message}"); }
        }
        return (errors, null);
    }

    /// <summary>An answer that needs no model: written whole, saved, and the stream closed.</summary>
    private async Task<MxResult<int>> FinishAsync(Conversation convo, IMxAskSink sink, string text, Dictionary<string, object?> meta, Stopwatch clock, CancellationToken ct)
    {
        await sink.ContentAsync(text);
        await sink.FrameAsync(new { type = "unverified", amounts = Array.Empty<decimal>(), formulas = Array.Empty<string>(), citations = Array.Empty<string>() });
        meta["firstWordMs"] = clock.ElapsedMilliseconds;
        meta["totalMs"] = clock.ElapsedMilliseconds;
        await _repo.AddMessageAsync(new Message { ConversationId = convo.Id, Role = "assistant", Content = text, MetaJson = JsonSerializer.Serialize(meta, Json) }, ct);
        await sink.FrameAsync(new { type = "done", firstWordMs = clock.ElapsedMilliseconds, totalMs = clock.ElapsedMilliseconds });
        return MxResult<int>.Success(convo.Id);
    }

    private static string MatrixFormat(int n, bool bengali) =>
        bengali ? string.Concat(n.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => (char)('০' + (c - '0')))) : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ───────────────────────── conversations ─────────────────────────

    public async Task<List<MxConversationDto>> ListConversationsAsync(User user, int? datasetId, CancellationToken ct = default) =>
        (await _repo.ListConversationsAsync(user.Id, datasetId is int id ? ScopeOf(id) : null, ct))
            .Select(c => new MxConversationDto(c.Id, c.Title, c.Scope, c.CreatedAt, c.Messages.Count)).ToList();

    public async Task<MxResult<List<MxMessageDto>>> ConversationAsync(User user, int id, CancellationToken ct = default)
    {
        if (await _repo.GetConversationAsync(id, user.Id, ct) is null) return MxResult<List<MxMessageDto>>.Fail(MxOutcome.NotFound, "No such conversation.");
        return MxResult<List<MxMessageDto>>.Success((await _repo.MessagesAsync(id, ct)).Select(m => new MxMessageDto(m.Id, m.Role, m.Content,
            m.MetaJson is null ? null : JsonDocument.Parse(m.MetaJson).RootElement.Clone(), m.CreatedAt)).ToList());
    }

    public async Task<MxResult<bool>> DeleteConversationAsync(User user, int id, CancellationToken ct = default)
    {
        var c = await _repo.GetConversationAsync(id, user.Id, ct);
        if (c is null) return MxResult<bool>.Fail(MxOutcome.NotFound, "No such conversation.");
        await _repo.DeleteConversationAsync(c, ct);
        return MxResult<bool>.Success(true);
    }
}
