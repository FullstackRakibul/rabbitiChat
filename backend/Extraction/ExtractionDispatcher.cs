// Detect → parse → validate → gate.
//
// The ordering is the point. A deterministic parser runs FIRST whenever a buyer
// profile matches and a parser is registered for it, because flattened text
// destroys table geometry — adjacent cells arrive concatenated, and no prompt
// recovers a space that was never in the input. The LLM path covers unknown
// templates and legacy documents whose original file was never stored, and its
// output must clear the same arithmetic checks.
//
// The rule that matters most: model output is never "ok". Deterministic parsing
// plus passing checks is the only path to a clean status; everything the model
// produces lands in needs_review for a human, even when the numbers reconcile.

using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using UglyToad.PdfPig;

namespace RabbitIC.Api.Extraction;

public sealed record DetectProbe(
    string? BuyerName, int? ProfileId, string? ParserKey, double Confidence, bool ParserRegistered);

public sealed record DispatchOutcome(
    string Status,                       // "ok" | "needs_review" | "failed"
    string ParserKey,
    string? BuyerName,
    int? BuyerProfileId,
    double DetectConfidence,
    ExtractionResult? Result,
    IReadOnlyList<ValidationCheck> FailedChecks,
    string? Note,
    string? Error,
    // ---- PO Studio: how the run was made (§3.5) ----
    // Null on the deterministic path, which uses no model and no prompt.
    string? Model = null,
    int? ProfileVersion = null,
    string? PromptHash = null,
    bool RepairUsed = false,
    ExtractionResult? FirstResult = null,
    int? PromptTokens = null,
    int? OutputTokens = null,
    int? DurationMs = null);

public sealed class ExtractionDispatcher(
    AppDbContext db, IEnumerable<IPoParser> parsers, LlmPoFallback fallback)
{
    /// <param name="chunkText">
    /// Loader for the document's indexed text — the probe and LLM input for
    /// legacy documents that have no stored file.
    /// </param>
    public async Task<DispatchOutcome> DispatchAsync(
        Document doc, Func<Task<string>> chunkText, int? forcedProfileId,
        string? model, CancellationToken ct = default)
    {
        var hasFile = doc.FilePath is not null && File.Exists(doc.FilePath);
        var isPdf = hasFile && Path.GetExtension(doc.FilePath!)
            .Equals(".pdf", StringComparison.OrdinalIgnoreCase);

        // ---- probe for detection ----
        string probeText; string? producer = null;
        if (isPdf)
        {
            try
            {
                (probeText, producer) = ReadPdfProbe(doc.FilePath!);
            }
            catch (Exception ex)
            {
                return new("failed", "none", null, null, 0, null,
                           Array.Empty<ValidationCheck>(), null,
                           $"Could not read the stored PDF: {ex.Message}");
            }
        }
        else
        {
            var text = await chunkText();
            probeText = text.Length > 4000 ? text[..4000] : text;
        }

        var (profile, confidence) = await ResolveProfileAsync(probeText, producer, forcedProfileId, ct);

        // ---- deterministic path ----
        var parser = profile is null ? null
            : parsers.FirstOrDefault(p => p.Key.Equals(profile.ParserKey, StringComparison.OrdinalIgnoreCase));

        string? note = null;
        if (profile is not null && parser is null && profile.ParserKey != "GenericLlm")
            note = $"Profile '{profile.Name}' names parser '{profile.ParserKey}', which is not registered — used the model instead.";
        if (profile is not null && parser is not null && !isPdf)
        {
            note = hasFile
                ? $"'{profile.Name}' matched but the stored file is not a PDF — used the model instead."
                : $"'{profile.Name}' matched but no original file is stored (uploaded before files were kept) — used the model on indexed text.";
            parser = null;
        }

        if (parser is not null)
        {
            try
            {
                await using var fs = File.OpenRead(doc.FilePath!);
                var result = parser.Parse(fs);
                ExtractionValidator.ApplyDerivedAndValidation(result);
                var failed = result.Validation.Where(v => !v.Passed).ToList();
                return new(failed.Count == 0 ? "ok" : "needs_review",
                           parser.Key, profile!.Name, profile.Id, confidence, result, failed, null, null);
            }
            catch (Exception ex)
            {
                // Layout drifted from the template — fall back to the model
                // rather than failing outright, but say so.
                note = $"Template parse failed ({ex.Message}); used the model instead.";
            }
        }

        // ---- fallback path ----
        // The profile carries its own rules, glossary and worked examples into the
        // prompt, and the validator is handed to the fallback so its repair round
        // can judge the second answer by the SAME checks the gate below applies.
        var llmInput = isPdf ? ReadAllPdfText(doc.FilePath!) : await chunkText();
        var examples = await PoStudio.LoadExamplesAsync(db, profile, DocumentTextAsync, ct);
        var promptProfile = PoStudio.ToPromptProfile(profile, examples);

        static IReadOnlyList<string> FailedCheckDetails(ExtractionResult candidate)
        {
            var copy = Clone(candidate);
            ExtractionValidator.ApplyDerivedAndValidation(copy);
            return copy.Validation.Where(v => !v.Passed).Select(v => v.Detail).ToList();
        }

        var chosenModel = string.IsNullOrWhiteSpace(model) ? NullIfBlank(profile?.ExtractionModel) : model;
        var llm = await fallback.ExtractAsync(llmInput, promptProfile, chosenModel, FailedCheckDetails, ct);

        if (llm.Result is null)
            return new("failed", "GenericLlm", profile?.Name, profile?.Id, confidence, null,
                       Array.Empty<ValidationCheck>(), note,
                       llm.Error ?? "The model did not return usable JSON.",
                       llm.Model, llm.ProfileVersion, llm.PromptHash, false, null,
                       llm.PromptTokens, llm.OutputTokens, llm.DurationMs);

        ExtractionValidator.ApplyDerivedAndValidation(llm.Result);
        var llmFailed = llm.Result.Validation.Where(v => !v.Passed).ToList();

        var firstResult = llm.FirstResult;
        if (firstResult is not null && !ReferenceEquals(firstResult, llm.Result))
            ExtractionValidator.ApplyDerivedAndValidation(firstResult);

        // Never "ok" from the model — see the header comment.
        return new("needs_review", "GenericLlm", profile?.Name, profile?.Id,
                   confidence, llm.Result, llmFailed, note, null,
                   llm.Model, llm.ProfileVersion, llm.PromptHash, llm.RepairUsed,
                   firstResult, llm.PromptTokens, llm.OutputTokens, llm.DurationMs);

        async Task<string> DocumentTextAsync(int documentId)
        {
            var exampleDoc = await db.Documents
                .FirstOrDefaultAsync(d => d.Id == documentId, ct);
            if (exampleDoc?.FilePath is not null && File.Exists(exampleDoc.FilePath)
                && Path.GetExtension(exampleDoc.FilePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return ReadAllPdfText(exampleDoc.FilePath);

            return string.Join("\n", await db.EmbeddingChunks
                .Where(c => c.DocumentId == documentId).OrderBy(c => c.Id)
                .Select(c => c.Text).ToListAsync(ct));
        }
    }

    /// <summary>Detection dry-run for tuning a DetectPattern — no parsing, no model call.</summary>
    public async Task<DetectProbe> ProbeAsync(Document doc, Func<Task<string>> chunkText, CancellationToken ct = default)
    {
        var isPdf = doc.FilePath is not null && File.Exists(doc.FilePath)
                    && Path.GetExtension(doc.FilePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

        string probeText; string? producer = null;
        if (isPdf)
        {
            (probeText, producer) = ReadPdfProbe(doc.FilePath!);
        }
        else
        {
            var text = await chunkText();
            probeText = text.Length > 4000 ? text[..4000] : text;
        }

        var (profile, confidence) = await ResolveProfileAsync(probeText, producer, null, ct);
        var registered = profile is not null
            && parsers.Any(p => p.Key.Equals(profile.ParserKey, StringComparison.OrdinalIgnoreCase));
        return new(profile?.Name, profile?.Id, profile?.ParserKey, confidence, registered);
    }

    private async Task<(BuyerProfile? Profile, double Confidence)> ResolveProfileAsync(
        string probeText, string? producer, int? forcedProfileId, CancellationToken ct)
    {
        if (forcedProfileId is int id)
        {
            var forced = await db.BuyerProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (forced is not null) return (forced, 1.0);
        }

        var haystack = (probeText + "\n" + (producer ?? "")).ToLowerInvariant();
        BuyerProfile? best = null; double bestScore = 0;

        foreach (var p in await db.BuyerProfiles.Where(p => p.IsActive).ToListAsync(ct))
        {
            var markers = p.DetectPattern
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (markers.Length == 0) continue;

            int hits = markers.Count(m => haystack.Contains(m.ToLowerInvariant()));
            double score = (double)hits / markers.Length;
            if (score > bestScore) { bestScore = score; best = p; }
        }

        // A majority of markers is required before a template is trusted — a
        // partial match falls through to the model rather than misparsing.
        return bestScore >= 0.5 ? (best, bestScore) : (null, bestScore);
    }

    private static (string FirstPage, string? Producer) ReadPdfProbe(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var producer = pdf.Information?.Producer;
        var page = pdf.NumberOfPages > 0 ? pdf.GetPage(1).Text : "";
        return (page, producer);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// A deep copy via the serializer, so running the validator to COUNT a
    /// candidate's failures never mutates the candidate the caller still holds.
    /// </summary>
    private static ExtractionResult Clone(ExtractionResult r) =>
        System.Text.Json.JsonSerializer.Deserialize<ExtractionResult>(
            System.Text.Json.JsonSerializer.Serialize(r))!;

    private static string ReadAllPdfText(string path)
    {
        var sb = new System.Text.StringBuilder();
        using var pdf = PdfDocument.Open(path);
        foreach (var page in pdf.GetPages()) sb.AppendLine(page.Text);
        return sb.ToString();
    }
}
