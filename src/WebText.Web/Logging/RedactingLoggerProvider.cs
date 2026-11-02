using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WebText.Web.Logging;

/// <summary>
/// Wraps every logging provider (console / IIS stdout, Windows Event Log, debug, EventSource…) so each message and
/// exception is passed through <see cref="SensitiveData.Redact"/> before any of them writes it.
/// </summary>
public static class RedactingLoggingExtensions
{
    /// <summary>Call after all logging providers are registered (i.e. late in Program.cs).</summary>
    public static IServiceCollection AddLogRedaction(this IServiceCollection services)
    {
        var originals = services.Where(d => d.ServiceType == typeof(ILoggerProvider) && !d.IsKeyedService).ToList();
        foreach (var d in originals)
        {
            services.Remove(d);
            services.Add(ServiceDescriptor.Singleton<ILoggerProvider>(sp => new RedactingLoggerProvider(Create(sp, d))));
        }
        return services;
    }

    private static ILoggerProvider Create(IServiceProvider sp, ServiceDescriptor d) =>
        (ILoggerProvider)(d.ImplementationInstance
            ?? d.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!));
}

public sealed class RedactingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ILoggerProvider _inner;
    public RedactingLoggerProvider(ILoggerProvider inner) => _inner = inner;

    public ILogger CreateLogger(string categoryName) => new RedactingLogger(_inner.CreateLogger(categoryName));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (_inner is ISupportExternalScope s) s.SetScopeProvider(scopeProvider);
    }

    public void Dispose() => _inner.Dispose();
}

internal sealed class RedactingLogger : ILogger
{
    private readonly ILogger _inner;
    public RedactingLogger(ILogger inner) => _inner = inner;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!_inner.IsEnabled(logLevel)) return;

        var message = formatter(state, null) ?? "";
        var redactedMessage = SensitiveData.Redact(message);
        var ex = exception;
        if (ex != null)
        {
            var full = ex.ToString();
            var redactedFull = SensitiveData.Redact(full);
            if (!ReferenceEquals(full, redactedFull) && full != redactedFull)
                ex = new RedactedException(ex.GetType().FullName ?? "Exception", redactedFull);
        }

        var structured = state as IReadOnlyList<KeyValuePair<string, object?>>;
        var valuesNeedRedaction = structured != null && structured.Any(kv =>
            kv.Key != "{OriginalFormat}" && kv.Value is not null && SensitiveData.NeedsRedaction(kv.Value.ToString()));

        if (redactedMessage == message && !valuesNeedRedaction)
        {
            // Nothing sensitive: pass through untouched (keeps structured properties exactly as they were).
            _inner.Log(logLevel, eventId, state, ex, formatter);
            return;
        }

        var safe = new RedactedState(redactedMessage, structured);
        _inner.Log(logLevel, eventId, safe, ex, static (s, _) => s.ToString());
    }
}

/// <summary>A log entry's properties with every value redacted; its text is the redacted message.</summary>
internal sealed class RedactedState : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly string _message;
    private readonly List<KeyValuePair<string, object?>> _values;

    public RedactedState(string message, IReadOnlyList<KeyValuePair<string, object?>>? original)
    {
        _message = message;
        _values = (original ?? Array.Empty<KeyValuePair<string, object?>>())
            .Where(kv => kv.Key != "{OriginalFormat}")
            .Select(kv => new KeyValuePair<string, object?>(kv.Key,
                kv.Value?.ToString() is { } text && SensitiveData.NeedsRedaction(text) ? SensitiveData.Redact(text) : kv.Value))
            .ToList();
        // The original template could itself contain the value (e.g. an interpolated string), so use the redacted text.
        _values.Add(new("{OriginalFormat}", message));
    }

    public KeyValuePair<string, object?> this[int index] => _values[index];
    public int Count => _values.Count;
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => _message;
}

/// <summary>Stands in for an exception whose message or stack trace contained something sensitive.</summary>
public sealed class RedactedException : Exception
{
    private readonly string _text;
    public RedactedException(string originalType, string redactedText) : base($"{originalType} (details redacted)") => _text = redactedText;
    public override string ToString() => _text;
    public override string? StackTrace => null;
}
