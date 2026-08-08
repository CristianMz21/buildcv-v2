using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuildCv.Api.Tests;

// Captures EVERYTHING a log call carries, not just the rendered message: the category, the level, the
// message, the exception text, every structured state pair and every enclosing scope. That breadth is
// the point — the leak test's question is "did any candidate text reach a log by ANY route", and a
// recorder that kept only the formatted message would miss a value passed as a structured property
// that a console formatter happens not to print, or one carried on a scope opened three frames up.
//
// Scopes are captured through ISupportExternalScope, which is how the real providers get them: the
// factory hands one IExternalScopeProvider to every provider that asks, so a scope opened on any
// logger is visible here.
internal sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<RecordedLog> _records = new();
    private IExternalScopeProvider? _scopes;

    public IReadOnlyList<RecordedLog> Records => [.. _records];

    // Every string this provider saw, flattened: messages, exception text, state keys and values, and
    // scope keys and values. What an absence assertion has to search.
    public IReadOnlyList<string> AllText =>
    [
        .. _records.SelectMany(record => record.AllText)
    ];

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

        // Trace, so nothing this provider is asked to record is filtered out inside the logger itself.
        // The host's filters still apply above it; the tests raise those to Trace as well.
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

        // A log state or scope is usually IEnumerable<KeyValuePair<string, object?>> — that is what
        // both the message-format overload and a dictionary scope produce. Both halves of every pair
        // are captured, because a leak can just as easily be in a property NAME built from input as in
        // its value. Anything else is captured by its ToString, which is all a sink would see either.
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

internal static class RecordingLogging
{
    // Registers the recorder AND opens the filters all the way down. Without the second half this is a
    // recorder that certifies whatever the host's appsettings happened to let through — and the repo's
    // appsettings.json already filters Microsoft.AspNetCore to Warning, which would hide the framework's
    // own Debug-level lines about binding failures. An absence assertion over a filtered log is exactly
    // the control that proves nothing.
    //
    // The rules are CLEARED rather than a permissive one added. Filter selection picks the most
    // SPECIFIC category match, so a catch-all rule loses to appsettings' "Microsoft.AspNetCore":
    // "Warning" for exactly the categories most worth watching. Configured here rather than through
    // ILoggingBuilder because this delegate runs after the host's own IConfigureOptions and therefore
    // wins; ObservabilityLeakTests.TheRecorderSeesTraceLevelFrameworkLines proves the levels really
    // are open rather than assuming it.
    public static Action<IServiceCollection> Capturing(RecordingLoggerProvider recorder) =>
        services =>
        {
            services.AddLogging(logging => logging.Services.AddSingleton<ILoggerProvider>(recorder));
            services.Configure<LoggerFilterOptions>(options =>
            {
                options.Rules.Clear();
                options.MinLevel = LogLevel.Trace;
            });
        };
}
