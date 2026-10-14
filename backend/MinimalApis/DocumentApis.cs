using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.DTOs;
using RabbitIC.Api.AuthHelper;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Retrieval;
using UglyToad.PdfPig;

namespace RabbitIC.Api.MinimalApis
{
    public static class DocumentApis
    {
        // Chunking: ~800 chars with ~100 overlap keeps a PO line item intact inside one chunk.
        private const int ChunkSize = 800;
        private const int ChunkOverlap = 100;
        private const long MaxFileBytes = 20 * 1024 * 1024; // 20 MB

        // Originals live beside rabbitic.db; the relative path resolves against the
        // same working directory the connection string does.
        private static readonly string UploadsDir = Path.Combine("data", "uploads");

        // Same camelCase contract the minimal-API responses use, so the cached
        // ExtractedJson round-trips into the review UI unchanged.
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

        /// <summary>A contiguous piece of extracted text plus where it came from (for citations).</summary>
        private sealed record Section(string SourceRef, string Text);

        public static IEndpointRouteBuilder MapDocumentApis(this IEndpointRouteBuilder app)
        {
            // Upload a PO / order file: extract text, chunk, embed, index. Inline — POs are small.
            app.MapPost("/documents", async (
                HttpContext ctx, AppDbContext db, EmbeddingService embedder, VectorIndex index,
                CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                if (!ctx.Request.HasFormContentType)
                    return Results.BadRequest(new { error = "Send multipart/form-data with a 'file' field." });

                var form = await ctx.Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file");
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "A non-empty 'file' field is required." });
                if (file.Length > MaxFileBytes)
                    return Results.BadRequest(new { error = "File exceeds the 20 MB limit." });

                var buyer = form["buyer"].ToString().Trim();
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (ext is not (".pdf" or ".xlsx" or ".txt" or ".csv"))
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

                var doc = new Document
                {
                    OwnerUserId = user.Id,
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    Buyer = string.IsNullOrEmpty(buyer) ? null : buyer,
                    Status = "processing",
                };
                db.Documents.Add(doc);
                await db.SaveChangesAsync(ct);

                // ---- persist the original ----
                // The stored file is the source of truth for deterministic re-parsing
                // and for the review screen's side-by-side PDF view. Saved before text
                // extraction so a failed ingest still leaves the file recoverable.
                Directory.CreateDirectory(UploadsDir);
                var storedPath = Path.Combine(UploadsDir, $"{doc.Id}{ext}");
                await using (var target = File.Create(storedPath))
                await using (var source = file.OpenReadStream())
                    await source.CopyToAsync(target, ct);
                doc.FilePath = storedPath;
                await db.SaveChangesAsync(ct);

                try
                {
                    // ---- extract ----
                    List<Section> sections;
                    await using (var stream = File.OpenRead(storedPath))
                    {
                        sections = ext switch
                        {
                            ".pdf" => ExtractPdf(stream, file.FileName),
                            ".xlsx" => ExtractXlsx(stream, file.FileName),
                            _ => await ExtractPlainText(stream, file.FileName, ct),
                        };
                    }
                    if (sections.Count == 0)
                        throw new InvalidOperationException("No text could be extracted from the file.");

                    // ---- chunk + embed + store ----
                    var chunkCount = 0;
                    foreach (var section in sections)
                    {
                        foreach (var piece in ChunkText(section.Text))
                        {
                            var vector = await embedder.EmbedAsync(piece, ct);
                            var chunk = new EmbeddingChunk
                            {
                                DocumentId = doc.Id,
                                OwnerUserId = user.Id,
                                Buyer = doc.Buyer,
                                SourceRef = section.SourceRef,
                                Text = piece,
                                Vector = VectorOps.ToBlob(vector),
                            };
                            db.EmbeddingChunks.Add(chunk);
                            await db.SaveChangesAsync(ct);   // per chunk, so chunk.Id is set before indexing
                            index.Add(chunk);
                            chunkCount++;
                        }
                    }

                    doc.Status = "ready";
                    await db.SaveChangesAsync(ct);

                    return Results.Ok(new
                    {
                        id = doc.Id,
                        fileName = doc.FileName,
                        buyer = doc.Buyer,
                        status = doc.Status,
                        chunks = chunkCount,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    doc.Status = "failed";
                    doc.Error = Authorization.Truncate(ex.Message, 500);
                    await db.SaveChangesAsync(CancellationToken.None);
                    return Results.Problem($"Ingestion failed: {doc.Error}", statusCode: 500);
                }
            });

            // List the current user's documents (newest first).
            app.MapGet("/documents", async (HttpContext ctx, AppDbContext db) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                return Results.Ok(await db.Documents
                    .Where(d => d.OwnerUserId == user.Id)
                    .OrderByDescending(d => d.CreatedAt)
                    .Select(d => new
                    {
                        id = d.Id,
                        fileName = d.FileName,
                        buyer = d.Buyer,
                        status = d.Status,
                        error = d.Error,
                        createdAt = d.CreatedAt,
                        chunks = d.Chunks.Count,
                        conversationId = d.ConversationId,
                        extractedAt = d.ExtractedAt,
                        hasExtraction = d.ExtractedJson != null,
                    })
                    .ToListAsync());
            });

            // Stream the stored original (review screen source pane). Same ownership
            // rule as everything else: not yours → 404, indistinguishable from absent.
            app.MapGet("/documents/{id:int}/file", async (int id, HttpContext ctx, AppDbContext db) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OwnerUserId == user.Id);
                if (doc is null) return Results.NotFound();
                if (doc.FilePath is null || !File.Exists(doc.FilePath))
                    return Results.Problem(
                        "No stored file — this document was uploaded before originals were kept. Re-upload it to review.",
                        statusCode: 410);

                var contentType = Path.GetExtension(doc.FilePath).ToLowerInvariant() switch
                {
                    ".pdf" => "application/pdf",
                    ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    ".csv" => "text/csv",
                    _ => "text/plain",
                };
                ctx.Response.Headers.ContentDisposition = $"inline; filename=\"{doc.FileName}\"";
                return Results.File(File.OpenRead(doc.FilePath), contentType);
            });

            // The stored extraction for a document — lets the UI re-display without re-running the model.
            app.MapGet("/documents/{id:int}/extract", async (int id, HttpContext ctx, AppDbContext db) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OwnerUserId == user.Id);
                if (doc is null) return Results.NotFound();
                if (doc.ExtractedJson is null) return Results.NoContent();

                using var parsed = JsonDocument.Parse(doc.ExtractedJson);
                return Results.Ok(new
                {
                    documentId = doc.Id,
                    fileName = doc.FileName,
                    buyer = doc.Buyer,
                    conversationId = doc.ConversationId,
                    extractedAt = doc.ExtractedAt,
                    extracted = parsed.RootElement.Clone(),
                    sources = Array.Empty<string>(),
                });
            });

            // Delete a document: rows go via cascade, then evict from the in-memory index.
            app.MapDelete("/documents/{id:int}", async (
                int id, HttpContext ctx, AppDbContext db, VectorIndex index) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OwnerUserId == user.Id);
                if (doc is null) return Results.NotFound();

                db.Documents.Remove(doc);
                await db.SaveChangesAsync();
                index.RemoveDocument(id);

                // Stored original goes with the row. Best-effort: a locked file
                // shouldn't fail the delete the user asked for.
                if (doc.FilePath is not null)
                    try { File.Delete(doc.FilePath); } catch (IOException) { }

                return Results.NoContent();
            });

            // Extract structured PO data: detect the buyer template, prefer the
            // deterministic parser, fall back to the model — and gate everything
            // through the same arithmetic checks. Model output is never "ok".
            app.MapPost("/documents/{id:int}/extract", async (
                int id, ExtractRequest req, HttpContext ctx, AppDbContext db,
                Extraction.ExtractionDispatcher dispatcher, int? buyerProfileId,
                RabbitIC.Api.Rbac.IModelPolicy modelPolicy,
                CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                // No model named means the policy's default for this role, falling
                // back to the existing config chain when the role is unrestricted.
                var extractPolicy = await modelPolicy.CheckAsync(user, req.Model, "local", ct);
                if (!string.IsNullOrWhiteSpace(req.Model) && !extractPolicy.IsAllowed)
                    return Results.Json(new { error = extractPolicy.Message }, statusCode: 403);

                var effectiveModel = string.IsNullOrWhiteSpace(req.Model)
                    ? await modelPolicy.DefaultForAsync(user, ct)
                    : req.Model;

                if (!string.IsNullOrWhiteSpace(req.Model) && IsEmbeddingModel(req.Model))
                    return Results.BadRequest(new { error = $"'{req.Model}' is an embedding model — pick a chat model." });

                var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OwnerUserId == user.Id, ct);
                if (doc is null) return Results.NotFound();

                // Legacy documents have no stored file; their indexed text is the
                // only input the fallback can use.
                async Task<string> ChunkText()
                {
                    var chunks = await db.EmbeddingChunks
                        .Where(c => c.DocumentId == id)
                        .OrderBy(c => c.Id)
                        .Select(c => c.Text)
                        .ToListAsync(ct);
                    return string.Join("\n", chunks);
                }

                var outcome = await dispatcher.DispatchAsync(doc, ChunkText, buyerProfileId, effectiveModel, ct);

                var resultJson = outcome.Result is null
                    ? ""
                    : JsonSerializer.Serialize(outcome.Result, WebJson);

                // Cache the machine output for re-display; confirm overwrites this
                // cache (not the audit row) with the human-approved version.
                if (outcome.Result is not null)
                {
                    doc.ExtractedJson = resultJson;
                    doc.ExtractedAt = DateTime.Now;
                }

                var run = new ExtractionRun
                {
                    DocumentId = doc.Id,
                    OwnerUserId = user.Id,
                    BuyerProfileId = buyerProfileId ?? outcome.BuyerProfileId,
                    ParserKey = outcome.ParserKey,
                    Status = outcome.Status,
                    ResultJson = resultJson,
                    FailedChecks = string.Join("; ", outcome.FailedChecks.Select(c => c.Check)),
                };
                db.ExtractionRuns.Add(run);
                await db.SaveChangesAsync(ct);

                var payload = new
                {
                    runId = run.Id,
                    documentId = doc.Id,
                    fileName = doc.FileName,
                    status = outcome.Status,
                    parser = outcome.ParserKey,
                    buyer = outcome.BuyerName,
                    detectConfidence = Math.Round(outcome.DetectConfidence, 2),
                    note = outcome.Note,
                    error = outcome.Error,
                    failedChecks = outcome.FailedChecks.Select(c => new { c.Check, c.Detail }),
                    checks = outcome.Result?.Validation.Select(c => new { c.Check, c.Passed, c.Detail }),
                    data = outcome.Result,
                };
                return outcome.Status == "failed"
                    ? Results.Json(payload, statusCode: 422)
                    : Results.Ok(payload);
            });

            // Human confirmation of an extraction. The machine output stays on the
            // run untouched; the approved payload and the reviewer's edits are
            // stored beside it so machine vs human is always auditable.
            app.MapPost("/documents/{id:int}/extraction/confirm", async (
                int id, ConfirmExtractionRequest req, HttpContext ctx, AppDbContext db,
                CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();

                var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OwnerUserId == user.Id, ct);
                if (doc is null) return Results.NotFound();
                if (req.ResultJson.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest(new { error = "resultJson must be the edited extraction object." });

                var run = await db.ExtractionRuns
                    .Where(r => r.DocumentId == id)
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync(ct);
                if (run is null)
                    return Results.BadRequest(new { error = "No extraction run to confirm — extract first." });

                run.ConfirmedJson = JsonSerializer.Serialize(req.ResultJson);
                run.ConfirmedByUserId = user.Id;
                run.ConfirmedAt = DateTime.Now;
                run.EditsJson = req.Edits.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? "[]"
                    : JsonSerializer.Serialize(req.Edits);

                // The display cache becomes the approved version.
                doc.ExtractedJson = run.ConfirmedJson;
                doc.ExtractedAt = run.ConfirmedAt;
                await db.SaveChangesAsync(ct);

                return Results.Ok(new
                {
                    runId = run.Id,
                    documentId = doc.Id,
                    status = run.Status,
                    confirmedAt = run.ConfirmedAt,
                    confirmedBy = user.Username,
                    edits = req.Edits,
                });
            });

            // Workspace chat: RAG chat, streamed. Separate from /chat on purpose — the model only
            // sees retrieved chunks, and turns are saved to the pinned document's own conversation
            // so the workspace thread (including extractions) survives a reload.
            app.MapPost("/documents/workspace-chat", async (
                WorkspaceChatRequest req, HttpContext ctx, AppDbContext db,
                EmbeddingService embedder, VectorIndex index, IHttpClientFactory http,
                RabbitIC.Api.Services.IContextSourceService contextSources,
                RabbitIC.Api.Rbac.IModelPolicy modelPolicy) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) { ctx.Response.StatusCode = 401; return; }
                // This endpoint answers in plain text (F5), so the refusal does too —
                // the same message, in the shape this caller already parses.
                var wsPolicy = await modelPolicy.CheckAsync(user, req.Model, "local", ctx.RequestAborted);
                if (!wsPolicy.IsAllowed)
                { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsync(wsPolicy.Message); return; }

                if (string.IsNullOrWhiteSpace(req.Message) || string.IsNullOrWhiteSpace(req.Model))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("model and message are required."); return; }
                if (IsEmbeddingModel(req.Model))
                { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync($"'{req.Model}' is an embedding model — pick a chat model."); return; }

                var ct = ctx.RequestAborted;

                // Context: whole document when pinned; otherwise top-K vector search over my chunks.
                List<(string? SourceRef, string Text)> contextChunks;
                Document? pinned = null;
                if (req.DocumentId is int docId)
                {
                    pinned = await db.Documents.FirstOrDefaultAsync(d => d.Id == docId && d.OwnerUserId == user.Id, ct);
                    if (pinned is null) { ctx.Response.StatusCode = 404; return; }

                    contextChunks = (await db.EmbeddingChunks
                        .Where(c => c.DocumentId == docId)
                        .OrderBy(c => c.Id)
                        .Select(c => new { c.SourceRef, c.Text })
                        .ToListAsync(ct))
                        .Select(c => (c.SourceRef, c.Text)).ToList();
                }
                else
                {
                    var queryVector = await embedder.EmbedAsync(req.Message, ct);
                    contextChunks = index.Search(queryVector, user.Id, topK: 6)
                        .Select(h => (h.SourceRef, h.Text)).ToList();
                }

                // Buyer profiles are context sources with Kind="buyer" — assembled by
                // the same service and the same method the chat tab uses, so there is
                // one implementation of "how context becomes a system prompt".
                var persona = await contextSources.BuildSystemPromptAsync(
                    user.Id,
                    activeIds: null,   // no per-turn toggles here; each profile's own switch decides
                    basePersona:
                        "You are a purchase-order assistant for a garment merchandiser. Answer using ONLY the document " +
                        "excerpts below. Cite the [source] you used. If the answer is not in the excerpts, say so.",
                    kind: RabbitIC.Api.Models.ContextSource.KindBuyer,
                    ct: ct);

                var sb = new StringBuilder(persona).Append("\n\n");
                if (contextChunks.Count == 0)
                    sb.Append("(No document excerpts matched — tell the user to upload or pin a document.)\n");
                foreach (var (sourceRef, text) in contextChunks)
                    sb.Append($"[{sourceRef}]\n{text}\n\n");

                var payload = new
                {
                    model = req.Model,
                    messages = new[]
                    {
                        new { role = "system", content = sb.ToString() },
                        new { role = "user", content = req.Message },
                    },
                    stream = true,
                };

                var client = http.CreateClient("ollama");
                using var upstream = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
                { Content = JsonContent.Create(payload) };

                HttpResponseMessage resp;
                try
                {
                    resp = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                }
                catch (HttpRequestException ex)
                {
                    ctx.Response.StatusCode = 502;
                    await ctx.Response.WriteAsync("Ollama error: " + ex.Message);
                    return;
                }

                // Pinned documents get a persisted thread; unpinned questions stay ephemeral.
                Conversation? convo = null;
                if (pinned is not null)
                {
                    convo = pinned.ConversationId is int existingId
                        ? await db.Conversations.FirstOrDefaultAsync(c => c.Id == existingId && c.UserId == user.Id, ct)
                        : null;
                    if (convo is null)
                    {
                        convo = new Conversation
                        {
                            UserId = user.Id,
                            Title = Authorization.Truncate($"Workspace · {pinned.FileName}", 40),
                        };
                        db.Conversations.Add(convo);
                        await db.SaveChangesAsync(ct);
                        pinned.ConversationId = convo.Id;
                    }
                    db.Messages.Add(new Message { ConversationId = convo.Id, Role = "user", Content = req.Message });
                    await db.SaveChangesAsync(ct);

                    ctx.Response.Headers["X-Conversation-Id"] = convo.Id.ToString();
                }

                ctx.Response.ContentType = "text/plain; charset=utf-8";
                var full = new StringBuilder();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string? token = null;
                    try
                    {
                        using var delta = JsonDocument.Parse(line);
                        if (delta.RootElement.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var content))
                            token = content.GetString();
                    }
                    catch (JsonException) { continue; }

                    if (!string.IsNullOrEmpty(token))
                    {
                        full.Append(token);
                        await ctx.Response.WriteAsync(token, ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }

                if (convo is not null && full.Length > 0)
                {
                    db.Messages.Add(new Message
                    { ConversationId = convo.Id, Role = "assistant", Content = full.ToString() });
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            });

            return app;
        }

        // Embedding models sit in the same /api/tags list as chat models but reject
        // /api/chat ("does not support chat"), so refuse them with a clear message.
        private static bool IsEmbeddingModel(string model) =>
            model.Contains("embed", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("bge-", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("gte-", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("e5-", StringComparison.OrdinalIgnoreCase);

        // ───────────────────────── text extraction ─────────────────────────

        private static List<Section> ExtractPdf(Stream stream, string fileName)
        {
            var sections = new List<Section>();
            using var pdf = PdfDocument.Open(stream);
            foreach (var page in pdf.GetPages())
            {
                var text = page.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                sections.Add(new Section($"{fileName} p{page.Number}", text));
            }
            return sections;
        }

        // Each sheet becomes "Header: value | Header: value" rows — embeds far better than raw cells.
        private static List<Section> ExtractXlsx(Stream stream, string fileName)
        {
            var sections = new List<Section>();
            using var workbook = new XLWorkbook(stream);
            foreach (var sheet in workbook.Worksheets)
            {
                var used = sheet.RangeUsed();
                if (used is null) continue;

                var rows = used.RowsUsed().ToList();
                if (rows.Count == 0) continue;

                var headers = rows[0].Cells().Select(c => c.GetString().Trim()).ToList();
                var lines = new List<string>();
                foreach (var row in rows.Skip(1))
                {
                    var cells = row.Cells(1, headers.Count).Select(c => c.GetString().Trim()).ToList();
                    var pairs = headers.Zip(cells, (h, v) => (h, v))
                        .Where(p => p.v.Length > 0)
                        .Select(p => p.h.Length > 0 ? $"{p.h}: {p.v}" : p.v);
                    var line = string.Join(" | ", pairs);
                    if (line.Length > 0) lines.Add(line);
                }
                if (lines.Count == 0) continue;
                sections.Add(new Section($"{fileName} [{sheet.Name}]", string.Join("\n", lines)));
            }
            return sections;
        }

        private static async Task<List<Section>> ExtractPlainText(Stream stream, string fileName, CancellationToken ct)
        {
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(ct);
            return string.IsNullOrWhiteSpace(text)
                ? new List<Section>()
                : new List<Section> { new(fileName, text) };
        }

        // ───────────────────────── chunking ─────────────────────────

        /// <summary>Split on newline boundaries where possible, ~800 chars with ~100 overlap.</summary>
        private static IEnumerable<string> ChunkText(string text)
        {
            text = text.Trim();
            if (text.Length <= ChunkSize) { yield return text; yield break; }

            var start = 0;
            while (start < text.Length)
            {
                var length = Math.Min(ChunkSize, text.Length - start);
                var end = start + length;

                // Prefer to cut at the last newline (then space) inside the window, not mid-word.
                if (end < text.Length)
                {
                    var cut = text.LastIndexOf('\n', end - 1, length);
                    if (cut <= start) cut = text.LastIndexOf(' ', end - 1, length);
                    if (cut > start) end = cut;
                }

                var piece = text[start..end].Trim();
                if (piece.Length > 0) yield return piece;

                if (end >= text.Length) yield break;
                start = Math.Max(end - ChunkOverlap, start + 1);
            }
        }
    }
}
