// Jiji's configuration, section "Jiji". Script model, speech provider and voice are fallbacks:
// Jiji → Settings stores the real values. The one secret (YouTube:ClientSecret) comes from
// user-secrets or the environment only; its name ends in a secret word, so SecretConfig drops
// it if it is ever put in appsettings. Speech keys live with the provider (Model providers).

namespace RabbitIC.Api.Jiji;

public sealed class JijiOptions
{
    public const string Section = "Jiji";

    /// <summary>Root of /data/jiji/{contentId}/. Every input and output path must resolve inside it.</summary>
    public string StoragePath { get; set; } = "Data/jiji";

    /// <summary>The model id that writes ideas and scripts (ModelSourceRegistry). Chosen on day 2.</summary>
    public string? Model { get; set; }

    /// <summary>The source Model is served by: local, cloud or an OpenAI-compatible provider key.</summary>
    public string? ModelSource { get; set; } = "local";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string? FontsDir { get; set; }

    public SpeechOptions Speech { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();
    public YouTubeOptions YouTube { get; set; } = new();

    /// <summary>
    /// Fallbacks only: Jiji → Settings (the JijiSettings row) wins. The speech provider itself, its
    /// URL and key, live in Settings → Model providers.
    /// </summary>
    public sealed class SpeechOptions
    {
        /// <summary>Key of a Model providers entry with the speech capability, e.g. "kokoro".</summary>
        public string? Provider { get; set; }

        /// <summary>The model the speech API is asked for.</summary>
        public string Model { get; set; } = "kokoro";

        /// <summary>A voice id from the provider's voice list.</summary>
        public string? Voice { get; set; }

        /// <summary>Speaking speed sent with every request.</summary>
        public double Speed { get; set; } = 1.0;

        /// <summary>Silence between two spoken parts in narration.wav, in milliseconds. Part of every timing.</summary>
        public int GapMs { get; set; } = 200;
    }

    /// <summary>JijiWorker. The defaults are the plan's; shorter values exist for the failure drills.</summary>
    public sealed class WorkerOptions
    {
        public bool Enabled { get; set; } = true;
        public int PollSeconds { get; set; } = 5;
        public int LeaseSeconds { get; set; } = 120;
        public int HeartbeatSeconds { get; set; } = 30;
    }

    public sealed class YouTubeOptions
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    }

    /// <summary>StoragePath made absolute, anchored to the app folder like the SQLite file.</summary>
    public string StorageRoot =>
        Path.GetFullPath(Path.IsPathRooted(StoragePath) ? StoragePath : Path.Combine(AppContext.BaseDirectory, StoragePath));
}
