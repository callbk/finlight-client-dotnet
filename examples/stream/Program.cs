// The enriched stream: prints 5 articles, then leaves the loop (which closes the connection).
// Reads FINLIGHT_API_KEY, optionally FINLIGHT_BASE_URL and FINLIGHT_WSS_URL.
using Finlight;

using var client = new FinlightClient(new FinlightConfig
{
    ApiKey = Environment.GetEnvironmentVariable("FINLIGHT_API_KEY") ?? string.Empty,
    BaseUrl = Environment.GetEnvironmentVariable("FINLIGHT_BASE_URL") ?? "https://api.finlight.me",
    WssUrl = Environment.GetEnvironmentVariable("FINLIGHT_WSS_URL") ?? "wss://wss.finlight.me",
});

var options = new StreamOptions
{
    // admitted comes before the subscribe is checked: it does not mean the subscribe was accepted.
    OnAdmitted = info => Console.WriteLine($"admitted (lease {info.LeaseId})"),
    // The SDK does not backfill: use LastSeenAt with articles.search to fill a gap.
    OnReconnect = info => Console.WriteLine($"reconnected ({info.Reason}); last seen {info.LastSeenAt ?? "nothing yet"}"),
    OnError = error => Console.Error.WriteLine($"stream problem: {error.Message}"),
};

await using var stream = client.StreamAsync(
    new StreamParams { Query = "ticker:NVDA", Include = [IncludeBlock.Companies], IncludeUpdates = true },
    options);

var received = 0;
try
{
    await foreach (var item in stream)
    {
        if (item.Kind == StreamEventKind.Other)
        {
            continue; // an event this SDK version does not know
        }

        Console.WriteLine($"{item.Kind}: {item.Data!.Title}");
        if (++received == 5)
        {
            break;
        }
    }
}
catch (PreemptedException preempted)
{
    Console.Error.WriteLine($"preempted: {preempted.Reason}");
    return 1;
}
catch (FinlightApiException error)
{
    Console.Error.WriteLine($"stream ended: {error.Status} {error.Code} {error.Detail}");
    return 1;
}

return 0;
