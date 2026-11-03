// The OpenAI-compatible speech API (Kokoro-FastAPI and the like): the voice list and one
// synthesis. Used by a provider's Test, Jiji's voice preview and the narration job, so all
// three ask the same way and fail with the same sentences.
//
//   GET  {base}/audio/voices   → { "voices": ["af_bella", …] } (or a bare list, or objects with id/name)
//   POST {base}/audio/speech   { model, voice, input, response_format: "wav", speed, stream: false } → WAV bytes
//
// No key = no Authorization header (a local server). The key is set on the request, never on a client.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RabbitIC.Api.ModelProviders;

/// <summary>A speech call that failed. Unreachable and 5xx/429/408 are worth retrying; a 4xx is not.</summary>
public sealed class SpeechException(string message, int? status, bool unreachable, Exception? inner = null) : Exception(message, inner)
{
    public int? Status { get; } = status;
    public bool Unreachable { get; } = unreachable;
    public bool Transient => Unreachable || Status is >= 500 or 429 or 408;
}

/// <summary>Where one speech provider is: from DbModelSourceProvider, the key already decrypted.</summary>
public sealed record SpeechTarget(string Key, string Name, Uri BaseUri, string? ApiKey);

public sealed class SpeechClient(IHttpClientFactory http)
{
    public const string ClientName = "speech";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The voice ids, or null when the server has no voices endpoint (404/405).</summary>
    public async Task<List<string>?> VoicesAsync(SpeechTarget t, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(t.BaseUri, "audio/voices"));
        Authorize(req, t);
        using var resp = await SendAsync(req, t, TimeSpan.FromSeconds(15), ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw Failure(t, (int)resp.StatusCode, body);
        return ParseVoices(body) ?? throw new SpeechException($"{t.Name}'s voice list is not in a known shape.", (int)resp.StatusCode, false);
    }

    /// <summary>One WAV. Throws <see cref="SpeechException"/>; an empty body is a failure too.</summary>
    public async Task<byte[]> SynthesizeAsync(SpeechTarget t, string model, string voice, string text, double speed, CancellationToken ct,
        TimeSpan? timeout = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["voice"] = voice,
            ["input"] = text,
            ["response_format"] = "wav",
            ["speed"] = speed,
            ["stream"] = false,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(t.BaseUri, "audio/speech"))
        {
            // Sized, not chunked: some servers refuse a chunked request body.
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };
        Authorize(req, t);
        using var resp = await SendAsync(req, t, timeout ?? TimeSpan.FromSeconds(120), ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (!resp.IsSuccessStatusCode) throw Failure(t, (int)resp.StatusCode, Encoding.UTF8.GetString(bytes));
        if (bytes.Length == 0) throw new SpeechException($"{t.Name} returned no audio.", (int)resp.StatusCode, false);
        return bytes;
    }

    private static void Authorize(HttpRequestMessage req, SpeechTarget t)
    {
        if (!string.IsNullOrEmpty(t.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.ApiKey);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, SpeechTarget t, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await http.CreateClient(ClientName).SendAsync(req, limit.Token);
        }
        catch (HttpRequestException e)
        {
            throw new SpeechException($"Speech provider not reachable at {Where(t)}. Is Kokoro running?", null, true, e);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new SpeechException($"Speech provider at {Where(t)} did not answer within {timeout.TotalSeconds:0} s.", null, true, e);
        }
    }

    private static string Where(SpeechTarget t) => t.BaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>The provider's own message: error.message, detail (a string or { message }), or message.</summary>
    private static SpeechException Failure(SpeechTarget t, int status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("error", out var e))
                    message = e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) ? em.GetString() : null;
                if (message is null && r.TryGetProperty("detail", out var d))
                    message = d.ValueKind == JsonValueKind.String ? d.GetString()
                        : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var dm) ? dm.GetString()
                        : d.ValueKind == JsonValueKind.Array ? d.GetRawText() : null;
                if (message is null && r.TryGetProperty("message", out var m)) message = m.GetString();
            }
        }
        catch (JsonException) { message = body.Length is > 0 and <= 300 ? body.Trim() : null; }

        message = string.IsNullOrWhiteSpace(message) ? $"answered {status}." : Observability.Redactor.Scrub(message!.Length > 300 ? message[..300] + "…" : message);
        return new SpeechException($"{t.Name}: {message} ({status})", status, false);
    }

    internal static List<string>? ParseVoices(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var list = r.ValueKind == JsonValueKind.Array ? r
                : r.ValueKind == JsonValueKind.Object && r.TryGetProperty("voices", out var v) && v.ValueKind == JsonValueKind.Array ? v
                : r.ValueKind == JsonValueKind.Object && r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
                : (JsonElement?)null;
            if (list is null) return null;
            var ids = new List<string>();
            foreach (var e in list.Value.EnumerateArray())
            {
                var id = e.ValueKind == JsonValueKind.String ? e.GetString()
                    : e.ValueKind == JsonValueKind.Object ? (e.TryGetProperty("id", out var i) ? i.GetString() : e.TryGetProperty("name", out var n) ? n.GetString() : null)
                    : null;
                if (!string.IsNullOrWhiteSpace(id) && id.Length <= 100) ids.Add(id.Trim());
                if (ids.Count >= 500) break;
            }
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }
        catch (JsonException) { return null; }
    }
}
