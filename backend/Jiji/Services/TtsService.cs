// Jiji — Azure Speech text to speech, over its REST API: one SSML request per spoken
// part, answered as 24 kHz 16-bit mono PCM WAV (riff-24khz-16bit-mono-pcm, the SDK's
// Riff24Khz16BitMonoPcm). The key goes in a header on this request only.

using System.Net;
using System.Security;
using System.Text;
using Microsoft.Extensions.Options;
using RabbitIC.Api.Jiji.Workers;

namespace RabbitIC.Api.Jiji.Services;

public sealed class TtsService(IHttpClientFactory http, IOptions<JijiOptions> options)
{
    public const string ClientName = "jiji-speech";
    public const string OutputFormat = "riff-24khz-16bit-mono-pcm";

    /// <summary>The WAV for one part. Throws <see cref="JijiJobException"/>, transient or not.</summary>
    public async Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken ct)
    {
        var opt = options.Value.AzureSpeech;
        if (!opt.IsConfigured)
            throw new JijiJobException("Azure Speech is not configured: set Jiji__AzureSpeech__ApiKey and Jiji:AzureSpeech:Region, then retry.", transient: false);
        if (!opt.Voices.TryGetValue(language, out var voice) || string.IsNullOrWhiteSpace(voice))
            throw new JijiJobException($"No voice for '{language}': set Jiji:AzureSpeech:Voices:{language}.", transient: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(opt.BaseUri, "cognitiveservices/v1"))
        {
            Content = new StringContent(Ssml(text, voice), Encoding.UTF8, "application/ssml+xml"),
        };
        req.Headers.Add("Ocp-Apim-Subscription-Key", opt.ApiKey);
        req.Headers.Add("X-Microsoft-OutputFormat", OutputFormat);
        req.Headers.UserAgent.ParseAdd("Mostisko-Jiji");

        HttpResponseMessage resp;
        try { resp = await http.CreateClient(ClientName).SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new JijiJobException($"Azure Speech could not be reached: {e.Message}", transient: true, e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        { throw new JijiJobException("Azure Speech did not answer in time.", transient: true, e); }

        using (resp)
        {
            if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync(ct);
            var code = (int)resp.StatusCode;
            var transient = code >= 500 || resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
            var hint = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? " Check the key and region." : "";
            throw new JijiJobException($"Azure Speech answered {code} {resp.ReasonPhrase}.{hint}", transient);
        }
    }

    /// <summary>The voice's own locale ("en-US-AndrewNeural" → en-US) and the text, XML-escaped; nothing in it can become markup.</summary>
    public static string Ssml(string text, string voice)
    {
        var parts = voice.Split('-');
        var locale = parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "en-US";
        return $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{SecurityElement.Escape(locale)}\">" +
               $"<voice name=\"{SecurityElement.Escape(voice)}\">{SecurityElement.Escape(text)}</voice></speak>";
    }
}
