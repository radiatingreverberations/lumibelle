using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Lumibelle.Tests;

internal sealed class UpgradeWarningLogger<T> : ILogger<T>
{
    internal readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries = new();
    internal readonly TaskCompletionSource Logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
    {
        Entries.Enqueue((level, formatter(state, error), error));
        Logged.TrySetResult();
    }
}
