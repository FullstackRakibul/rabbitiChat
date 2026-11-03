// Jiji — text to speech through the speech provider chosen in Jiji → Settings: a Model providers
// entry with the speech capability (Kokoro-FastAPI or any OpenAI-compatible speech API).
// POST {baseUrl}/audio/speech with model, voice, input, response_format "wav", speed and
// "stream": false; one call per spoken part. The provider's URL and key stay in Model providers.

using RabbitIC.Api.Jiji.Workers;
using RabbitIC.Api.ModelProviders;

namespace RabbitIC.Api.Jiji.Services;

/// <summary>Who will speak: the provider, the model and the voice, resolved from the settings.</summary>
public sealed record JijiVoiceChoice(SpeechTarget Target, string Model, string Voice, double Speed, int GapMs);

public sealed class TtsService(SpeechClient speech, DbModelSourceProvider providers, JijiSettingsService settings)
{
    /// <summary>The provider, model and voice the settings name. Throws a permanent <see cref="JijiJobException"/> when one is missing.</summary>
    public async Task<JijiVoiceChoice> ChooseAsync(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (s.SpeechProvider is null)
            throw new JijiJobException("No speech provider is set: choose one in Jiji → Settings, then retry.", transient: false);
        if (providers.Speech(s.SpeechProvider) is not { } p)
        {
            var rt = providers.Runtime(s.SpeechProvider);
            throw new JijiJobException(rt is null
                ? $"The speech provider '{s.SpeechProvider}' no longer exists in Settings → Model providers. Choose another in Jiji → Settings."
                : $"{rt.Name} is switched off or cannot be used ({rt.Message ?? rt.Status}). Check Settings → Model providers, then retry.", transient: false);
        }
        if (s.Voice is null)
            throw new JijiJobException("No voice is set: choose one in Jiji → Settings, then retry.", transient: false);
        return new JijiVoiceChoice(p.Target, s.SpeechModel, s.Voice, s.Speed, s.GapMs);
    }

    /// <summary>The WAV for one part. Throws <see cref="JijiJobException"/>: unreachable and 5xx/429 are transient (retried), a 4xx is not.</summary>
    public async Task<byte[]> SynthesizeAsync(JijiVoiceChoice v, string text, CancellationToken ct)
    {
        try
        {
            return await speech.SynthesizeAsync(v.Target, v.Model, v.Voice, text, v.Speed, ct);
        }
        catch (SpeechException e)
        {
            throw new JijiJobException(e.Message, e.Transient, e);
        }
    }

    /// <summary>One sentence in the chosen (or given) voice, for the Preview button.</summary>
    public async Task<byte[]> PreviewAsync(string? provider, string? voice, string text, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var key = string.IsNullOrWhiteSpace(provider) ? s.SpeechProvider : provider.Trim();
        if (key is null || providers.Speech(key) is not { } p)
            throw new SpeechException("Choose a speech provider first.", 400, false);
        var v = string.IsNullOrWhiteSpace(voice) ? s.Voice : voice.Trim();
        if (v is null) throw new SpeechException("Choose a voice first.", 400, false);
        return await speech.SynthesizeAsync(p.Target, s.SpeechModel, v, text, s.Speed, ct, TimeSpan.FromSeconds(60));
    }
}
