namespace Finlight.WebSockets;

/// <summary>
/// Tunes the streaming clients. The defaults match the TypeScript, Python, and
/// Go clients.
/// </summary>
public sealed class FinlightWebSocketOptions
{
    /// <summary>Application-level ping cadence.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Force a reconnect when no pong arrives within this window.</summary>
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>First reconnect backoff delay.</summary>
    public TimeSpan BaseReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Reconnect backoff cap.</summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Proactive connection rotation, kept under the 2-hour server cap.</summary>
    public TimeSpan ConnectionLifetime { get; init; } = TimeSpan.FromMinutes(115);

    /// <summary>Take over an existing connection for the same API key.</summary>
    public bool Takeover { get; init; }

    /// <summary>
    /// Invoked with (closeCode, reason) whenever a connection closes. Exceptions
    /// thrown by the callback are caught and logged.
    /// </summary>
    public Action<int, string>? OnClose { get; init; }
}
