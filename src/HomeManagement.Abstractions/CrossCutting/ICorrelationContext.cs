namespace HomeManagement.Abstractions.CrossCutting;

/// <summary>
/// Provides an ambient correlation ID that flows through all async calls within a single
/// user-initiated operation. Backed by <see cref="AsyncLocal{T}"/>.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>Current correlation ID. Returns a generated ID if none has been set.</summary>
    string CorrelationId { get; }

    /// <summary>Start a new correlation scope. Dispose the result to restore the previous ID.</summary>
    IDisposable BeginScope(string? correlationId = null);
}

/// <summary>
/// Default <see cref="ICorrelationContext"/> implementation using <see cref="AsyncLocal{T}"/>.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> _current = new();

    public string CorrelationId => _current.Value ?? Guid.NewGuid().ToString("N");

    public IDisposable BeginScope(string? correlationId = null)
    {
        var previous = _current.Value;
        _current.Value = correlationId ?? Guid.NewGuid().ToString("N");
        return new CorrelationScope(previous);
    }

    private sealed class CorrelationScope(string? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
