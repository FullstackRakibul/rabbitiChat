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

                try
                {
                    // ---- extract ----
                    List<Section> sections;
                    await using (var stream = file.OpenReadStream())
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
                return Results.NoContent();
            });

            // Extract structured PO JSON from an ingested document. Uses ALL of the
            // document's chunks (not retrieval) so no line item is missed, and Ollama's
            // format:"json" so the output is always valid JSON.
            app.MapPost("/documents/{id:int}/extract", async (
                int id, ExtractRequest req, HttpContext ctx, AppDbContext db,
                IHttpClientFactory http, CancellationToken ct) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) return Results.Unauthorized();
                if (string.IsNullOrWhiteSpace(req.Model))
                    return Results.BadRequest(new { error = "model is required." });
                if (IsEmbeddingModel(req.Model))
                    return Results.BadRequest(new { error = $"'{req.Model}' is an embedding model — pick a chat model." });

                var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id && d.OwnerUserId == user.Id, ct);
                if (doc is null) return Results.NotFound();

                var chunks = await db.EmbeddingChunks
                    .Where(c => c.DocumentId == id)
                    .OrderBy(c => c.Id)
                    .Select(c => new { c.SourceRef, c.Text })
                    .ToListAsync(ct);
                if (chunks.Count == 0)
                    return Results.BadRequest(new { error = "Document has no indexed content." });

                var docText = new StringBuilder();
                foreach (var c in chunks)
                    docText.AppendLine($"[{c.SourceRef}]\n{c.Text}\n");

                var system =
                    "You extract purchase-order data from garment/retail PO documents. " +
                    "Output ONLY a JSON object matching this schema (use null for missing fields, numbers as numbers, dates as YYYY-MM-DD):\n" +
                    """
                    {
                      "po_number": "string", "buyer": "string", "po_type": "string",
                      "po_date": "YYYY-MM-DD", "cancel_date": "YYYY-MM-DD",
                      "ship_window": { "start": "YYYY-MM-DD", "end": "YYYY-MM-DD" },
                      "exit_factory_date": "YYYY-MM-DD",
                      "vendor": "string", "manufacturing_plant": "string",
                      "style_number": "string", "product_description": "string",
                      "total_quantity": 0, "po_total": 0.0, "currency": "string",
                      "line_items": [ { "line": 0, "sku": "string", "upc": "string", "color": "string",
                                        "size": "string", "unit_cost": 0.0, "retail_price": 0.0,
                                        "qty": 0, "uom": "string" } ]
                    }
                    """ +
                    "\nRules: a prepack/assortment header line summarises the size-breakdown lines below it — " +
                    "list the size-breakdown lines as line_items and do not double-count quantities. " +
                    "Unit price pairs like '6.29 34.99' are unit_cost then retail_price.";

                if (!string.IsNullOrWhiteSpace(doc.Buyer))
                    system += $"\nThe buyer profile for this document is: {doc.Buyer}.";

                var payload = new
                {
                    model = req.Model,
                    messages = new[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = "Extract the purchase order data from this document:\n\n" + docText },
                    },
                    stream = false,
                    format = "json",
                    options = new { temperature = 0 },
                };

                var client = http.CreateClient("ollama");
                var resp = await client.PostAsJsonAsync("/api/chat", payload, ct);
                if (!resp.IsSuccessStatusCode)
                    return Results.Problem($"Ollama error: {await resp.Content.ReadAsStringAsync(ct)}", statusCode: 502);

                using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var content = body.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";

                JsonDocument parsed;
                try
                {
                    parsed = JsonDocument.Parse(content);
                }
                catch (JsonException)
                {
                    return Results.Problem("Model returned invalid JSON. Try again or use a stronger model.", statusCode: 502);
                }

                using (parsed)
                {
                    // Normalise once so what we store and what we return are identical.
                    var pretty = JsonSerializer.Serialize(parsed.RootElement,
                        new JsonSerializerOptions { WriteIndented = true });
                    var sources = chunks.Select(c => c.SourceRef).Distinct().ToList();

                    // Persist on the document so the UI can re-display without re-running the model.
                    doc.ExtractedJson = pretty;
                    doc.ExtractedAt = DateTime.UtcNow;

                    // Append to the document's workspace conversation, creating it on first extract.
                    var convo = doc.ConversationId is int existingId
                        ? await db.Conversations.FirstOrDefaultAsync(c => c.Id == existingId && c.UserId == user.Id, ct)
                        : null;
                    if (convo is null)
                    {
                        convo = new Conversation
                        {
                            UserId = user.Id,
                            Title = Authorization.Truncate($"Workspace · {doc.FileName}", 40),
                        };
                        db.Conversations.Add(convo);
                        await db.SaveChangesAsync(ct);
                        doc.ConversationId = convo.Id;
                    }

                    db.Messages.Add(new Message
                    {
                        ConversationId = convo.Id,
                        Role = "user",
                        Content = $"Extract structured JSON from {doc.FileName}" +
                                  (string.IsNullOrWhiteSpace(doc.Buyer) ? "" : $" (buyer profile: {doc.Buyer})"),
                    });
                    db.Messages.Add(new Message
                    {
                        ConversationId = convo.Id,
                        Role = "assistant",
                        Content = $"```json\n{pretty}\n```\n\nSources: {string.Join(", ", sources)}",
                    });
                    await db.SaveChangesAsync(ct);

                    return Results.Ok(new
                    {
                        documentId = doc.Id,
                        fileName = doc.FileName,
                        buyer = doc.Buyer,
                        conversationId = convo.Id,
                        extractedAt = doc.ExtractedAt,
                        extracted = parsed.RootElement.Clone(),
                        sources,
                    });
                }
            });

            // Workspace chat: RAG chat, streamed. Separate from /chat on purpose — the model only
            // sees retrieved chunks, and turns are saved to the pinned document's own conversation
            // so the workspace thread (including extractions) survives a reload.
            app.MapPost("/documents/workspace-chat", async (
                WorkspaceChatRequest req, HttpContext ctx, AppDbContext db,
                EmbeddingService embedder, VectorIndex index, IHttpClientFactory http) =>
            {
                var user = await Authorization.ResolveUser(ctx, db);
                if (user is null) { ctx.Response.StatusCode = 401; return; }
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

                var sb = new StringBuilder(
                    "You are a purchase-order assistant for a garment merchandiser. Answer using ONLY the document " +
                    "excerpts below. Cite the [source] you used. If the answer is not in the excerpts, say so.\n\n");
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
