using Microsoft.Extensions.Logging;

namespace Renamer.Tests.TestSupport;

public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, int EventId, Exception? Error)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, eventId.Id, exception));
}
