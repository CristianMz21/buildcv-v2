using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BuildCv.Infrastructure.Tests.Persistence;

// Captures EVERYTHING a log call carries, not just the rendered message: the category, the level, the
// message, the exception text, every structured state pair and every enclosing scope.
//
// A sibling of the Api tests' recorder rather than a share of it, because the two live in different
// assemblies and this one answers a different question: what EF Core writes while talking to a real
// SQL Server. The breadth is the point either way — a recorder that kept only the formatted message
// would miss a value passed as a structured property, and EF Core passes the command text, the
// parameter list and the exception chain as structured properties.
internal sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<RecordedLog> _records = new();
    private IExternalScopeProvider? _scopes;

    public IReadOnlyList<RecordedLog> Records => [.. _records];

    // Every string this provider saw, flattened: messages, exception text, state keys and values, and
    // scope keys and values. What an absence assertion has to search.
    public IReadOnlyList<string> AllText => [.. _records.SelectMany(record => record.AllText)];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private void Add(RecordedLog record) => _records.Enqueue(record);

    private sealed class RecordingLogger(RecordingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            provider._scopes?.Push(state) ?? NullScope.Instance;

        // Always enabled, so nothing this provider is asked to record is filtered out inside the logger
        // itself. The factory's own minimum level still applies above it and the tests set it to Trace.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var stateValues = Flatten(state);

            var scopeValues = new List<string>();
            provider._scopes?.ForEachScope(
                (scope, collected) => collected.AddRange(Flatten(scope)), scopeValues);

            provider.Add(new RecordedLog(
                category,
                logLevel,
                formatter(state, exception),
                exception?.ToString(),
                stateValues,
                scopeValues));
        }

        // A log state or scope is usually IEnumerable<KeyValuePair<string, object?>>. Both halves of
        // every pair are captured, because a leak can just as easily sit in a property NAME built from
        // input as in its value. Anything else is captured by its ToString, which is all a sink sees.
        private static List<string> Flatten(object? state)
        {
            var text = new List<string>();
            if (state is null)
                return text;

            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    text.Add(pair.Key);
                    if (pair.Value is not null)
                        text.Add(pair.Value.ToString() ?? string.Empty);
                }
            }

            var rendered = state.ToString();
            if (rendered is not null)
                text.Add(rendered);

            return text;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

internal sealed record RecordedLog(
    string Category,
    LogLevel Level,
    string Message,
    string? Exception,
    IReadOnlyList<string> StateValues,
    IReadOnlyList<string> ScopeValues)
{
    public IEnumerable<string> AllText =>
        new[] { Category, Message, Exception ?? string.Empty }
            .Concat(StateValues)
            .Concat(ScopeValues);
}
