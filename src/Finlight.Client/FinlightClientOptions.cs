namespace Finlight;

/// <summary>
/// Configures the finlight client. Only <see cref="ApiKey"/> is required; the
/// defaults match the TypeScript, Python, and Go clients.
/// </summary>
public sealed class FinlightClientOptions
{
    /// <summary>Your finlight API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Base URL of the REST API.</summary>
    public string BaseUrl { get; init; } = "https://api.finlight.me";

    /// <summary>Base URL of the WebSocket endpoint.</summary>
    public string WssUrl { get; init; } = "wss://wss.finlight.me";

    /// <summary>Per-attempt timeout for REST requests and the WebSocket handshake.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Total REST request attempts (initial try plus retries).</summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>Clock used for retry backoff and reconnect scheduling. Override in tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
