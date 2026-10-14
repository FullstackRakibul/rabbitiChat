// Oddity — the gateway proxy.
//
// Speaks Ollama's own API, so any Ollama client works by changing its base URL
// and adding a key. Two rules carry the design:
//
//   Bytes out are Ollama's bytes. A reply is copied through unparsed — same
//   status, same Content-Type, flushed after every read — and only tapped on the
//   side for usage and a capped preview. /api/tags is the one response rebuilt.
//
//   The request is edited as a JSON tree read from the raw body, whatever
//   Content-Type the caller sent (curl -d sends a form type). Unknown fields,
//   tools, format and images survive; only "oddity" is removed, and the runner
//   settings (num_ctx, keep_alive) are the operator's, not the caller's.
//
// Gateway traffic never goes through IChatModelSource or ChatService: those
// reshape the stream into the app's own events.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using RabbitIC.Api.Data;
using RabbitIC.Api.Models;
using RabbitIC.Api.Observability;
using RabbitIC.Api.Services;

namespace RabbitIC.Api.Oddity;

public sealed class OddityProxy
{
    public const string TabKey = "oddity";
    public const string ClientName = "oddity-upstream";

    /// <summary>One message for a missing, unknown, disabled, expired or deleted key.</summary>
    private const string BadKey = "a valid gateway key is required (Authorization: Bearer odd_…)";

    private const int PreviewChars = 2_000;
    private const int FullChars = 200_000;

    /// <summary>The gateway's body limit: Kestrel's default, about 28 MB.</summary>
    private const long MaxBodyBytes = 30_000_000;

    /// <summary>
    /// An oversized body is read to the end (and discarded) up to this size before
    /// the 413 goes out. Closing on unread upload data makes the OS reset the
    /// connection, and a reset can destroy the 413 before the client reads it.
    /// </summary>
    private const long DrainCapBytes = 100_000_000;

    /// <summary>Keeps non-ASCII text as UTF-8 rather than \u escapes; Ollama parses either.</summary>
    private static readonly JsonSerializerOptions UpstreamJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly AppDbContext _db;
    private readonly OddityKeyService _keys;
    private readonly OdditySettingsService _settings;
    private readonly OddityRecorder _recorder;
    private readonly RbacService _rbac;
    private readonly RabbitIC.Api.Rbac.ModelPolicy _modelPolicy;
    private readonly IHttpClientFactory _http;

    public OddityProxy(
        AppDbContext db, OddityKeyService keys, OdditySettingsService settings,
        OddityRecorder recorder, RbacService rbac, IHttpClientFactory http,
        RabbitIC.Api.Rbac.ModelPolicy modelPolicy)
    {
        _db = db;
        _keys = keys;
        _settings = settings;
        _recorder = recorder;
        _rbac = rbac;
        _modelPolicy = modelPolicy;
        _http = http;
    }

    // ─────────────── chat, generate, embed, embeddings ───────────────

    /// <summary>
    /// A model call. Every call that presents a valid key leaves exactly one
    /// request row, written in finally — on success, rejection, upstream error or
    /// client abort alike.
    /// </summary>
    public async Task ModelCallAsync(HttpContext ctx, string endpoint)
    {
        var clock = Stopwatch.StartNew();

        var key = await _keys.VerifyAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
        if (key is null)
        {
            // No owner to file it under; the activity log already has the request.
            await ErrorAsync(ctx, StatusCodes.Status401Unauthorized, BadKey);
            return;
        }

        var row = new OddityRequest
        {
            TimestampUtc = DateTime.UtcNow,
            CorrelationId = ctx.Items[CorrelationMiddleware.ItemKey] as string,
            OwnerUserId = key.OwnerUserId,
            KeyId = key.Id,
            KeyName = key.Name,
            ClientIp = ClientIp(ctx),
            UserAgent = Truncate(ctx.Request.Headers.UserAgent.ToString(), 512),
            Endpoint = endpoint,
            Outcome = "rejected",
        };

        try
        {
            await ForwardModelCallAsync(ctx, endpoint, key, row, clock);
        }
        catch (Exception ex) when (ClientGone(ctx, ex))
        {
            row.Outcome = "client_abort";
        }
        finally
        {
            row.TotalMs = clock.ElapsedMilliseconds;
            if (row.StatusCode == 0) row.StatusCode = ctx.Response.StatusCode;
            row.Error = Truncate(row.Error, 500);
            await _recorder.WriteAsync(row);
        }
    }

    private async Task ForwardModelCallAsync(
        HttpContext ctx, string endpoint, OddityKey key, OddityRequest row, Stopwatch clock)
    {
        var ct = ctx.RequestAborted;

        // 2. The owner must still hold the tab — a key outlives a role change.
        var owner = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == key.OwnerUserId, ct);
        if (owner is null || !await _rbac.CanAccessTabAsync(owner, TabKey, ct))
        {
            await RejectAsync(ctx, row, StatusCodes.Status403Forbidden, "this key's owner no longer has the Oddity tab");
            return;
        }

        // 3. Gateway switched on.
        var settings = await _settings.GetAsync(ct);
        if (!settings.Enabled)
        {
            await RejectAsync(ctx, row, StatusCodes.Status503ServiceUnavailable, "the gateway is turned off");
            return;
        }

        // 4. Rate limit for this key.
        var limit = key.RatePerMinute ?? settings.DefaultRatePerMinute;
        if (!OddityRateLimiter.TryTake(key.Id, limit))
        {
            await RejectAsync(ctx, row, StatusCodes.Status429TooManyRequests, $"rate limit reached ({limit} calls a minute for this key)");
            return;
        }

        // 5. Read and parse the body, whatever Content-Type it came with.
        var raw = await ReadBodyAsync(ctx, ct);
        if (raw is null)
        {
            await RejectAsync(ctx, row, StatusCodes.Status413PayloadTooLarge,
                $"the request body is over {MaxBodyBytes / 1_000_000} MB; upload large files in the Documents tab");
            return;
        }
        row.RequestBytes = raw.Length;

        JsonObject? body = null;
        try { body = JsonNode.Parse(raw) as JsonObject; } catch (JsonException) { /* answered below */ }
        if (body is null)
        {
            await RejectAsync(ctx, row, StatusCodes.Status400BadRequest, "the body is not a JSON object");
            return;
        }

        var embed = endpoint is "embed" or "embeddings";
        row.RequestedModel = AsString(body["model"]);
        row.Stream = !embed && (body["stream"] is not JsonValue s || !s.TryGetValue<bool>(out var streamFlag) || streamFlag);
        row.RequestPreview = RequestPreview(settings, endpoint, body);

        // 7. Model: the default when none is named, then the allowed list.
        var model = row.RequestedModel;
        if (!embed && string.IsNullOrWhiteSpace(model) && settings.DefaultModel.Length > 0) model = settings.DefaultModel;
        row.Model = model;
        if (!OdditySettingsService.IsAllowed(settings, model))
        {
            await RejectAsync(ctx, row, StatusCodes.Status403Forbidden, $"model '{model}' is not allowed on this gateway");
            return;
        }

        // The key's OWNER is still subject to their role's model policy. Without
        // this, a key would be a way around the restriction: the gateway has its
        // own allow-list, but that is the operator's list, not the role's.
        // Embedding calls are exempt, as they are everywhere else (§10.3).
        if (!embed)
        {
            var rolePolicy = await _modelPolicy.CheckAsync(owner, model, "local", ct: ct);
            if (!rolePolicy.Allowed)
            {
                await RejectAsync(ctx, row, StatusCodes.Status403Forbidden, rolePolicy.Error!);
                return;
            }
        }

        // Embedding calls are forwarded byte for byte; chat and generate get the
        // operator's runner settings (R4) and lose the "oddity" field (R2).
        var upstreamBody = raw;
        if (!embed)
        {
            body.Remove("oddity");
            if (model != row.RequestedModel) body["model"] = model;

            switch (body["options"])
            {
                case null: body["options"] = new JsonObject(); break;
                case JsonObject: break;
                default:
                    await RejectAsync(ctx, row, StatusCodes.Status400BadRequest, "options must be an object");
                    return;
            }
            body["options"]!.AsObject()["num_ctx"] = settings.NumCtx;
            body["keep_alive"] = KeepAliveNode(settings.KeepAlive);

            upstreamBody = JsonSerializer.SerializeToUtf8Bytes(body, UpstreamJson);
        }

        // 9. Forward.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/" + endpoint)
        {
            Content = new ByteArrayContent(upstreamBody),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        CopyAccept(ctx, request);

        HttpResponseMessage response;
        try
        {
            response = await _http.CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            row.Outcome = "upstream_error";
            row.StatusCode = StatusCodes.Status502BadGateway;
            row.Error = "Ollama is unreachable: " + ex.Message;
            await ErrorAsync(ctx, StatusCodes.Status502BadGateway, "Ollama is unreachable");
            return;
        }

        // 10. Stream back.
        using (response)
        {
            await RelayAsync(ctx, response, row, clock, PreviewLimit(settings), endpoint, ct);
        }
    }

    /// <summary>Copies Ollama's reply through unchanged, tapping it for usage and the preview.</summary>
    private static async Task RelayAsync(
        HttpContext ctx, HttpResponseMessage response, OddityRequest row, Stopwatch clock,
        int previewLimit, string endpoint, CancellationToken ct)
    {
        row.StatusCode = (int)response.StatusCode;
        ctx.Response.StatusCode = row.StatusCode;
        if (response.Content.Headers.ContentType is { } type) ctx.Response.ContentType = type.ToString();
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var tap = new StreamTap(previewLimit, endpoint);
        await using var upstream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await upstream.ReadAsync(buffer, ct)) > 0)
        {
            row.FirstByteMs ??= clock.ElapsedMilliseconds;
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            await ctx.Response.Body.FlushAsync(ct);
            row.ResponseBytes += read;
            tap.Feed(buffer.AsSpan(0, read));
        }
        tap.Complete();

        tap.ApplyUsage(row);
        row.ResponsePreview = tap.Preview;

        var ok = response.IsSuccessStatusCode && tap.Error is null;
        row.Outcome = ok ? "ok" : "upstream_error";
        row.Error = ok ? null : tap.Error ?? $"Ollama answered {row.StatusCode}";
    }

    // ─────────────── tags, show, version, ps ───────────────

    /// <summary>A read-only call forwarded as is. Needs a valid key; no rate limit and no row.</summary>
    public async Task PassThroughAsync(HttpContext ctx, HttpMethod method, string path)
    {
        if (await AdmitAsync(ctx) is null) return;
        var ct = ctx.RequestAborted;

        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
        {
            var body = await ReadBodyAsync(ctx, ct);
            if (body is null)
            {
                await ErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, "the request body is too large");
                return;
            }
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        CopyAccept(ctx, request);

        using var response = await SendOrFailAsync(ctx, request, ct);
        if (response is null) return;

        ctx.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } type) ctx.Response.ContentType = type.ToString();
        await using var upstream = await response.Content.ReadAsStreamAsync(ct);
        await upstream.CopyToAsync(ctx.Response.Body, ct);
    }

    /// <summary>Ollama's model list, limited to the allowed models. The one response Oddity rebuilds.</summary>
    public async Task TagsAsync(HttpContext ctx)
    {
        var settings = await AdmitAsync(ctx);
        if (settings is null) return;
        var ct = ctx.RequestAborted;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tags");
        using var response = await SendOrFailAsync(ctx, request, ct);
        if (response is null) return;

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        JsonObject? root = null;
        try { root = JsonNode.Parse(bytes) as JsonObject; } catch (JsonException) { /* relayed as is below */ }

        if (!response.IsSuccessStatusCode || root?["models"] is not JsonArray models)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type) ctx.Response.ContentType = type.ToString();
            await ctx.Response.Body.WriteAsync(bytes, ct);
            return;
        }

        var kept = new JsonArray();
        foreach (var m in models)
        {
            if (m is not JsonObject entry) continue;
            if (OdditySettingsService.IsAllowed(settings, AsString(entry["name"]))
                || OdditySettingsService.IsAllowed(settings, AsString(entry["model"])))
            {
                kept.Add(entry.DeepClone());
            }
        }
        root["models"] = kept;

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(root, UpstreamJson), ct);
    }

    /// <summary>Steps 1–3 for the read-only calls. Null when the caller was refused (the answer is written).</summary>
    private async Task<OdditySettings?> AdmitAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        var key = await _keys.VerifyAsync(ctx.Request.Headers.Authorization.ToString(), ct);
        if (key is null)
        {
            await ErrorAsync(ctx, StatusCodes.Status401Unauthorized, BadKey);
            return null;
        }

        var owner = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == key.OwnerUserId, ct);
        if (owner is null || !await _rbac.CanAccessTabAsync(owner, TabKey, ct))
        {
            await ErrorAsync(ctx, StatusCodes.Status403Forbidden, "this key's owner no longer has the Oddity tab");
            return null;
        }

        var settings = await _settings.GetAsync(ct);
        if (!settings.Enabled)
        {
            await ErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "the gateway is turned off");
            return null;
        }
        return settings;
    }

    private async Task<HttpResponseMessage?> SendOrFailAsync(HttpContext ctx, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            await ErrorAsync(ctx, StatusCodes.Status502BadGateway, "Ollama is unreachable");
            return null;
        }
    }

    // ─────────────── helpers ───────────────

    /// <summary>Every gateway refusal: <c>{"error":"oddity: …"}</c>, the shape Ollama's clients already read.</summary>
    public static async Task ErrorAsync(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "oddity: " + message }));
    }

    private static Task RejectAsync(HttpContext ctx, OddityRequest row, int status, string message)
    {
        row.Outcome = "rejected";
        row.StatusCode = status;
        row.Error = message;
        return ErrorAsync(ctx, status, message);
    }

    /// <summary>
    /// The whole body, or null when it is over <see cref="MaxBodyBytes"/>. An
    /// oversized body is still read to its end (up to <see cref="DrainCapBytes"/>)
    /// and thrown away, so the 413 that follows reaches the client intact.
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx, CancellationToken ct)
    {
        var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = DrainCapBytes;

        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var tooLarge = false;
        try
        {
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (tooLarge) continue;                       // draining
                if (ms.Length + read > MaxBodyBytes) { tooLarge = true; continue; }
                ms.Write(buffer, 0, read);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;                                       // past the drain cap: Kestrel closes
        }

        return tooLarge ? null : ms.ToArray();
    }

    /// <summary>R3: only Content-Type (set by the caller of this) and Accept go upstream.</summary>
    private static void CopyAccept(HttpContext ctx, HttpRequestMessage request)
    {
        var accept = ctx.Request.Headers.Accept.ToString();
        if (accept.Length > 0) request.Headers.TryAddWithoutValidation("Accept", accept);
    }

    /// <summary>A plain number goes as a JSON number; a duration ("30m") as a string.</summary>
    private static JsonNode KeepAliveNode(string keepAlive) =>
        long.TryParse(keepAlive, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds)
            ? JsonValue.Create(seconds)
            : JsonValue.Create(keepAlive);

    private static int PreviewLimit(OdditySettings s) => s.LogBodies switch
    {
        OdditySettingsService.LogFull => FullChars,
        OdditySettingsService.LogPreview => PreviewChars,
        _ => 0,
    };

    /// <summary>
    /// preview: the last user message (or prompt, or input), cut to 2,000 characters.
    /// full: the caller's body as JSON, images replaced by a marker, cut to 200,000.
    /// off: nothing.
    /// </summary>
    private static string? RequestPreview(OdditySettings s, string endpoint, JsonObject body)
    {
        switch (s.LogBodies)
        {
            case OdditySettingsService.LogPreview:
                var text = endpoint switch
                {
                    "chat" => (body["messages"] as JsonArray)?
                        .OfType<JsonObject>()
                        .LastOrDefault(m => AsString(m["role"]) == "user")?["content"] is { } c ? AsString(c) : null,
                    "generate" or "embeddings" => AsString(body["prompt"]),
                    _ => body["input"] switch
                    {
                        JsonArray items => string.Join("\n", items.Select(AsString)),
                        var one => AsString(one),
                    },
                };
                return Truncate(text, PreviewChars);

            case OdditySettingsService.LogFull:
                var copy = body.DeepClone().AsObject();
                StripImages(copy);
                return Truncate(copy.ToJsonString(UpstreamJson), FullChars);

            default:
                return null;
        }
    }

    /// <summary>Images are never stored: each becomes "[image, N bytes]".</summary>
    private static void StripImages(JsonObject node)
    {
        if (node["images"] is JsonArray images) ReplaceImages(images);
        if (node["messages"] is JsonArray messages)
        {
            foreach (var m in messages.OfType<JsonObject>())
                if (m["images"] is JsonArray inner) ReplaceImages(inner);
        }

        static void ReplaceImages(JsonArray images)
        {
            for (var i = 0; i < images.Count; i++)
            {
                var length = AsString(images[i])?.Length ?? 0;
                images[i] = $"[image, {length * 3 / 4} bytes]";
            }
        }
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? ClientIp(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        return ip?.ToString();
    }

    private static bool ClientGone(HttpContext ctx, Exception ex) =>
        ctx.RequestAborted.IsCancellationRequested && ex is OperationCanceledException or IOException;

    private static string? Truncate(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max];

    // ─────────────── the tap ───────────────

    /// <summary>
    /// Watches the reply on its way past. Holds only the current partial line and
    /// the last complete one (R9) — the usage figures are on the final line — plus
    /// the capped preview, which is filled only while it has room.
    /// </summary>
    private sealed class StreamTap
    {
        private readonly MemoryStream _line = new();
        private readonly StringBuilder? _preview;
        private readonly int _limit;
        private readonly string _endpoint;
        private byte[]? _last;

        public StreamTap(int previewLimit, string endpoint)
        {
            _limit = previewLimit;
            _endpoint = endpoint;
            _preview = previewLimit > 0 && endpoint is "chat" or "generate" ? new StringBuilder() : null;
        }

        public string? Error { get; private set; }

        public string? Preview => _preview is null ? null : Truncate(_preview.ToString(), _limit);

        public void Feed(ReadOnlySpan<byte> chunk)
        {
            while (true)
            {
                var newline = chunk.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    _line.Write(chunk);
                    return;
                }
                _line.Write(chunk[..newline]);
                EndLine();
                chunk = chunk[(newline + 1)..];
            }
        }

        /// <summary>A non-streamed reply has no trailing newline: its one line ends here.</summary>
        public void Complete() => EndLine();

        private void EndLine()
        {
            if (_line.Length == 0) return;
            _last = _line.ToArray();
            _line.SetLength(0);

            if (_preview is null || _preview.Length >= _limit) return;
            try
            {
                using var doc = JsonDocument.Parse(_last);
                var root = doc.RootElement;
                if (_endpoint == "chat" && root.TryGetProperty("message", out var m)
                    && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    _preview.Append(c.GetString());
                else if (_endpoint == "generate" && root.TryGetProperty("response", out var r)
                    && r.ValueKind == JsonValueKind.String)
                    _preview.Append(r.GetString());
            }
            catch (JsonException) { /* not a frame; nothing to preview */ }
        }

        /// <summary>Usage from the last complete line only (R9). Durations arrive in nanoseconds.</summary>
        public void ApplyUsage(OddityRequest row)
        {
            if (_last is null) return;
            try
            {
                using var doc = JsonDocument.Parse(_last);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                if (root.TryGetProperty("error", out var e))
                    Error = e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
                if (root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String)
                    row.DoneReason = dr.GetString();

                row.PromptTokens = Int(root, "prompt_eval_count");
                row.CachedPromptTokens = Int(root, "prompt_eval_cached_count");
                row.OutputTokens = Int(root, "eval_count");
                row.LoadMs = Ms(root, "load_duration");
                row.PromptMs = Ms(root, "prompt_eval_duration");
                row.GenerationMs = Ms(root, "eval_duration");
            }
            catch (JsonException) { /* the last line was not JSON; no usage to read */ }
        }

        private static int? Int(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

        private static long? Ms(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.TryGetInt64(out var ns) ? ns / 1_000_000 : null;
    }
}
