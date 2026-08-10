namespace Finlight;

/// <summary>
/// Thrown by the WebSocket clients when the server permanently rejected the
/// connection (close code 1008). Reconnecting will not help; contact finlight
/// support.
/// </summary>
public sealed class FinlightBlockedException : FinlightException
{
    /// <summary>Initializes the exception.</summary>
    public FinlightBlockedException()
        : base("finlight: connection rejected by server (blocked)")
    {
    }
}
