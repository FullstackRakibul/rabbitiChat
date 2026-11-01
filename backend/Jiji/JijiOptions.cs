// Jiji's configuration, section "Jiji". The secrets (AzureSpeech:ApiKey, YouTube:ClientSecret)
// come from user-secrets or the environment only, e.g. Jiji__AzureSpeech__ApiKey. Both names
// end in a secret word, so SecretConfig drops either if it is ever put in appsettings.

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

    public AzureSpeechOptions AzureSpeech { get; set; } = new();
    public WorkerOptions Worker { get; set; } = new();
    public YouTubeOptions YouTube { get; set; } = new();

    public sealed class AzureSpeechOptions
    {
        public string? ApiKey { get; set; }
        public string? Region { get; set; }

        /// <summary>Optional base URL instead of https://{Region}.tts.speech.microsoft.com (a sovereign cloud, a test double).</summary>
        public string? Endpoint { get; set; }

        /// <summary>Language → neural voice name, e.g. en → en-US-AndrewNeural.</summary>
        public Dictionary<string, string> Voices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Silence between two spoken parts in narration.wav, in milliseconds. Part of every timing.</summary>
        public int GapMs { get; set; } = 200;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && (!string.IsNullOrWhiteSpace(Region) || !string.IsNullOrWhiteSpace(Endpoint));

        public Uri BaseUri => new((string.IsNullOrWhiteSpace(Endpoint) ? $"https://{Region}.tts.speech.microsoft.com" : Endpoint).TrimEnd('/') + "/");
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
