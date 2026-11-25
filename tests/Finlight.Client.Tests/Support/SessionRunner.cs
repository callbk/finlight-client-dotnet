using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace Finlight.Tests.Support;

/// <summary>
/// Plays a <c>ws-sessions/*.json</c> fixture against the SDK: server steps are sent by the in-process
/// server, client steps are read from what the SDK really sent, time moves on a fake clock. Steps the
/// SDK can never produce (a truncated frame, a legacy key, a re-subscribe on the same connection, a
/// ping on <c>$default</c>) are not sent; the server's answers in the fixture still are.
/// </summary>
internal sealed class SessionRunner
{
    private static readonly string[] SubscribeExtras = ["payloadVersion", "clientNonce"];

    private readonly WsMock _ws;
    private readonly JsonObject _fixture;
    private readonly bool _raw;
    private readonly string[] _forbidden;
    private readonly List<JsonNode> _steps;
    private ServerConn? _conn;
    private ServerConn? _subscribedOn;
    private int _admitted;
    private int _items;
    private int _pongs;
    private int _scheduled;
    private int _errors;
    private int _duplicates;
    private int _quotaWaits;

    public SessionRunner(WsMock ws, JsonObject fixture, params string[] forbidden)
    {
        _ws = ws;
        _fixture = fixture;
        _raw = fixture["endpoint"]?.GetValue<string>() == "raw";
        _forbidden = forbidden;
        _steps = [.. fixture["steps"]!.AsArray().Select(s => s!)];
        Subscribe = Without(
            _steps.FirstOrDefault(s => Step(s) == "clientSends" && IsSubscribe(s["frame"]))?["frame"]?.AsObject()
                ?? new JsonObject { ["payloadVersion"] = 3, ["query"] = "ticker:XMPL", ["clientNonce"] = "c-kit-1" },
            forbidden);
    }

    public StreamProbe Probe { get; private set; } = null!;

    /// <summary>The subscribe the SDK must send (the fixture's, without keys it never sends).</summary>
    public JsonObject Subscribe { get; }

    public ServerConn? Conn => _conn;

    public WsMock Ws => _ws;

    public async Task RunAsync(Func<SessionRunner, Task>? after = null, Func<FinlightConfig, FinlightConfig>? configure = null)
    {
        _ws.AutoPong = !_steps.Any(s => Step(s) == "clientSends" && s["match"] is not null);
        foreach (var dial in _steps.Where(s => Step(s) == "dial"))
        {
            var status = dial["server"]!["status"]!.GetValue<int>();
            _ws.DialScript.Enqueue((status, dial["server"]!["body"]?.GetValue<string>()));
        }

        var firstDial = _steps.First(s => Step(s) == "dial");
        var apiKey = firstDial["expectRequestHeaders"]?["x-api-key"]?.GetValue<string>() ?? "sk_missing_in_fixture";
        var args = Without(Subscribe, SubscribeExtras);
        var clientConfig = _fixture["clientConfig"] as JsonObject;
        Probe = new StreamProbe(_ws).Start(
            _raw,
            args,
            apiKey,
            Subscribe["clientNonce"]?.GetValue<string>() ?? "c-kit-1",
            config =>
            {
                if (clientConfig?["quotaReconnect"] is { } quota)
                {
                    config = config with { QuotaReconnect = quota.GetValue<bool>() };
                }

                return configure?.Invoke(config) ?? config;
            });

        try
        {
            for (var i = 0; i < _steps.Count; i++)
            {
                await StepAsync(_steps[i], i + 1 < _steps.Count ? _steps[i + 1] : null, i);
            }

            if (after is not null)
            {
                await after(this);
            }
        }
        finally
        {
            await Probe.StopAsync();
        }

        // Stopping closes every socket and clears every timer.
        Assert.Equal(0, Probe.Core.OpenTimers);
        Assert.Equal(0, Probe.Core.OpenConnections);
        Assert.True(Probe.Stream.Closed);
        await Wait.ForAsync(() => _ws.OpenConnections() == 0, "every server connection closed");
        Probe.Client.Dispose();
    }

    private async Task StepAsync(JsonNode step, JsonNode? next, int index)
    {
        switch (Step(step))
        {
            case "dial":
                {
                    var dial = await _ws.NextDialAsync($"dial (step {index})");
                    Assert.Equal(_raw ? "/raw" : "/", dial.Path);
                    Assert.DoesNotContain("apikey=", dial.RawUrl, StringComparison.OrdinalIgnoreCase);
                    Assert.True(dial.Headers.ContainsKey("x-client-version"));
                    Assert.StartsWith("finlight-client-dotnet/", dial.Headers["User-Agent"], StringComparison.Ordinal);
                    if (step["expectRequestHeaders"]?["x-api-key"]?.GetValue<string>() is { } key)
                    {
                        Assert.Equal(key, dial.Headers["x-api-key"]);
                    }

                    if (dial.Conn is not null)
                    {
                        _conn = dial.Conn;
                    }

                    break;
                }

            case "clientSends":
                await ClientSendsAsync(step);
                break;
            case "serverSends":
                {
                    var before = Probe.Frames;
                    var frame = step["frame"]!;
                    await _conn!.SendAsync(frame is JsonValue v && v.TryGetValue<string>(out var text) ? text : frame.ToJsonString());
                    await Wait.ForAsync(() => Probe.Frames > before || Probe.Done, $"the client to handle step {index}");
                    await Probe.SettleAsync();
                    break;
                }

            case "serverCloses":
                if (_conn is { Open: true })
                {
                    await _conn.CloseAsync(WebSocketCloseStatus.EndpointUnavailable);
                }

                await Probe.SettleAsync();
                await Wait.SettleAsync();
                await Probe.SettleAsync();
                break;
            case "advance":
                await Probe.AdvanceAsync(TimeSpan.FromMilliseconds(step["ms"]!.GetValue<long>()));
                break;
            case "expect":
                await ExpectAsync(step, next);
                break;
            default:
                throw new InvalidOperationException($"unknown step {Step(step)}");
        }
    }

    private async Task ClientSendsAsync(JsonNode step)
    {
        if (step["match"] is JsonObject match)
        {
            var message = await _conn!.NextAsync($"a client frame matching {match.ToJsonString()}");
            foreach (var (key, value) in match)
            {
                Assert.Equal(Kit.Text(value), Kit.Text(message?[key]));
            }

            if (match["action"]?.GetValue<string>() == "ping")
            {
                Assert.Equal(System.Text.Json.JsonValueKind.Number, message!["t"]!.GetValueKind());
            }

            return;
        }

        var frame = step["frame"];
        if (frame is JsonObject obj && obj["action"]?.GetValue<string>() == "ping")
        {
            // A ping may or may not come before the subscribe; take it only if it is there.
            if (_conn!.Peek() is JsonObject head && head["action"]?.GetValue<string>() == "ping")
            {
                _conn.Skip();
            }

            return;
        }

        if (!IsSubscribe(frame) || _subscribedOn == _conn)
        {
            // A truncated frame, a ping on $default or a re-subscribe on the same connection: never sent by the SDK.
            return;
        }

        var sent = (await _conn!.NextAsync("the subscribe frame", skipPings: true))!.AsObject();
        AssertSubscribe(sent);
        _subscribedOn = _conn;
    }

    private void AssertSubscribe(JsonObject sent)
    {
        Assert.Equal(Kit.Canonical(Subscribe), Kit.Canonical(sent));
        Assert.Equal(System.Text.Json.JsonValueKind.Number, sent["payloadVersion"]!.GetValueKind());
        Assert.Equal(3, sent["payloadVersion"]!.GetValue<int>());
        foreach (var key in new[] { "includeContent", "includeEntities", "extended" }.Concat(_forbidden))
        {
            Assert.False(sent.ContainsKey(key), $"the subscribe must not carry {key}");
        }
    }

    private async Task ExpectAsync(JsonNode step, JsonNode? next)
    {
        var probe = Probe;
        switch (step["outcome"]!.GetValue<string>())
        {
            case "admitted":
                {
                    await Wait.ForAsync(() => probe.Admitted.Count > _admitted, "connection.admitted");
                    var admitted = probe.Admitted[_admitted++];
                    if (step["leaseId"]?.GetValue<string>() is { } lease)
                    {
                        Assert.Equal(lease, admitted.LeaseId);
                    }

                    if (step["clientNonce"]?.GetValue<string>() is { } nonce)
                    {
                        Assert.Equal(nonce, admitted.ClientNonce);
                    }

                    Assert.NotNull(admitted.ServerNow);
                    break;
                }

            case "noAdmitted":
                await Wait.SettleAsync();
                Assert.Equal(_admitted, probe.Admitted.Count);
                Assert.Null(probe.Terminal);
                Assert.False(probe.Done);
                break;
            case "item":
                {
                    await Wait.ForAsync(() => probe.Items.Skip(_items).Any(i => i.Kind != StreamEventKind.Other), "an item");
                    while (probe.Items[_items].Kind == StreamEventKind.Other)
                    {
                        _items++;
                    }

                    var item = probe.Items[_items++];
                    Assert.Equal(step["kind"]!.GetValue<string>(), item.Kind.Value);
                    Assert.NotNull(item.Data);
                    if (step["id"]?.GetValue<string>() is { } id)
                    {
                        Assert.Equal(id, item.Data!.Id);
                    }

                    if (_raw)
                    {
                        Assert.False(item.Data!.ContentPresent);
                        Assert.False(item.Data.SentimentPresent);
                        Assert.False(item.Data.CompaniesPresent);
                        Assert.False(item.Data.EventsPresent);
                        Assert.Null(item.Data.AvailableAt);
                    }

                    break;
                }

            case "duplicateDropped":
                {
                    await Wait.ForAsync(() => probe.Duplicates.Count > _duplicates, "a dropped duplicate");
                    var id = probe.Duplicates[_duplicates++];
                    if (step["id"]?.GetValue<string>() is { } expected)
                    {
                        Assert.Equal(expected, id);
                    }

                    await Wait.SettleAsync();
                    Assert.Equal(_items, probe.Items.Count);
                    break;
                }

            case "liveness":
                await Wait.ForAsync(() => probe.Pongs > _pongs, "a pong refreshing the watchdog");
                _pongs = probe.Pongs;
                Assert.Null(probe.Terminal);
                Assert.Equal(_scheduled, probe.Scheduled.Count);
                break;
            case "logOnly":
                {
                    await Wait.ForAsync(
                        () => probe.Errors.Count > _errors || probe.Items.Skip(_items).Any(i => i.Kind == StreamEventKind.Other),
                        "a reported error or an other item");
                    if (step["code"]?.GetValue<string>() is { } code)
                    {
                        Assert.Contains(code, probe.Errors.Skip(_errors).OfType<FinlightApiException>().Select(e => e.Code));
                    }

                    _errors = probe.Errors.Count;
                    while (_items < probe.Items.Count && probe.Items[_items].Kind == StreamEventKind.Other)
                    {
                        _items++;
                    }

                    await Wait.SettleAsync();
                    Assert.Null(probe.Terminal);
                    Assert.False(probe.Done);
                    Assert.Equal(_scheduled, probe.Scheduled.Count);
                    Assert.True(_conn?.Open);
                    break;
                }

            case "reported":
                {
                    Assert.Equal("onQuotaWait", step["hook"]!.GetValue<string>());
                    await Wait.ForAsync(() => probe.QuotaWaits.Count > _quotaWaits, "an onQuotaWait report");
                    CheckQuotaWait(probe.QuotaWaits[_quotaWaits++], step);
                    Assert.False(probe.Done);
                    Assert.Null(probe.Terminal);
                    Assert.Empty(probe.Errors.OfType<PreemptedException>());
                    break;
                }

            case "terminalError":
                {
                    await Wait.ForAsync(() => probe.Done, "the terminal error");
                    CheckError(probe.Terminal, step);
                    Assert.Equal(_scheduled, probe.Scheduled.Count);
                    break;
                }

            case "noReconnect":
                {
                    var dials = _ws.DialCount;
                    await probe.SettleAsync();
                    await Wait.SettleAsync();
                    if (probe.Done)
                    {
                        // After a terminal error nothing dials again, however long we wait.
                        probe.Clock.Advance(TimeSpan.FromHours(3));
                        await Wait.SettleAsync();
                    }

                    Assert.Equal(dials, _ws.DialCount);
                    break;
                }

            case "reconnect":
                await ReconnectAsync(step, next);
                break;
            default:
                throw new InvalidOperationException($"unknown outcome {step["outcome"]}");
        }
    }

    private async Task ReconnectAsync(JsonNode step, JsonNode? next)
    {
        var probe = Probe;
        await Wait.ForAsync(() => probe.Scheduled.Count > _scheduled, "a scheduled reconnect");
        var (delay, reason, at) = probe.Scheduled[_scheduled++];
        Assert.False(probe.Done);
        Assert.Null(probe.Terminal);
        var backoff = step["backoff"];
        if (backoff?.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            switch (backoff.GetValue<string>())
            {
                case "long":
                    Assert.True(delay >= TimeSpan.FromSeconds(60), $"long backoff, got {delay}");
                    break;
                case "quota":
                    Assert.Equal(probe.Config.QuotaReconnectInterval, delay);
                    break;
            }
        }
        else if (backoff?.GetValueKind() == System.Text.Json.JsonValueKind.True)
        {
            Assert.True(delay > TimeSpan.Zero, "a backoff delay");
        }

        if (step["firstRetryMinMs"] is { } min)
        {
            Assert.True(delay >= TimeSpan.FromMilliseconds(min.GetValue<long>()), $"first retry after {delay}");
        }

        if (step["reason"]?.GetValue<string>() is { } expectedReason)
        {
            Assert.Equal(expectedReason, reason.Value);
        }

        if (step["reported"] is JsonObject reported)
        {
            await Wait.ForAsync(() => probe.QuotaWaits.Count > _quotaWaits, "an onQuotaWait report");
            CheckQuotaWait(probe.QuotaWaits[_quotaWaits++], reported);
        }

        if (step["hint"]?.GetValue<string>() == "takeover")
        {
            Assert.Contains(probe.Errors, e => e.Message.Contains("takeover", StringComparison.OrdinalIgnoreCase)
                || (e is FinlightApiException api && (api.Detail ?? string.Empty).Contains("takeover", StringComparison.OrdinalIgnoreCase)));
        }

        // No dial before the delay is over, then exactly at it.
        var due = at + delay;
        var now = probe.Clock.GetUtcNow();
        var dialsBefore = _ws.DialCount;
        if (due - now > TimeSpan.FromMilliseconds(1))
        {
            await probe.AdvanceAsync(due - now - TimeSpan.FromMilliseconds(1));
            await Wait.SettleAsync();
            Assert.Equal(dialsBefore, _ws.DialCount);
            await probe.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        }

        if (next is not null && Step(next) == "dial")
        {
            return; // the next dial step takes the new connection
        }

        var dial = await _ws.NextDialAsync("the redial");
        if (step["resubscribe"]?.GetValue<bool>() == true)
        {
            _conn = dial.Conn ?? throw new InvalidOperationException("the redial was refused");
            var sent = (await _conn.NextAsync("the re-subscribe", skipPings: true))!.AsObject();
            AssertSubscribe(sent);
            _subscribedOn = _conn;
        }

        if (step["surfaceAfterConsecutive"] is { } threshold)
        {
            await SearchFailedUntilAsync(threshold.GetValue<int>());
        }
    }

    /// <summary>S-14: SEARCH_FAILED is reported only after the threshold of failures in a row.</summary>
    private async Task SearchFailedUntilAsync(int threshold)
    {
        var probe = Probe;
        int Surfaced() => probe.Errors.OfType<FinlightApiException>().Count(e => e.Code == "SEARCH_FAILED");
        Assert.Equal(0, Surfaced());
        for (var n = 2; n <= threshold; n++)
        {
            await _conn!.SendAsync("""{"event":"connection.admitted","payloadVersion":3,"data":{"leaseId":"l","serverNow":1,"clientNonce":"c-kit-1"}}""");
            await _conn.SendAsync("""{"event":"connection.error","payloadVersion":3,"data":{"code":"SEARCH_FAILED","message":"Search temporarily unavailable"}}""");
            await _conn.CloseAsync(WebSocketCloseStatus.EndpointUnavailable);
            await Wait.ForAsync(() => probe.Scheduled.Count > _scheduled, "the next reconnect");
            Assert.Equal(n == threshold ? 1 : 0, Surfaced());
            var (delay, reason, _) = probe.Scheduled[_scheduled++];
            Assert.Equal(ReconnectReason.SearchFailed, reason);
            await probe.AdvanceAsync(delay);
            var dial = await _ws.NextDialAsync("the redial after SEARCH_FAILED");
            _conn = dial.Conn!;
            AssertSubscribe((await _conn.NextAsync("the re-subscribe", skipPings: true))!.AsObject());
        }

        Assert.Null(probe.Terminal);
        Assert.False(probe.Done);
    }

    private static void CheckQuotaWait(QuotaWaitInfo info, JsonNode expected)
    {
        Assert.Equal(expected["attempt"]!.GetValue<int>(), info.Attempt);
        Assert.Equal(TimeSpan.FromHours(1), info.NextAttemptIn);
        CheckError(info.Error, expected);
    }

    private static void CheckError(Exception? error, JsonNode expected)
    {
        Assert.NotNull(error);
        switch (expected["error"]!.GetValue<string>())
        {
            case "PreemptedError":
                {
                    var preempted = Assert.IsType<PreemptedException>(error);
                    Assert.Equal("preempted", preempted.Kind.Value);
                    Assert.Equal(expected["reason"]!.GetValue<string>(), preempted.Reason.Value);
                    Assert.Equal(expected["newLeaseId"]?.GetValue<string>(), preempted.NewLeaseId);
                    break;
                }

            case "FinlightApiError":
                {
                    var api = Assert.IsAssignableFrom<FinlightApiException>(error);
                    Assert.Equal("api", api.Kind.Value);
                    if (expected["apiKind"]?.GetValue<string>() is { } apiKind)
                    {
                        Assert.Equal(apiKind, api.ApiKind.Value);
                        var expectedType = apiKind switch
                        {
                            "unauthorized" => typeof(UnauthorizedException),
                            "forbidden" => typeof(ForbiddenException),
                            "validation_failed" => typeof(ValidationFailedException),
                            _ => typeof(FinlightApiException),
                        };
                        Assert.IsType(expectedType, api);
                    }

                    if (expected["status"] is { } status)
                    {
                        Assert.Equal(status.GetValue<int>(), api.Status);
                    }

                    if (expected["code"]?.GetValue<string>() is { } code)
                    {
                        Assert.Equal(code, api.Code);
                        Assert.NotEmpty(api.Errors);
                        Assert.False(string.IsNullOrEmpty(api.Errors[0].Field));
                        Assert.NotNull(api.Errors[0].Constraints);
                        Assert.NotEmpty(api.Errors[0].Constraints!);
                    }

                    break;
                }

            default:
                throw new InvalidOperationException($"unknown error {expected["error"]}");
        }
    }

    private static string Step(JsonNode step) => step["step"]!.GetValue<string>();

    private static bool IsSubscribe(JsonNode? frame)
        => frame is JsonObject obj && obj.ContainsKey("payloadVersion") && !obj.ContainsKey("type") && !obj.ContainsKey("action");

    private static JsonObject Without(JsonObject frame, IEnumerable<string> keys)
    {
        var copy = frame.DeepClone().AsObject();
        foreach (var key in keys)
        {
            copy.Remove(key);
        }

        return copy;
    }
}
