using System.Text.Json;

namespace Finlight.WebSockets;

/// <summary>The envelope of every message the finlight WebSocket server sends.</summary>
internal sealed class ServerMessage
{
    public string? Action { get; init; }

    /// <summary>Epoch milliseconds echoed back in pong messages.</summary>
    public long T { get; init; }

    public string? LeaseId { get; init; }

    public string? ClientNonce { get; init; }

    public string? Reason { get; init; }

    public string? NewLeaseId { get; init; }

    /// <summary>Milliseconds to wait before reconnecting after an admin kick.</summary>
    public long RetryAfter { get; init; }

    public JsonElement? Data { get; init; }

    public JsonElement? Error { get; init; }
}
