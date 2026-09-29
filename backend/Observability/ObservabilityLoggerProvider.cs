// Observability — ILoggerProvider that forwards Warning+ from app code to the
// same ILogQueue as every other observability entry (Category "app").
//
// Only Warning and above: Debug/Information from ASP.NET internals would
// swamp the queue. No external dependency (no Serilog).

using Microsoft.Extensions.Logging;
using RabbitIC.Api.Models;

namespace RabbitIC.Api.Observability;

[ProviderAlias("Observability")]
public sealed class ObservabilityLoggerProvider : ILoggerProvider
{
    private readonly ILogQueue           _queue;
    private readonly IWebHostEnvironment _env;

    public ObservabilityLoggerProvider(ILogQueue queue, IWebHostEnvironment env)
    {
        _queue = queue;
        _env   = env;
    }

    public ILogger CreateLogger(string categoryName) =>
        new ObservabilityLogger(categoryName, _queue, _env);

    public void Dispose() { }
}

internal sealed class ObservabilityLogger : ILogger
{
    private readonly string              _category;
    private readonly ILogQueue           _queue;
    private readonly IWebHostEnvironment _env;

    public ObservabilityLogger(string category, ILogQueue queue, IWebHostEnvironment env)
    {
        _category = category;
        _queue    = queue;
        _env      = env;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        if (_category.StartsWith("RabbitIC.Api.Observability", StringComparison.Ordinal)) return; // no recursion

        var message = formatter(state, exception);
        if (exception is not null)
            message = $"{message}\n{exception}";

        _queue.Enqueue(new ActivityLog
        {
            TimestampUtc = DateTime.UtcNow,
            Level        = logLevel.ToString(),
            Category     = "app",
            Message      = message.Length > 4000 ? message[..4000] : message,
            EventName    = eventId.Name,
            Environment  = _env.EnvironmentName,
            MachineName  = System.Net.Dns.GetHostName(),
        });
    }
}
