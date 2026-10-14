// RabbitIC — vector store for RAG retrieval.
//
// Design notes:
//  * Vectors are L2-normalised at INGEST. Cosine(a,b) == Dot(a,b) when ‖a‖=‖b‖=1,
//    so the hot path skips recomputing magnitudes for every stored chunk on every
//    query. TensorPrimitives.Dot is SIMD-accelerated the same as CosineSimilarity.
//  * float, not double — nomic-embed-text emits float32, half the memory, twice
//    the SIMD lanes per register.
//  * Stored as a raw BLOB, not a JSON array. MemoryMarshal reinterprets the bytes
//    with no parse and no per-element allocation.
//  * The whole index lives in RAM. nomic-embed-text is 768 dims => 3 KB/chunk,
//    so 10,000 chunks is ~30 MB. SQLite is the durable store; memory is the query
//    path. Revisit only when the corpus stops fitting comfortably.
//  * Metadata filter runs BEFORE similarity. This is a security control, not an
//    optimisation: a chunk the caller may not read must never enter the candidate
//    set, let alone the model's context.

using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Retrieval;

// ───────────────────────── vector maths ─────────────────────────

public static class VectorOps
{
    /// <summary>Scale to unit length so later comparisons are a bare dot product.</summary>
    public static float[] Normalize(ReadOnlySpan<float> v)
    {
        var result = new float[v.Length];
        float norm = TensorPrimitives.Norm(v);
        if (norm == 0f) return result;          // degenerate: all-zero embedding
        TensorPrimitives.Divide(v, norm, result);
        return result;
    }

    /// <summary>Cosine similarity for already-normalised vectors.</summary>
    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => TensorPrimitives.Dot(a, b);

    public static byte[] ToBlob(float[] v) => MemoryMarshal.AsBytes<float>(v).ToArray();

    public static float[] FromBlob(byte[] blob) => MemoryMarshal.Cast<byte, float>(blob).ToArray();
}

// ───────────────────────── in-memory index ─────────────────────────

public sealed record RetrievalHit(int ChunkId, float Score, string Text, string? SourceRef, int? ContextSourceId = null);

public sealed class VectorIndex
{
    private sealed record Entry(
        int ChunkId, int? DocumentId, int? ContextSourceId, string? Kind, int? OwnerUserId,
        string? Buyer, string? StyleNumber, string? SourceRef,
        string Text, float[] Vector);

    private readonly List<Entry> _entries = new();
    private readonly ReaderWriterLockSlim _lock = new();

    public int Count { get { _lock.EnterReadLock(); try { return _entries.Count; } finally { _lock.ExitReadLock(); } } }

    /// <summary>Load the whole index from the database once at startup.</summary>
    public void Load(IEnumerable<EmbeddingChunk> chunks)
    {
        _lock.EnterWriteLock();
        try
        {
            _entries.Clear();
            // A chunk with no vector yet (an HR item saved while Ollama was away)
            // is not searchable; it joins through Add once it has been embedded.
            foreach (var c in chunks) if (c.Vector.Length > 0) _entries.Add(ToEntry(c));
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Append after ingesting a new document — no full rebuild needed.</summary>
    public void Add(EmbeddingChunk chunk)
    {
        if (chunk.Vector.Length == 0) return;
        _lock.EnterWriteLock();
        try { _entries.Add(ToEntry(chunk)); }
        finally { _lock.ExitWriteLock(); }
    }

    public void RemoveDocument(int documentId)
    {
        _lock.EnterWriteLock();
        try { _entries.RemoveAll(e => e.DocumentId == documentId); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Drops every chunk of one HR library item — before re-adding its new ones, or on delete.</summary>
    public void RemoveContextSource(int contextSourceId)
    {
        _lock.EnterWriteLock();
        try { _entries.RemoveAll(e => e.ContextSourceId == contextSourceId); }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Chunks of one kind currently in memory, per library item — for the library's own consistency check.</summary>
    public IReadOnlyDictionary<int, int> CountByContextSource(string kind)
    {
        _lock.EnterReadLock();
        try
        {
            return _entries.Where(e => e.Kind == kind && e.ContextSourceId is not null)
                .GroupBy(e => e.ContextSourceId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());
        }
        finally { _lock.ExitReadLock(); }
    }

    private static Entry ToEntry(EmbeddingChunk c) => new(
        c.Id, c.DocumentId, c.ContextSourceId, c.Kind, c.OwnerUserId, c.Buyer, c.StyleNumber, c.SourceRef,
        c.Text, VectorOps.FromBlob(c.Vector));

    /// <summary>
    /// Top-K nearest chunks the caller is permitted to see.
    /// <paramref name="queryVector"/> must come from the SAME embedding model,
    /// normalised via VectorOps.Normalize.
    /// </summary>
    public IReadOnlyList<RetrievalHit> Search(
        float[] queryVector,
        int userId,
        int topK = 5,
        float minScore = 0.25f,
        string? buyer = null,
        string? styleNumber = null,
        string? kind = null,
        IReadOnlySet<int>? contextSources = null)
    {
        // Min-heap of size topK: the weakest surviving hit sits at the root, so a
        // new candidate only needs one comparison to be accepted or discarded.
        var heap = new PriorityQueue<RetrievalHit, float>(topK);

        _lock.EnterReadLock();
        try
        {
            foreach (var e in _entries)
            {
                // ---- metadata gate (runs first, by design) ----
                // Kind first: document search (kind null) never sees an HR library
                // chunk, and HR search never sees a document chunk.
                if (!string.Equals(e.Kind, kind, StringComparison.Ordinal)) continue;
                // HR search scoped to some library items (or to the active ones).
                if (contextSources is not null && (e.ContextSourceId is not int cs || !contextSources.Contains(cs))) continue;
                if (e.OwnerUserId is not null && e.OwnerUserId != userId) continue;
                if (buyer is not null && !string.Equals(e.Buyer, buyer, StringComparison.OrdinalIgnoreCase)) continue;
                if (styleNumber is not null && !string.Equals(e.StyleNumber, styleNumber, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Vector.Length != queryVector.Length) continue;   // dimension mismatch = different model

                float score = VectorOps.Similarity(queryVector, e.Vector);
                if (score < minScore) continue;

                var hit = new RetrievalHit(e.ChunkId, score, e.Text, e.SourceRef, e.ContextSourceId);
                if (heap.Count < topK) heap.Enqueue(hit, score);
                else if (heap.TryPeek(out _, out float weakest) && score > weakest)
                    heap.DequeueEnqueue(hit, score);
            }
        }
        finally { _lock.ExitReadLock(); }

        var results = new List<RetrievalHit>(heap.Count);
        while (heap.TryDequeue(out var hit, out _)) results.Add(hit);
        results.Reverse();                     // strongest first
        return results;
    }
}

// ───────────────────────── embedding client ─────────────────────────

/// <summary>Calls Ollama's /api/embeddings using the same host as the chat model.</summary>
public sealed class EmbeddingService
{
    private readonly IHttpClientFactory _http;
    private readonly string _model;

    public EmbeddingService(IHttpClientFactory http, IConfiguration config)
    {
        _http = http;
        _model = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
    }

    private sealed record EmbedResponse(float[]? embedding);

    /// <summary>Returns an L2-normalised vector, ready to store or compare.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var client = _http.CreateClient("ollama");
        var resp = await client.PostAsJsonAsync("/api/embeddings",
            new { model = _model, prompt = text }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Ollama embeddings returned {(int)resp.StatusCode} for model '{_model}'. " +
                $"If the model is missing, run: ollama pull {_model}. Detail: {detail}");
        }

        var body = await resp.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: ct);
        if (body?.embedding is null || body.embedding.Length == 0)
            throw new InvalidOperationException($"Embedding model '{_model}' returned no vector.");

        return VectorOps.Normalize(body.embedding);
    }

    public async Task<List<float[]>> EmbedManyAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var results = new List<float[]>();
        foreach (var t in texts) results.Add(await EmbedAsync(t, ct));   // Ollama embeds one at a time
        return results;
    }
}
