using System.Text.Json;

namespace Finlight.Internal;

internal enum FrameType
{
    Article,
    Admitted,
    Pong,
    Preempted,
    Error,
    Other,
    Ignored,
}

/// <summary>A decoded WebSocket frame.</summary>
internal sealed record DecodedFrame(FrameType Type)
{
    public string? Event { get; init; }

    public StreamEventKind Kind { get; init; }

    public JsonElement Data { get; init; }

    public string? LeaseId { get; init; }

    public long? ServerNow { get; init; }

    public string? ClientNonce { get; init; }

    public string? Reason { get; init; }

    public string? NewLeaseId { get; init; }

    public long? Timestamp { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public JsonElement Details { get; init; }

    public bool Legacy { get; init; }

    public string? Ignored { get; init; }
}

/// <summary>
/// WebSocket frame decoder (R-WS-1). A frame with <c>event</c> goes to the v3 dispatch. A frame
/// with only <c>action</c> is a control frame in v2 form: before the first v3 subscribe the server
/// still answers pings, preempts and errors that way; they map onto the same v3 types. A v2 article
/// frame (<c>action: "sendArticle"</c>) is never delivered as an article.
/// </summary>
internal static class Frames
{
    public static DecodedFrame Decode(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object)
        {
            return new DecodedFrame(FrameType.Ignored) { Ignored = "the frame is not a JSON object" };
        }

        if (frame.TryGetProperty("event", out var eventElement) && eventElement.ValueKind == JsonValueKind.String)
        {
            var name = eventElement.GetString()!;
            frame.TryGetProperty("data", out var data);
            var obj = data.ValueKind == JsonValueKind.Object ? data : default;
            return name switch
            {
                StreamEventNameValues.ArticleCreated => new DecodedFrame(FrameType.Article) { Event = name, Kind = StreamEventKind.Created, Data = data },
                StreamEventNameValues.ArticleUpdated => new DecodedFrame(FrameType.Article) { Event = name, Kind = StreamEventKind.Updated, Data = data },
                StreamEventNameValues.ConnectionAdmitted => Control("admitted", obj, legacy: false),
                StreamEventNameValues.ConnectionPong => Control("pong", obj, legacy: false),
                StreamEventNameValues.ConnectionPreempted => Control("preempted", obj, legacy: false),
                StreamEventNameValues.ConnectionError => new DecodedFrame(FrameType.Error)
                {
                    Code = Str(obj, "code"),
                    Message = Str(obj, "message"),
                    Details = Prop(obj, "details"),
                },
                _ => new DecodedFrame(FrameType.Other) { Event = name, Kind = StreamEventKind.Other },
            };
        }

        if (frame.TryGetProperty("action", out var actionElement) && actionElement.ValueKind == JsonValueKind.String)
        {
            // v2-form control frames, sent before the first v3 subscribe.
            var action = actionElement.GetString()!;
            switch (action)
            {
                case "admit":
                    return Control("admitted", frame, legacy: true);
                case "pong":
                case "preempted":
                    return Control(action, frame, legacy: true);
                case "error":
                    var payload = Prop(frame, "error");
                    if (payload.ValueKind == JsonValueKind.Undefined)
                    {
                        payload = Prop(frame, "data");
                    }

                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        return new DecodedFrame(FrameType.Error)
                        {
                            Code = Str(payload, "code"),
                            Message = Str(payload, "message") ?? Str(payload, "error"),
                            Details = Prop(payload, "details"),
                            Legacy = true,
                        };
                    }

                    return new DecodedFrame(FrameType.Error)
                    {
                        Message = payload.ValueKind == JsonValueKind.String ? payload.GetString() : null,
                        Legacy = true,
                    };
                case "sendArticle":
                    return new DecodedFrame(FrameType.Ignored) { Ignored = "a v2 article frame (this stream speaks payloadVersion 3 only)" };
                default:
                    return new DecodedFrame(FrameType.Ignored) { Ignored = $"unknown v2 action '{action}'" };
            }
        }

        return new DecodedFrame(FrameType.Ignored) { Ignored = "the frame has neither event nor action" };
    }

    private static DecodedFrame Control(string name, JsonElement data, bool legacy) => name switch
    {
        "admitted" => new DecodedFrame(FrameType.Admitted)
        {
            LeaseId = Str(data, "leaseId"),
            ServerNow = Long(data, "serverNow"),
            ClientNonce = Str(data, "clientNonce"),
            Legacy = legacy,
        },
        "pong" => new DecodedFrame(FrameType.Pong) { ServerNow = Long(data, "serverNow"), Legacy = legacy },
        _ => new DecodedFrame(FrameType.Preempted)
        {
            Reason = Str(data, "reason") ?? "unknown",
            NewLeaseId = Str(data, "newLeaseId"),
            Timestamp = Long(data, "timestamp"),
            Legacy = legacy,
        },
    };

    private static JsonElement Prop(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value : default;

    private static string? Str(JsonElement obj, string key)
    {
        var value = Prop(obj, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long? Long(JsonElement obj, string key)
    {
        var value = Prop(obj, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }
}
