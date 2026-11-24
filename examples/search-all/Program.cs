// Walks the cursor over at most 3 pages. Every page costs quota.
// Reads FINLIGHT_API_KEY, optionally FINLIGHT_BASE_URL and FINLIGHT_WSS_URL.
using Finlight;

using var client = new FinlightClient(new FinlightConfig
{
    ApiKey = Environment.GetEnvironmentVariable("FINLIGHT_API_KEY") ?? string.Empty,
    BaseUrl = Environment.GetEnvironmentVariable("FINLIGHT_BASE_URL") ?? "https://api.finlight.me",
    WssUrl = Environment.GetEnvironmentVariable("FINLIGHT_WSS_URL") ?? "wss://wss.finlight.me",
});

var parameters = new SearchParams { Query = "ticker:NVDA", Languages = ["en"], Limit = 20 };
var result = client.Articles.SearchAllAsync(parameters, new SearchAllOptions { MaxPages = 3 });
var count = 0;
try
{
    await foreach (var article in result)
    {
        count++;
    }
}
catch (FinlightApiException error)
{
    Console.Error.WriteLine($"API error {error.Status} {error.Code} (requestId {error.RequestId}): {error.Detail}");
    return 1;
}

Console.WriteLine($"articles: {count}, pages: {result.PagesFetched}");
if (result.Truncated)
{
    // Continue later with the same parameters plus the cursor.
    Console.WriteLine($"stopped at maxPages; continue with Cursor = \"{result.NextCursor}\"");
}

return 0;
